namespace FoundryCopilotA2A.Cli;

internal sealed record CliContext(
    TextWriter Out,
    TextWriter Error,
    ProcessRunner Processes,
    HttpClient HttpClient);

internal sealed class CliApplication(CliContext context)
{
    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length == 0 ||
            arguments[0] is "-h" or "--help" or "help")
        {
            WriteHelp();
            return 0;
        }

        try
        {
            var command = arguments[0].ToLowerInvariant();
            var commandArguments = CommandArguments.Parse(arguments.Skip(1));

            if (commandArguments.Has("help"))
            {
                WriteCommandHelp(command);
                return 0;
            }

            return command switch
            {
                "register-app" => await EntraCommands.RegisterAppAsync(
                    context, commandArguments, cancellationToken),
                "register-spa" => await EntraCommands.RegisterSpaAsync(
                    context, commandArguments, cancellationToken),
                "delete-app" => await EntraCommands.DeleteAppAsync(
                    context, commandArguments, cancellationToken),
                "consent" => await EntraCommands.ConsentAsync(
                    context, commandArguments, cancellationToken),
                "run-adapter" => await RuntimeCommands.RunAdapterAsync(
                    context, commandArguments, cancellationToken),
                "run-mock" => await RuntimeCommands.RunMockAsync(
                    context, commandArguments, cancellationToken),
                "start-tunnel" => await RuntimeCommands.StartTunnelAsync(
                    context, commandArguments, cancellationToken),
                "test-adapter" => await SmokeTestCommands.TestAdapterAsync(
                    context, commandArguments, cancellationToken),
                "test-foundry" => await SmokeTestCommands.TestFoundryAsync(
                    context, commandArguments, cancellationToken),
                "enable-foundry-a2a" => await FoundryCommands.EnableA2AAsync(
                    context, commandArguments, cancellationToken),
                "configure-foundry-chain" => await FoundryCommands.ConfigureChainAsync(
                    context, commandArguments, cancellationToken),
                _ => throw new CliException(
                    $"Unknown command '{command}'. Run with --help to list commands.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.Error.WriteLine("Canceled.");
            return 130;
        }
        catch (OperationCanceledException)
        {
            context.Error.WriteLine("Error: The operation timed out.");
            return 1;
        }
        catch (CliException exception)
        {
            context.Error.WriteLine($"Error: {exception.Message}");
            return 2;
        }
        catch (ExternalCommandException exception)
        {
            context.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
        catch (HttpRequestException exception)
        {
            context.Error.WriteLine($"HTTP error: {exception.Message}");
            return 1;
        }
    }

    private void WriteHelp()
    {
        context.Out.WriteLine(
            """
            Foundry Copilot A2A CLI

            Usage:
              dotnet run --project src/FoundryCopilotA2A.Cli -- <command> [options]

            Commands:
              register-app  Create the backend API app used by the adapter and OBO flow.
              register-spa  Create a frontend SPA app with delegated access to the backend API.
              consent       Grant CopilotStudio.Copilots.Invoke for the signed-in user.
              run-adapter   Start the adapter against a live Copilot Studio connection URL.
              run-mock      Start the adapter with its local mock backend.
              test-adapter  Exercise an adapter directly over A2A.
              start-tunnel  Expose the local adapter through an anonymous Dev Tunnel.
              test-foundry  Connect Foundry to an adapter and run the cloud smoke test.
              enable-foundry-a2a
                            Expose an existing Foundry prompt agent through incoming A2A.
              configure-foundry-chain
                            Add an authenticated Copilot Studio A2A tool to a Foundry prompt agent.
              delete-app    Delete a temporary adapter app registration.

            Run '<command> --help' for command-specific options.
            """);
    }

    private void WriteCommandHelp(string command)
    {
        var help = command switch
        {
            "register-app" =>
                """
                register-app [--display-name <name>] [--preauthorize-azure-cli] [--admin-consent]

                Creates the single-tenant backend API app in the tenant selected by `az login`,
                exposes access_as_user, adds CopilotStudio.Copilots.Invoke, enables the optional
                device-code consent workflow, and creates a one-year OBO client secret.
                """,
            "register-spa" =>
                """
                register-spa --api-client-id <backend-application-id>
                             [--display-name <name>] [--redirect-uri <url>] [--admin-consent]

                Creates a secretless single-tenant SPA app and grants it delegated access to the
                backend API's access_as_user scope. The redirect URI defaults to
                http://localhost:5173. Admin consent is opt-in.
                """,
            "delete-app" =>
                """
                delete-app --client-id <application-id>

                Deletes the app registration and its service principal through Azure CLI.
                """,
            "consent" =>
                """
                consent --tenant-id <tenant-id> --client-id <application-id>
                        [--scope <space-separated-scopes>]

                Runs MSAL device-code authentication and records a per-user delegated grant.
                The backend app must have public client flow enabled; register-app configures it.
                """,
            "run-adapter" =>
                """
                run-adapter --tenant-id <tenant-id> --client-id <application-id>
                            --direct-connect-url <Copilot-Studio-connection-string>
                            [--client-secret-env <name>] [--urls <url>]
                            [--public-base-url <url>] [--allowed-origin <origin>]
                            [--adapter-project <path>]

                Reads the client secret from COPILOT_STUDIO_CLIENT_SECRET by default. It is
                intentionally not accepted as an argument, because command lines are observable.
                """,
            "test-adapter" =>
                """
                test-adapter [--base-url <url>] [--expected-output-pattern <regex>]
                             [--tenant-id <tenant> --client-id <application-id>]
                             [--bearer-token-env <name>]

                Checks the agent card and sends one A2A 1.0 message. For an authenticated adapter,
                tenant plus client id acquires an access_as_user token by device code. An existing
                bearer token can instead be read from a named environment variable.
                """,
            "run-mock" =>
                """
                run-mock [--urls <url>] [--public-base-url <url>]
                         [--allowed-origin <origin>] [--adapter-project <path>]

                Starts the mock backend in explicit anonymous-development mode. This mode cannot
                be enabled accidentally for the live Copilot Studio backend.
                """,
            "start-tunnel" =>
                """
                start-tunnel [--port <port>]

                Runs `devtunnel host --allow-anonymous` for the local adapter port.
                """,
            "test-foundry" =>
                """
                test-foundry --adapter-url <url> --project-endpoint <url>
                             --resource-group <name> --account-name <name>
                             --project-name <name> --model-deployment <name>
                             [--connection-name <name>] [--agent-name <name>]
                             [--expected-output-pattern <regex>] [--prompt <text>]

                Creates or updates an unauthenticated remote-A2A connection and prompt agent,
                invokes it, and requires matching A2A tool output.
                """,
            "enable-foundry-a2a" =>
                """
                enable-foundry-a2a --agent-url <Foundry-agent-or-responses-url>
                                   --description <text> --skill-id <id>
                                   --skill-name <name> --skill-description <text>
                                   [--card-version <version>] [--replace-card]
                                   [--smoke-prompt <text>]

                Enables the responses and A2A protocols on an existing Foundry prompt agent,
                publishes the explicitly supplied agent card, and verifies its discovery URL.
                Existing cards are preserved unless --replace-card is specified. A smoke prompt
                also verifies a live A2A JSON-RPC 1.0 call.
                """,
            "configure-foundry-chain" =>
                """
                configure-foundry-chain --agent-url <Foundry-agent-url>
                                        --adapter-url <public-adapter-url>
                                        --audience <adapter-application-id-uri>
                                        --tenant-id <tenant-id>
                                        --subscription-id <subscription-id>
                                        --resource-group <name> --account-name <name>
                                        --project-name <name> --target-agent-id <id>
                                        --target-agent-name <display-name>
                                        [--auth-mode <oauth|user-entra-token|project-managed-identity>]
                                        [--oauth-client-id <application-id>]
                                        [--oauth-client-secret-env <name>]
                                        [--reuse-connection]
                                        [--connection-name <name>] [--smoke-prompt <text>]

                Creates an authenticated remote-A2A connection to one target-specific adapter
                route, then publishes a new version of the existing Foundry prompt agent. The
                current Azure Developer CLI login bootstraps projects with no existing connections.
                Model, instructions, description, and metadata are preserved. A2A tools whose
                project connection no longer exists are pruned so a replaced connection does not
                leave a dangling reference.
                OAuth identity passthrough is the default and requires --oauth-client-id. The
                client secret is read from FOUNDRY_A2A_OAUTH_CLIENT_SECRET by default. The command
                prints Foundry's redirect URL, which must be registered before user authorization.
                OAuth needs a connector gateway and interactive consent; when that infrastructure
                is unavailable the tool call fails inside Foundry with "Received 500 from a service
                request" and no consent link appears.
                project-managed-identity avoids the connector gateway and consent entirely by
                calling the target as the project identity, at the cost of end-user identity
                passthrough. UserEntraToken remains available for supported managed Microsoft
                services.
                --reuse-connection attaches an existing named project connection without reading
                or replacing its credentials.
                """,
            _ => $"Unknown command '{command}'."
        };

        context.Out.WriteLine(help);
    }
}
