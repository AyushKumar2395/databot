using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

internal sealed class HeartbeatService(
    ISqlExecutor sqlExecutor,
    IPowerShellExecutor psExecutor,
    IUserServerRepository userServerRepository,
    IEnumerable<ILLMClient> llmClients,
    IModelSelector modelSelector,
    ILogger<HeartbeatService> logger) : IHeartbeatService
{
    private const int MaxConcurrent = 8;
    private const int TimeoutMs = 25_000;
    private const int MaxSnapshots = 10;

    private readonly Dictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(c => c.Provider, c => c, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cache of WinRM pre-flight results. true = reachable, false = failed (retry after cooldown).
    /// Persists across heartbeat pulses so pre-flight only runs once per server.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (bool Reachable, DateTime CheckedUtc)> WinRmCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long to wait before retrying a failed WinRM pre-flight (5 minutes).</summary>
    private static readonly TimeSpan WinRmRetryCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-server ring buffer: last 10 heartbeat snapshots. Static so it persists across scoped instances.
    /// Key = server name (case-insensitive).
    /// </summary>
    private static readonly ConcurrentDictionary<string, LinkedList<HeartbeatSnapshot>> SnapshotBuffer = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SnapshotLock = new();

    // ═══════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════

    public async Task<HeartbeatResponse> GetVitalsAsync(
        HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var response = new HeartbeatResponse
        {
            Environment = request.Environment,
            TimestampUtc = DateTime.UtcNow.ToString("o")
        };

        var isWindows = EnvironmentRules.IsWindows(request.Environment);

        // Phase 0: Ensure WinRM connectivity for unverified Windows targets.
        // Skip servers that passed, or that failed recently (cooldown prevents CMD spam).
        if (isWindows)
        {
            var now = DateTime.UtcNow;
            var needCheck = request.SelectedTargets
                .Where(t =>
                {
                    if (!WinRmCache.TryGetValue(t, out var entry)) return true;    // never checked
                    if (entry.Reachable) return false;                              // already verified
                    return now - entry.CheckedUtc > WinRmRetryCooldown;             // retry after cooldown
                })
                .ToList();
            if (needCheck.Count > 0)
                await EnsureWinRmAsync(needCheck, cancellationToken);
        }

        // Phase 0b: For SqlServer_Live, resolve port-aware connection tokens.
        // UI sends "CTS02#ADMIN" but we need "CTS02,1432#ADMIN" for non-default ports.
        var resolvedTargets = request.SelectedTargets;
        List<UserServerEntry>? sqlServerEntries = null;
        if (!isWindows && !string.IsNullOrWhiteSpace(request.BearerToken))
        {
            try
            {
                sqlServerEntries = await userServerRepository.GetSqlServersAsync(request.BearerToken, cancellationToken);

                // Log what UI sent vs what DB has for debugging
                logger.LogInformation(
                    "Phase 0b: UI sent targets=[{Targets}], DB tokens=[{DbTokens}]",
                    string.Join(", ", request.SelectedTargets),
                    string.Join(", ", sqlServerEntries.Select(s => s.Token)));

                // Build lookup by BOTH token and displayName so UI can send either format
                var lookup = new Dictionary<string, UserServerEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in sqlServerEntries)
                {
                    lookup.TryAdd(entry.Token, entry);        // "CTS02#ADMIN"
                    lookup.TryAdd(entry.DisplayName, entry);  // "CTS02\ADMIN,1432"
                }

                resolvedTargets = request.SelectedTargets
                    .Select(t => lookup.TryGetValue(t, out var entry) ? BuildConnectionToken(entry) : t)
                    .ToArray();

                logger.LogInformation("Phase 0b: Resolved targets=[{Resolved}]",
                    string.Join(", ", resolvedTargets));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve SQL ports — using raw tokens.");
            }
        }

        // Phase 1: Execute Windows or SQL Server vitals on each target in parallel.
        var semaphore = new SemaphoreSlim(MaxConcurrent);
        var tasks = resolvedTargets.Select(target =>
            ExecuteServerVitalsAsync(target, isWindows, semaphore, cancellationToken));
        var cards = await Task.WhenAll(tasks);

        // Map connection tokens back to display names + service status (reuse sqlServerEntries from Phase 0b)
        Dictionary<string, UserServerEntry>? sqlEntryMap = null;
        if (sqlServerEntries is { Count: > 0 })
        {
            sqlEntryMap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sqlServerEntries)
            {
                var connToken = BuildConnectionToken(entry);
                sqlEntryMap.TryAdd(connToken, entry);
                // Also map bare server name for default instances (e.g., "CTS02" → entry)
                sqlEntryMap.TryAdd(entry.ServerName, entry);
            }
        }

        foreach (var c in cards)
        {
            c.DatabotEnvironment = isWindows ? "Windows_Live" : "SqlServer_Live";
            if (!string.IsNullOrWhiteSpace(request.MonitoringEnvironment))
                c.MonitoringEnvironment = request.MonitoringEnvironment;
            // Enrich SQL Server tab cards with display name, environment, and service status
            if (sqlEntryMap != null && sqlEntryMap.TryGetValue(c.Server, out var entry))
            {
                c.Server = entry.DisplayName;
                if (!string.IsNullOrWhiteSpace(entry.MonitoringEnvironment))
                    c.MonitoringEnvironment = entry.MonitoringEnvironment;
                c.SqlServiceOnline = entry.SqlServiceOnline;
                c.SqlAgentOnline = entry.SqlAgentOnline;
            }
        }
        response.Servers = [.. cards];

        // Phase 1b: Fetch latest alert for each successful server (parallel, fire-and-forget style).
        await FetchAlertsAsync(response.Servers, isWindows, semaphore, cancellationToken);

        // Phase 2: For Windows_Live, discover SQL instances and probe them.
        if (isWindows && !string.IsNullOrWhiteSpace(request.BearerToken))
        {
            await DiscoverAndProbeSqlInstancesAsync(
                response.Servers, request.BearerToken, semaphore, cancellationToken);
        }

        // Phase 3: Build summary.
        response.Summary = BuildSummary(response.Servers);

        // Phase 4: Record snapshots into ring buffer for AI analysis.
        RecordSnapshots(response);

        return response;
    }

    /// <summary>
    /// Streams heartbeat phases incrementally so the UI renders fast.
    /// Phase 1 (vitals) is sent immediately (~2-3s), then alerts and SQL instances follow.
    /// </summary>
    public async IAsyncEnumerable<HeartbeatStreamUpdate> GetVitalsStreamAsync(
        HeartbeatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = new HeartbeatResponse
        {
            Environment = request.Environment,
            TimestampUtc = DateTime.UtcNow.ToString("o")
        };

        var isWindows = EnvironmentRules.IsWindows(request.Environment);

        // ── Phase 0: WinRM pre-flight ──────────────────────────────────────
        if (isWindows)
        {
            var now = DateTime.UtcNow;
            var needCheck = request.SelectedTargets
                .Where(t =>
                {
                    if (!WinRmCache.TryGetValue(t, out var entry)) return true;
                    if (entry.Reachable) return false;
                    return now - entry.CheckedUtc > WinRmRetryCooldown;
                })
                .ToList();
            if (needCheck.Count > 0)
                await EnsureWinRmAsync(needCheck, cancellationToken);
        }

        // ── Phase 0b: SQL port resolution ──────────────────────────────────
        var resolvedTargets = request.SelectedTargets;
        List<UserServerEntry>? sqlServerEntries = null;
        if (!isWindows && !string.IsNullOrWhiteSpace(request.BearerToken))
        {
            try
            {
                sqlServerEntries = await userServerRepository.GetSqlServersAsync(request.BearerToken, cancellationToken);
                var lookup = sqlServerEntries.ToDictionary(s => s.Token, s => s, StringComparer.OrdinalIgnoreCase);
                resolvedTargets = request.SelectedTargets
                    .Select(t => lookup.TryGetValue(t, out var entry) ? BuildConnectionToken(entry) : t)
                    .ToArray();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve SQL ports — using raw tokens.");
            }
        }

        // ── Phase 1: VITALS (fast — send immediately) ──────────────────────
        // Use a separate CTS so individual target timeouts don't kill the whole stream
        using var vitalsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        vitalsCts.CancelAfter(TimeoutMs + 5000); // slightly longer than per-target timeout

        var semaphore = new SemaphoreSlim(MaxConcurrent);
        var tasks = resolvedTargets.Select(target =>
            ExecuteServerVitalsAsync(target, isWindows, semaphore, vitalsCts.Token));
        var cards = await Task.WhenAll(tasks);

        // Enrich with display names and service status
        Dictionary<string, UserServerEntry>? sqlEntryMap = null;
        if (sqlServerEntries is { Count: > 0 })
        {
            sqlEntryMap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sqlServerEntries)
                sqlEntryMap.TryAdd(BuildConnectionToken(entry), entry);
        }

        foreach (var c in cards)
        {
            c.DatabotEnvironment = isWindows ? "Windows_Live" : "SqlServer_Live";
            if (!string.IsNullOrWhiteSpace(request.MonitoringEnvironment))
                c.MonitoringEnvironment = request.MonitoringEnvironment;
            if (sqlEntryMap != null && sqlEntryMap.TryGetValue(c.Server, out var entry))
            {
                c.Server = entry.DisplayName;
                if (!string.IsNullOrWhiteSpace(entry.MonitoringEnvironment))
                    c.MonitoringEnvironment = entry.MonitoringEnvironment;
                c.SqlServiceOnline = entry.SqlServiceOnline;
                c.SqlAgentOnline = entry.SqlAgentOnline;
            }
        }

        response.Servers = [.. cards];
        response.Summary = BuildSummary(response.Servers);

        // YIELD Phase 1 — UI can render server cards NOW
        yield return HeartbeatStreamUpdate.PartialUpdate(response, "vitals");

        // ── Phase 2: ALERTS (parallel, enriches cards) ─────────────────────
        var alertsOk = false;
        try
        {
            await FetchAlertsAsync(response.Servers, isWindows, semaphore, cancellationToken);
            alertsOk = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alert fetch failed during stream.");
        }
        if (alertsOk)
            yield return HeartbeatStreamUpdate.PartialUpdate(response, "alerts");

        // ── Phase 3: SQL INSTANCES (Windows_Live only) ─────────────────────
        var sqlOk = false;
        if (isWindows && !string.IsNullOrWhiteSpace(request.BearerToken))
        {
            try
            {
                await DiscoverAndProbeSqlInstancesAsync(
                    response.Servers, request.BearerToken, semaphore, cancellationToken);
                response.Summary = BuildSummary(response.Servers);
                sqlOk = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SQL instance discovery failed during stream.");
            }
            if (sqlOk)
                yield return HeartbeatStreamUpdate.PartialUpdate(response, "sql_instances");
        }

        // ── Final: full response with everything ───────────────────────────
        response.Summary = BuildSummary(response.Servers);
        RecordSnapshots(response);
        yield return HeartbeatStreamUpdate.Vitals(response);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SNAPSHOT RING BUFFER (last 10 per server)
    // ═══════════════════════════════════════════════════════════════════════

    public void RecordSnapshots(HeartbeatResponse response)
    {
        var timestamp = response.TimestampUtc;
        foreach (var card in response.Servers)
        {
            // Record the Windows/SQL server card itself
            RecordSnapshot(card.Server, timestamp, card);

            // Also record each nested SQL instance as its own snapshot
            // so AI Analysis works when clicking ANALYZE on a SQL instance card
            if (card.SqlInstances is { Count: > 0 })
            {
                foreach (var inst in card.SqlInstances)
                {
                    // Create a virtual HeartbeatServerCard from the SQL instance
                    var instCard = new HeartbeatServerCard
                    {
                        Server = inst.Instance,
                        DatabotEnvironment = inst.DatabotEnvironment,
                        MonitoringEnvironment = inst.MonitoringEnvironment,
                        Status = inst.Status,
                        Health = inst.Health,
                        HealthScore = inst.HealthScore,
                        PulseRate = inst.PulseRate,
                        GlowColor = inst.GlowColor,
                        Metrics = inst.Metrics,
                        Error = inst.Error,
                        AlertSummary = inst.AlertSummary,
                        SqlServiceOnline = inst.SqlServiceOnline,
                        SqlAgentOnline = inst.SqlAgentOnline,
                    };
                    RecordSnapshot(inst.Instance, timestamp, instCard);
                }
            }
        }
    }

    private void RecordSnapshot(string key, string timestamp, HeartbeatServerCard card)
    {
        // Always store under canonical key so lookups work regardless of input format
        var canonicalKey = ServerIdentity.Canonicalize(key);
        if (string.IsNullOrEmpty(canonicalKey)) return;

        var snapshot = new HeartbeatSnapshot
        {
            TimestampUtc = timestamp,
            Card = card
        };

        lock (SnapshotLock)
        {
            var list = SnapshotBuffer.GetOrAdd(canonicalKey, _ => new LinkedList<HeartbeatSnapshot>());
            list.AddLast(snapshot);
            while (list.Count > MaxSnapshots)
                list.RemoveFirst();
        }
    }

    public List<HeartbeatSnapshot> GetSnapshots(string server)
    {
        // Canonicalize the requested key so any format matches
        var canonicalKey = ServerIdentity.Canonicalize(server);
        if (string.IsNullOrEmpty(canonicalKey)) return [];

        lock (SnapshotLock)
        {
            if (SnapshotBuffer.TryGetValue(canonicalKey, out var list))
                return [.. list];
        }
        return [];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AI ANALYSIS (LLM-powered server health analysis from snapshots)
    // ═══════════════════════════════════════════════════════════════════════

    public async Task<HeartbeatAnalysisResponse> AnalyzeServerAsync(
        HeartbeatAnalysisRequest request, CancellationToken cancellationToken)
    {
        var snapshots = GetSnapshots(request.Server);
        var response = new HeartbeatAnalysisResponse
        {
            Server = request.Server,
            SnapshotCount = snapshots.Count,
            SnapshotTimestamps = snapshots.Select(s => s.TimestampUtc).ToList()
        };

        if (snapshots.Count == 0)
        {
            response.Verdict = "unknown";
            response.Summary = "No heartbeat data available. Start the heartbeat monitor first, then wait for a few cycles before requesting analysis.";
            return response;
        }

        // Build time range
        response.TimeRangeUtc = $"{snapshots[0].TimestampUtc} → {snapshots[^1].TimestampUtc}";

        // Build deterministic metric trends (always available, even if LLM fails)
        response.MetricTrends = BuildMetricTrends(snapshots);

        // Populate system context, SQL summaries, alert digest (always available)
        PopulateContextFields(snapshots, response);

        // Determine verdict from latest snapshot
        var latestCard = snapshots[^1].Card;
        response.Verdict = latestCard.Health;

        // Build the LLM prompt with all snapshot data
        var prompt = BuildAnalysisPrompt(request.Server, request.Environment, snapshots, response.MetricTrends);

        try
        {
            var explainModel = modelSelector.SelectExplainModel();
            var client = ResolveClient(explainModel.Provider);
            var raw = await client.GenerateAsync(
                prompt,
                $"Analyze heartbeat for {request.Server}",
                request.Environment,
                explainModel.ModelKey,
                cancellationToken,
                apiKey: explainModel.ApiKeyEncrypted);

            ParseAnalysisResponse(raw, response);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM analysis failed for {Server}, falling back to deterministic.", request.Server);
            BuildDeterministicAnalysis(snapshots, response);
        }

        return response;
    }

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;
        throw new InvalidOperationException($"No ILLMClient registered for provider '{provider}'.");
    }

    private static List<HeartbeatMetricTrend> BuildMetricTrends(List<HeartbeatSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return [];

        // Collect all metric keys from the latest snapshot
        var latestMetrics = snapshots[^1].Card.Metrics;
        var trends = new List<HeartbeatMetricTrend>();

        foreach (var metric in latestMetrics)
        {
            if (metric.Value is null) continue;

            // Gather this metric's values across all snapshots
            var values = snapshots
                .Select(s => s.Card.Metrics.FirstOrDefault(m => m.Key == metric.Key)?.Value)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (values.Count == 0) continue;

            var current = values[^1];
            var min = values.Min();
            var max = values.Max();
            var avg = values.Average();

            // Trend: compare first half average to second half average
            var trend = "stable";
            if (values.Count >= 3)
            {
                var mid = values.Count / 2;
                var firstHalf = values.Take(mid).Average();
                var secondHalf = values.Skip(mid).Average();
                var change = avg > 0 ? Math.Abs(secondHalf - firstHalf) / avg : 0;
                if (change > 0.15)
                    trend = secondHalf > firstHalf ? "rising" : "falling";
                if (max - min > avg * 0.5 && avg > 0)
                    trend = "volatile";
            }

            trends.Add(new HeartbeatMetricTrend
            {
                MetricKey = metric.Key,
                MetricLabel = metric.Label,
                Current = Math.Round(current, 2),
                Min = Math.Round(min, 2),
                Max = Math.Round(max, 2),
                Average = Math.Round(avg, 2),
                Trend = trend,
                Unit = metric.Unit,
                Health = metric.Health
            });
        }

        return trends;
    }

    private static string BuildAnalysisPrompt(
        string server, string environment, List<HeartbeatSnapshot> snapshots, List<HeartbeatMetricTrend> trends)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior DBA and Windows Server administrator. Analyze the following heartbeat monitoring data and provide a detailed health assessment.");
        sb.AppendLine();
        sb.AppendLine($"Server: {server}");
        sb.AppendLine($"Environment: {environment}");
        sb.AppendLine($"Snapshots: {snapshots.Count} captures from {snapshots[0].TimestampUtc} to {snapshots[^1].TimestampUtc}");
        sb.AppendLine();

        // System info (from latest snapshot if Windows)
        var sysInfo = snapshots[^1].Card.SystemInfo;
        if (sysInfo is not null)
        {
            sb.AppendLine("=== SYSTEM PROFILE ===");
            sb.AppendLine($"OS: {sysInfo.OsVersion} (Build {sysInfo.OsBuild})");
            sb.AppendLine($"CPU: {sysInfo.CpuName} | {sysInfo.Cores} cores, {sysInfo.LogicalProcessors} logical | {sysInfo.BaseSpeedGHz} GHz base");
            sb.AppendLine($"RAM: {sysInfo.TotalMemoryGB} GB | Virtualization: {sysInfo.Virtualization}");
            sb.AppendLine($"Uptime: {sysInfo.Uptime} (since {sysInfo.LastBootUtc})");
            sb.AppendLine($"Activity: {sysInfo.Processes} processes, {sysInfo.Threads} threads, {sysInfo.Handles} handles");
            sb.AppendLine();
        }

        // Metric trends
        sb.AppendLine("=== METRIC TRENDS (across all snapshots) ===");
        foreach (var t in trends)
        {
            sb.AppendLine($"  {t.MetricLabel} ({t.MetricKey}): current={t.Current}, min={t.Min}, max={t.Max}, avg={t.Average:F1}, trend={t.Trend}, health={t.Health}");
        }
        sb.AppendLine();

        // Per-snapshot timeline
        sb.AppendLine("=== SNAPSHOT TIMELINE ===");
        foreach (var snap in snapshots)
        {
            var c = snap.Card;
            var metricsStr = string.Join(", ", c.Metrics
                .Where(m => m.Value.HasValue)
                .Select(m => $"{m.Label}={m.DisplayValue}"));
            sb.AppendLine($"[{snap.TimestampUtc}] status={c.Status}, health={c.Health}, score={c.HealthScore}, metrics: {metricsStr}");
        }
        sb.AppendLine();

        // SQL instances (from latest)
        var sqlInstances = snapshots[^1].Card.SqlInstances;
        if (sqlInstances is { Count: > 0 })
        {
            sb.AppendLine("=== SQL INSTANCES ===");
            foreach (var inst in sqlInstances)
            {
                var instMetrics = string.Join(", ", inst.Metrics
                    .Where(m => m.Value.HasValue)
                    .Select(m => $"{m.Label}={m.DisplayValue}"));
                sb.AppendLine($"  {inst.Instance}: status={inst.Status}, health={inst.Health}, score={inst.HealthScore}");
                if (!string.IsNullOrWhiteSpace(inst.SqlVersion))
                    sb.AppendLine($"    Version: {inst.SqlVersion} ({inst.SqlEdition})");
                sb.AppendLine($"    Metrics: {instMetrics}");
            }
            sb.AppendLine();
        }

        // Alert summary (from latest)
        var alerts = snapshots[^1].Card.AlertSummary;
        if (alerts is not null)
        {
            sb.AppendLine("=== EVENT LOG SUMMARY (24h) ===");
            sb.AppendLine($"Critical: {alerts.CriticalCount}, Errors: {alerts.ErrorCount}, Warnings: {alerts.WarningCount}");
            if (alerts.LatestAlert is not null)
                sb.AppendLine($"Latest: [{alerts.LatestAlert.Severity}] {alerts.LatestAlert.Source} @ {alerts.LatestAlert.TimeUtc}: {alerts.LatestAlert.Message}");
            sb.AppendLine();
        }

        sb.AppendLine("=== INSTRUCTIONS ===");
        sb.AppendLine("Return a JSON object with this exact structure (no markdown, no code fences):");
        sb.AppendLine("""
{
  "verdict": "healthy|degraded|critical",
  "summary": "1-2 sentence executive summary of server health",
  "sections": [
    { "title": "section title", "content": "detailed markdown analysis", "severity": "healthy|warning|critical" }
  ],
  "recommendations": [
    { "text": "actionable recommendation", "priority": "critical|high|medium|low" }
  ]
}
""");
        sb.AppendLine("Sections should cover: CPU Analysis, Memory Analysis, Disk I/O, and any SQL instance health if present.");
        sb.AppendLine("Focus on trends, anomalies, and actionable insights. Be specific with numbers.");
        sb.AppendLine("If event log has critical/errors, include an Event Log Analysis section.");
        sb.AppendLine("'degraded' verdict = some metrics in warning but server is functional.");

        return sb.ToString();
    }

    private static void ParseAnalysisResponse(string raw, HeartbeatAnalysisResponse response)
    {
        // Strip markdown fences if present
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0) json = json[(firstNewline + 1)..];
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) json = json[..lastFence];
            json = json.Trim();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("verdict", out var verdict))
            response.Verdict = verdict.GetString() ?? response.Verdict;
        if (root.TryGetProperty("summary", out var summary))
            response.Summary = summary.GetString() ?? string.Empty;

        if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var sec in sections.EnumerateArray())
            {
                response.Sections.Add(new HeartbeatAnalysisSection
                {
                    Title = sec.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Content = sec.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    Severity = sec.TryGetProperty("severity", out var s) ? s.GetString() ?? "healthy" : "healthy"
                });
            }
        }

        if (root.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in recs.EnumerateArray())
            {
                response.Recommendations.Add(new HeartbeatRecommendation
                {
                    Text = rec.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    Priority = rec.TryGetProperty("priority", out var p) ? p.GetString() ?? "medium" : "medium"
                });
            }
        }
    }

    /// <summary>Populates systemContext, sqlInstanceSummaries, and alertDigest from the latest snapshot.</summary>
    private static void PopulateContextFields(List<HeartbeatSnapshot> snapshots, HeartbeatAnalysisResponse response)
    {
        var latest = snapshots[^1].Card;

        // System context
        var sys = latest.SystemInfo;
        if (sys is not null)
        {
            response.SystemContext = new HeartbeatAnalysisSystemContext
            {
                Os = $"{sys.OsVersion} (Build {sys.OsBuild})",
                Cpu = sys.CpuName,
                TotalRamGB = sys.TotalMemoryGB,
                Cores = sys.Cores,
                LogicalProcessors = sys.LogicalProcessors,
                Uptime = sys.Uptime,
                Virtualization = sys.Virtualization
            };
        }

        // SQL instance summaries
        if (latest.SqlInstances is { Count: > 0 })
        {
            response.SqlInstanceSummaries = latest.SqlInstances.Select(inst => new HeartbeatAnalysisSqlSummary
            {
                Instance = inst.Instance,
                Health = inst.Health,
                HealthScore = inst.HealthScore,
                Version = inst.SqlVersion,
                Edition = inst.SqlEdition,
                KeyMetrics = inst.Metrics
                    .Where(m => m.Value.HasValue)
                    .Select(m => $"{m.Label}: {m.DisplayValue}")
                    .ToList()
            }).ToList();
        }

        // Alert digest
        var alerts = latest.AlertSummary;
        if (alerts is not null)
        {
            var total = alerts.TotalCount;
            var noiseLevel = total == 0 ? "Clean" :
                             alerts.CriticalCount > 0 ? "Very noisy" :
                             total > 20 ? "Noisy" :
                             total > 5 ? "Moderate" : "Low noise";

            response.AlertDigest = new HeartbeatAnalysisAlertDigest
            {
                CriticalCount = alerts.CriticalCount,
                ErrorCount = alerts.ErrorCount,
                WarningCount = alerts.WarningCount,
                NoiseLevel = noiseLevel,
                LatestMessage = alerts.LatestAlert is not null
                    ? $"[{alerts.LatestAlert.Severity}] {alerts.LatestAlert.Source}: {alerts.LatestAlert.Message}"
                    : null
            };
        }
    }

    /// <summary>Fallback when LLM is unavailable — deterministic trend-based analysis with actionable insights.</summary>
    private static void BuildDeterministicAnalysis(List<HeartbeatSnapshot> snapshots, HeartbeatAnalysisResponse response)
    {
        var latest = snapshots[^1].Card;
        response.Verdict = latest.Health;

        // ── Build 3-line summary: Line 1 = verdict, Line 2 = critical/warning metrics, Line 3 = trends + noise ──
        var criticals = response.MetricTrends.Where(t => t.Health == "critical").ToList();
        var warnings = response.MetricTrends.Where(t => t.Health == "warning").ToList();
        var rising = response.MetricTrends.Where(t => t.Trend == "rising").ToList();
        var volatile_ = response.MetricTrends.Where(t => t.Trend == "volatile").ToList();
        var alerts = latest.AlertSummary;
        var sqlInstances = latest.SqlInstances;

        var line1 = latest.Health switch
        {
            "critical" => $"{response.Server} is in CRITICAL condition — immediate attention required.",
            "warning" => $"{response.Server} has DEGRADED performance — review warning metrics below.",
            "healthy" => $"{response.Server} is healthy — all {response.MetricTrends.Count} metrics within normal thresholds.",
            _ => $"{response.Server} status is unknown — check server connectivity."
        };

        // Line 2: specific metric callouts
        var line2Parts = new List<string>();
        foreach (var c in criticals)
            line2Parts.Add($"{c.MetricLabel} {c.Current}{FormatUnit(c.Unit)} (CRITICAL)");
        foreach (var w in warnings)
            line2Parts.Add($"{w.MetricLabel} {w.Current}{FormatUnit(w.Unit)} (WARNING)");
        if (sqlInstances is { Count: > 0 })
        {
            var sickSql = sqlInstances.Where(i => i.Health is "critical" or "warning").ToList();
            if (sickSql.Count > 0)
                line2Parts.Add($"SQL: {string.Join(", ", sickSql.Select(s => $"{s.Instance} ({s.Health})"))}");
        }
        var line2 = line2Parts.Count > 0
            ? string.Join(" · ", line2Parts)
            : "All metrics within healthy thresholds.";

        // Line 3: trends + event noise
        var line3Parts = new List<string>();
        if (rising.Count > 0)
            line3Parts.Add($"Rising: {string.Join(", ", rising.Select(r => r.MetricLabel))}");
        if (volatile_.Count > 0)
            line3Parts.Add($"Volatile: {string.Join(", ", volatile_.Select(v => v.MetricLabel))}");
        if (alerts is not null && alerts.TotalCount > 0)
            line3Parts.Add($"Events (24h): {alerts.CriticalCount} crit, {alerts.ErrorCount} err, {alerts.WarningCount} warn");
        else
            line3Parts.Add("Event log: Clean");
        var line3 = string.Join(" · ", line3Parts);

        response.Summary = $"{line1}\n{line2}\n{line3}";

        // ── System Profile section ──
        var sys = latest.SystemInfo;
        if (sys is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**OS**: {sys.OsVersion} (Build {sys.OsBuild})");
            sb.AppendLine($"**CPU**: {sys.CpuName} — {sys.Sockets} socket(s), {sys.Cores} cores, {sys.LogicalProcessors} logical processors");
            sb.AppendLine($"**Speed**: {sys.CurrentSpeedGHz} GHz current / {sys.BaseSpeedGHz} GHz base");
            sb.AppendLine($"**RAM**: {sys.TotalMemoryGB} GB total");
            sb.AppendLine($"**Virtualization**: {sys.Virtualization}");
            sb.AppendLine($"**Uptime**: {sys.Uptime} (since {sys.LastBootUtc})");
            sb.AppendLine($"**Activity**: {sys.Processes} processes, {sys.Threads} threads, {sys.Handles} handles");
            if (sys.L2CacheKB > 0 || sys.L3CacheKB > 0)
                sb.AppendLine($"**Cache**: L2 {sys.L2CacheKB} KB, L3 {sys.L3CacheKB} KB");
            response.Sections.Add(new HeartbeatAnalysisSection
            {
                Title = "Server Profile",
                Content = sb.ToString().TrimEnd(),
                Severity = "healthy"
            });
        }

        // ── CPU Analysis ──
        var cpuMetrics = response.MetricTrends.Where(t =>
            t.MetricKey is "PercentProcessorTime" or "SqlServerCPU" or "ProcessorQueueLength").ToList();
        if (cpuMetrics.Count > 0)
        {
            var sb = new StringBuilder();
            var cpu = cpuMetrics.FirstOrDefault(m => m.MetricKey is "PercentProcessorTime" or "SqlServerCPU");
            var queue = cpuMetrics.FirstOrDefault(m => m.MetricKey == "ProcessorQueueLength");

            if (cpu is not null)
            {
                sb.AppendLine($"**{cpu.MetricLabel}**: {cpu.Current}% (min {cpu.Min}%, max {cpu.Max}%, avg {cpu.Average:F1}%) — trend: {cpu.Trend}");
                if (cpu.Current >= 90)
                    sb.AppendLine("CPU is saturated. Processes are competing for processor time. This directly impacts query execution, application responsiveness, and user experience.");
                else if (cpu.Current >= 70)
                    sb.AppendLine("CPU utilization is elevated. Under additional load, this server may become a bottleneck.");
                else
                    sb.AppendLine("CPU utilization is within normal operating range.");
            }

            if (queue is not null)
            {
                sb.AppendLine($"**{queue.MetricLabel}**: {queue.Current} (min {queue.Min}, max {queue.Max}, avg {queue.Average:F1}) — trend: {queue.Trend}");
                if (cpu is not null && cpu.Current >= 80 && queue.Current >= 2)
                    sb.AppendLine("High CPU + queue depth > 2 indicates **CPU saturation**. Threads are waiting for processor time. Consider reducing workload or adding CPU capacity.");
                else if (queue.Average >= 4)
                    sb.AppendLine("Sustained high queue length suggests the CPU cannot keep up with the workload.");
            }

            response.Sections.Add(new HeartbeatAnalysisSection
            {
                Title = "CPU Analysis",
                Content = sb.ToString().TrimEnd(),
                Severity = cpuMetrics.Any(m => m.Health == "critical") ? "critical" :
                           cpuMetrics.Any(m => m.Health == "warning") ? "warning" : "healthy"
            });
        }

        // ── Memory Analysis ──
        var memMetrics = response.MetricTrends.Where(t =>
            t.MetricKey is "MemoryUsage" or "AvailableMemory" or "PageLifeExpectancy_seconds" or "MemoryGrantsPending" or "AvailableMemory_GB").ToList();
        if (memMetrics.Count > 0)
        {
            var sb = new StringBuilder();
            var memPct = memMetrics.FirstOrDefault(m => m.MetricKey == "MemoryUsage");
            var availMem = memMetrics.FirstOrDefault(m => m.MetricKey == "AvailableMemory");
            var ple = memMetrics.FirstOrDefault(m => m.MetricKey == "PageLifeExpectancy_seconds");
            var grants = memMetrics.FirstOrDefault(m => m.MetricKey == "MemoryGrantsPending");

            if (memPct is not null)
            {
                sb.AppendLine($"**Memory Usage**: {memPct.Current}% (min {memPct.Min}%, max {memPct.Max}%, avg {memPct.Average:F1}%) — trend: {memPct.Trend}");
                if (memPct.Current >= 90)
                    sb.AppendLine("Memory is critically high. The OS may be paging to disk, which severely impacts performance. Check for memory-hungry processes.");
                else if (memPct.Current >= 80)
                    sb.AppendLine("Memory usage is elevated. Monitor for further increase.");
            }

            if (availMem is not null)
                sb.AppendLine($"**Available RAM**: {availMem.Current:F0} MB (min {availMem.Min:F0}, max {availMem.Max:F0}, avg {availMem.Average:F0}) — trend: {availMem.Trend}");

            if (ple is not null)
            {
                sb.AppendLine($"**Page Life Expectancy**: {ple.Current:F0}s (min {ple.Min:F0}s, max {ple.Max:F0}s, avg {ple.Average:F0}s) — trend: {ple.Trend}");
                if (ple.Current < 300)
                    sb.AppendLine("PLE below 300s is critical — SQL Server is constantly recycling buffer pool pages. Queries will hit disk instead of cache. Consider adding RAM or reducing memory-intensive queries.");
                else if (ple.Current < 1000)
                    sb.AppendLine("PLE is below optimal. Buffer pool is under pressure. Watch for increased disk I/O from cache misses.");
                else
                    sb.AppendLine("PLE is healthy — data pages are staying in the buffer cache effectively.");
            }

            if (grants is not null && grants.Current > 0)
                sb.AppendLine($"**Memory Grants Pending**: {grants.Current:F0} — queries are waiting for memory to execute. This causes query queuing and timeouts. Investigate large sort/hash operations.");

            response.Sections.Add(new HeartbeatAnalysisSection
            {
                Title = "Memory Analysis",
                Content = sb.ToString().TrimEnd(),
                Severity = memMetrics.Any(m => m.Health == "critical") ? "critical" :
                           memMetrics.Any(m => m.Health == "warning") ? "warning" : "healthy"
            });
        }

        // ── Disk I/O Analysis ──
        var diskMetrics = response.MetricTrends.Where(t =>
            t.MetricKey is "PercentageDiskTimeTotal" or "AvgDiskQueueLengthTotal").ToList();
        if (diskMetrics.Count > 0)
        {
            var sb = new StringBuilder();
            var diskPct = diskMetrics.FirstOrDefault(m => m.MetricKey == "PercentageDiskTimeTotal");
            var diskQueue = diskMetrics.FirstOrDefault(m => m.MetricKey == "AvgDiskQueueLengthTotal");

            if (diskPct is not null)
            {
                sb.AppendLine($"**Disk Time**: {diskPct.Current}% (min {diskPct.Min}%, max {diskPct.Max}%, avg {diskPct.Average:F1}%) — trend: {diskPct.Trend}");
                if (diskPct.Current >= 80)
                    sb.AppendLine("Disk subsystem is heavily saturated. Check for large backup operations, index rebuilds, or table scans hitting disk. Consider moving to faster storage (SSD/NVMe).");
                else if (diskPct.Trend == "volatile")
                    sb.AppendLine("Disk activity is bursty. This may indicate periodic jobs (backups, ETL) or memory pressure forcing data to disk.");
            }

            if (diskQueue is not null)
            {
                sb.AppendLine($"**Disk Queue**: {diskQueue.Current} (min {diskQueue.Min}, max {diskQueue.Max}, avg {diskQueue.Average:F2}) — trend: {diskQueue.Trend}");
                if (diskQueue.Average >= 2)
                    sb.AppendLine("Sustained disk queue > 2 means I/O requests are backing up. This slows everything: queries, log writes, checkpoints.");
            }

            response.Sections.Add(new HeartbeatAnalysisSection
            {
                Title = "Disk I/O Analysis",
                Content = sb.ToString().TrimEnd(),
                Severity = diskMetrics.Any(m => m.Health == "critical") ? "critical" :
                           diskMetrics.Any(m => m.Health == "warning") ? "warning" : "healthy"
            });
        }

        // ── SQL Instance Analysis ──
        if (latest.SqlInstances is { Count: > 0 })
        {
            foreach (var inst in latest.SqlInstances)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"**Instance**: {inst.Instance} — Health: **{inst.Health.ToUpperInvariant()}** (Score: {inst.HealthScore ?? 0}/100)");
                if (!string.IsNullOrWhiteSpace(inst.SqlVersion))
                    sb.AppendLine($"**Version**: {inst.SqlVersion} ({inst.SqlEdition})");

                foreach (var m in inst.Metrics.Where(m => m.Value.HasValue))
                    sb.AppendLine($"  {m.Label}: {m.DisplayValue} — {m.Health}");

                var blocking = inst.Metrics.FirstOrDefault(m => m.Key == "BlockingCount");
                if (blocking?.Value > 0)
                    sb.AppendLine($"**{blocking.Value:F0} blocking chain(s) detected** — sessions are waiting on locks. Run `sp_who2` or query `sys.dm_exec_requests` to identify the head blocker and the waiting queries.");

                var batchReq = inst.Metrics.FirstOrDefault(m => m.Key == "BatchRequests_sec");
                var userConn = inst.Metrics.FirstOrDefault(m => m.Key == "UserConnections");
                if (batchReq?.Value > 0 && userConn?.Value > 0)
                    sb.AppendLine($"Workload: {batchReq.DisplayValue} batch requests/sec across {userConn.DisplayValue} connections.");

                var instAlerts = inst.AlertSummary;
                if (instAlerts is not null && instAlerts.TotalCount > 0)
                    sb.AppendLine($"SQL Error Log (24h): {instAlerts.ErrorCount} errors, {instAlerts.WarningCount} warnings. {(instAlerts.LatestAlert is not null ? $"Latest: {instAlerts.LatestAlert.Message}" : "")}");

                response.Sections.Add(new HeartbeatAnalysisSection
                {
                    Title = $"SQL Instance — {inst.Instance}",
                    Content = sb.ToString().TrimEnd(),
                    Severity = inst.Health == "critical" ? "critical" :
                               inst.Health == "warning" ? "warning" : "healthy"
                });
            }
        }

        // ── Event Log Analysis ──
        if (alerts is not null && alerts.TotalCount > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**24-hour event summary**: {alerts.CriticalCount} critical, {alerts.ErrorCount} errors, {alerts.WarningCount} warnings ({alerts.TotalCount} total)");
            if (alerts.CriticalCount > 0)
                sb.AppendLine("Critical events detected in the Windows Event Log. These typically indicate hardware failures, service crashes, or security breaches that need immediate attention.");
            else if (alerts.ErrorCount > 10)
                sb.AppendLine("High error count suggests a recurring issue. Check the Application and System logs for repeating patterns — often a misconfigured service or failing driver.");

            if (alerts.LatestAlert is not null)
                sb.AppendLine($"\n**Latest event**: [{alerts.LatestAlert.Severity}] {alerts.LatestAlert.Source} @ {alerts.LatestAlert.TimeUtc}\n{alerts.LatestAlert.Message}");

            response.Sections.Add(new HeartbeatAnalysisSection
            {
                Title = "Event Log Analysis",
                Content = sb.ToString().TrimEnd(),
                Severity = alerts.CriticalCount > 0 ? "critical" :
                           alerts.ErrorCount > 5 ? "warning" : "healthy"
            });
        }

        // ── Correlation Insights ──
        var correlations = BuildCorrelationInsights(response.MetricTrends, latest);
        if (correlations.Count > 0)
        {
            response.Sections.Add(new HeartbeatAnalysisSection
            {
                Title = "Correlation Insights",
                Content = string.Join("\n", correlations),
                Severity = "warning"
            });
        }

        // ── Actionable Recommendations ──
        BuildDeterministicRecommendations(response, latest, snapshots);
    }

    private static List<string> BuildCorrelationInsights(List<HeartbeatMetricTrend> trends, HeartbeatServerCard latest)
    {
        var insights = new List<string>();
        var cpu = trends.FirstOrDefault(t => t.MetricKey is "PercentProcessorTime" or "SqlServerCPU");
        var mem = trends.FirstOrDefault(t => t.MetricKey == "MemoryUsage");
        var disk = trends.FirstOrDefault(t => t.MetricKey == "PercentageDiskTimeTotal");
        var ple = trends.FirstOrDefault(t => t.MetricKey == "PageLifeExpectancy_seconds");
        var queue = trends.FirstOrDefault(t => t.MetricKey == "ProcessorQueueLength");
        var grants = trends.FirstOrDefault(t => t.MetricKey == "MemoryGrantsPending");

        // CPU saturation pattern
        if (cpu is { Current: >= 80 } && queue is { Average: >= 2 })
            insights.Add("**CPU Saturation**: High CPU ({0}%) combined with processor queue depth ({1}) means threads are actively waiting for CPU time. This slows query execution, increases latencies, and can cascade into connection pool exhaustion."
                .Replace("{0}", $"{cpu.Current}").Replace("{1}", $"{queue.Average:F1}"));

        // Memory pressure → disk thrashing
        if (mem is { Current: >= 85 } && disk is { Current: >= 30 })
            insights.Add($"**Memory-Disk Correlation**: High memory usage ({mem.Current}%) with active disk I/O ({disk.Current}%) suggests the OS or SQL Server is paging to disk. This creates a vicious cycle — disk I/O slows everything, causing more queuing.");

        // Low PLE + high memory = buffer pool pressure
        if (ple is { Current: < 600 } && mem is { Current: >= 70 })
            insights.Add($"**Buffer Pool Pressure**: PLE at {ple.Current:F0}s with {mem.Current}% memory usage indicates SQL Server's data cache is being constantly flushed. Queries repeatedly read from disk instead of memory, compounding I/O load.");

        // Memory grants pending = query queuing
        if (grants is { Current: > 0 } && cpu is { Current: >= 50 })
            insights.Add($"**Query Memory Starvation**: {grants.Current:F0} pending memory grants while CPU is at {cpu.Current}%. Large queries (sorts, hash joins) are queuing for workspace memory. Consider adding `OPTION (MAX_GRANT_PERCENT = ...)` hints or increasing max server memory.");

        // Everything healthy
        if (insights.Count == 0 && cpu is { Current: < 50 } && mem is { Current: < 70 })
            insights.Add("No concerning metric correlations detected. Server resources are well-balanced across CPU, memory, and disk subsystems.");

        return insights;
    }

    private static void BuildDeterministicRecommendations(HeartbeatAnalysisResponse response, HeartbeatServerCard latest, List<HeartbeatSnapshot> snapshots)
    {
        foreach (var t in response.MetricTrends)
        {
            if (t.Health == "critical")
            {
                var action = t.MetricKey switch
                {
                    "PercentProcessorTime" or "SqlServerCPU" =>
                        $"CPU at {t.Current}%. Open Task Manager → sort by CPU. For SQL: run `SELECT TOP 10 * FROM sys.dm_exec_requests ORDER BY cpu_time DESC` to find expensive queries. Consider killing long-running queries or adding CPU resources.",
                    "MemoryUsage" =>
                        $"Memory at {t.Current}%. Check for memory leaks: Task Manager → sort by memory. For SQL: check `DBCC MEMORYSTATUS` and consider lowering `max server memory` if SQL is consuming too much.",
                    "PageLifeExpectancy_seconds" =>
                        $"PLE at {t.Current:F0}s (critical below 300). SQL buffer pool is thrashing. Run `SELECT * FROM sys.dm_os_buffer_descriptors` to check buffer distribution. Consider adding RAM or optimizing heavy queries with missing indexes.",
                    "PercentageDiskTimeTotal" =>
                        $"Disk at {t.Current}%. Check for active backups, index rebuilds, or DBCC operations. Run `SELECT * FROM sys.dm_io_virtual_file_stats(NULL, NULL)` to find which databases have the highest I/O.",
                    "AvgDiskQueueLengthTotal" =>
                        $"Disk queue at {t.Current}. I/O requests are backing up. Check `sys.dm_io_virtual_file_stats` and consider moving TempDB or log files to faster storage.",
                    "MemoryGrantsPending" =>
                        $"{t.Current:F0} queries waiting for memory grants. Run `SELECT * FROM sys.dm_exec_query_memory_grants WHERE grant_time IS NULL` to identify waiting queries. Consider resource governor or query hints.",
                    "ProcessorQueueLength" =>
                        $"Processor queue at {t.Current}. More threads are ready to execute than CPUs available. Reduce parallelism with `MAXDOP` or investigate runaway queries.",
                    _ => $"{t.MetricLabel} is critical at {t.Current}{t.Unit ?? ""}. Investigate immediately."
                };
                response.Recommendations.Add(new HeartbeatRecommendation { Text = action, Priority = "critical" });
            }
            else if (t.Health == "warning")
            {
                var action = t.MetricKey switch
                {
                    "PercentProcessorTime" or "SqlServerCPU" =>
                        $"CPU at {t.Current}% (warning). Monitor for further increase. If this persists, review query plans for missing indexes: `SELECT * FROM sys.dm_db_missing_index_details`.",
                    "PageLifeExpectancy_seconds" =>
                        $"PLE at {t.Current:F0}s (warning below 1000). Buffer pool is under moderate pressure. Check for large scan queries that flush the cache.",
                    _ => $"{t.MetricLabel} at {t.Current}{t.Unit ?? ""} is in warning range. Monitor for further degradation."
                };
                response.Recommendations.Add(new HeartbeatRecommendation
                {
                    Text = action,
                    Priority = t.Trend == "rising" ? "high" : "medium"
                });
            }
        }

        // Trend-based recommendations
        foreach (var t in response.MetricTrends.Where(t => t.Trend == "rising" && t.Health == "healthy"))
        {
            if (t.MetricKey is "PercentProcessorTime" or "MemoryUsage" or "PercentageDiskTimeTotal")
            {
                response.Recommendations.Add(new HeartbeatRecommendation
                {
                    Text = $"{t.MetricLabel} is rising (avg {t.Average:F1} → current {t.Current}). Still healthy but trending upward. Set an alert threshold to catch early.",
                    Priority = "low"
                });
            }
        }

        // SQL-specific recommendations
        if (latest.SqlInstances is { Count: > 0 })
        {
            foreach (var inst in latest.SqlInstances.Where(i => i.Health is "critical" or "warning"))
            {
                var blocking = inst.Metrics.FirstOrDefault(m => m.Key == "BlockingCount");
                if (blocking?.Value > 0)
                {
                    response.Recommendations.Add(new HeartbeatRecommendation
                    {
                        Text = $"[{inst.Instance}] {blocking.Value:F0} blocking chain(s). Run: `SELECT blocking_session_id, session_id, wait_type, wait_time FROM sys.dm_exec_requests WHERE blocking_session_id > 0` to identify blockers.",
                        Priority = "critical"
                    });
                }
            }
        }

        // Event log recommendations
        var alerts = latest.AlertSummary;
        if (alerts is { CriticalCount: > 0 })
        {
            response.Recommendations.Add(new HeartbeatRecommendation
            {
                Text = $"{alerts.CriticalCount} critical events in Windows Event Log (24h). Open Event Viewer → filter by Critical level. These often indicate hardware failures, driver crashes, or service unavailability.",
                Priority = "critical"
            });
        }
        else if (alerts is { ErrorCount: > 10 })
        {
            response.Recommendations.Add(new HeartbeatRecommendation
            {
                Text = $"{alerts.ErrorCount} errors in Event Log (24h). High error volume often indicates a recurring issue. Filter by source to identify the repeat offender.",
                Priority = "high"
            });
        }

        // Uptime recommendation
        var sys = latest.SystemInfo;
        if (sys is not null)
        {
            // Parse uptime days from "D:HH:MM:SS" format
            var parts = sys.Uptime.Split(':');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var days) && days > 90)
            {
                response.Recommendations.Add(new HeartbeatRecommendation
                {
                    Text = $"Server uptime is {days} days. Consider scheduling a maintenance window for Windows Updates and a clean reboot. Prolonged uptime can hide memory leaks and deferred patches.",
                    Priority = "low"
                });
            }
        }

        // If everything is healthy, add a positive note
        if (response.Recommendations.Count == 0)
        {
            response.Recommendations.Add(new HeartbeatRecommendation
            {
                Text = "All metrics are within healthy thresholds with stable trends. No action required. Continue monitoring.",
                Priority = "low"
            });
        }
    }

    /// <summary>Formats a unit string for display: null → "", "percent" → "%", others → " MB" etc.</summary>
    private static string FormatUnit(string? unit) => unit switch
    {
        null or "" => "",
        "percent" or "%" => "%",
        _ => $" {unit}"
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  PHASE 0: WINRM PRE-FLIGHT (test → trusted hosts → enable → retry)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task EnsureWinRmAsync(List<string> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            try
            {
                var now = DateTime.UtcNow;

                // Step 1: Test if WinRM is already reachable.
                if (await TestWinRmAsync(target, ct))
                {
                    WinRmCache[target] = (true, now);
                    logger.LogInformation("WinRM pre-flight: {Target} is reachable.", target);
                    continue;
                }

                logger.LogWarning("WinRM pre-flight: {Target} not reachable. Attempting remediation...", target);

                // Step 2: Add to TrustedHosts on the API server.
                await RunLocalPowerShellAsync(
                    HeartbeatScripts.AddTrustedHost.Replace("{{$server}}", target, StringComparison.Ordinal),
                    ct);
                logger.LogInformation("WinRM pre-flight: Added {Target} to TrustedHosts.", target);

                // Step 3: Retest after TrustedHosts — it may be all that was needed.
                if (await TestWinRmAsync(target, ct))
                {
                    WinRmCache[target] = (true, now);
                    logger.LogInformation("WinRM pre-flight: {Target} reachable after TrustedHosts update.", target);
                    continue;
                }

                // Step 4: Attempt to enable WinRM remotely via WMI.
                logger.LogWarning("WinRM pre-flight: Attempting remote WinRM enablement on {Target} via WMI...", target);
                await RunLocalPowerShellAsync(
                    HeartbeatScripts.EnableWinRMRemotely.Replace("{{$server}}", target, StringComparison.Ordinal),
                    ct);

                // Step 5: Final retest.
                if (await TestWinRmAsync(target, ct))
                {
                    WinRmCache[target] = (true, now);
                    logger.LogInformation("WinRM pre-flight: {Target} reachable after remote enablement.", target);
                }
                else
                {
                    // Cache as failed — won't retry for 5 minutes.
                    WinRmCache[target] = (false, now);
                    logger.LogError(
                        "WinRM pre-flight: {Target} still unreachable after all remediation steps. " +
                        "Will retry in {CooldownMin} minutes. Manual fix: RDP to {Target} and run Enable-PSRemoting -Force, " +
                        "or check firewall ports 5985/5986.",
                        target, WinRmRetryCooldown.TotalMinutes, target);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WinRM pre-flight failed for {Target}.", target);
            }
        }
    }

    private async Task<bool> TestWinRmAsync(string target, CancellationToken ct)
    {
        try
        {
            var script = HeartbeatScripts.TestWinRM.Replace("{{$server}}", target, StringComparison.Ordinal);
            var (exitCode, stdout, _) = await RunLocalPowerShellRawAsync(script, ct);
            if (exitCode != 0) return false;

            // Parse: { "Reachable": true/false }
            var text = stdout.Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("Reachable", out var prop)
                   && prop.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a PowerShell script locally (not via Invoke-Command) for pre-flight tasks.
    /// </summary>
    private async Task RunLocalPowerShellAsync(string script, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunLocalPowerShellRawAsync(script, ct);
        if (exitCode != 0)
        {
            logger.LogWarning(
                "Local PowerShell pre-flight returned exit code {ExitCode}. Stderr={Stderr} Stdout={Stdout}",
                exitCode, stderr.Length > 500 ? stderr[..500] : stderr, stdout.Length > 500 ? stdout[..500] : stdout);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunLocalPowerShellRawAsync(
        string script, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"databot_preflight_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tempFile, script, Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PHASE 1: PER-SERVER VITAL SIGNS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<HeartbeatServerCard> ExecuteServerVitalsAsync(
        string target, bool isWindows, SemaphoreSlim semaphore, CancellationToken ct)
    {
        var card = new HeartbeatServerCard { Server = target };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await semaphore.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);

            ExecutorRunResult result;
            if (isWindows)
                result = await psExecutor.ExecuteAsync(target, HeartbeatScripts.WindowsVitals, cts.Token);
            else
                result = await sqlExecutor.ExecuteAsync(target, HeartbeatScripts.SqlServerVitals, cts.Token);

            sw.Stop();
            card.DurationMs = sw.ElapsedMilliseconds;

            if (!result.Success)
            {
                card.Status = "FAILED";
                card.Health = "unknown";
                card.Error = result.Error;
                ApplyVisuals(card);
                return card;
            }

            card.Status = "SUCCESS";
            card.Metrics = isWindows
                ? ParseWindowsMetrics(result.Rows)
                : ParseSqlServerMetrics(result.Rows);
            if (isWindows)
                card.SystemInfo = ParseSystemInfo(result.Rows);
            ComputeHealth(card);
            ApplyVisuals(card);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            card.DurationMs = sw.ElapsedMilliseconds;
            card.Status = "TIMEOUT";
            card.Health = "unknown";
            card.Error = $"Heartbeat timed out after {TimeoutMs / 1000} seconds.";
            ApplyVisuals(card);
        }
        catch (Exception ex)
        {
            sw.Stop();
            card.DurationMs = sw.ElapsedMilliseconds;
            card.Status = "FAILED";
            card.Health = "unknown";
            card.Error = ex.Message;
            ApplyVisuals(card);
            logger.LogWarning(ex, "Heartbeat failed for {Target}", target);
        }
        finally
        {
            semaphore.Release();
        }

        return card;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PHASE 2: SQL INSTANCE DISCOVERY + PROBE
    // ═══════════════════════════════════════════════════════════════════════

    private async Task DiscoverAndProbeSqlInstancesAsync(
        List<HeartbeatServerCard> cards,
        string bearerToken,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        // Fetch all SQL servers for the user via the existing Get_UserSQLServer SP.
        // This returns proper tokens with port info (e.g., "CTS02#Admin").
        List<UserServerEntry> allSqlServers;
        try
        {
            allSqlServers = await userServerRepository.GetSqlServersAsync(bearerToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch SQL servers via repository — skipping SQL instance discovery.");
            return;
        }

        if (allSqlServers.Count == 0) return;

        foreach (var card in cards)
        {
            if (card.Status != "SUCCESS") continue;

            // Extract the plain hostname (strip any port/instance from Windows target).
            var winServer = card.Server.Split('#', ',')[0].Trim();

            // Filter SQL instances that belong to this Windows server.
            var instances = allSqlServers
                .Where(s => s.ServerName.Equals(winServer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (instances.Count == 0) continue;

            // Propagate monitoring environment from the DB to the Windows card
            var monEnv = instances.FirstOrDefault()?.MonitoringEnvironment;
            if (!string.IsNullOrWhiteSpace(monEnv))
                card.MonitoringEnvironment = monEnv;

            try
            {
                // Build connection token with port for each instance.
                // SqlExecutor.ParseSqlInstanceToken converts "CTS02#Admin" → "CTS02\Admin".
                // For non-default ports, prepend "server,port" so connection string becomes
                // "Server=CTS02,1432\Admin" which bypasses SQL Browser.
                // Build lookup for service status by display name
                var svcStatusByName = instances.ToDictionary(
                    i => i.DisplayName, i => i, StringComparer.OrdinalIgnoreCase);

                var probeTasks = instances.Select(inst =>
                {
                    var connectionToken = BuildConnectionToken(inst);
                    return ProbeSqlInstanceAsync(connectionToken, inst.DisplayName, semaphore, ct);
                });
                var probed = await Task.WhenAll(probeTasks);
                foreach (var inst in probed)
                {
                    inst.ParentServer = winServer;
                    inst.DatabotEnvironment = "SqlServer_Live";
                    inst.MonitoringEnvironment = monEnv;
                    // Propagate service status from the DB
                    if (svcStatusByName.TryGetValue(inst.Instance, out var svcEntry))
                    {
                        inst.SqlServiceOnline = svcEntry.SqlServiceOnline;
                        inst.SqlAgentOnline = svcEntry.SqlAgentOnline;
                    }
                }
                card.SqlInstances = [.. probed];
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SQL instance probe failed for {WinServer}", winServer);
            }
        }
    }

    /// <summary>
    /// Builds a connection-ready token. For non-default ports, embeds "server,port"
    /// so SqlExecutor resolves to "Server=CTS02,1432\Admin" instead of relying on SQL Browser.
    /// </summary>
    private static string BuildConnectionToken(UserServerEntry entry)
    {
        var server = entry.Port > 0 && entry.Port != 1433
            ? $"{entry.ServerName},{entry.Port}"
            : entry.ServerName;

        // MSSQLSERVER is the default instance — connect with just the server name,
        // don't append \MSSQLSERVER to the connection string.
        var isDefault = string.IsNullOrWhiteSpace(entry.InstanceName)
                     || entry.InstanceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase);

        return !isDefault
            ? $"{server}#{entry.InstanceName}"
            : server;
    }

    private async Task<HeartbeatSqlInstance> ProbeSqlInstanceAsync(
        string connectionToken, string displayName, SemaphoreSlim semaphore, CancellationToken ct)
    {
        var inst = new HeartbeatSqlInstance { Instance = displayName };

        await semaphore.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);

            var result = await sqlExecutor.ExecuteAsync(connectionToken, HeartbeatScripts.SqlServerVitals, cts.Token);

            if (!result.Success)
            {
                inst.Status = "FAILED";
                inst.Health = "unknown";
                inst.Error = result.Error;
                ApplyInstanceVisuals(inst);
                return inst;
            }

            inst.Status = "SUCCESS";
            inst.Metrics = ParseSqlServerMetrics(result.Rows);
            ParseSqlVersion(result.Rows, inst);
            ComputeInstanceHealth(inst);
            ApplyInstanceVisuals(inst);

            // Fetch SQL error log alert summary (best-effort, don't fail the probe).
            try
            {
                using var alertCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                alertCts.CancelAfter(8000);
                var alertResult = await sqlExecutor.ExecuteAsync(connectionToken, HeartbeatScripts.SqlAlertSummary, alertCts.Token);
                if (alertResult.Success)
                    inst.AlertSummary = ParseAlertSummary(alertResult.Rows);
            }
            catch { /* best-effort */ }
        }
        catch (OperationCanceledException)
        {
            inst.Status = "TIMEOUT";
            inst.Health = "unknown";
            inst.Error = "SQL instance probe timed out.";
            ApplyInstanceVisuals(inst);
        }
        catch (Exception ex)
        {
            inst.Status = "FAILED";
            inst.Health = "unknown";
            inst.Error = ex.Message;
            ApplyInstanceVisuals(inst);
            logger.LogWarning(ex, "SQL instance probe failed for {Instance}", displayName);
        }
        finally
        {
            semaphore.Release();
        }

        return inst;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PHASE 1b: ALERT SUMMARIES (Event Log / SQL Error Log)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task FetchAlertsAsync(
        List<HeartbeatServerCard> cards, bool isWindows, SemaphoreSlim semaphore, CancellationToken ct)
    {
        var alertTasks = cards
            .Where(c => c.Status == "SUCCESS")
            .Select(async card =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(8000); // 8s budget — counts take slightly longer than single-event fetch

                    if (isWindows)
                    {
                        var result = await psExecutor.ExecuteAsync(
                            card.Server, HeartbeatScripts.WindowsAlertSummary, cts.Token);
                        if (result.Success)
                            card.AlertSummary = ParseAlertSummary(result.Rows);
                    }
                    else
                    {
                        var result = await sqlExecutor.ExecuteAsync(
                            card.Server, HeartbeatScripts.SqlAlertSummary, cts.Token);
                        if (result.Success)
                            card.AlertSummary = ParseAlertSummary(result.Rows);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Alert summary fetch failed for {Server} (best-effort).", card.Server);
                }
                finally
                {
                    semaphore.Release();
                }
            });

        await Task.WhenAll(alertTasks);
    }

    private static HeartbeatAlertSummary? ParseAlertSummary(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return null;
        var row = rows[0];

        var summary = new HeartbeatAlertSummary
        {
            CriticalCount = ParseInt(row, "CriticalCount"),
            ErrorCount = ParseInt(row, "ErrorCount"),
            WarningCount = ParseInt(row, "WarningCount")
        };

        // Parse the latest alert detail (if any event exists)
        var message = row.GetValueOrDefault("Message")?.ToString();
        if (!string.IsNullOrWhiteSpace(message))
        {
            summary.LatestAlert = new HeartbeatAlert
            {
                Source = row.GetValueOrDefault("Source")?.ToString() ?? "Unknown",
                Severity = row.GetValueOrDefault("Severity")?.ToString() ?? "Error",
                TimeUtc = row.GetValueOrDefault("TimeUtc")?.ToString() ?? string.Empty,
                Message = message.Length > 500 ? message[..500] + "..." : message
            };
        }

        return summary;
    }

    private static int ParseInt(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var val) && val is not null &&
            int.TryParse(val.ToString(), out var parsed))
            return parsed;
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  METRIC PARSING
    // ═══════════════════════════════════════════════════════════════════════

    private static List<HeartbeatMetric> ParseWindowsMetrics(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return [];
        var row = rows[0];

        // Check for error row
        if (row.TryGetValue("ErrorMessage", out var err) && err is not null)
            return [];

        return
        [
            BuildMetric("PercentProcessorTime", "CPU", row, "percent"),
            BuildMetric("MemoryUsage", "Memory %", row, "percent"),
            BuildMetric("AvailableMemory", "Available RAM", row, "mb"),
            BuildMetric("PercentageDiskTimeTotal", "Disk Time", row, "percent"),
            BuildMetric("AvgDiskQueueLengthTotal", "Disk Queue", row, null),
            BuildMetric("ProcessorQueueLength", "CPU Queue", row, null),
        ];
    }

    private static HeartbeatSystemInfo? ParseSystemInfo(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return null;
        var row = rows[0];

        // If this is an error row, no system info available
        if (row.ContainsKey("ErrorMessage")) return null;

        return new HeartbeatSystemInfo
        {
            CpuName = row.GetValueOrDefault("CpuName")?.ToString() ?? string.Empty,
            CurrentSpeedGHz = ParseDouble(row, "CurrentSpeedGHz"),
            BaseSpeedGHz = ParseDouble(row, "BaseSpeedGHz"),
            Sockets = ParseInt(row, "Sockets"),
            Cores = ParseInt(row, "Cores"),
            LogicalProcessors = ParseInt(row, "LogicalProcessors"),
            Virtualization = row.GetValueOrDefault("Virtualization")?.ToString() ?? "Unknown",
            L1CacheKB = ParseInt(row, "L1CacheKB"),
            L2CacheKB = ParseInt(row, "L2CacheKB"),
            L3CacheKB = ParseInt(row, "L3CacheKB"),
            Processes = ParseInt(row, "Processes"),
            Threads = ParseInt(row, "Threads"),
            Handles = ParseInt(row, "Handles"),
            Uptime = row.GetValueOrDefault("Uptime")?.ToString() ?? string.Empty,
            LastBootUtc = row.GetValueOrDefault("LastBootUtc")?.ToString() ?? string.Empty,
            TotalMemoryGB = ParseDouble(row, "TotalMemoryGB"),
            OsVersion = row.GetValueOrDefault("OsVersion")?.ToString() ?? string.Empty,
            OsBuild = row.GetValueOrDefault("OsBuild")?.ToString() ?? string.Empty
        };
    }

    private static double ParseDouble(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var val) && val is not null &&
            double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }

    /// <summary>
    /// Extracts SQL Server version info from vitals row and sets it on the instance.
    /// Builds a friendly version like "SQL Server 2019 RTM-CU18 (15.0.4261.1)".
    /// </summary>
    private static void ParseSqlVersion(List<Dictionary<string, object?>> rows, HeartbeatSqlInstance inst)
    {
        if (rows.Count == 0) return;
        var row = rows[0];

        var fullVersion = row.GetValueOrDefault("SqlVersionFull")?.ToString();
        var edition = row.GetValueOrDefault("SqlEdition")?.ToString();
        var productVersion = row.GetValueOrDefault("SqlProductVersion")?.ToString();
        var productLevel = row.GetValueOrDefault("SqlProductLevel")?.ToString();

        // Build friendly version: "SQL Server 2019 RTM-CU18 (15.0.4261.1)"
        if (!string.IsNullOrWhiteSpace(fullVersion))
        {
            // @@VERSION first line: "Microsoft SQL Server 2019 (RTM-CU18) (KB5007377) - 15.0.4261.1 ..."
            // Extract just "SQL Server 2019" and the CU info
            var friendly = fullVersion;
            var dashIdx = fullVersion.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0) friendly = fullVersion[..dashIdx].Trim();
            // Remove "Microsoft " prefix
            if (friendly.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
                friendly = friendly[10..];
            // Trim at first newline
            var nlIdx = friendly.IndexOfAny(['\r', '\n']);
            if (nlIdx > 0) friendly = friendly[..nlIdx];

            inst.SqlVersion = !string.IsNullOrWhiteSpace(productVersion)
                ? $"{friendly} ({productVersion})"
                : friendly;
        }
        else if (!string.IsNullOrWhiteSpace(productVersion))
        {
            inst.SqlVersion = !string.IsNullOrWhiteSpace(productLevel)
                ? $"SQL Server {productLevel} ({productVersion})"
                : productVersion;
        }

        if (!string.IsNullOrWhiteSpace(edition))
            inst.SqlEdition = edition;
    }

    private static List<HeartbeatMetric> ParseSqlServerMetrics(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return [];
        var row = rows[0];

        return
        [
            BuildMetric("SqlServerCPU", "SQL CPU", row, "percent"),
            BuildMetric("PageLifeExpectancy_seconds", "PLE", row, "seconds"),
            BuildMetric("BufferCacheHitRatio", "Cache Hit", row, "percent"),
            BuildMetric("AvailableMemory_GB", "Free RAM", row, "gb"),
            BuildMetric("MemoryGrantsPending", "Grants Pending", row, "count"),
            BuildMetric("BlockingCount", "Blocking", row, "count"),
            BuildMetric("SignalWaitPct", "Signal Wait", row, "percent"),
            BuildMetric("MaxDataFileReadLatency_ms", "Disk Latency", row, "ms"),
            BuildMetric("LongRunningQueries", "Long Queries", row, "count"),
            BuildMetric("BatchRequests_sec", "Batch Req/s", row, "count"),
            BuildMetric("UserConnections", "Connections", row, "count"),
        ];
    }

    private static HeartbeatMetric BuildMetric(
        string key, string label, Dictionary<string, object?> row, string? unit)
    {
        var metric = new HeartbeatMetric { Key = key, Label = label, Unit = unit };

        if (row.TryGetValue(key, out var raw) && raw is not null &&
            double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
        {
            metric.Value = val;
            metric.DisplayValue = FormatValue(val, unit);
            metric.Health = ClassifyHealth(key, val);
            metric.Thresholds = GetThresholds(key);
        }
        else
        {
            metric.DisplayValue = "N/A";
            metric.Health = "unknown";
        }

        return metric;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HEALTH SCORING (uses MetricThresholdRegistry from AskPipelineService)
    // ═══════════════════════════════════════════════════════════════════════

    internal static string ClassifyHealth(string metricKey, double value)
    {
        if (!AskPipelineService.MetricThresholdRegistry.TryGetValue(metricKey, out var th))
            return "healthy"; // No threshold → assume healthy

        if (th.CriticalHigh > 0 && value >= th.CriticalHigh) return "critical";
        if (th.CriticalLow > 0 && value <= th.CriticalLow) return "critical";
        if (th.WarningHigh > 0 && value >= th.WarningHigh) return "warning";
        if (th.WarningLow > 0 && value <= th.WarningLow) return "warning";
        return "healthy";
    }

    private static HeartbeatThresholds? GetThresholds(string metricKey)
    {
        if (!AskPipelineService.MetricThresholdRegistry.TryGetValue(metricKey, out var th))
            return null;
        return new HeartbeatThresholds
        {
            WarningLow = th.WarningLow,
            CriticalLow = th.CriticalLow,
            WarningHigh = th.WarningHigh,
            CriticalHigh = th.CriticalHigh
        };
    }

    // Weighted health score: CPU/Memory are 3x, Disk 1.5x, others 1x.
    private static readonly Dictionary<string, double> MetricWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PercentProcessorTime"] = 3.0,
        ["SqlServerCPU"] = 3.0,
        ["MemoryUsage"] = 2.5,
        ["AvailableMemory"] = 2.0,
        ["AvailableMemory_GB"] = 2.0,
        ["PageLifeExpectancy_seconds"] = 2.5,
        ["MemoryGrantsPending"] = 2.0,
        ["BufferCacheHitRatio"] = 1.5,
        ["PercentageDiskTimeTotal"] = 1.5,
        ["AvgDiskQueueLengthTotal"] = 1.5,
        ["BlockingCount"] = 1.5,
        ["SignalWaitPct"] = 2.0,
        ["MaxDataFileReadLatency_ms"] = 1.5,
        ["LongRunningQueries"] = 1.5,
    };

    private static void ComputeHealth(HeartbeatServerCard card)
    {
        if (card.Metrics.Count == 0)
        {
            card.Health = "unknown";
            card.HealthScore = null;
            return;
        }

        double totalWeight = 0, weightedSum = 0;
        var hasCritical = false;
        var hasWarning = false;

        foreach (var m in card.Metrics)
        {
            if (m.Value is null) continue;
            var weight = MetricWeights.GetValueOrDefault(m.Key, 1.0);
            var score = m.Health switch
            {
                "critical" => 0.0,
                "warning" => 50.0,
                _ => 100.0
            };

            if (m.Health == "critical") hasCritical = true;
            if (m.Health == "warning") hasWarning = true;

            weightedSum += score * weight;
            totalWeight += weight;
        }

        card.HealthScore = totalWeight > 0 ? (int)Math.Round(weightedSum / totalWeight) : null;
        card.Health = hasCritical ? "critical" : hasWarning ? "warning" : "healthy";
    }

    private static void ComputeInstanceHealth(HeartbeatSqlInstance inst)
    {
        if (inst.Metrics.Count == 0)
        {
            inst.Health = "unknown";
            inst.HealthScore = null;
            return;
        }

        double totalWeight = 0, weightedSum = 0;
        var hasCritical = false;
        var hasWarning = false;

        foreach (var m in inst.Metrics)
        {
            if (m.Value is null) continue;
            var weight = MetricWeights.GetValueOrDefault(m.Key, 1.0);
            var score = m.Health switch
            {
                "critical" => 0.0,
                "warning" => 50.0,
                _ => 100.0
            };

            if (m.Health == "critical") hasCritical = true;
            if (m.Health == "warning") hasWarning = true;

            weightedSum += score * weight;
            totalWeight += weight;
        }

        inst.HealthScore = totalWeight > 0 ? (int)Math.Round(weightedSum / totalWeight) : null;
        inst.Health = hasCritical ? "critical" : hasWarning ? "warning" : "healthy";
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  VISUALS — pulse rate, glow color
    // ═══════════════════════════════════════════════════════════════════════

    private static void ApplyVisuals(HeartbeatServerCard card)
    {
        var (pulse, glow) = GetVisuals(card.Health, card.HealthScore);
        card.PulseRate = pulse;
        card.GlowColor = glow;
    }

    private static void ApplyInstanceVisuals(HeartbeatSqlInstance inst)
    {
        var (pulse, glow) = GetVisuals(inst.Health, inst.HealthScore);
        inst.PulseRate = pulse;
        inst.GlowColor = glow;
    }

    private static (int PulseRate, string GlowColor) GetVisuals(string health, int? score)
    {
        // Map health score to BPM: 100→60bpm (calm), 50→120bpm, 0→180bpm
        var bpm = score switch
        {
            >= 80 => 60,
            >= 60 => 90,
            >= 40 => 120,
            >= 20 => 150,
            > 0 => 180,
            _ => health == "unknown" ? 0 : 60 // 0 = flatline for unknown/failed
        };

        var color = health switch
        {
            "healthy" => "#22c55e",  // green-500
            "warning" => "#f59e0b",  // amber-500
            "critical" => "#ef4444", // red-500
            _ => "#6b7280"           // gray-500 (flatline)
        };

        return (bpm, color);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FORMATTING / SUMMARY
    // ═══════════════════════════════════════════════════════════════════════

    private static string FormatValue(double val, string? unit) => unit switch
    {
        "percent" => $"{val:F1}%",
        "gb" => $"{val:F2} GB",
        "mb" => $"{val:F0} MB",
        "seconds" => val >= 1000 ? $"{val / 1000:F1}K s" : $"{val:F0} s",
        "count" => val >= 1_000_000 ? $"{val / 1_000_000:F1}M" : val >= 1000 ? $"{val / 1000:F1}K" : $"{val:F0}",
        _ => $"{val:F2}"
    };

    private static HeartbeatSummary BuildSummary(List<HeartbeatServerCard> servers)
    {
        var summary = new HeartbeatSummary { TotalServers = servers.Count };
        foreach (var s in servers)
        {
            switch (s.Health)
            {
                case "healthy": summary.HealthyCount++; break;
                case "warning": summary.WarningCount++; break;
                case "critical": summary.CriticalCount++; break;
                default: summary.FailedCount++; break;
            }
        }

        summary.OverallHealth = summary.CriticalCount > 0 ? "critical"
            : summary.WarningCount > 0 ? "warning"
            : summary.FailedCount > 0 ? "unknown"
            : "healthy";

        return summary;
    }
}
