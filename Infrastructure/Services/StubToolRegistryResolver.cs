using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Current dev stub for ToolRegistry resolution.
/// Always returns Found=false so the pipeline falls back to LLM generation.
/// </summary>
public sealed class StubToolRegistryResolver(ILogger<StubToolRegistryResolver> logger) : IToolRegistryResolver
{
    private readonly ILogger<StubToolRegistryResolver> _logger = logger;

    public Task<ToolResolutionResult> ResolveBestToolAsync(
        string environment,
        string tunedQuestion,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ToolRegistry lookup skipped (stub). Environment={Environment}, Question={Question}",
            environment,
            tunedQuestion);

        // TODO(DB ToolRegistry): connect to SQLGig on CTS03 and read [SQLGig].[DataBOT].[ToolRegistry].
        // TODO(DB ToolRegistry): filter by environment and IsActive=1.
        // TODO(Scoring): score = EnvMatchWeight + KeywordHitCount*10 + TagOverlap*3 + Priority.
        // TODO(Tie-break): if top2 within 5%, apply one-winner rules:
        // - rule/allow/block/port -> WIN_FIREWALL_RULES_UNIFIED
        // - profile/domain/private/public/default inbound -> WIN_FIREWALL_UNIFIED
        // - warning -> WIN_EVENTLOG_WARNINGS_UNIFIED
        // - error/critical -> WIN_EVENTLOG_ERRORS_UNIFIED
        // - pending/restart required -> WIN_REBOOT_PENDING_UNIFIED
        // - history/last reboot/unexpected shutdown -> WIN_REBOOT_HISTORY_UNIFIED
        // - listen/listening/open port -> WIN_PORTS_LISTENING_UNIFIED
        // - established/active connection/remote endpoint -> WIN_TCP_CONNECTIONS_UNIFIED

        return Task.FromResult(new ToolResolutionResult
        {
            Found = false,
            QueryCode = null,
            ScriptLanguage = null,
            ScriptTemplate = null,
            Score = null
        });
    }
}
