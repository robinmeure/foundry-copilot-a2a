namespace FoundryCopilotA2A.Adapter;

public interface ICopilotStudioInvoker
{
    IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken);
}
