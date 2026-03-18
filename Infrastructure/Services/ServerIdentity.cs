namespace Infrastructure.Services;

/// <summary>
/// Normalizes SQL Server instance identifiers across all formats used in the system:
///   Token:       "CTS02#ADMIN"
///   DisplayName: "CTS02\ADMIN,1432"
///   Connection:  "CTS02,1432#ADMIN"  (port-embedded token)
///   BackSlash:   "CTS02\ADMIN"       (SQL @@SERVERNAME format)
///   Bare:        "CTS02"             (default instance, no qualifier)
///
/// The canonical key is "SERVER\INSTANCE" (uppercase, no port) for named instances,
/// or just "SERVER" for default instances (MSSQLSERVER stripped).
/// This key is used for snapshot buffer lookups, analysis requests, and display matching.
/// </summary>
public static class ServerIdentity
{
    /// <summary>
    /// Produces a stable canonical key from ANY server identifier format.
    /// All equivalent identifiers produce the same key.
    /// </summary>
    /// <example>
    /// "CTS02#ADMIN"          → "CTS02\ADMIN"
    /// "CTS02\ADMIN,1432"     → "CTS02\ADMIN"
    /// "CTS02,1432#ADMIN"     → "CTS02\ADMIN"
    /// "CTS02\ADMIN"          → "CTS02\ADMIN"
    /// "CTS02#MSSQLSERVER"    → "CTS02"
    /// "CTS02,1433"           → "CTS02"
    /// "CTS02"                → "CTS02"
    /// "CTS03\CTSGLOBAL"      → "CTS03\CTSGLOBAL"
    /// "CTS03#CTSGlobal"      → "CTS03\CTSGLOBAL"
    /// "CTS03\CTSGlobal,1431" → "CTS03\CTSGLOBAL"
    /// </example>
    public static string Canonicalize(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return string.Empty;

        var s = identifier.Trim();

        // Step 1: Extract server and instance from any format
        string server;
        string? instance;

        // Format: "SERVER#INSTANCE" (token format, may have port in server part)
        var hashIdx = s.IndexOf('#');
        if (hashIdx >= 0)
        {
            server = s[..hashIdx];
            instance = s[(hashIdx + 1)..];
        }
        // Format: "SERVER\INSTANCE" or "SERVER\INSTANCE,PORT"
        else
        {
            var backslashIdx = s.IndexOf('\\');
            if (backslashIdx >= 0)
            {
                server = s[..backslashIdx];
                instance = s[(backslashIdx + 1)..];
            }
            else
            {
                // Format: "SERVER" or "SERVER,PORT"
                server = s;
                instance = null;
            }
        }

        // Step 2: Strip port from server (e.g., "CTS02,1432" → "CTS02")
        var commaIdx = server.IndexOf(',');
        if (commaIdx >= 0)
            server = server[..commaIdx];

        // Step 3: Strip port from instance (e.g., "ADMIN,1432" → "ADMIN")
        if (instance is not null)
        {
            var instComma = instance.IndexOf(',');
            if (instComma >= 0)
                instance = instance[..instComma];
        }

        // Step 4: Normalize MSSQLSERVER → default instance (no instance qualifier)
        if (!string.IsNullOrWhiteSpace(instance) &&
            instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
            instance = null;

        // Step 5: Build canonical key (uppercase for stable matching)
        server = server.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(instance))
            return server;

        return $@"{server}\{instance.Trim().ToUpperInvariant()}";
    }

    /// <summary>
    /// Checks if two server identifiers refer to the same instance.
    /// </summary>
    public static bool AreSame(string? a, string? b)
        => string.Equals(Canonicalize(a), Canonicalize(b), StringComparison.Ordinal);

    /// <summary>
    /// Checks if identifier refers to a default instance (no named qualifier).
    /// </summary>
    public static bool IsDefaultInstance(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return true;
        var canonical = Canonicalize(identifier);
        return !canonical.Contains('\\');
    }
}
