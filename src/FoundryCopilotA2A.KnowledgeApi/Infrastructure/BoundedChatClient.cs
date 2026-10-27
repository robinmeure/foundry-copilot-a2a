using System.ClientModel;
using System.Runtime.CompilerServices;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace FoundryCopilotA2A.KnowledgeApi.Infrastructure;

internal sealed class BoundedChatClient(
    IChatClient innerClient,
    IReadOnlySet<string> allowedTools,
    Action checkToolFailure) : DelegatingChatClient(innerClient)
{
    private readonly HashSet<string> callIds = new(StringComparer.Ordinal);
    private int rounds;

    public override async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        StartRound();

        Microsoft.Extensions.AI.ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ClientResultException exception)
        {
            throw ModelServiceError(exception);
        }

        EnsureFinished(response.FinishReason);
#pragma warning disable OPENAI001
        if (response.RawRepresentation is ResponseResult result && result.Status != ResponseStatus.Completed)
        {
            throw new ApiException(502, "incomplete_response", "The model did not complete its response.");
        }
#pragma warning restore OPENAI001
        var calls = response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>().ToArray();
        if (calls.Length > 0 &&
            (rounds == ResponsesCoordinator.MaxRounds ||
             callIds.Count + calls.Length > ResponsesCoordinator.MaxToolCalls))
        {
            throw BudgetExceeded();
        }

        foreach (var call in calls)
        {
            AcceptCall(call);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StartRound();

        IAsyncEnumerator<ChatResponseUpdate> updates;
        try
        {
            updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (ClientResultException exception)
        {
            throw ModelServiceError(exception);
        }

        await using (updates)
        {
            while (await MoveNextAsync(updates))
            {
                var update = updates.Current;
                EnsureFinished(update.FinishReason);
#pragma warning disable OPENAI001
                if (update.RawRepresentation is StreamingResponseIncompleteUpdate or StreamingResponseFailedUpdate)
                {
                    throw new ApiException(502, "incomplete_response", "The model did not complete its response.");
                }
#pragma warning restore OPENAI001
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                {
                    if (rounds == ResponsesCoordinator.MaxRounds ||
                        callIds.Count + 1 > ResponsesCoordinator.MaxToolCalls)
                    {
                        throw BudgetExceeded();
                    }

                    AcceptCall(call);
                }

                yield return update;
            }
        }
    }

    private void StartRound()
    {
        checkToolFailure();
        if (++rounds > ResponsesCoordinator.MaxRounds)
        {
            throw BudgetExceeded();
        }
    }

    private void AcceptCall(FunctionCallContent call)
    {
        if (string.IsNullOrWhiteSpace(call.CallId) ||
            !callIds.Add(call.CallId) || !allowedTools.Contains(call.Name))
        {
            throw new ApiException(502, "invalid_model_tool",
                "The model requested a disabled tool or repeated a tool call ID.");
        }
    }

    private static void EnsureFinished(ChatFinishReason? reason)
    {
        if (reason is { } value && value != ChatFinishReason.Stop && value != ChatFinishReason.ToolCalls)
        {
            throw new ApiException(502, "incomplete_response", "The model did not complete its response.");
        }
    }

    private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> updates)
    {
        try
        {
            return await updates.MoveNextAsync();
        }
        catch (ClientResultException exception)
        {
            throw ModelServiceError(exception);
        }
    }

    private static ApiException ModelServiceError(ClientResultException exception) => new(
        exception.Status == 429 ? 429 : 502,
        "model_service_error",
        $"The model service returned HTTP {exception.Status}. Check deployment, RBAC and quota.");

    private static ApiException BudgetExceeded() => new(
        502, "graph_tool_budget",
        "The model exceeded the bounded knowledge tool budget. Try a narrower question.");
}
