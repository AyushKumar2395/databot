using System.Text.RegularExpressions;
using Application.Common.Interfaces;

namespace Infrastructure.Repository;

public sealed class QueryCodeRouterService : IQueryCodeRouterService
{
    public string Route(string question, string environmentTag)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;

        var q = question.Trim();
        var isSql = string.Equals(environmentTag, "<SqlServer_Live>", StringComparison.OrdinalIgnoreCase);

        // Normalize
        var lower = q.ToLowerInvariant();

        // Very conservative SQL routes
        if (isSql)
        {
            // Top CPU queries
            if (lower.Contains("top") && lower.Contains("cpu") && (lower.Contains("query") || lower.Contains("queries")))
                return "SQL_TOP_CPU_QUERIES";

            // SQL version/edition
            if ((lower.Contains("version") || lower.Contains("build") || lower.Contains("edition"))
                && (lower.Contains("sql") || lower.Contains("instance") || Regex.IsMatch(lower, @"\bsql\s*server\b")))
                return "SQL_INSTANCE_VERSION";

            // Blocking
            if (lower.Contains("blocking") || (lower.Contains("blocked") && lower.Contains("session")))
                return "SQL_BLOCKING";

            // Wait stats
            if (lower.Contains("wait") && (lower.Contains("stats") || lower.Contains("statistics")))
                return "SQL_WAITSTATS";

            // Uptime / start time
            if (lower.Contains("uptime")
                || (lower.Contains("start") && (lower.Contains("time") || lower.Contains("uptime")))
                || lower.Contains("sqlserver_start_time")
                || (lower.Contains("restart") && (lower.Contains("sql") || lower.Contains("instance"))))
                return "SQL_UPTIME";

            return string.Empty;
        }
        // Windows routes (conservative)
        // Disk free
        if (lower.Contains("disk") && (lower.Contains("free") || lower.Contains("space")))
            return "WIN_DISK_FREE";

        // OS + last boot
        if (lower.Contains("os") || lower.Contains("operating system") || lower.Contains("boot") || lower.Contains("reboot"))
            return "WIN_OS_BOOT";

        return string.Empty;
    }
}
