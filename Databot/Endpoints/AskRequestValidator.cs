using Application.Common.Models;

namespace Databot.Endpoints;

internal static class AskRequestValidator
{
    public static Dictionary<string, string[]> ValidateAsk(AskApiRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Environment))
            errors["environment"] = ["Environment is required."];

        if (!IsKnownEnvironment(request.Environment))
            errors["environment"] = ["Environment must be General, SqlServer_*, or Windows_*."];

        request.SelectedTargets ??= [];
        if (request.SelectedTargets.Length == 0 && request.SelectedServers?.Length > 0)
            request.SelectedTargets = request.SelectedServers;

        if (RequiresSelectedServers(request.Environment) && request.SelectedTargets.Length == 0)
        {
            errors["selectedTargets"] =
            [
                "selectedTargets must contain at least one value for Live environments (SqlServer_Live or Windows_Live)."
            ];
        }

        if (request.SampleId.HasValue)
        {
            if (string.Equals(request.Environment, "General", StringComparison.OrdinalIgnoreCase))
                errors["environment"] = ["Sample execution is not supported for General environment."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateExecute(ScriptExecutionRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(request.Environment))
            errors["environment"] = ["Environment is required."];

        if (!request.Environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase) &&
            !request.Environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase))
        {
            errors["environment"] = ["Environment must be SqlServer_* or Windows_*."];
        }

        request.SelectedServers ??= [];
        if (request.SelectedServers.Length == 0)
            errors["selectedServers"] = ["selectedServers must contain at least one target."];

        if (string.IsNullOrWhiteSpace(request.ScriptLanguage))
            errors["scriptLanguage"] = ["scriptLanguage is required (SQL or PS)."];

        if (string.IsNullOrWhiteSpace(request.GeneratedScript))
            errors["generatedScript"] = ["generatedScript is required."];

        return errors;
    }

    private static bool RequiresSelectedServers(string environment)
    {
        // History environments run centrally on CTS03 — no selected servers needed.
        if (IsHistory(environment)) return false;
        return environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistory(string environment)
    {
        return environment.EndsWith("_History", StringComparison.OrdinalIgnoreCase)
               && (environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
                   || environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownEnvironment(string environment)
    {
        return string.Equals(environment, "General", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);
    }
}
