using Application.Common.Models;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic router that decides the execution path at the start of the pipeline.
/// No I/O — pure function of request fields + generator flag.
/// </summary>
internal static class ExecutionRouter
{
    public static ExecutionRoute Route(AskApiRequest request, int generatorFlag)
    {
        // GENERAL always wins regardless of SampleId
        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            return new ExecutionRoute
            {
                RouteKind = "ANSWER_ONLY",
                ScriptSource = null,
                GeneratorMode = "ANSWER_ONLY"
            };
        }

        // METRICS-TAB route: History environment + metricKey → generic sample with GroupKey="Metrics"
        if (EnvironmentRules.IsHistory(request.Environment)
            && !string.IsNullOrWhiteSpace(request.MetricKey))
        {
            return new ExecutionRoute
            {
                RouteKind = "SAMPLE_ONLY",
                ScriptSource = "SAMPLE",
                SampleId = null,
                GroupKey = "Metrics",
                GeneratorMode = "SAMPLE_ONLY"
            };
        }

        // SAMPLE route: SampleId present, SQL/Windows env.
        // Live envs require targets; History envs run centrally on CTS03 so targets are optional.
        if (request.SampleId.HasValue
            && ((request.SelectedTargets?.Length ?? 0) > 0
                || EnvironmentRules.IsHistory(request.Environment)))
        {
            return new ExecutionRoute
            {
                RouteKind = "SAMPLE_ONLY",
                ScriptSource = "SAMPLE",
                SampleId = request.SampleId.Value,
                GroupKey = request.GroupKey,
                GeneratorMode = "SAMPLE_ONLY"
            };
        }

        // SQL/Windows: template-first vs LLM-only based on generator flag
        if (generatorFlag == 2)
        {
            return new ExecutionRoute
            {
                RouteKind = "TEMPLATE_OR_LLM",
                ScriptSource = "TEMPLATE",
                GeneratorMode = "TEMPLATE_OR_LLM"
            };
        }

        return new ExecutionRoute
        {
            RouteKind = "LLM_ONLY",
            ScriptSource = "LLM",
            GeneratorMode = "LLM_ONLY"
        };
    }
}
