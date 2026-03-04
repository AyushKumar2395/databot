using Application.Common.Models;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class SqlToolRegistryResolverTests
{
    [Fact]
    public void ResolveFromCandidates_Maps_Fails_Jobs_To_Jobs_Tool_Not_Health()
    {
        var candidates = BuildSqlCandidates();

        var result = ToolRegistryResolver.ResolveFromCandidatesForTesting(
            "SqlServer_Live",
            "list all fails jobs",
            candidates);

        Assert.True(result.Found);
        Assert.Equal("SQL_AGENT_JOBS_UNIFIED", result.QueryCode);
    }

    [Fact]
    public void ResolveFromCandidates_Maps_Health_Report_To_Health_Tool()
    {
        var candidates = BuildSqlCandidates();

        var result = ToolRegistryResolver.ResolveFromCandidatesForTesting(
            "SqlServer_Live",
            "health report",
            candidates);

        Assert.True(result.Found);
        Assert.Equal("SQL_HEALTH_UNIFIED", result.QueryCode);
    }

    private static List<QueryCodeCandidate> BuildSqlCandidates()
    {
        return
        [
            new QueryCodeCandidate
            {
                ToolId = 1,
                QueryCode = "SQL_AGENT_JOBS_UNIFIED",
                ToolName = "SQL Agent Jobs - Unified",
                Environment = "SqlServer_Live",
                Description = "Running jobs, failed jobs, job history and schedules from msdb sysjobs",
                Keywords = "sql jobs;agent jobs;job history;failed jobs;running jobs;schedule;sysjobs;msdb",
                ToolTags = "Jobs;Agent;Monitoring",
                Priority = 10,
                IsReadOnly = true,
                ScriptLanguage = "SQL",
                ScriptTemplate = "SELECT name FROM msdb.dbo.sysjobs;",
                ParameterSchema = "{\"parameters\":[]}",
                OutputSchema = null
            },
            new QueryCodeCandidate
            {
                ToolId = 2,
                QueryCode = "SQL_HEALTH_UNIFIED",
                ToolName = "SQL Health - Unified",
                Environment = "SqlServer_Live",
                Description = "High-level SQL health summary",
                Keywords = "health report;instance health;diagnostics;failed jobs",
                ToolTags = "Health;Monitoring",
                Priority = 500,
                IsReadOnly = true,
                ScriptLanguage = "SQL",
                ScriptTemplate = "SELECT 'OK' AS Health;",
                ParameterSchema = "{\"parameters\":[]}",
                OutputSchema = null
            },
            new QueryCodeCandidate
            {
                ToolId = 3,
                QueryCode = "SQL_LOCKS_UNIFIED",
                ToolName = "SQL Locks - Unified",
                Environment = "SqlServer_Live",
                Description = "Current lock requests and lock waits",
                Keywords = "lock requests;locks;dm_tran_locks",
                ToolTags = "Locks;Blocking",
                Priority = 20,
                IsReadOnly = true,
                ScriptLanguage = "SQL",
                ScriptTemplate = "SELECT * FROM sys.dm_tran_locks;",
                ParameterSchema = "{\"parameters\":[]}",
                OutputSchema = null
            }
        ];
    }
}
