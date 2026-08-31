namespace FoundryCopilotA2A.Cli;

internal static class RuntimeCommands
{
    public static async Task<int> RunMockAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "urls", "public-base-url", "allowed-origin", "adapter-project", "help");
        var urls = arguments.Optional("urls", "http://localhost:5099")!;
        var publicBaseUrl = arguments.Optional("public-base-url", urls)!;
        var allowedOrigin = arguments.Optional("allowed-origin", "http://localhost:5173")!;
        if (publicBaseUrl.Contains(';', StringComparison.Ordinal))
        {
            throw new CliException(
                "--public-base-url must be one URL, even when --urls contains multiple bindings.");
        }

        var adapterProject = RepoPaths.ResolveAdapterProject(
            arguments.Optional("adapter-project"));
        var environment = new Dictionary<string, string?>
        {
            ["Adapter__Backend"] = "Mock",
            ["Adapter__PublicBaseUrl"] = publicBaseUrl,
            ["Adapter__AllowedOrigins__0"] = allowedOrigin,
            ["Adapter__AllowAnonymousDevelopmentMode"] = "true",
            ["Authentication__Enabled"] = "false",
            ["ASPNETCORE_URLS"] = urls
        };

        context.Out.WriteLine($"Starting mock adapter at {urls}...");
        await context.Processes.RunInteractiveAsync(
            "dotnet",
            ["run", "--project", adapterProject, "--no-launch-profile", "--urls", urls],
            cancellationToken,
            environment,
            Path.GetDirectoryName(adapterProject));
        return 0;
    }

    public static async Task<int> RunAdapterAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "tenant-id",
            "client-id",
            "direct-connect-url",
            "client-secret-env",
            "urls",
            "public-base-url",
            "allowed-origin",
            "adapter-project",
            "help");

        var tenantId = arguments.Require("tenant-id");
        var clientId = arguments.Require("client-id");
        var directConnectUrl = arguments.AbsoluteHttpUri("direct-connect-url");
        if (directConnectUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new CliException("Copilot Studio direct connection URLs must use HTTPS.");
        }

        var clientSecretEnvironment = arguments.Optional(
            "client-secret-env", "COPILOT_STUDIO_CLIENT_SECRET")!;
        var clientSecret = Environment.GetEnvironmentVariable(clientSecretEnvironment);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new CliException(
                $"Environment variable '{clientSecretEnvironment}' does not contain the client secret.");
        }

        var urls = arguments.Optional("urls", "http://localhost:5099")!;
        var publicBaseUrl = arguments.Optional("public-base-url", urls)!;
        var allowedOrigin = arguments.Optional("allowed-origin", "http://localhost:5173")!;
        if (publicBaseUrl.Contains(';', StringComparison.Ordinal))
        {
            throw new CliException(
                "--public-base-url must be one URL, even when --urls contains multiple bindings.");
        }

        var adapterProject = RepoPaths.ResolveAdapterProject(
            arguments.Optional("adapter-project"));
        var environment = new Dictionary<string, string?>
        {
            ["Adapter__Backend"] = "CopilotStudio",
            ["Adapter__PublicBaseUrl"] = publicBaseUrl,
            ["Adapter__AllowedOrigins__0"] = allowedOrigin,
            ["Authentication__Enabled"] = "true",
            ["Authentication__Authority"] =
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
            ["Authentication__Audience"] = $"api://{clientId}",
            ["CopilotStudio__DirectConnectUrl"] = directConnectUrl.AbsoluteUri,
            ["CopilotStudio__TenantId"] = tenantId,
            ["CopilotStudio__ClientId"] = clientId,
            ["CopilotStudio__ClientSecret"] = clientSecret,
            ["CopilotStudio__Cloud"] = "Prod",
            ["ASPNETCORE_URLS"] = urls
        };

        context.Out.WriteLine($"Starting live adapter at {urls}...");
        await context.Processes.RunInteractiveAsync(
            "dotnet",
            ["run", "--project", adapterProject, "--no-launch-profile", "--urls", urls],
            cancellationToken,
            environment,
            Path.GetDirectoryName(adapterProject));
        return 0;
    }

    public static async Task<int> StartTunnelAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("port", "help");
        var port = arguments.Integer("port", 5099);

        context.Out.WriteLine($"Starting anonymous Dev Tunnel for port {port}...");
        await context.Processes.RunInteractiveAsync(
            "devtunnel",
            ["host", "-p", port.ToString(), "--allow-anonymous"],
            cancellationToken);
        return 0;
    }
}

internal static class RepoPaths
{
    private static readonly string RelativeAdapterProject = Path.Combine(
        "src",
        "FoundryCopilotA2A.Adapter",
        "FoundryCopilotA2A.Adapter.csproj");

    public static string ResolveAdapterProject(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            return File.Exists(fullPath)
                ? fullPath
                : throw new CliException($"Adapter project not found at '{fullPath}'.");
        }

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, RelativeAdapterProject);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        throw new CliException(
            "Could not find src/FoundryCopilotA2A.Adapter. Use --adapter-project explicitly.");
    }
}
