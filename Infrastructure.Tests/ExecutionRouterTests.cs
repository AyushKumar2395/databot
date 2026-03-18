using Application.Common.Models;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ExecutionRouterTests
{
    [Fact]
    public void Route_SampleId_SqlEnv_GroupKey_Targets_Returns_SAMPLE_ONLY()
    {
        var request = new AskApiRequest
        {
            SampleId = 42,
            GroupKey = "Databases",
            Environment = "SqlServer_Live",
            SelectedTargets = ["CTS03#Admin"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 1);

        Assert.Equal("SAMPLE_ONLY", route.RouteKind);
        Assert.Equal("SAMPLE", route.ScriptSource);
        Assert.Equal(42, route.SampleId);
        Assert.Equal("Databases", route.GroupKey);
        Assert.Equal("SAMPLE_ONLY", route.GeneratorMode);
    }

    [Fact]
    public void Route_SampleId_WindowsEnv_Returns_SAMPLE_ONLY()
    {
        var request = new AskApiRequest
        {
            SampleId = 10,
            GroupKey = "Services",
            Environment = "Windows_Live",
            SelectedTargets = ["CTS03"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 2);

        Assert.Equal("SAMPLE_ONLY", route.RouteKind);
        Assert.Equal("SAMPLE", route.ScriptSource);
    }

    [Fact]
    public void Route_SampleId_GeneralEnv_Returns_ANSWER_ONLY()
    {
        var request = new AskApiRequest
        {
            SampleId = 1,
            GroupKey = "Concepts",
            Environment = "General",
            SelectedTargets = ["CTS03"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 1);

        Assert.Equal("ANSWER_ONLY", route.RouteKind);
        Assert.Null(route.ScriptSource);
        Assert.Null(route.SampleId);
    }

    [Fact]
    public void Route_SampleId_NoGroupKey_StillRoutes_To_SAMPLE_ONLY()
    {
        var request = new AskApiRequest
        {
            SampleId = 42,
            GroupKey = null,
            Environment = "SqlServer_Live",
            SelectedTargets = ["CTS03#Admin"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 1);

        Assert.Equal("SAMPLE_ONLY", route.RouteKind);
        Assert.Null(route.GroupKey);
    }

    [Fact]
    public void Route_SampleId_NoTargets_FallsThrough_To_LLM()
    {
        var request = new AskApiRequest
        {
            SampleId = 42,
            GroupKey = "Databases",
            Environment = "SqlServer_Live",
            SelectedTargets = []
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 1);

        Assert.Equal("LLM_ONLY", route.RouteKind);
    }

    [Fact]
    public void Route_NoSampleId_SqlEnv_Generator1_Returns_LLM_ONLY()
    {
        var request = new AskApiRequest
        {
            Environment = "SqlServer_Live",
            SelectedTargets = ["CTS03#Admin"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 1);

        Assert.Equal("LLM_ONLY", route.RouteKind);
        Assert.Equal("LLM", route.ScriptSource);
        Assert.Equal("LLM_ONLY", route.GeneratorMode);
    }

    [Fact]
    public void Route_NoSampleId_SqlEnv_Generator2_Returns_TEMPLATE_OR_LLM()
    {
        var request = new AskApiRequest
        {
            Environment = "SqlServer_Live",
            SelectedTargets = ["CTS03#Admin"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 2);

        Assert.Equal("TEMPLATE_OR_LLM", route.RouteKind);
        Assert.Equal("TEMPLATE", route.ScriptSource);
        Assert.Equal("TEMPLATE_OR_LLM", route.GeneratorMode);
    }

    [Fact]
    public void Route_NoSampleId_GeneralEnv_Returns_ANSWER_ONLY()
    {
        var request = new AskApiRequest
        {
            Environment = "General"
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 1);

        Assert.Equal("ANSWER_ONLY", route.RouteKind);
        Assert.Null(route.ScriptSource);
        Assert.Equal("ANSWER_ONLY", route.GeneratorMode);
    }

    [Fact]
    public void Route_NoSampleId_WindowsEnv_Generator2_Returns_TEMPLATE_OR_LLM()
    {
        var request = new AskApiRequest
        {
            Environment = "Windows_Live",
            SelectedTargets = ["CTS03"]
        };

        var route = ExecutionRouter.Route(request, generatorFlag: 2);

        Assert.Equal("TEMPLATE_OR_LLM", route.RouteKind);
    }
}
