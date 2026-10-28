using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoundryCopilotA2A.Cli;

internal static partial class FoundryCommands
{
    internal static JsonObject BuildChainDefinition(
        JsonElement existingDefinition,
        string connectionId,
        string targetAgentId,
        string targetAgentName,
        IReadOnlySet<string>? existingConnectionIds = null,
        string? targetUrl = null,
        string? replacedConnectionId = null)
    {
        var definition = JsonNode.Parse(existingDefinition.GetRawText()) as JsonObject
            ?? throw new CliException("The latest Foundry agent version has no prompt definition.");
        if (definition["kind"]?.GetValue<string>() != "prompt")
        {
            throw new CliException("Only Foundry prompt agents can be configured for this chain.");
        }

        var tools = definition["tools"] as JsonArray ?? [];
        if (definition["tools"] is not JsonArray)
        {
            definition["tools"] = tools;
        }

        // Drop A2A tools whose connection no longer exists. Preserving them leaves the agent with
        // several indistinguishable A2A tools for the same target, so the model picks between them
        // nondeterministically and can select a deleted connection, which fails at runtime.
        if (existingConnectionIds is not null)
        {
            var stale = tools
                .OfType<JsonObject>()
                .Where(tool =>
                {
                    if (tool["type"]?.GetValue<string>() != "a2a_preview")
                    {
                        return false;
                    }

                    var toolConnection = tool["project_connection_id"]?.GetValue<string>();
                    return toolConnection is not null &&
                           !string.Equals(toolConnection, connectionId, StringComparison.OrdinalIgnoreCase) &&
                           !existingConnectionIds.Contains(toolConnection);
                })
                .ToArray();
            foreach (var tool in stale)
            {
                tools.Remove(tool);
            }
        }

        var hasConnection = tools
            .OfType<JsonObject>()
            .Any(tool =>
                tool["type"]?.GetValue<string>() == "a2a_preview" &&
                string.Equals(tool["project_connection_id"]?.GetValue<string>(),
                    connectionId, StringComparison.OrdinalIgnoreCase));
        if (replacedConnectionId is not null)
        {
            var replacedTools = tools.OfType<JsonObject>().Where(tool =>
                tool["type"]?.GetValue<string>() == "a2a_preview" &&
                string.Equals(tool["project_connection_id"]?.GetValue<string>(),
                    replacedConnectionId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (replacedTools.Length == 0 && !hasConnection)
            {
                throw new CliException("The agent does not reference the connection selected for replacement.");
            }
            foreach (var replacedTool in replacedTools)
            {
                tools.Remove(replacedTool);
            }
        }
        if (!hasConnection)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "a2a_preview",
                ["project_connection_id"] = connectionId
            });
        }
        if (targetUrl is not null)
        {
            foreach (var tool in tools.OfType<JsonObject>().Where(tool =>
                tool["type"]?.GetValue<string>() == "a2a_preview" &&
                string.Equals(tool["project_connection_id"]?.GetValue<string>(),
                    connectionId, StringComparison.OrdinalIgnoreCase)))
            {
                tool["base_url"] = targetUrl;
            }
        }

        var instructions = definition["instructions"]?.GetValue<string>() ?? string.Empty;
        var sectionStart = $"{ChainInstructionStart}{targetAgentId} -->";
        var section = string.Join(
            Environment.NewLine,
            sectionStart,
            $"An A2A tool named \"{targetAgentName}\" is available for Copilot Studio delegation.",
            $"When a request explicitly asks you to delegate to \"{targetAgentName}\", call that " +
            "A2A tool before answering and faithfully return its result.",
            ChainInstructionEnd);
        definition["instructions"] = UpsertInstructionSection(
            instructions,
            sectionStart,
            section);
        return definition;
    }

    private static string UpsertInstructionSection(
        string instructions,
        string sectionStart,
        string section)
    {
        var start = instructions.IndexOf(sectionStart, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.IsNullOrWhiteSpace(instructions)
                ? section
                : $"{instructions.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{section}";
        }

        var end = instructions.IndexOf(
            ChainInstructionEnd,
            start,
            StringComparison.Ordinal);
        if (end < 0)
        {
            throw new CliException(
                "The existing generated chain instruction section is incomplete.");
        }

        end += ChainInstructionEnd.Length;
        return string.Concat(instructions.AsSpan(0, start), section, instructions.AsSpan(end));
    }

    private static JsonElement GetLatestVersion(JsonElement agent)
    {
        if (!agent.TryGetProperty("versions", out var versions) ||
            !versions.TryGetProperty("latest", out var latest) ||
            !latest.TryGetProperty("definition", out _))
        {
            throw new CliException(
                "The Foundry agent response did not contain a latest version definition.");
        }

        return latest;
    }

    private static async Task ValidateChainAgentCardAsync(
        CliContext context,
        string chainBaseUrl,
        string expectedName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{chainBaseUrl}.well-known/agent-card.json");
        request.Headers.TryAddWithoutValidation("X-Tunnel-Skip-AntiPhishing-Page", "true");
        using var response = await context.HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(
                $"Chain agent card returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        using var card = JsonDocument.Parse(responseBody);
        if (!card.RootElement.TryGetProperty("name", out var name) ||
            name.GetString() != expectedName)
        {
            throw new CliException(
                $"The chain agent card at '{chainBaseUrl}' did not describe '{expectedName}'.");
        }
        var runtimeUrl = $"{chainBaseUrl}a2a";
        if (!AgentCardContract.AdvertisesRuntime(
                card.RootElement, url => SameEndpoint(url, runtimeUrl)))
        {
            throw new CliException(
                "The specialist card advertises a different runtime. Set Adapter:PublicBaseUrl " +
                "to the same public gateway base before configuring a native connection.");
        }
    }

    private static void ValidateConfiguredChain(JsonElement agentVersion, string connectionId)
    {
        if (!agentVersion.TryGetProperty("definition", out var definition) ||
            !definition.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array ||
            !tools.EnumerateArray().Any(tool =>
                tool.TryGetProperty("type", out var type) &&
                type.GetString() == "a2a_preview" &&
                tool.TryGetProperty("project_connection_id", out var id) &&
                id.GetString() == connectionId))
        {
            throw new CliException(
                "Foundry created the agent version, but the A2A tool was not present.");
        }
    }

    private static bool HasAgentCard(JsonElement agent) =>
        agent.TryGetProperty("agent_card", out var card) &&
        card.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static void ValidatePublishedCard(
        JsonElement card,
        string expectedDescription,
        string expectedSkillId)
    {
        if (!card.TryGetProperty("description", out var description) ||
            description.GetString() != expectedDescription ||
            !card.TryGetProperty("skills", out var skills) ||
            skills.ValueKind != JsonValueKind.Array ||
            !skills.EnumerateArray().Any(
                skill => skill.TryGetProperty("id", out var id) &&
                         id.GetString() == expectedSkillId))
        {
            throw new CliException(
                "Foundry accepted the update, but the published A2A agent card did not match it.");
        }
    }

    private static void ValidateCardValue(
        string option,
        string value,
        bool allowSpaces)
    {
        if (value.Contains('/') ||
            value.Any(char.IsControl) ||
            (!allowSpaces && value.Any(char.IsWhiteSpace)))
        {
            throw new CliException($"Option '--{option}' contains unsupported characters.");
        }
    }
}
