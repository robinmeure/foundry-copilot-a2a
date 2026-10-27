namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;

/// <summary>
/// Knowledge-specific options an AG-UI client sends in <c>RunAgentInput.forwardedProps</c>.
/// They match the optional fields of <see cref="ChatRequest"/>.
/// </summary>
/// <param name="Sources">Knowledge sources the turn may use, such as <c>a2a</c> or Graph sources.</param>
/// <param name="TimeZone">IANA or Windows time zone used when the mailbox setting is unavailable.</param>
/// <param name="Agents">APIM A2A agent IDs that narrow the <c>a2a</c> source.</param>
public sealed record ChatRunProperties(
    string[]? Sources = null,
    string? TimeZone = null,
    string[]? Agents = null);
