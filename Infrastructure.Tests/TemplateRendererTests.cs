using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TemplateRendererTests
{
    [Fact]
    public void Render_Binds_NameContains_And_Removes_Placeholders()
    {
        var renderer = new TemplateRenderer(NullLogger<TemplateRenderer>.Instance);
        var request = new TemplateRenderRequest
        {
            Environment = "SqlServer_Live",
            ScriptLanguage = "SQL",
            RawQuestion = "list all database name contains SQLGIG",
            TunedQuestion = "List databases where name contains SQLGIG.",
            ScriptTemplate = """
DECLARE @TopN int = {{TopN}};
DECLARE @NameContains nvarchar(256) = {{NameContains}};
DECLARE @OnlyUser bit = {{OnlyUser}};
DECLARE @State nvarchar(20) = {{State}};
SELECT @TopN, @NameContains, @OnlyUser, @State;
""",
            ParameterSchemaJson = """
{
  "parameters": [
    {
      "name":"TopN",
      "type":"int",
      "required":false,
      "default":500,
      "min":1,
      "max":5000,
      "extract":{"regex":"top\\s+(\\d+)","group":1}
    },
    {
      "name":"NameContains",
      "type":"string",
      "required":false,
      "default":"",
      "extract":{"regex":"(database|db)\\s+(name\\s+)?contains\\s+'?([a-zA-Z0-9 _.-]+)'?","group":3}
    },
    {
      "name":"OnlyUser",
      "type":"bit",
      "required":false,
      "default":1,
      "derive":{"containsAny":["include system databases","system databases"],"valueIfTrue":0}
    },
    {
      "name":"State",
      "type":"string",
      "required":false,
      "default":""
    }
  ],
  "safety": {
    "readOnly": true,
    "blockedKeywords": ["DELETE","UPDATE","INSERT","MERGE","DROP","ALTER","TRUNCATE","xp_cmdshell","sp_configure"]
  }
}
"""
        };

        var result = renderer.Render(request);

        Assert.True(result.Success);
        Assert.Equal("SQLGIG", result.BoundParameters["NameContains"]);
        Assert.Equal(500, result.BoundParameters["TopN"]);
        Assert.Contains("DECLARE @NameContains nvarchar(256) = N'SQLGIG';", result.RenderedScript);
        Assert.DoesNotContain("{{", result.RenderedScript);
    }

    [Theory]
    [InlineData("list all databases contains 'SQLGIG'")]
    [InlineData("name like '%SQLGIG%'")]
    [InlineData("database name contains SQLGIG")]
    public void Render_NameContains_Extraction_Supports_Quoted_Unquoted_And_Like(string rawQuestion)
    {
        var renderer = new TemplateRenderer(NullLogger<TemplateRenderer>.Instance);
        var request = new TemplateRenderRequest
        {
            Environment = "SqlServer_Live",
            ScriptLanguage = "SQL",
            RawQuestion = rawQuestion,
            TunedQuestion = "List databases where name contains SQLGIG.",
            ScriptTemplate = "DECLARE @NameContains nvarchar(256) = {{NameContains}};",
            ParameterSchemaJson = """
{
  "parameters":[
    {
      "name":"NameContains",
      "type":"string",
      "required":false,
      "default":"",
      "extract":{"regex":"(database|db)\\s+(name\\s+)?contains\\s+'?([a-zA-Z0-9 _.-]+)'?","group":3}
    }
  ],
  "safety":{"readOnly":true,"blockedKeywords":[]}
}
"""
        };

        var result = renderer.Render(request);

        Assert.True(result.Success);
        Assert.Equal("SQLGIG", result.BoundParameters["NameContains"]);
        Assert.Contains("N'SQLGIG'", result.RenderedScript, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", result.RenderedScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Supports_Extract3_From_ParameterSchema()
    {
        var renderer = new TemplateRenderer(NullLogger<TemplateRenderer>.Instance);
        var request = new TemplateRenderRequest
        {
            Environment = "SqlServer_Live",
            ScriptLanguage = "SQL",
            RawQuestion = "name like '%SQLGIG%'",
            TunedQuestion = "List databases where name LIKE '%SQLGIG%'.",
            ScriptTemplate = "DECLARE @NameContains nvarchar(256) = {{NameContains}};",
            ParameterSchemaJson = """
{
  "parameters":[
    {
      "name":"NameContains",
      "type":"string",
      "default":"",
      "extract3":{"regex":"(?i)\\bname\\s+like\\s+'%([^']+)%'","group":1}
    }
  ],
  "safety":{"readOnly":true,"blockedKeywords":[]}
}
"""
        };

        var result = renderer.Render(request);

        Assert.True(result.Success);
        Assert.Equal("SQLGIG", result.BoundParameters["NameContains"]);
        Assert.Contains("N'SQLGIG'", result.RenderedScript, StringComparison.Ordinal);
    }
}
