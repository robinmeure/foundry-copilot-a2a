using System.Globalization;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;

/// <summary>A cited chunk of evidence returned by document search or an external knowledge source.</summary>
public sealed partial record Passage(
    string Id,
    string DocumentId,
    string Title,
    string Content,
    string Locator,
    string? Url = null,
    string? UserId = null,
    OriginatingAgentInfo? OriginatingAgent = null)
{
    public int? PageNumber => ReadLocationNumber("page");
    public int? PassageNumber => ReadLocationNumber("passage");

    private int? ReadLocationNumber(string group)
    {
        var value = PageLocator().Match(Locator).Groups[group].Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    [GeneratedRegex(@"^Page (?<page>[1-9][0-9]*)(?:, passage (?<passage>[1-9][0-9]*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PageLocator();
}
