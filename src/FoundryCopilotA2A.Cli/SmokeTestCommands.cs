using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.Cli;

internal static partial class SmokeTestCommands
{
    public static async Task<int> TestAdapterAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "base-url",
            "expected-output-pattern",
            "bearer-token-env",
            "tenant-id",
            "client-id",
            "help");
        var baseUrl = arguments
            .AbsoluteHttpUri("base-url", "http://localhost:5099")
            .AbsoluteUri.TrimEnd('/');
        var expectedPattern = arguments.Optional(
            "expected-output-pattern", "mock-copilot-studio")!;

        await ValidateAgentCardAsync(context, baseUrl, cancellationToken);

        var suffix = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = $"test-{suffix}",
            method = "SendMessage",
            @params = new
            {
                message = new
                {
                    role = "ROLE_USER",
                    parts = new[] { new { text = "hello from the A2A repro" } },
                    messageId = $"message-{suffix}",
                    contextId = $"context-{suffix}"
                }
            }
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/a2a/copilot-studio");
        request.Headers.Add("A2A-Version", "1.0");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var bearerTokenEnvironment = arguments.Optional("bearer-token-env");
        var tenantId = arguments.Optional("tenant-id");
        var clientId = arguments.Optional("client-id");
        if ((tenantId is null) != (clientId is null))
        {
            throw new CliException(
                "--tenant-id and --client-id must be supplied together.");
        }

        if (bearerTokenEnvironment is not null && tenantId is not null)
        {
            throw new CliException(
                "Use either --bearer-token-env or --tenant-id with --client-id, not both.");
        }

        if (tenantId is not null && clientId is not null)
        {
            var authentication = await DeviceCodeAuth.AcquireAsync(
                context,
                tenantId,
                clientId,
                [$"api://{clientId}/access_as_user"],
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", authentication.AccessToken);
        }
        else if (bearerTokenEnvironment is not null)
        {
            var token = Environment.GetEnvironmentVariable(bearerTokenEnvironment);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new CliException(
                    $"Environment variable '{bearerTokenEnvironment}' does not contain a bearer token.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await context.HttpClient.SendAsync(
            request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(
                $"Adapter returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        if (!IsMatch(responseBody, expectedPattern))
        {
            throw new CliException(
                $"Adapter response did not match '{expectedPattern}'. Response: {responseBody}");
        }

        context.Out.WriteLine("A2A adapter smoke test passed.");
        context.Out.WriteLine(responseBody);
        return 0;
    }

    public static async Task<int> TestFoundryAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "adapter-url",
            "project-endpoint",
            "resource-group",
            "account-name",
            "project-name",
            "model-deployment",
            "connection-name",
            "agent-name",
            "expected-output-pattern",
            "prompt",
            "help");

        var adapterUrl = arguments
            .AbsoluteHttpUri("adapter-url")
            .AbsoluteUri.TrimEnd('/');
        var projectEndpoint = arguments
            .AbsoluteHttpUri("project-endpoint")
            .AbsoluteUri.TrimEnd('/');
        var resourceGroup = arguments.Require("resource-group");
        var accountName = arguments.Require("account-name");
        var projectName = arguments.Require("project-name");
        var modelDeployment = arguments.Require("model-deployment");
        var connectionName = arguments.Optional(
            "connection-name", "copilot-a2a-repro-tunnel")!;
        var agentName = arguments.Optional(
            "agent-name", "foundry-copilot-a2a-repro")!;
        var expectedPattern = arguments.Optional(
            "expected-output-pattern", "mock-copilot-studio")!;
        var prompt = arguments.Optional(
            "prompt",
            "Delegate this exact request to the specialist: say hello from Foundry.")!;

        await ValidateAgentCardAsync(context, adapterUrl, cancellationToken);

        context.Out.WriteLine(
            $"Creating or updating Foundry connection '{connectionName}'...");
        await context.Processes.CaptureAsync(
            "azd",
            [
                "ai", "connection", "create", connectionName,
                "--project-endpoint", projectEndpoint,
                "--kind", "remote-a2a",
                "--target", adapterUrl,
                "--auth-type", "none",
                "--force",
                "--no-prompt"
            ],
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["AZURE_DEV_USER_AGENT"] = "microsoft_foundry_skill"
            });

        var subscriptionId = (await context.Processes.CaptureAsync(
            "az",
            ["account", "show", "--query", "id", "--output", "tsv"],
            cancellationToken))
            .StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new CliException("Unable to resolve the active Azure subscription.");
        }

        var connectionId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/" +
            $"Microsoft.CognitiveServices/accounts/{accountName}/projects/{projectName}/" +
            $"connections/{connectionName}";

        var agentBody = JsonSerializer.Serialize(new
        {
            description = "Repro orchestrator for the Copilot Studio A2A adapter",
            definition = new
            {
                kind = "prompt",
                model = modelDeployment,
                instructions =
                    "You are a test orchestrator. Always call the Copilot Studio specialist " +
                    "A2A tool for every user request, then return its answer.",
                tools = new[]
                {
                    new
                    {
                        type = "a2a_preview",
                        project_connection_id = connectionId
                    }
                }
            }
        });

        var agent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Post,
            $"{projectEndpoint}/agents/{agentName}/versions?api-version=v1",
            agentBody,
            cancellationToken);

        var invokeBody = JsonSerializer.Serialize(new
        {
            input = prompt,
            tool_choice = "required",
            stream = false
        });
        var invocation = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Post,
            $"{projectEndpoint}/agents/{agentName}/endpoint/protocols/openai/responses?api-version=v1",
            invokeBody,
            cancellationToken);

        var a2aOutput = ExtractA2AOutput(invocation.RootElement);
        if (string.IsNullOrWhiteSpace(a2aOutput) ||
            !IsMatch(a2aOutput, expectedPattern))
        {
            throw new CliException(
                $"Foundry did not return A2A output matching '{expectedPattern}'.");
        }

        var returnedName = agent.RootElement.TryGetProperty("name", out var name)
            ? name.GetString()
            : agentName;
        var version = agent.RootElement.TryGetProperty("version", out var agentVersion)
            ? agentVersion.ToString()
            : "(unknown)";

        context.Out.WriteLine("Foundry A2A smoke test passed.");
        context.Out.WriteLine($"Agent: {returnedName} version {version}");
        context.Out.WriteLine($"A2A output: {a2aOutput}");
        return 0;
    }

    private static async Task ValidateAgentCardAsync(
        CliContext context,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        using var response = await context.HttpClient.GetAsync(
            $"{baseUrl}/.well-known/agent-card.json", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(
                $"Agent card returned HTTP {(int)response.StatusCode}: {body}");
        }

        JsonDocument card;
        try
        {
            card = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new CliException($"Agent card returned invalid JSON: {exception.Message}");
        }

        using (card)
        {
            if (!card.RootElement.TryGetProperty("name", out var name) ||
                name.GetString() != "Specialist Agent Router")
            {
                throw new CliException($"Unexpected agent card at {baseUrl}.");
            }
        }
    }

    private static string? ExtractA2AOutput(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var output in outputs.EnumerateArray())
        {
            if (output.TryGetProperty("type", out var type) &&
                type.GetString() == "a2a_preview_call_output" &&
                output.TryGetProperty("output", out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.GetRawText();
            }
        }

        return null;
    }

    private static bool IsMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new CliException($"Invalid regular expression '{pattern}': {exception.Message}");
        }
    }
}
