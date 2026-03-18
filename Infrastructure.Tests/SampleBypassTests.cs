using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Regression tests: sample questions from the Samples tab must NEVER be blocked
/// by ENV_MISMATCH, intent classification, or dangerous-action heuristics.
/// They bypass the policy gate because the sample catalog is the source of truth.
/// </summary>
public sealed class SampleBypassTests
{
    // ── Windows_Live sample is NOT blocked ─────────────────────────────────

    [Fact]
    public async Task Windows_Live_sample_does_not_block()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "Windows_Live",
            Question = "Show recent WARNING events in last 12 hours (System+Application).",
            SampleId = 800,
            GroupKey = "WindowsHealth",
            SelectedTargets = ["CTS02"]
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        // Key assertion: NOT blocked by ENV_MISMATCH policy
        Assert.DoesNotContain("ENV_MISMATCH", response.Tuning.StopReason ?? "");
        Assert.DoesNotContain("BLOCKED", response.Tuning.StopReason ?? "");
        // Plan should show SAMPLE_ONLY route
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
    }

    // ── SqlServer_Live sample is NOT blocked ──────────────────────────────

    [Fact]
    public async Task SqlServer_Live_sample_does_not_block()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "SqlServer_Live",
            Question = "Why is this SQL Server struggling right now?",
            SampleId = 797,
            GroupKey = "SqlMonitor",
            SelectedTargets = ["CTS03#CTSGlobal"]
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        // Key assertion: NOT blocked by ENV_MISMATCH policy
        Assert.DoesNotContain("ENV_MISMATCH", response.Tuning.StopReason ?? "");
        Assert.DoesNotContain("BLOCKED", response.Tuning.StopReason ?? "");
        // Plan should show SAMPLE_ONLY route, not ANSWER_ONLY (which policy block produces)
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
    }

    // ── Windows_History sample is NOT blocked ─────────────────────────────

    [Fact]
    public async Task Windows_History_sample_does_not_block()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "Windows_History",
            Question = "Show CPU trend for all Windows servers",
            SampleId = 810,
            GroupKey = "WinHistory"
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.DoesNotContain("ENV_MISMATCH", response.Tuning.StopReason ?? "");
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
    }

    // ── SqlServer_History sample is NOT blocked ───────────────────────────

    [Fact]
    public async Task SqlServer_History_sample_does_not_block()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "SqlServer_History",
            Question = "Show blocking chains in the last 7 days",
            SampleId = 815,
            GroupKey = "SqlHistory"
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.DoesNotContain("ENV_MISMATCH", response.Tuning.StopReason ?? "");
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
    }

    // ── Sample with env-mismatch-looking text still executes ──────────────

    [Fact]
    public async Task Sample_with_mismatch_text_still_executes()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "Windows_Live",
            Question = "Check if SQL Server services are running on this Windows server",
            SampleId = 820,
            SelectedTargets = ["CTS02"]
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.DoesNotContain("ENV_MISMATCH", response.Tuning.StopReason ?? "");
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
    }

    // ── Manual freeform question still validates normally ──────────────────

    [Fact]
    public async Task Manual_freeform_question_still_validates()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "Windows_Live",
            // No SampleId, no GroupKey — this is a freeform question
            Question = "drop table customers",
            SelectedTargets = ["CTS02"]
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        // Should be BLOCKED by the policy gate — it's dangerous + manual
        Assert.Equal("STOPPED", response.Result.Status);
    }

    // ── Missing/invalid sampleId fails safely ─────────────────────────────

    [Fact]
    public async Task Invalid_sampleId_fails_safely()
    {
        var service = CreateServiceWithBlockingPolicy();
        var request = new AskApiRequest
        {
            Environment = "SqlServer_Live",
            Question = "Some question",
            SampleId = 999999, // nonexistent
            SelectedTargets = ["CTS03"]
        };

        var response = await service.ExecuteAsync(request, CancellationToken.None);

        // Should fail with "sample not found" — not ENV_MISMATCH
        Assert.DoesNotContain("ENV_MISMATCH", response.Tuning.StopReason ?? "");
        // The result should indicate sample wasn't found
        Assert.True(
            response.Result.Status is "STOPPED" or "FAILED"
            || (response.Result.AnswerText?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true),
            $"Expected sample-not-found failure, got status={response.Result.Status}, answer={response.Result.AnswerText}");
    }

    // ── Helper: create service with a BLOCKING policy that rejects everything ──

    private static AskPipelineService CreateServiceWithBlockingPolicy()
    {
        var modelSelector = new TestHelpers.SharedFakeModelSelector();
        ILLMClient[] clients =
        [
            new TestHelpers.SharedFakeLlmClient("Gemini"),
            new TestHelpers.SharedFakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new TestHelpers.SharedFakeOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new BlockEverythingPolicy(), // <-- blocks all manual questions
            new TestHelpers.SharedStubSamplesRepo(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    /// <summary>Policy that blocks EVERYTHING — simulates aggressive ENV_MISMATCH detection.</summary>
    private sealed class BlockEverythingPolicy : IRequestPolicyService
    {
        public PolicyDecision Evaluate(AskApiRequest request, string tunedQuestionOrRaw)
            => new()
            {
                Allowed = false,
                ReasonCode = "ENV_MISMATCH",
                Message = "Simulated environment mismatch block for testing."
            };
    }
}
