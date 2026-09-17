using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Cli;

internal static class InteractiveAuth
{
    public static async Task<AuthenticationResult> AcquireAsync(
        CliContext context,
        string tenantId,
        string clientId,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        var application = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        context.Out.WriteLine("Opening the system browser for administrator sign-in...");
        try
        {
            return await application
                .AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalException exception)
        {
            throw new CliException(
                $"Interactive authentication failed ({exception.ErrorCode}): {exception.Message}");
        }
    }
}
