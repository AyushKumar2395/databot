using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal static partial class TunedQuestionRefiner
{
    public static string Refine(string tunedQuestion, string environment)
    {
        if (string.IsNullOrWhiteSpace(tunedQuestion))
            return tunedQuestion;

        var text = tunedQuestion.Trim();
        text = CollapseSpacesRegex().Replace(text, " ");
        text = CleanupFillerRegex().Replace(text, " ");
        text = CollapseSpacesRegex().Replace(text, " ").Trim();
        var canonical = text.TrimEnd('.', '!', '?', ';', ',');

        // Expand ambiguous server list requests into environment-specific actionable intent.
        if (EnvironmentRules.IsWindows(environment) && IsGenericServerList(canonical))
            return "List all Windows server details and all info.";

        if (EnvironmentRules.IsSqlServer(environment) && IsGenericServerList(canonical))
            return "List all SQL Server instance details and all info.";

        // Clarify broad singular "server" phrases in Windows mode.
        if (EnvironmentRules.IsWindows(environment) && GenericServerWordRegex().IsMatch(canonical))
            text = GenericServerWordRegex().Replace(text, "Windows server");

        // Clarify broad singular "server" phrases in SQL mode.
        if (EnvironmentRules.IsSqlServer(environment) && GenericServerWordRegex().IsMatch(canonical))
            text = GenericServerWordRegex().Replace(text, "SQL Server instance");

        text = NormalizeSentence(text);
        return text;
    }

    private static bool IsGenericServerList(string text)
    {
        return GenericListServersRegex().IsMatch(text)
               || GenericShowServersRegex().IsMatch(text)
               || GenericGetServersRegex().IsMatch(text);
    }

    private static string NormalizeSentence(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Show details.";

        var text = input.Trim().TrimEnd('.', '!', '?', ';', ',');
        if (string.IsNullOrWhiteSpace(text))
            return "Show details.";

        text = char.ToUpperInvariant(text[0]) + text[1..];
        return $"{text}.";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseSpacesRegex();

    [GeneratedRegex(@"\b(the\s+then|then\s+the)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CleanupFillerRegex();

    [GeneratedRegex(@"^list\s+all\s+servers?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericListServersRegex();

    [GeneratedRegex(@"^show\s+all\s+servers?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericShowServersRegex();

    [GeneratedRegex(@"^get\s+all\s+servers?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericGetServersRegex();

    [GeneratedRegex(@"\bservers?\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericServerWordRegex();
}
