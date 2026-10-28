using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// Issues and resolves client-held references to Copilot Studio conversations. A handle is
/// encrypted and authenticated by ASP.NET Core Data Protection and is bound to the caller,
/// agent, and A2A context, so it cannot be read, forged, or reused by another user or chat.
/// It lets a saved chat or a restarted adapter continue a conversation instead of replaying the
/// transcript into a new one.
/// </summary>
public sealed class CopilotConversationHandles
{
    /// <summary>Key under <c>params.message.metadata</c> carrying handles returned by earlier turns.</summary>
    public const string RequestMetadataKey = "copilotStudioConversations";

    /// <summary>Key in A2A answer part metadata carrying the handle for the current conversation.</summary>
    public const string PartMetadataKey = "copilotStudioConversation";

    public const int MaxHandlesPerRequest = 8;
    public const int MaxHandleLength = 2048;

    private const string Purpose = "FoundryCopilotA2A.CopilotStudioConversation.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public CopilotConversationHandles(
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _lifetime = TimeSpan.FromMinutes(configuration.GetValue("Adapter:ConversationHandleLifetimeMinutes", 480));
    }

    public string? Issue(A2ARequestMetadata metadata, string conversationId) =>
        metadata.ContextId is null
            ? null
            : _protector.Protect(
                JsonSerializer.Serialize(new HandlePayload(
                    metadata.UserId,
                    metadata.AgentId,
                    metadata.ContextId,
                    conversationId)),
                _lifetime);

    /// <summary>
    /// Returns the conversation of the first handle that is valid for this caller, agent, and
    /// context. Expired, tampered, or foreign handles are ignored.
    /// </summary>
    public string? Resolve(A2ARequestMetadata metadata)
    {
        if (metadata.ContextId is null)
        {
            return null;
        }

        foreach (var handle in metadata.ConversationHandles)
        {
            HandlePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<HandlePayload>(_protector.Unprotect(handle));
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException
                                                  or FormatException)
            {
                continue;
            }

            if (payload is not null &&
                !string.IsNullOrWhiteSpace(payload.ConversationId) &&
                string.Equals(payload.UserId, metadata.UserId, StringComparison.Ordinal) &&
                string.Equals(payload.AgentId, metadata.AgentId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(payload.ContextId, metadata.ContextId, StringComparison.Ordinal))
            {
                return payload.ConversationId;
            }
        }

        return null;
    }

    private sealed record HandlePayload(
        string UserId,
        string AgentId,
        string ContextId,
        string ConversationId);
}
