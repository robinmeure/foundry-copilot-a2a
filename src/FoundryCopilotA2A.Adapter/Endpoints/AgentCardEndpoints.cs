using A2A;
using A2A.V0_3Compat;


namespace FoundryCopilotA2A.Adapter;

internal static class AgentCardEndpoints
{
    public static void MapAgentCards(
        this WebApplication app,
        AdapterOptions adapterOptions,
        AuthenticationOptions authenticationOptions)
    {
        var publicBaseUrl = adapterOptions.PublicBaseUrl.TrimEnd('/');
        // Foundry and Copilot Studio use different discovery conventions. Every alias must remain
        // specialist-bound instead of falling back to the generic router card.
        // Authoring portals fetch these public bootstrap documents from the browser before OAuth.
        var agentCardEndpoints = app.MapGroup("").RequireCors("agent-card");
        foreach (var cardFile in new[] { "agent-card.json", "agent.json" })
        {
            agentCardEndpoints.MapGet(
                $"{AdapterConstants.ChainAgentsPath}/{{agentId}}/.well-known/{cardFile}",
                (string agentId, AgentCatalog catalog) => BuildChainAgentCard(agentId, catalog, adapterOptions, authenticationOptions));
            agentCardEndpoints.MapGet(
                $"{AdapterConstants.ChainAgentsPath}/{{agentId}}/a2a/.well-known/{cardFile}",
                (string agentId, AgentCatalog catalog) => BuildChainAgentCard(agentId, catalog, adapterOptions, authenticationOptions));
        }

        var runtimeUrl = $"{publicBaseUrl}{AdapterConstants.RuntimePath}";
        var agentCard = new AgentCard
        {
            Name = "Specialist Agent Router",
            Description = "Delegates requests to the selected Copilot Studio or Foundry agent.",
            Version = "0.1.0",
            DefaultInputModes = [.. A2AInputFiles.MediaTypes],
            DefaultOutputModes = ["text/plain", "application/json"],
            Capabilities = new AgentCapabilities
            {
                Streaming = true,
                PushNotifications = false,
                Extensions =
                [
                    new AgentExtension
                    {
                        Uri = AgentCitations.ExtensionUri,
                        Description = AgentCitations.Description,
                        Required = false
                    },
                    new AgentExtension { Uri = AgentPlanUpdates.ExtensionUri, Description = AgentPlanUpdates.Description, Required = false },
                    .. adapterOptions.AdaptiveCardsEnabled
                        ? new[]
                        {
                            new AgentExtension
                            {
                                Uri = AgentAdaptiveCards.ExtensionUri,
                                Description = AgentAdaptiveCards.Description,
                                Required = false
                            }
                        }
                        : Array.Empty<AgentExtension>()
                ]
            },
            SupportedInterfaces =
            [
                new AgentInterface
                {
                    Url = runtimeUrl,
                    ProtocolBinding = "JSONRPC",
                    ProtocolVersion = "1.0"
                }
            ],
            Skills =
            [
                new A2A.AgentSkill
                {
                    Id = "specialist-agent",
                    Name = "Specialist agent",
                    Description = "Uses the selected Copilot Studio or Foundry agent to answer a request.",
                    Tags = ["copilot-studio", "foundry", "specialist"],
                    Examples = ["Ask the selected specialist agent to handle this request."]
                }
            ]
        };
        var routerCardEndpoints = agentCardEndpoints.MapGroup("");
        routerCardEndpoints.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            // The preview SDK's v0.3 card converter omits extensions in both strict and blended cards.
            if (result is IValueHttpResult { Value: A2A.V0_3.AgentCard legacyCard })
            {
                legacyCard.Capabilities.Extensions =
                [
                    new A2A.V0_3.AgentExtension
                    {
                        Uri = AgentCitations.ExtensionUri,
                        Description = AgentCitations.Description,
                        Required = false
                    },
                    new A2A.V0_3.AgentExtension { Uri = AgentPlanUpdates.ExtensionUri, Description = AgentPlanUpdates.Description, Required = false },
                    .. adapterOptions.AdaptiveCardsEnabled
                        ? new[]
                        {
                            new A2A.V0_3.AgentExtension
                            {
                                Uri = AgentAdaptiveCards.ExtensionUri,
                                Description = AgentAdaptiveCards.Description,
                                Required = false
                            }
                        }
                        : Array.Empty<A2A.V0_3.AgentExtension>()
                ];
            }
            else if (result is IValueHttpResult { Value: System.Text.Json.Nodes.JsonObject blendedCard } &&
                     blendedCard["capabilities"] is System.Text.Json.Nodes.JsonObject capabilities)
            {
                capabilities["extensions"] = System.Text.Json.JsonSerializer.SerializeToNode(
                    agentCard.Capabilities.Extensions,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            return result;
        });
        routerCardEndpoints.MapAgentCardGetWithV03Compat(() => Task.FromResult(agentCard));
    }

