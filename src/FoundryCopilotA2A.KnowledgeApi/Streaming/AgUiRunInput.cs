using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>
/// Translates an AG-UI <see cref="RunAgentInput"/> into the API's <see cref="ChatRequest"/>.
/// </summary>
/// <remarks>
/// The AG-UI <c>threadId</c> is the conversation ID, the last message must be the user's
/// question, and <see cref="ChatRunProperties"/> arrive in <c>forwardedProps</c>. Earlier
/// messages are ignored because the server keeps the authoritative conversation history.
/// Client-side tools, context and state are not used by this API and are ignored.
/// </remarks>
internal static class AgUiRunInput
{
    /// <summary>
    /// Maps the run to a chat request, throwing a 400 <see cref="ApiException"/> when the run
    /// cannot become a question. A null input means the body was not a valid AG-UI run
    /// (for example, it used content types the AG-UI serializer does not know).
    /// </summary>
    public static ChatRequest ToChatRequest(RunAgentInput? input, JsonSerializerOptions jsonOptions)
    {
        if (input is null)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Send an AG-UI RunAgentInput whose last message is the user's text question.");
        }

        var properties = ReadProperties(input.ForwardedProperties, jsonOptions);
        return new ChatRequest(
            ConversationId: input.ThreadId ?? "",
            Text: ReadQuestion(input.Messages),
            Sources: properties.Sources,
            TimeZone: properties.TimeZone,
            Agents: properties.Agents);
    }

    private static string ReadQuestion(IList<AGUIMessage>? messages)
    {
        if (messages is not [.., AGUIUserMessage question])
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_question",
                "End the run with the user's question.");
        }

        if (question.Content.IsText)
        {
            return question.Content.Value as string ?? "";
        }

        var text = new StringBuilder();
        foreach (var part in question.Content)
        {
            if (part is not AGUITextInputContent textPart)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "unsupported_content",
                    "Questions may contain text only. Upload documents separately.");
            }

            text.Append(textPart.Text);
        }

        return text.ToString();
    }

    private static ChatRunProperties ReadProperties(JsonElement? forwardedProps, JsonSerializerOptions jsonOptions)
    {
        if (forwardedProps is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            return new ChatRunProperties();
        }

        try
        {
            return value.ValueKind == JsonValueKind.Object
                ? value.Deserialize<ChatRunProperties>(jsonOptions) ?? new ChatRunProperties()
                : throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "forwardedProps must be an object with optional sources, agents and timeZone.");
        }
    }
}
