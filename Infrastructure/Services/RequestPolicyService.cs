using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic request-level safety gate that runs BEFORE any LLM call.
/// Order: 1) Sexual content  2) EnvironmentIntentGate  3) DangerousActionGate
/// </summary>
internal sealed class RequestPolicyService(
    ILogger<RequestPolicyService> logger) : IRequestPolicyService
{
    private readonly ILogger<RequestPolicyService> _logger = logger;

    // ── SQL dangerous action verbs (state-changing) ─────────────────────────
    private static readonly Regex SqlDangerousActionPattern = new(
        @"\b(backup\s+database|restore\s+database|backup\s+log|restore\s+log|drop\s+(database|table|index|view|procedure|proc|login|user|schema|role|function|trigger)|truncate\s+table|alter\s+(database|table|login|user|schema|role)|create\s+(database|login|user|schema|role)|insert\s+into|update\s+\S+\s+set\b|delete\s+from|merge\s+into|grant\s+\S+\s+to|revoke\s+\S+\s+from|deny\s+\S+\s+to|kill\s+\d+|reconfigure|shrink\s*database|dbcc\s+shrink|xp_cmdshell|sp_OACreate|sp_(add|update|delete)_(job|jobstep|jobschedule))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Windows dangerous action verbs (state-changing) ─────────────────────
    private static readonly Regex WindowsDangerousActionPattern = new(
        @"\b(restart\s+(server|computer|machine|host|service)|reboot\s+(server|computer|machine|host)|shutdown\s+(server|computer|machine|host)|stop\s+(service|process|computer)|start\s+service|kill\s+process|remove-item|set-itemproperty|new-itemproperty|restart-computer|stop-computer|start-service|stop-service|restart-service|stop-process|taskkill|format-volume|clear-eventlog|disable-netadapter|new-netfirewallrule|set-netfirewallrule|remove-netfirewallrule|install-\w+|uninstall-\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Informational override patterns ─────────────────────────────────────
    private static readonly Regex InformationalOverridePattern = new(
        @"\b(when\s+(did|was|were|is)|show\s+(me\s+)?(last|recent|latest|history|log|status)|list\s+(all\s+)?(recent|last|failed|running|stopped)|check\s+(if|whether|error|status|log|history)|did\s+(the\s+)?(backup|restore|update|restart|job)|what\s+(is|are|was|were)\s+(the\s+)?(last|latest|recent|current|status)|how\s+(long|many|often|much)|history\s+of|log\s+(for|of|entries)|status\s+of|is\s+\S+\s+(running|stopped|online|offline|up|down|started|restarted|healthy)|was\s+\S+\s+(restarted|backed\s*up|updated|patched|restored)|get\s+(the\s+)?(last|latest|recent|current|status))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Explicit action-command patterns ─────────────────────────────────────
    private static readonly Regex ExplicitActionCommandPattern = new(
        @"^(please\s+)?(backup|restore|drop|truncate|delete|remove|restart|reboot|shutdown|stop|kill|start|create|alter|grant|revoke|disable|enable|install|uninstall|format|clear)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Sexual content keywords ─────────────────────────────────────────────
    private static readonly Regex SexualContentPattern = new(
        @"\b(porn|pornograph|hentai|xxx|nsfw|nude|naked|sex\s*chat|erotic|fetish|orgasm|masturbat|genital|sexual\s+(intercourse|content|act|fantasy)|strip\s*tease)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PolicyDecision Evaluate(AskApiRequest request, string tunedQuestionOrRaw)
    {
        var question = string.IsNullOrWhiteSpace(tunedQuestionOrRaw)
            ? request.Question
            : tunedQuestionOrRaw;
        var raw = request.Question;
        var env = request.Environment ?? string.Empty;

        // ── Step 1: Sexual content check (General environment only) ─────────
        if (EnvironmentRules.IsGeneral(env) && IsSexualContent(raw))
        {
            _logger.LogInformation("Policy BLOCKED: SEXUAL_CONTENT for question '{Question}'", raw);
            return PolicyDecision.BlockSexualContent(
                "This type of content is not supported. Please ask a technical or operational question.");
        }

        // ── Step 2: Environment Intent Gate (Windows vs SQL) ────────────────
        // History environments run centralized T-SQL on CTS03 — skip Live-mode intent checking.
        if (EnvironmentRules.IsLive(env))
        {
            var intentResult = EnvironmentIntentService.Evaluate(raw, env);

            if (intentResult.IsMismatch)
            {
                _logger.LogInformation(
                    "Policy BLOCKED: ENV_MISMATCH for question '{Question}' in {Environment}",
                    raw, env);
                return PolicyDecision.BlockEnvMismatch(
                    intentResult.Message!,
                    intentResult.SuggestedEnvironment!,
                    intentResult.Alternatives);
            }

            if (intentResult.IsClarificationNeeded)
            {
                _logger.LogInformation(
                    "Policy NEEDS_CLARIFICATION for question '{Question}' in {Environment}",
                    raw, env);
                return PolicyDecision.Clarify(
                    intentResult.Message!,
                    intentResult.Suggestion1!,
                    intentResult.Suggestion2!);
            }
        }

        // ── Step 3: Dangerous action intent check (with informational override) ──
        if (EnvironmentRules.IsSqlServer(env) || EnvironmentRules.IsWindows(env))
        {
            var dangerDecision = CheckDangerousAction(raw, question, env);
            if (dangerDecision is not null)
            {
                _logger.LogInformation(
                    "Policy BLOCKED: DANGEROUS_ACTION for question '{Question}' in {Environment}",
                    raw, env);
                return dangerDecision;
            }
        }

        return PolicyDecision.Allow();
    }

    // ── Dangerous action detection ──────────────────────────────────────────

    private static PolicyDecision? CheckDangerousAction(string raw, string tuned, string env)
    {
        // History environments always generate T-SQL, so check SQL patterns even for Windows_History.
        var isSql = EnvironmentRules.IsSqlServer(env) || EnvironmentRules.IsHistory(env);

        var hasDangerInRaw = isSql
            ? SqlDangerousActionPattern.IsMatch(raw)
            : WindowsDangerousActionPattern.IsMatch(raw);
        var hasDangerInTuned = isSql
            ? SqlDangerousActionPattern.IsMatch(tuned)
            : WindowsDangerousActionPattern.IsMatch(tuned);

        if (!hasDangerInRaw && !hasDangerInTuned)
            return null;

        if (IsInformationalQuestion(raw) && !IsExplicitActionCommand(raw))
            return null;

        if (IsInformationalQuestion(tuned) && !IsExplicitActionCommand(tuned))
            return null;

        var alternatives = isSql
            ? new[]
            {
                "When did the last backup complete?",
                "Show recent failed SQL Agent jobs",
                "List databases larger than 10GB"
            }
            : new[]
            {
                "When was the server last restarted?",
                "Show stopped services",
                "Check disk space on all drives"
            };

        return PolicyDecision.BlockDangerousAction(
            "Only read-only diagnostics and inventory queries are allowed. Your request appears to involve a state-changing operation.",
            alternatives);
    }

    private static bool IsInformationalQuestion(string text) =>
        !string.IsNullOrWhiteSpace(text) && InformationalOverridePattern.IsMatch(text);

    private static bool IsExplicitActionCommand(string text) =>
        !string.IsNullOrWhiteSpace(text) && ExplicitActionCommandPattern.IsMatch(text.TrimStart());

    private static bool IsSexualContent(string text) =>
        SexualContentPattern.IsMatch(text);
}
