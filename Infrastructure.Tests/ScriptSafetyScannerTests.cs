using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ScriptSafetyScannerTests
{
    private readonly ScriptSafetyScanner _sut = new();

    // ── INSERT into table variable: ALLOW ────────────────────────────────────

    [Fact]
    public void Allow_InsertInto_TableVariable()
    {
        var script = """
            DECLARE @t TABLE(x int);
            INSERT INTO @t VALUES (1);
            SELECT * FROM @t;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Allow_Insert_TableVariable_WithoutInto()
    {
        var script = """
            DECLARE @t TABLE(x int);
            INSERT @t SELECT 1;
            SELECT * FROM @t;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Allow_InsertInto_TempTable()
    {
        // Only the INSERT line — CREATE TABLE is separately blocked by DDL rules
        var script = "INSERT INTO #tmp SELECT object_id FROM sys.objects;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Allow_Insert_TempTable_WithoutInto()
    {
        var script = "INSERT #tmp SELECT 1;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    // ── INSERT into real table: BLOCK ────────────────────────────────────────

    [Fact]
    public void Block_InsertInto_RealTable_Dbo()
    {
        var script = "INSERT INTO dbo.Users(Name) VALUES ('test');";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("INSERT_INTO_TABLE", result.BlockedToken);
    }

    [Fact]
    public void Block_InsertInto_RealTable_SchemaQualified()
    {
        var script = "INSERT INTO DataBOT.dbo.AuditLog(msg) VALUES ('x');";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("INSERT_INTO_TABLE", result.BlockedToken);
    }

    [Fact]
    public void Block_InsertInto_RealTable_Unqualified()
    {
        var script = "INSERT INTO Users(Name) VALUES ('test');";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("INSERT_INTO_TABLE", result.BlockedToken);
    }

    [Fact]
    public void Block_Insert_RealTable_WithoutInto()
    {
        var script = "INSERT Users SELECT 'test';";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("INSERT_INTO_TABLE", result.BlockedToken);
    }

    // ── Comments should not trigger false blocks ───────────────────────────

    [Fact]
    public void Allow_Insert_InsideLineComment()
    {
        var script = """
            -- INSERT INTO dbo.Users VALUES ('should not block')
            SELECT 1 AS safe;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Allow_Insert_InsideBlockComment()
    {
        var script = """
            /* INSERT INTO dbo.Users VALUES ('should not block') */
            SELECT 1 AS safe;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Allow_Delete_InsideComment()
    {
        var script = """
            -- DELETE FROM dbo.Users
            SELECT @@VERSION;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Block_RealInsert_EvenWhenCommentedInsertAlsoPresent()
    {
        var script = """
            -- This is safe: INSERT INTO dbo.Logs
            INSERT INTO dbo.Users(Name) VALUES ('evil');
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("INSERT_INTO_TABLE", result.BlockedToken);
    }

    // ── Realistic sample script (SqlHealth pattern) ──────────────────────────

    [Fact]
    public void Allow_SqlHealth_SampleScript_With_TableVariable()
    {
        var script = """
            DECLARE @H TABLE (
                DatabaseName NVARCHAR(128),
                BackupType NVARCHAR(20),
                LastBackupDate DATETIME
            );
            INSERT INTO @H
            SELECT d.name, 'FULL',
                   MAX(b.backup_finish_date)
            FROM sys.databases d
            LEFT JOIN msdb.dbo.backupset b ON d.name = b.database_name AND b.type = 'D'
            GROUP BY d.name;
            SELECT * FROM @H ORDER BY LastBackupDate;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW for sample script, got blocked: {result.BlockedToken}");
    }

    // ── DROP: allow temp table cleanup, block real objects ─────────────────

    [Fact]
    public void Allow_DropTable_TempTable()
    {
        var script = "IF OBJECT_ID('tempdb..#Blockers') IS NOT NULL DROP TABLE #Blockers;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Allow_DropTable_IfExists_TempTable()
    {
        var script = "DROP TABLE IF EXISTS #tmp;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Block_DropTable_RealTable()
    {
        var script = "DROP TABLE dbo.Users;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("DROP", result.BlockedToken);
    }

    [Fact]
    public void Block_DropDatabase()
    {
        var script = "DROP DATABASE SQLGig;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("DROP", result.BlockedToken);
    }

    [Fact]
    public void Block_DropLogin()
    {
        var script = "DROP LOGIN testuser;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("DROP", result.BlockedToken);
    }

    // ── CREATE: allow temp table, block real objects ─────────────────────

    [Fact]
    public void Allow_CreateTable_TempTable()
    {
        var script = "CREATE TABLE #Blockers (spid INT, blocked_by INT);";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW, got blocked: {result.BlockedToken}");
    }

    [Fact]
    public void Block_CreateTable_RealTable()
    {
        var script = "CREATE TABLE dbo.Hackers (id INT);";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("CREATE", result.BlockedToken);
    }

    [Fact]
    public void Block_CreateDatabase()
    {
        var script = "CREATE DATABASE EvilDb;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("CREATE", result.BlockedToken);
    }

    [Fact]
    public void Block_CreateLogin()
    {
        var script = "CREATE LOGIN hacker WITH PASSWORD = 'p@ss';";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("CREATE", result.BlockedToken);
    }

    // ── ALTER: block real objects ────────────────────────────────────────

    [Fact]
    public void Block_AlterDatabase()
    {
        var script = "ALTER DATABASE SQLGig SET SINGLE_USER;";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("ALTER", result.BlockedToken);
    }

    [Fact]
    public void Block_AlterTable()
    {
        var script = "ALTER TABLE dbo.Users ADD Email NVARCHAR(255);";

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("ALTER", result.BlockedToken);
    }

    // ── Full realistic sample script (blocking chain pattern) ────────────

    [Fact]
    public void Allow_Full_BlockingChains_SampleScript()
    {
        var script = """
            IF OBJECT_ID('tempdb..#Blockers') IS NOT NULL DROP TABLE #Blockers;
            CREATE TABLE #Blockers (
                spid INT,
                blocked_by INT,
                wait_type NVARCHAR(60),
                wait_time_ms BIGINT,
                database_name NVARCHAR(128),
                sql_text NVARCHAR(MAX)
            );
            INSERT INTO #Blockers
            SELECT
                r.session_id,
                r.blocking_session_id,
                r.wait_type,
                r.wait_time,
                DB_NAME(r.database_id),
                t.text
            FROM sys.dm_exec_requests r
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.blocking_session_id <> 0;
            SELECT * FROM #Blockers ORDER BY blocked_by;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.True(result.IsSafe, $"Expected ALLOW for sample blocking-chains script, got blocked: {result.BlockedToken}");
    }

    // ── Mixed: one safe DROP + one dangerous DROP → BLOCK ────────────────

    [Fact]
    public void Block_WhenMixOfSafeAndUnsafeDrop()
    {
        var script = """
            DROP TABLE IF EXISTS #tmp;
            DROP TABLE dbo.Users;
            """;

        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal("DROP", result.BlockedToken);
    }

    // ── Other DML still blocked ──────────────────────────────────────────────

    [Theory]
    [InlineData("UPDATE dbo.Users SET Name = 'x'", "UPDATE")]
    [InlineData("DELETE FROM dbo.Users", "DELETE")]
    [InlineData("MERGE dbo.Target USING src ON 1=1 WHEN MATCHED THEN UPDATE SET x=1;", "MERGE")]
    [InlineData("TRUNCATE TABLE dbo.Logs;", "TRUNCATE")]
    public void Block_Other_DML(string script, string expectedToken)
    {
        var result = _sut.Scan("SqlServer_Live", "SQL", script);

        Assert.False(result.IsSafe);
        Assert.Equal(expectedToken, result.BlockedToken);
    }

    // ── StripSqlComments unit tests ──────────────────────────────────────────

    [Fact]
    public void StripSqlComments_Removes_LineComment()
    {
        var input = "SELECT 1; -- this is a comment\nSELECT 2;";
        var result = ScriptSafetyScanner.StripSqlComments(input);

        Assert.DoesNotContain("this is a comment", result);
        Assert.Contains("SELECT 1;", result);
        Assert.Contains("SELECT 2;", result);
    }

    [Fact]
    public void StripSqlComments_Removes_BlockComment()
    {
        var input = "SELECT /* dangerous INSERT INTO dbo.X */ 1;";
        var result = ScriptSafetyScanner.StripSqlComments(input);

        Assert.DoesNotContain("INSERT", result);
        Assert.Contains("SELECT", result);
    }

    [Fact]
    public void StripSqlComments_Removes_MultilineBlockComment()
    {
        var input = "SELECT 1;\n/*\nINSERT INTO dbo.Users VALUES('x')\nDELETE FROM dbo.Orders\n*/\nSELECT 2;";
        var result = ScriptSafetyScanner.StripSqlComments(input);

        Assert.DoesNotContain("INSERT", result);
        Assert.DoesNotContain("DELETE", result);
    }
}
