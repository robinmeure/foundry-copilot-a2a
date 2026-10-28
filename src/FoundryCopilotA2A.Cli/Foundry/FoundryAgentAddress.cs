namespace FoundryCopilotA2A.Cli;

internal enum ChainAuthenticationMode
{
    OAuth,
    UserEntraToken,
    ProjectManagedIdentity
}

internal sealed record FoundryAgentAddress(string ProjectEndpoint, string AgentName)
{
    public static FoundryAgentAddress Parse(Uri agentUrl)
    {
        var marker = "/agents/";
        var markerIndex = agentUrl.AbsolutePath.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 1)
        {
            throw new CliException(
                "Option '--agent-url' must be a Foundry agent or agent protocol endpoint URL.");
        }

        var agentPath = agentUrl.AbsolutePath[(markerIndex + marker.Length)..];
        var separatorIndex = agentPath.IndexOf('/');
        var encodedAgentName = separatorIndex < 0
            ? agentPath
            : agentPath[..separatorIndex];
        var agentName = Uri.UnescapeDataString(encodedAgentName);
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new CliException(
                "Option '--agent-url' does not contain a Foundry agent name.");
        }

        var authority = agentUrl.GetLeftPart(UriPartial.Authority);
        var projectPath = agentUrl.AbsolutePath[..markerIndex].TrimEnd('/');
        return new FoundryAgentAddress(
            $"{authority}{projectPath}",
            agentName);
    }
}
