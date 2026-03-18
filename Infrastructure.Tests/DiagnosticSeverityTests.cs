using Application.Common.Models;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Regression tests ensuring the diagnostic engine respects collector failures,
/// pending reboot, event log errors, and service failures — never reporting
/// "No Issues Found" when raw evidence shows problems.
/// </summary>
public sealed class DiagnosticSeverityTests
{
    // ── PendingReboot must surface as a finding ───────────────────────────

    [Fact]
    public void PendingReboot_YES_produces_finding()
    {
        var result = BuildResultWithRows("CTS02", [
            DiagRow("Maintenance", "PendingReboot", "YES", "MEDIUM", "Pending reboot indicator"),
            DiagRow("CPU", "HostCpuPct", "15", "INFO", "Overall host CPU"),
        ]);

        var report = AskPipelineService.BuildDiagnosticReport(result);

        Assert.Contains(report.ServerDiagnostics[0].Findings,
            f => f.Title.Contains("Pending reboot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PendingReboot_NO_does_not_produce_finding()
    {
        var result = BuildResultWithRows("CTS02", [
            DiagRow("Maintenance", "PendingReboot", "NO", "INFO", "Pending reboot indicator"),
        ]);

        var report = AskPipelineService.BuildDiagnosticReport(result);

        Assert.DoesNotContain(report.ServerDiagnostics[0].Findings,
            f => f.Title.Contains("Pending reboot", StringComparison.OrdinalIgnoreCase));
    }

    // ── Event log error spike must surface ────────────────────────────────

    [Fact]
    public void EventLog_50plus_errors_produces_critical_finding()
    {
        var result = BuildResultWithRows("CTS03", [
            DiagRow("EventLog", "ErrorsLast1Hour", "75", "CRITICAL", "Critical+Error events"),
        ]);

        var report = AskPipelineService.BuildDiagnosticReport(result);

        var finding = report.ServerDiagnostics[0].Findings
            .FirstOrDefault(f => f.Title.Contains("error/critical events", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(finding);
        Assert.Equal("critical", finding.Severity);
    }

    // ── Stopped automatic service must surface ────────────────────────────

    [Fact]
    public void Stopped_automatic_service_produces_high_finding()
    {
        var result = BuildResultWithRows("CTS02", [
            DiagRow("Service", "W3SVC:Status", "Stopped", "HIGH", "StartType=Automatic"),
        ]);

        var report = AskPipelineService.BuildDiagnosticReport(result);

        Assert.Contains(report.ServerDiagnostics[0].Findings,
            f => f.Title.Contains("W3SVC", StringComparison.OrdinalIgnoreCase)
              && f.Title.Contains("Stopped", StringComparison.OrdinalIgnoreCase));
    }

    // ── SQL Service down must be critical ──────────────────────────────────

    [Fact]
    public void SQL_service_stopped_produces_finding()
    {
        var result = BuildResultWithRows("CTS02", [
            DiagRow("SQLService", "MSSQL$ADMIN:Status", "Stopped", "HIGH", "StartType=Automatic; DisplayName=SQL Server (ADMIN)"),
        ]);

        var report = AskPipelineService.BuildDiagnosticReport(result);

        var finding = report.ServerDiagnostics[0].Findings
            .FirstOrDefault(f => f.Title.Contains("SQL Service", StringComparison.OrdinalIgnoreCase)
                              || f.Title.Contains("MSSQL", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(finding);
        // Should be at least high severity
        Assert.True(finding.Severity is "critical" or "high",
            $"Expected critical or high severity, got '{finding.Severity}'");
    }

    // ── DataQuality flags execution failures ──────────────────────────────

    [Fact]
    public void DataQuality_detects_execution_failure()
    {
        var result = new AskResponseResult
        {
            Status = "FAILED",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS02",
                    Status = "FAILED",
                    RowCount = 0,
                    Rows = [new() { ["ErrorMessage"] = "Connection timeout" }]
                }
            ]
        };

        var quality = AskPipelineService.BuildDataQuality(result);

        Assert.True(quality.HasExecutionFailures);
        Assert.Equal("degraded", quality.Source);
        Assert.NotNull(quality.Warnings);
        Assert.Contains(quality.Warnings, w => w.Contains("CTS02"));
    }

    [Fact]
    public void DataQuality_real_when_all_succeed()
    {
        var result = new AskResponseResult
        {
            Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem { Target = "CTS03", Status = "SUCCESS", RowCount = 50 }
            ]
        };

        var quality = AskPipelineService.BuildDataQuality(result);

        Assert.Equal("real", quality.Source);
        Assert.False(quality.UsedMockLlm);
        Assert.False(quality.HasExecutionFailures);
    }

    [Fact]
    public void DataQuality_mock_flag_propagates()
    {
        var result = new AskResponseResult
        {
            Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03", Status = "SUCCESS", RowCount = 10 }]
        };

        var quality = AskPipelineService.BuildDataQuality(result, usedMockLlm: true);

        Assert.Equal("mock", quality.Source);
        Assert.True(quality.UsedMockLlm);
        Assert.Contains(quality.Warnings!, w => w.Contains("LLM unavailable"));
    }

    // ── Helper: build a result with diagnostic rows ───────────────────────

    private static AskResponseResult BuildResultWithRows(string server, List<Dictionary<string, object?>> rows)
    {
        return new AskResponseResult
        {
            Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = server,
                    Status = "SUCCESS",
                    RowCount = rows.Count,
                    Rows = rows
                }
            ]
        };
    }

    private static Dictionary<string, object?> DiagRow(
        string category, string metricName, string metricValue, string severity, string detail)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["ServerName"] = "TestServer",
            ["CapturedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Category"] = category,
            ["MetricName"] = metricName,
            ["MetricValue"] = metricValue,
            ["SeverityHint"] = severity,
            ["Detail"] = detail
        };
    }
}
