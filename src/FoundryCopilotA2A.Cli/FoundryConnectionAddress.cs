namespace FoundryCopilotA2A.Cli;

internal sealed record FoundryConnectionAddress(
    string SubscriptionId,
    string ResourceGroup,
    string AccountName,
    string ProjectName,
    string Name)
{
    public string ResourceId =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/" +
        $"Microsoft.CognitiveServices/accounts/{AccountName}/projects/{ProjectName}/connections/{Name}";

    public string ManagementUrl(string apiVersion)
    {
        return $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(SubscriptionId)}/" +
            $"resourceGroups/{Uri.EscapeDataString(ResourceGroup)}/providers/Microsoft.CognitiveServices/" +
            $"accounts/{Uri.EscapeDataString(AccountName)}/projects/{Uri.EscapeDataString(ProjectName)}/" +
            $"connections/{Uri.EscapeDataString(Name)}?api-version={apiVersion}";
    }
}
