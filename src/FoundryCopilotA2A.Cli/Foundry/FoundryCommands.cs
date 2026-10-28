using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.Cli;

internal static partial class FoundryCommands
{
    private const string ChainInstructionStart = "<!-- foundry-copilot-a2a:";
    private const string ChainInstructionEnd = "<!-- /foundry-copilot-a2a -->";
    private const string DefaultOAuthSecretEnvironmentVariable =
        "FOUNDRY_A2A_OAUTH_CLIENT_SECRET";
    private static readonly Regex ConnectionNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{2,32}$",
        RegexOptions.CultureInvariant);


    public static async Task<int> EnableA2AAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "agent-url",
            "description",
            "skill-id",
            "skill-name",
            "skill-description",
            "card-version",
            "replace-card",
            "smoke-prompt",
            "help");

        var address = FoundryAgentAddress.Parse(
            arguments.AbsoluteHttpUri("agent-url"));
        var description = arguments.Require("description");
        var skillId = arguments.Require("skill-id");
        var skillName = arguments.Require("skill-name");
        var skillDescription = arguments.Require("skill-description");
        var cardVersion = arguments.Optional("card-version", "1.0")!;
        ValidateCardValue("skill-id", skillId, allowSpaces: false);
        ValidateCardValue("card-version", cardVersion, allowSpaces: false);

        var agentUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}?api-version=v1";
        using var existingAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Get,
            agentUrl,
            body: null,
            cancellationToken);

        if (HasAgentCard(existingAgent.RootElement) && !arguments.Flag("replace-card"))
        {
            throw new CliException(
                "The agent already has an agent card. Review it, then rerun with " +
                "--replace-card to confirm replacement.");
        }

        var patchBody = JsonSerializer.Serialize(new
        {
            agent_card = new
            {
                description,
                version = cardVersion,
                skills = new[]
                {
                    new
                    {
                        id = skillId,
                        name = skillName,
                        description = skillDescription
                    }
                }
            },
            agent_endpoint = new
            {
                protocols = new[] { "responses", "a2a" }
            }
        });

        using var updatedAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Patch,
            agentUrl,
            patchBody,
            cancellationToken);

        var cardUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            $"/endpoint/protocols/a2a/agentCard/v{cardVersion}";
        using var card = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Get,
            cardUrl,
            body: null,
            cancellationToken);

        ValidatePublishedCard(card.RootElement, description, skillId);

        var smokePrompt = arguments.Optional("smoke-prompt");
        if (smokePrompt is not null)
        {
            await SmokeTestAsync(context, address, smokePrompt, cancellationToken);
        }

        context.Out.WriteLine($"Enabled incoming A2A for '{address.AgentName}'.");
        context.Out.WriteLine($"Agent card: {cardUrl}");
        return 0;
    }

    public static async Task<int> ConfigureChainAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "agent-url",
            "adapter-url",
            "audience",
            "tenant-id",
            "subscription-id",
            "resource-group",
            "account-name",
            "project-name",
            "target-agent-id",
            "target-agent-name",
            "connection-name",
            "auth-mode",
            "oauth-client-id",
            "oauth-client-secret-env",
            "reuse-connection",
            "prepare-connection",
            "replace-connection-name",
            "smoke-prompt",
            "help");

        var address = FoundryAgentAddress.Parse(arguments.AbsoluteHttpUri("agent-url"));
        var adapterUrl = arguments
            .AbsoluteHttpUri("adapter-url")
            .AbsoluteUri.TrimEnd('/');
        var audience = arguments.Require("audience");
        if (!Uri.TryCreate(audience, UriKind.Absolute, out _))
        {
            throw new CliException("Option '--audience' must be an absolute Entra audience URI.");
        }

        var resourceGroup = arguments.Require("resource-group");
        var tenantId = arguments.Require("tenant-id");
        var subscriptionId = arguments.Require("subscription-id");
        var accountName = arguments.Require("account-name");
        var projectName = arguments.Require("project-name");
        var targetAgentId = arguments.Require("target-agent-id");
        var targetAgentName = arguments.Require("target-agent-name");
        ValidateCardValue("target-agent-id", targetAgentId, allowSpaces: false);
        var connectionName = arguments.Optional(
            "connection-name",
            BuildDefaultConnectionName(targetAgentId))!;
        ValidateConnectionName(connectionName);
        var replacedConnectionName = arguments.Optional("replace-connection-name");
        if (replacedConnectionName is not null)
        {
            ValidateConnectionName(replacedConnectionName);
            if (string.Equals(connectionName, replacedConnectionName, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("--replace-connection-name must name a different connection.");
            }
        }
        var reuseConnection = arguments.Flag("reuse-connection");
        var prepareConnection = arguments.Flag("prepare-connection");
        if (prepareConnection &&
            (reuseConnection || replacedConnectionName is not null || arguments.Has("smoke-prompt")))
        {
            throw new CliException(
                "--prepare-connection creates a new connection without changing an agent. " +
                "Do not combine it with --reuse-connection, --replace-connection-name, or --smoke-prompt.");
        }
        var authMode = ParseAuthenticationMode(arguments.Optional("auth-mode", "oauth")!);
        var oauthClientId = arguments.Optional("oauth-client-id");
        var oauthClientSecret = ReadOAuthClientSecret(arguments, authMode, reuseConnection, oauthClientId);
        var connectionAddress = new FoundryConnectionAddress(
            subscriptionId, resourceGroup, accountName, projectName, connectionName);
        var connectionId = connectionAddress.ResourceId;

        var chainBaseUrl = BuildChainBaseUrl(adapterUrl, targetAgentId);
        var chainTargetUrl = $"{chainBaseUrl}a2a";
        await ValidateChainAgentCardAsync(
            context,
            chainBaseUrl,
            targetAgentName,
            cancellationToken);

        var armToken = await AcquireArmTokenAsync(context, tenantId, cancellationToken);

        if (!reuseConnection)
        {
            var operation = authMode == ChainAuthenticationMode.OAuth
                ? "Creating"
                : "Creating or updating";
            context.Out.WriteLine(
                $"{operation} authenticated Foundry connection '{connectionName}'...");
            if (authMode == ChainAuthenticationMode.UserEntraToken)
            {
                await ValidateUserTokenAcquisitionAsync(
                    context,
                    tenantId,
                    audience,
                    cancellationToken);
            }

            var redirectUrl = await PutRemoteA2AConnectionAsync(
                context.HttpClient,
                armToken,
                connectionAddress,
                target: chainTargetUrl,
                audience: audience,
                authenticationMode: authMode,
                tenantId: tenantId,
                oauthClientId: oauthClientId,
                oauthClientSecret: oauthClientSecret,
                cancellationToken: cancellationToken);
            if (redirectUrl is not null)
            {
                context.Out.WriteLine($"OAuth redirect URL: {redirectUrl}");
                context.Out.WriteLine(
                    "Add this URL as a Web redirect URI on the OAuth app registration before " +
                    "authorizing the A2A tool in the Foundry playground.");
            }

            if (authMode == ChainAuthenticationMode.OAuth)
            {
                if (!await HasNativeOAuthConnectionMetadataAsync(
                        context.HttpClient,
                        armToken,
                        connectionId,
                        cancellationToken))
                {
                    throw new CliException(ConnectorGatewayMissingMessage);
                }
            }
        }
        else
        {
            context.Out.WriteLine(
                $"Reusing existing Foundry connection '{connectionName}' without updating credentials.");
        }

        await ValidateReusableConnectionAsync(
            context.HttpClient,
            armToken,
            connectionId,
            chainTargetUrl,
            audience,
            authMode,
            cancellationToken);
        if (prepareConnection)
        {
            context.Out.WriteLine($"Prepared Foundry connection '{connectionName}' for {chainTargetUrl}.");
            context.Out.WriteLine(
                "No agent definition was changed. Register the returned OAuth callback on the existing " +
                "backend before attaching with --reuse-connection. Native user consent and an observed " +
                "specialist callback are still required.");
            return 0;
        }
        var replacedConnectionId = replacedConnectionName is null
            ? null
            : $"{connectionId[..(connectionId.LastIndexOf('/') + 1)]}{replacedConnectionName}";
        if (replacedConnectionId is not null)
        {
            await ValidateReplacementTargetAsync(
                context.HttpClient, armToken, replacedConnectionId, targetAgentId, cancellationToken);
        }
        var agentUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            "?api-version=v1";
        using var existingAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Get,
            agentUrl,
            body: null,
            cancellationToken);
        var latest = GetLatestVersion(existingAgent.RootElement);
        var existingConnectionIds = await ListProjectConnectionIdsAsync(
            context.HttpClient,
            armToken,
            subscriptionId,
            resourceGroup,
            accountName,
            projectName,
            cancellationToken);
        var definition = BuildChainDefinition(
            latest.GetProperty("definition"),
            connectionId,
            targetAgentId,
            targetAgentName,
            existingConnectionIds,
            targetUrl: chainTargetUrl,
            replacedConnectionId: replacedConnectionId);
        var body = new JsonObject
        {
            ["description"] = latest.TryGetProperty("description", out var description)
                ? description.GetString() ?? string.Empty
                : string.Empty,
            ["definition"] = definition
        };
        if (latest.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            body["metadata"] = JsonNode.Parse(metadata.GetRawText());
        }

        using var updatedAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Post,
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            "/versions?api-version=v1",
            body.ToJsonString(),
            cancellationToken);
        ValidateConfiguredChain(updatedAgent.RootElement, connectionId);

        var smokePrompt = arguments.Optional("smoke-prompt");
        if (smokePrompt is not null)
        {
            await SmokeTestAsync(
                context,
                address,
                $"Delegate this request to {targetAgentName} through A2A: {smokePrompt}",
                cancellationToken);
        }

        var version = updatedAgent.RootElement.TryGetProperty("version", out var versionElement)
            ? versionElement.ToString()
            : "(unknown)";
        context.Out.WriteLine(
            $"Configured '{address.AgentName}' version {version} to call '{targetAgentName}' via A2A.");
        context.Out.WriteLine($"Remote A2A target: {chainTargetUrl}");
        if (replacedConnectionName is not null)
        {
            context.Out.WriteLine(
                $"Replaced this agent's '{replacedConnectionName}' tool reference; " +
                "the shared project connection was not deleted.");
        }
        return 0;
    }

    internal static string BuildChainBaseUrl(string adapterUrl, string targetAgentId) =>
        $"{adapterUrl.TrimEnd('/')}/a2a-agents/{Uri.EscapeDataString(targetAgentId)}/";

    private static async Task SmokeTestAsync(
        CliContext context,
        FoundryAgentAddress address,
        string prompt,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = $"smoke-{suffix}",
            method = "SendMessage",
            @params = new
            {
                message = new
                {
                    role = "ROLE_USER",
                    parts = new[] { new { text = prompt } },
                    messageId = $"message-{suffix}",
                    contextId = $"context-{suffix}"
                }
            }
        });
        var runtimeUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            "/endpoint/protocols/a2a";
        using var response = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Post,
            runtimeUrl,
            body,
            cancellationToken,
            new Dictionary<string, string> { ["A2A-Version"] = "1.0" });

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            throw new CliException($"Foundry A2A smoke test failed: {error.GetRawText()}");
        }

        if (!response.RootElement.TryGetProperty("result", out _))
        {
            throw new CliException(
                "Foundry A2A smoke test returned neither a result nor a JSON-RPC error.");
        }

        context.Out.WriteLine("Foundry A2A smoke test passed.");
    }
}
