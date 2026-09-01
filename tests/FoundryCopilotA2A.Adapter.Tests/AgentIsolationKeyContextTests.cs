using Microsoft.Agents.AI.Hosting;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class AgentIsolationKeyContextTests
{
    [Fact]
    public async Task IsolationKeyFlowsToBackgroundWorkAfterRequestContextIsCleared()
    {
        var context = new AgentIsolationKeyContext
        {
            Current = "tenant|caller"
        };
        AgentIsolationKeyProvider provider = new RequestScopedAgentIsolationKeyProvider(context);
        var backgroundStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBackground = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var background = Task.Run(async () =>
        {
            backgroundStarted.SetResult();
            await continueBackground.Task;
            return await provider.GetIsolationKeyAsync();
        });

        await backgroundStarted.Task;
        context.Current = null;
        continueBackground.SetResult();

        Assert.Equal("tenant|caller", await background);
        Assert.Null(await provider.GetIsolationKeyAsync());
    }
}
