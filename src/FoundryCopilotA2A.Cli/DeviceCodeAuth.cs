using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Cli;

internal static class DeviceCodeAuth
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
            .Build();

        try
        {
            return await application
                .AcquireTokenWithDeviceCode(
                    scopes,
                    deviceCode =>
                    {
                        context.Out.WriteLine(deviceCode.Message);
                        return Task.CompletedTask;
                    })
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalException exception)
        {
            throw new CliException(
                $"Device-code authentication failed ({exception.ErrorCode}): {exception.Message}");
        }
    }
}