    private static IResult BuildChainAgentCard(
        string agentId,
        AgentCatalog catalog,
        AdapterOptions adapterOptions,
        AuthenticationOptions authenticationOptions)
    {
        var publicBaseUrl = adapterOptions.PublicBaseUrl.TrimEnd('/');
        AgentDescriptor agent;
        try
        {
            agent = catalog.ResolveAgent(agentId);
        }
        catch (AdapterRequestException)
        {
            return Results.NotFound();
        }

        if (agent.ProviderKind is not (
            FoundryCopilotA2A.Adapter.AgentProvider.CopilotStudio or
            FoundryCopilotA2A.Adapter.AgentProvider.Foundry))
        {
            return Results.NotFound();
        }

        var chainRuntimeUrl =
            $"{publicBaseUrl}{AdapterConstants.ChainAgentRuntimePath(agent.Id)}";

        // When the runtime is protected, the card must advertise how to authenticate. A card that
        // omits securitySchemes tells a remote caller the endpoint is anonymous, so it never attaches
        // a credential and never prompts the user to consent.
        object? securitySchemes = null;
        object[]? security = null;
        if (authenticationOptions.Enabled &&
            !string.IsNullOrWhiteSpace(authenticationOptions.Authority) &&
            !string.IsNullOrWhiteSpace(authenticationOptions.Audience))
        {
            // Authority is the OIDC issuer form (".../{tenant}/v2.0"), but the authorize/token
            // endpoints hang off the tenant root, so strip the version segment before composing them.
            var authority = authenticationOptions.Authority.TrimEnd('/');
            if (authority.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
            {
                authority = authority[..^"/v2.0".Length];
            }

            var scope = $"{authenticationOptions.Audience.TrimEnd('/')}/access_as_user";
            securitySchemes = new Dictionary<string, object>
            {
                ["entra"] = new
                {
                    type = "oauth2",
                    description = "Microsoft Entra ID delegated access to the adapter API.",
                    flows = new
                    {
                        authorizationCode = new
                        {
                            authorizationUrl = $"{authority}/oauth2/v2.0/authorize",
                            tokenUrl = $"{authority}/oauth2/v2.0/token",
                            refreshUrl = $"{authority}/oauth2/v2.0/token",
                            scopes = new Dictionary<string, string>
                            {
                                [scope] = "Invoke the adapter on behalf of the signed-in user."
                            }
                        }
                    }
                }
            };
            security = [new Dictionary<string, string[]> { ["entra"] = [scope] }];
        }

        return Results.Ok(new
        {
            name = agent.DisplayName,
            description = $"{agent.DisplayName} exposed through the A2A adapter.",
            url = chainRuntimeUrl,
            version = "0.1.0",
            // Copilot Studio rejects a card containing v1 supportedInterfaces, even with this version.
            protocolVersion = "0.3.0",
            capabilities = new
            {
                streaming = true,
                pushNotifications = false,
                stateTransitionHistory = false,
                extensions = adapterOptions.AdaptiveCardsEnabled
                    ? new[]
                    {
                        new { uri = AgentCitations.ExtensionUri, description = AgentCitations.Description, required = false },
                        new { uri = AgentPlanUpdates.ExtensionUri, description = AgentPlanUpdates.Description, required = false },
                        new { uri = AgentAdaptiveCards.ExtensionUri, description = AgentAdaptiveCards.Description, required = false }
                    }
                    : new[]
                    {
                        new { uri = AgentCitations.ExtensionUri, description = AgentCitations.Description, required = false },
                        new { uri = AgentPlanUpdates.ExtensionUri, description = AgentPlanUpdates.Description, required = false }
                    }
            },
            securitySchemes,
            security,
            defaultInputModes = agent.ProviderKind == FoundryCopilotA2A.Adapter.AgentProvider.CopilotStudio
                ? A2AInputFiles.MediaTypes : new[] { "text/plain" },
            defaultOutputModes = new[] { "text/plain", "application/json" },
            skills = new[]
            {
                new
                {
                    id = $"{agent.Provider}-{agent.Id}",
                    name = agent.DisplayName,
                    description = $"Delegate a request to {agent.DisplayName}.",
                    tags = new[] { agent.Provider, "specialist" },
                    examples = new[] { $"Ask {agent.DisplayName} to handle this request." }
                }
            },
            supportsAuthenticatedExtendedCard = false,
            additionalInterfaces = Array.Empty<object>(),
            preferredTransport = "JSONRPC"
        });
    }
}
