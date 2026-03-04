using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic ToolRegistry resolver with strict semantic overlap gating.
/// </summary>
public sealed class ToolRegistryResolver(
    IToolRegistrySqlRepository repository,
    ILogger<ToolRegistryResolver> logger) : IToolRegistryResolver
{
    private const int EnvMatchWeight = 1000;
    private const double PriorityWeight = 0.1d;
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "all", "list", "show", "give", "get", "please", "to", "for", "of", "in", "on", "with",
        "sql", "windows", "unified", "live", "history"
    ];

    private static readonly IReadOnlyDictionary<string, string> SingularPluralMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["databases"] = "database",
            ["jobs"] = "job",
            ["logins"] = "login",
            ["users"] = "user",
            ["services"] = "service",
            ["principals"] = "principal",
            ["drives"] = "drive",
            ["fails"] = "fail",
            ["failed"] = "fail",
            ["failures"] = "fail",
            ["failure"] = "fail",
            ["errored"] = "fail",
            ["errors"] = "error"
        };

    private readonly IToolRegistrySqlRepository _repository = repository;
    private readonly ILogger<ToolRegistryResolver> _logger = logger;

    public async Task<ToolResolutionResult> ResolveBestToolAsync(
        string environment,
        string tunedQuestion,
        CancellationToken cancellationToken)
    {
        if (EnvironmentRules.IsGeneral(environment))
        {
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_SKIPPED_GENERAL"
            };
        }

        if (environment.Contains('<') || environment.Contains('>'))
        {
            _logger.LogWarning("Invalid environment format for ToolRegistry lookup: {Environment}", environment);
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_INVALID_ENVIRONMENT"
            };
        }

        try
        {
            var candidates = await _repository.GetActiveToolsByEnvironmentAsync(environment, cancellationToken);
            return ResolveFromCandidatesForTesting(environment, tunedQuestion, candidates, _logger);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("DB_CONFIG_MISSING", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("ConnectionStrings:SqlGig is missing. ToolRegistry lookup skipped.");
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_CONFIG_MISSING"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ToolRegistry lookup failed for environment {Environment}.", environment);
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_ERROR"
            };
        }
    }

    internal static ToolResolutionResult ResolveFromCandidatesForTesting(
        string environment,
        string tunedQuestion,
        List<QueryCodeCandidate> candidates,
        ILogger? logger = null)
    {
        var normalizedQuestion = NormalizeText(tunedQuestion);
        var questionTokens = Tokenize(normalizedQuestion);

        logger?.LogInformation(
            "Resolver input EnvironmentDb={EnvironmentDb}, TunedQuestionNormalized={TunedQuestionNormalized}",
            environment,
            normalizedQuestion);

        if (candidates.Count == 0)
        {
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_NO_MATCH"
            };
        }

        if (EnvironmentRules.IsSqlServer(environment)
            && IsAmbiguousUsersQuestion(normalizedQuestion)
            && !candidates.Any(c => c.QueryCode.Contains("SQL_USERS", StringComparison.OrdinalIgnoreCase)))
        {
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_LOW_CONFIDENCE_AMBIGUOUS_USERS"
            };
        }

        var filteredByIntent = ApplyIntentGate(environment, normalizedQuestion, candidates);

        var scored = filteredByIntent
            .Where(c =>
                string.Equals(c.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.ScriptTemplate))
            .Select(c => ScoreCandidate(c, normalizedQuestion, questionTokens))
            .Where(c => c.HasOverlap)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.KeywordHitCount)
            .ThenByDescending(c => c.TagOverlap)
            .ThenByDescending(c => c.Priority * PriorityWeight)
            .ToList();

        if (scored.Count == 0)
        {
            return new ToolResolutionResult
            {
                Found = false,
                SelectionMethod = "DB_LOW_CONFIDENCE"
            };
        }

        foreach (var candidate in scored.Take(5))
        {
            logger?.LogInformation(
                "Resolver top candidate QueryCode={QueryCode}, Score={Score}, KeywordHitCount={KeywordHitCount}, TokenOverlap={TokenOverlap}, NameTokenOverlap={NameTokenOverlap}, TagOverlap={TagOverlap}, Priority={Priority}",
                candidate.QueryCode,
                candidate.Score,
                candidate.KeywordHitCount,
                candidate.KeywordTokenOverlap,
                candidate.NameTokenOverlap,
                candidate.TagOverlap,
                candidate.Priority);
        }

        var winner = scored[0];
        var runnerUp = scored.Count > 1 ? scored[1] : null;
        var selectionMethod = "DB_RANK";
        var tieBreakForced = false;

        if (runnerUp is not null && IsWithinFivePercent(winner.Score, runnerUp.Score))
        {
            var tieBreak = ResolveTieBreak(environment, normalizedQuestion, scored);
            if (tieBreak.FailLowConfidence)
            {
                return BuildLowConfidenceResult(scored, winner.Score, CalculateConfidence(winner.Score, runnerUp.Score), tieBreak.SelectionMethod);
            }

            if (!string.IsNullOrWhiteSpace(tieBreak.ForcedQueryCode))
            {
                var forced = scored.FirstOrDefault(c =>
                    c.QueryCode.Equals(tieBreak.ForcedQueryCode, StringComparison.OrdinalIgnoreCase));
                if (forced is not null)
                {
                    winner = forced;
                    selectionMethod = tieBreak.SelectionMethod ?? "DB_RANK_TIE_BREAK";
                    tieBreakForced = true;
                }
            }
        }

        var confidence = CalculateConfidence(winner.Score, runnerUp?.Score);
        if (!tieBreakForced && confidence < 0.05d)
        {
            return BuildLowConfidenceResult(scored, winner.Score, confidence, "DB_LOW_CONFIDENCE");
        }

        if (tieBreakForced && confidence < 0.05d)
            confidence = 0.51d;

        logger?.LogInformation(
            "Resolver final winner QueryCode={QueryCode}, SelectionMethod={SelectionMethod}, Score={Score}, Confidence={Confidence}",
            winner.QueryCode,
            selectionMethod,
            winner.Score,
            confidence);

        return new ToolResolutionResult
        {
            Found = true,
            ToolId = winner.ToolId,
            QueryCode = winner.QueryCode,
            ToolName = winner.ToolName,
            Environment = winner.Environment,
            Description = winner.Description,
            Keywords = winner.Keywords,
            ToolTags = winner.ToolTags,
            Priority = winner.Priority,
            IsReadOnly = winner.IsReadOnly,
            ScriptLanguage = winner.ScriptLanguage,
            ScriptTemplate = winner.ScriptTemplate,
            ParameterSchema = winner.ParameterSchema,
            OutputSchema = winner.OutputSchema,
            Score = winner.Score,
            Confidence = confidence,
            ScoreBreakdown =
                $"env={EnvMatchWeight};kw={winner.KeywordHitCount};tag={winner.TagOverlap};priority={winner.Priority}",
            SelectionMethod = selectionMethod,
            IsLowConfidence = false,
            Candidates = scored.Take(5).Select(ToCandidateModel).ToList()
        };
    }

    private static ToolResolutionResult BuildLowConfidenceResult(
        List<ScoredCandidate> scored,
        double score,
        double confidence,
        string? selectionMethod)
    {
        return new ToolResolutionResult
        {
            Found = false,
            SelectionMethod = selectionMethod ?? "DB_LOW_CONFIDENCE",
            IsLowConfidence = true,
            Score = score,
            Confidence = confidence,
            Candidates = scored.Take(5).Select(ToCandidateModel).ToList()
        };
    }

    private static ScoredCandidate ScoreCandidate(
        QueryCodeCandidate candidate,
        string normalizedQuestion,
        HashSet<string> questionTokens)
    {
        var keywordPhrases = SplitPhrases(candidate.Keywords);
        var keywordHitCount = keywordPhrases.Count(phrase =>
            normalizedQuestion.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        var keywordTokens = keywordPhrases.SelectMany(Tokenize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keywordTokenOverlap = IntersectCount(questionTokens, keywordTokens);

        var tagTokens = SplitPhrases(candidate.ToolTags).SelectMany(Tokenize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagOverlap = IntersectCount(questionTokens, tagTokens);

        var nameTokens = Tokenize($"{candidate.ToolName} {candidate.QueryCode}");
        var nameTokenOverlap = IntersectCount(questionTokens, nameTokens);

        var hasOverlap = keywordHitCount > 0 || keywordTokenOverlap > 0 || tagOverlap > 0 || nameTokenOverlap > 0;

        var score = hasOverlap
            ? EnvMatchWeight + (keywordHitCount * 50) + (tagOverlap * 10) + (nameTokenOverlap * 5) + (candidate.Priority * PriorityWeight)
            : 0d;

        return new ScoredCandidate(
            candidate,
            score,
            keywordHitCount,
            keywordTokenOverlap,
            nameTokenOverlap,
            tagOverlap,
            hasOverlap);
    }

    private static List<string> SplitPhrases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeText)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeText(string value)
    {
        var normalized = (value ?? string.Empty).ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s_\.]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        foreach (var kv in SingularPluralMap)
        {
            normalized = Regex.Replace(
                normalized,
                $@"\b{Regex.Escape(kv.Key)}\b",
                kv.Value,
                RegexOptions.IgnoreCase);
        }

        return normalized;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(value ?? string.Empty, "[a-z0-9_\\.]+", RegexOptions.IgnoreCase))
        {
            var token = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(token) && !StopWords.Contains(token))
                tokens.Add(token);
        }

        return tokens;
    }

    private static int IntersectCount(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;

        var count = 0;
        foreach (var token in left)
        {
            if (right.Contains(token))
                count++;
        }

        return count;
    }

    private static bool IsWithinFivePercent(double topScore, double secondScore)
    {
        if (topScore <= 0 || secondScore <= 0)
            return false;

        var diffPercent = (topScore - secondScore) / topScore;
        return diffPercent <= 0.05d;
    }

    private static double CalculateConfidence(double bestScore, double? secondScore)
    {
        if (bestScore <= 0) return 0;
        if (!secondScore.HasValue || secondScore.Value <= 0) return 1;

        var confidence = (bestScore - secondScore.Value) / bestScore;
        return Math.Max(0, Math.Min(1, confidence));
    }

    private static bool IsAmbiguousUsersQuestion(string normalizedQuestion)
    {
        var hasUser = ContainsAny(normalizedQuestion, "user", "users");
        var hasLogin = ContainsAny(normalizedQuestion, "login", "logins", "server principal", "sys.server_principals");
        var hasDbUser = ContainsAny(normalizedQuestion, "db user", "database user", "sys.database_principals");
        return hasUser && !hasLogin && !hasDbUser;
    }

    private static IEnumerable<QueryCodeCandidate> ApplyIntentGate(
        string environment,
        string normalizedQuestion,
        List<QueryCodeCandidate> candidates)
    {
        if (environment.Equals("SqlServer_Live", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(normalizedQuestion, "job", "agent", "schedule", "sysjobs"))
                return Gate(candidates, IsJobsCandidate);

            if (ContainsAny(normalizedQuestion, "database", "sys.databases", "db"))
                return Gate(candidates, c => HasAny(c, "database", "databases", "SQL_DATABASE"));

            if (ContainsAny(normalizedQuestion, "blocking", "blocked", "head blocker", "lck_"))
                return Gate(candidates, c => HasAny(c, "blocking", "blocked", "SQL_BLOCKING"));

            if (ContainsAny(normalizedQuestion, "wait", "waits", "dm_os_wait_stats"))
                return Gate(candidates, c => HasAny(c, "wait", "waits", "SQL_WAITS"));

            if (ContainsAny(normalizedQuestion, "deadlock", "deadlocks", "xml_deadlock_report", "system_health"))
                return Gate(candidates, c => HasAny(c, "deadlock", "xe", "SQL_XE_SYSTEM_HEALTH_DEADLOCKS_UNIFIED"));

            if (ContainsAny(normalizedQuestion, "errorlog", "xp_readerrorlog", "login failed"))
                return Gate(candidates, c => HasAny(c, "errorlog", "SQL_ERRORLOG"));

            if (ContainsAny(normalizedQuestion, "login", "logins", "server principal", "sys.server_principals"))
                return Gate(candidates, c => HasAny(c, "login", "logins", "principal", "SQL_LOGINS_LIST_UNIFIED"));
        }

        if (environment.Equals("Windows_Live", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(normalizedQuestion, "firewall", "rule", "allow", "block", "port"))
                return Gate(candidates, c => HasAny(c, "firewall", "rule", "port", "WIN_FIREWALL"));

            if (ContainsAny(normalizedQuestion, "disk", "drive", "free space"))
                return Gate(candidates, c => HasAny(c, "disk", "drive", "storage", "WIN_DISK"));

            if (ContainsAny(normalizedQuestion, "service", "services"))
                return Gate(candidates, c => HasAny(c, "service", "services", "WIN_SERVICE"));

            if (ContainsAny(normalizedQuestion, "eventlog", "error", "critical", "warning"))
                return Gate(candidates, c => HasAny(c, "event", "eventlog", "warning", "error", "WIN_EVENTLOG"));

            if (ContainsAny(normalizedQuestion, "reboot", "restart", "pending", "history"))
                return Gate(candidates, c => HasAny(c, "reboot", "restart", "pending", "history", "WIN_REBOOT"));
        }

        return candidates;
    }

    private static IEnumerable<QueryCodeCandidate> Gate(
        List<QueryCodeCandidate> candidates,
        Func<QueryCodeCandidate, bool> predicate)
    {
        var gated = candidates.Where(predicate).ToList();
        return gated.Count > 0 ? gated : candidates;
    }

    private static bool IsJobsCandidate(QueryCodeCandidate candidate)
    {
        var tags = candidate.ToolTags ?? string.Empty;
        var queryCode = candidate.QueryCode ?? string.Empty;
        var toolName = candidate.ToolName ?? string.Empty;

        return tags.Contains("Jobs", StringComparison.OrdinalIgnoreCase)
               || tags.Contains("Agent", StringComparison.OrdinalIgnoreCase)
               || queryCode.Contains("JOBS", StringComparison.OrdinalIgnoreCase)
               || toolName.Contains("Agent Jobs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAny(QueryCodeCandidate candidate, params string[] terms)
    {
        var payload =
            $"{candidate.QueryCode} {candidate.ToolName} {candidate.Description} {candidate.Keywords} {candidate.ToolTags}";
        return terms.Any(term => payload.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static TieBreakResult ResolveTieBreak(
        string environment,
        string normalizedQuestion,
        List<ScoredCandidate> scored)
    {
        if (environment.Equals("SqlServer_Live", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(normalizedQuestion, "job", "sql agent"))
                return TieBreakResult.Force("SQL_AGENT_JOBS_UNIFIED", "DB_RANK_TIE_BREAK_SQL_JOBS");

            if (ContainsAny(normalizedQuestion, "login", "server principal", "sys.server_principals"))
                return TieBreakResult.Force("SQL_LOGINS_LIST_UNIFIED", "DB_RANK_TIE_BREAK_SQL_LOGINS");

            if (ContainsAny(normalizedQuestion, "database"))
            {
                if (scored.Any(c => c.QueryCode.Equals("SQL_DATABASES_LIST_UNIFIED", StringComparison.OrdinalIgnoreCase)))
                    return TieBreakResult.Force("SQL_DATABASES_LIST_UNIFIED", "DB_RANK_TIE_BREAK_SQL_DATABASES");

                return TieBreakResult.Force("SQL_DATABASE_DETAILS_UNIFIED", "DB_RANK_TIE_BREAK_SQL_DATABASES_DETAILS");
            }

            if (ContainsAny(normalizedQuestion, "user"))
            {
                if (!scored.Any(c => c.QueryCode.Contains("SQL_USERS", StringComparison.OrdinalIgnoreCase)))
                    return TieBreakResult.Fail("DB_LOW_CONFIDENCE_AMBIGUOUS_USERS");
            }
        }

        if (environment.Equals("Windows_Live", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(normalizedQuestion, "rule", "allow", "block", "port"))
                return TieBreakResult.Force("WIN_FIREWALL_RULES_UNIFIED", "DB_RANK_TIE_BREAK_WIN_FW_RULES");
            if (ContainsAny(normalizedQuestion, "profile", "domain", "private", "public", "default inbound"))
                return TieBreakResult.Force("WIN_FIREWALL_UNIFIED", "DB_RANK_TIE_BREAK_WIN_FW_PROFILE");
            if (ContainsAny(normalizedQuestion, "warning"))
                return TieBreakResult.Force("WIN_EVENTLOG_WARNINGS_UNIFIED", "DB_RANK_TIE_BREAK_WIN_WARNINGS");
            if (ContainsAny(normalizedQuestion, "error", "critical"))
                return TieBreakResult.Force("WIN_EVENTLOG_ERRORS_UNIFIED", "DB_RANK_TIE_BREAK_WIN_ERRORS");
            if (ContainsAny(normalizedQuestion, "pending", "restart required"))
                return TieBreakResult.Force("WIN_REBOOT_PENDING_UNIFIED", "DB_RANK_TIE_BREAK_WIN_REBOOT_PENDING");
            if (ContainsAny(normalizedQuestion, "history", "last reboot", "unexpected shutdown"))
                return TieBreakResult.Force("WIN_REBOOT_HISTORY_UNIFIED", "DB_RANK_TIE_BREAK_WIN_REBOOT_HISTORY");
            if (ContainsAny(normalizedQuestion, "listen", "listening", "open port"))
                return TieBreakResult.Force("WIN_PORTS_LISTENING_UNIFIED", "DB_RANK_TIE_BREAK_WIN_PORTS");
            if (ContainsAny(normalizedQuestion, "established", "active connection", "remote endpoint"))
                return TieBreakResult.Force("WIN_TCP_CONNECTIONS_UNIFIED", "DB_RANK_TIE_BREAK_WIN_TCP");
        }

        return TieBreakResult.None();
    }

    private static bool ContainsAny(string source, params string[] terms)
    {
        return terms.Any(term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static QueryCodeCandidate ToCandidateModel(ScoredCandidate scored)
    {
        return new QueryCodeCandidate
        {
            ToolId = scored.ToolId,
            QueryCode = scored.QueryCode,
            ToolName = scored.ToolName,
            Environment = scored.Environment,
            Description = scored.Description,
            Keywords = scored.Keywords,
            ToolTags = scored.ToolTags,
            Priority = scored.Priority,
            IsReadOnly = scored.IsReadOnly,
            ScriptLanguage = scored.ScriptLanguage,
            ScriptTemplate = scored.ScriptTemplate,
            ParameterSchema = scored.ParameterSchema,
            OutputSchema = scored.OutputSchema,
            Score = scored.Score,
            KeywordHitCount = scored.KeywordHitCount,
            NameHitCount = scored.NameTokenOverlap,
            TagOverlapCount = scored.TagOverlap,
            DescriptionHitCount = scored.KeywordTokenOverlap,
            CompatibilityPenalty = 0
        };
    }

    private sealed class ScoredCandidate
    {
        public ScoredCandidate(
            QueryCodeCandidate source,
            double score,
            int keywordHitCount,
            int keywordTokenOverlap,
            int nameTokenOverlap,
            int tagOverlap,
            bool hasOverlap)
        {
            ToolId = source.ToolId;
            QueryCode = source.QueryCode;
            ToolName = source.ToolName;
            Environment = source.Environment;
            Description = source.Description;
            Keywords = source.Keywords;
            ToolTags = source.ToolTags;
            Priority = source.Priority;
            IsReadOnly = source.IsReadOnly;
            ScriptLanguage = source.ScriptLanguage;
            ScriptTemplate = source.ScriptTemplate;
            ParameterSchema = source.ParameterSchema;
            OutputSchema = source.OutputSchema;
            Score = score;
            KeywordHitCount = keywordHitCount;
            KeywordTokenOverlap = keywordTokenOverlap;
            NameTokenOverlap = nameTokenOverlap;
            TagOverlap = tagOverlap;
            HasOverlap = hasOverlap;
        }

        public int ToolId { get; }
        public string QueryCode { get; }
        public string ToolName { get; }
        public string Environment { get; }
        public string Description { get; }
        public string Keywords { get; }
        public string ToolTags { get; }
        public int Priority { get; }
        public bool IsReadOnly { get; }
        public string ScriptLanguage { get; }
        public string ScriptTemplate { get; }
        public string? ParameterSchema { get; }
        public string? OutputSchema { get; }
        public double Score { get; }
        public int KeywordHitCount { get; }
        public int KeywordTokenOverlap { get; }
        public int NameTokenOverlap { get; }
        public int TagOverlap { get; }
        public bool HasOverlap { get; }
    }

    private sealed class TieBreakResult
    {
        public string? ForcedQueryCode { get; private init; }
        public bool FailLowConfidence { get; private init; }
        public string? SelectionMethod { get; private init; }

        public static TieBreakResult None() => new();

        public static TieBreakResult Force(string queryCode, string method)
        {
            return new TieBreakResult
            {
                ForcedQueryCode = queryCode,
                SelectionMethod = method
            };
        }

        public static TieBreakResult Fail(string method)
        {
            return new TieBreakResult
            {
                FailLowConfidence = true,
                SelectionMethod = method
            };
        }
    }
}
