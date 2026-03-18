using Application.Common.Models;

namespace Databot.Endpoints;

internal static class HeartbeatRequestValidator
{
    public static Dictionary<string, string[]> Validate(HeartbeatRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Environment))
            errors["environment"] = ["Environment is required."];
        else if (!IsLiveEnvironment(request.Environment))
            errors["environment"] = ["Environment must be SqlServer_Live or Windows_Live for heartbeat."];

        request.SelectedTargets ??= [];
        if (request.SelectedTargets.Length == 0)
            errors["selectedTargets"] = ["At least one target server is required."];

        if (request.IntervalSeconds is < 5 or > 60)
            errors["intervalSeconds"] = ["Interval must be between 5 and 60 seconds."];

        return errors;
    }

    private static bool IsLiveEnvironment(string env)
    {
        return env.EndsWith("_Live", StringComparison.OrdinalIgnoreCase)
               && (env.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
                   || env.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase));
    }
}
