using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ServerIdentityTests
{
    // ── Token format: SERVER#INSTANCE ──────────────────────────────────────

    [Theory]
    [InlineData("CTS02#ADMIN", "CTS02\\ADMIN")]
    [InlineData("CTS02#FINANCE", "CTS02\\FINANCE")]
    [InlineData("CTS03#CTSGlobal", "CTS03\\CTSGLOBAL")]
    [InlineData("CTS02#MSSQLSERVER", "CTS02")]
    public void Token_format_canonicalizes_correctly(string input, string expected)
        => Assert.Equal(expected, ServerIdentity.Canonicalize(input));

    // ── DisplayName format: SERVER\INSTANCE,PORT ──────────────────────────

    [Theory]
    [InlineData("CTS02\\ADMIN,1432", "CTS02\\ADMIN")]
    [InlineData("CTS02\\FINANCE,1431", "CTS02\\FINANCE")]
    [InlineData("CTS03\\CTSGlobal,1431", "CTS03\\CTSGLOBAL")]
    [InlineData("CTS03,1433", "CTS03")]
    [InlineData("CTS02,1433", "CTS02")]
    public void DisplayName_format_canonicalizes_correctly(string input, string expected)
        => Assert.Equal(expected, ServerIdentity.Canonicalize(input));

    // ── Connection token format: SERVER,PORT#INSTANCE ─────────────────────

    [Theory]
    [InlineData("CTS02,1432#ADMIN", "CTS02\\ADMIN")]
    [InlineData("CTS02,1431#FINANCE", "CTS02\\FINANCE")]
    [InlineData("CTS03,1431#CTSGlobal", "CTS03\\CTSGLOBAL")]
    public void Connection_token_format_canonicalizes_correctly(string input, string expected)
        => Assert.Equal(expected, ServerIdentity.Canonicalize(input));

    // ── Backslash format: SERVER\INSTANCE (SQL @@SERVERNAME) ──────────────

    [Theory]
    [InlineData("CTS02\\ADMIN", "CTS02\\ADMIN")]
    [InlineData("CTS03\\CTSGLOBAL", "CTS03\\CTSGLOBAL")]
    [InlineData("CTS02\\MSSQLSERVER", "CTS02")]
    public void Backslash_format_canonicalizes_correctly(string input, string expected)
        => Assert.Equal(expected, ServerIdentity.Canonicalize(input));

    // ── Bare server name (default instance) ───────────────────────────────

    [Theory]
    [InlineData("CTS02", "CTS02")]
    [InlineData("CTS03", "CTS03")]
    [InlineData("cts02", "CTS02")]
    [InlineData("  CTS02  ", "CTS02")]
    public void Bare_server_canonicalizes_correctly(string input, string expected)
        => Assert.Equal(expected, ServerIdentity.Canonicalize(input));

    // ── Edge cases ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Null_empty_whitespace_returns_empty(string? input, string expected)
        => Assert.Equal(expected, ServerIdentity.Canonicalize(input));

    // ── Cross-format equivalence: ALL these must produce the same key ─────

    [Fact]
    public void All_CTS02_ADMIN_formats_are_equivalent()
    {
        var formats = new[]
        {
            "CTS02#ADMIN",           // token
            "CTS02\\ADMIN,1432",     // displayName
            "CTS02,1432#ADMIN",      // connection token
            "CTS02\\ADMIN",          // SQL @@SERVERNAME
            "cts02\\admin",          // lowercase
            "CTS02\\ADMIN,1432",     // displayName (redundant, confirms stability)
        };

        var keys = formats.Select(ServerIdentity.Canonicalize).Distinct().ToList();
        Assert.Single(keys);
        Assert.Equal("CTS02\\ADMIN", keys[0]);
    }

    [Fact]
    public void All_CTS03_CTSGlobal_formats_are_equivalent()
    {
        var formats = new[]
        {
            "CTS03#CTSGlobal",
            "CTS03\\CTSGlobal,1431",
            "CTS03,1431#CTSGlobal",
            "CTS03\\CTSGlobal",
            "CTS03\\CTSGLOBAL",
        };

        var keys = formats.Select(ServerIdentity.Canonicalize).Distinct().ToList();
        Assert.Single(keys);
        Assert.Equal("CTS03\\CTSGLOBAL", keys[0]);
    }

    [Fact]
    public void All_default_instance_formats_are_equivalent()
    {
        var formats = new[]
        {
            "CTS02",
            "CTS02#MSSQLSERVER",
            "CTS02,1433",
            "CTS02\\MSSQLSERVER",
            "cts02",
        };

        var keys = formats.Select(ServerIdentity.Canonicalize).Distinct().ToList();
        Assert.Single(keys);
        Assert.Equal("CTS02", keys[0]);
    }

    // ── AreSame ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CTS03#CTSGlobal", "CTS03\\CTSGlobal", true)]
    [InlineData("CTS03#CTSGlobal", "CTS03\\CTSGlobal,1431", true)]
    [InlineData("CTS02#ADMIN", "CTS02\\ADMIN,1432", true)]
    [InlineData("CTS02#ADMIN", "CTS02#FINANCE", false)]
    [InlineData("CTS02", "CTS02#MSSQLSERVER", true)]
    [InlineData("CTS02", "CTS02#ADMIN", false)]
    public void AreSame_compares_correctly(string a, string b, bool expected)
        => Assert.Equal(expected, ServerIdentity.AreSame(a, b));

    // ── IsDefaultInstance ─────────────────────────────────────────────────

    [Theory]
    [InlineData("CTS02", true)]
    [InlineData("CTS02#MSSQLSERVER", true)]
    [InlineData("CTS02,1433", true)]
    [InlineData("CTS02#ADMIN", false)]
    [InlineData("CTS02\\ADMIN,1432", false)]
    public void IsDefaultInstance_detects_correctly(string input, bool expected)
        => Assert.Equal(expected, ServerIdentity.IsDefaultInstance(input));
}
