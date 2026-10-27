using System.Security.Cryptography;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.AspNetCore.DataProtection;

namespace FoundryCopilotA2A.KnowledgeApi.Authentication;

public sealed class ContinuationTokens(IDataProtectionProvider provider)
{
    private const string ProtectorPurpose = "Knowledge.Graph.Pagination.v1";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly IDataProtector protector = provider.CreateProtector(ProtectorPurpose);

    private sealed record ProtectedPaginationToken(
        string Tenant,
        string User,
        string Browse,
        string Path,
        DateTimeOffset Expires);

    public string Write(Caller caller, string browse, string path)
    {
        var token = new ProtectedPaginationToken(
            Tenant: caller.Tenant,
            User: caller.User,
            Browse: browse,
            Path: path,
            Expires: DateTimeOffset.UtcNow.Add(TokenLifetime));

        var serialized = JsonSerializer.Serialize(token);
        return protector.Protect(serialized);
    }

    public string Read(string input, Caller caller, string browse)
    {
        try
        {
            var unprotectedJson = protector.Unprotect(input);
            var token = JsonSerializer.Deserialize<ProtectedPaginationToken>(unprotectedJson);

            var isInvalidOrExpired =
                token is null ||
                token.Tenant != caller.Tenant ||
                token.User != caller.User ||
                token.Browse != browse ||
                token.Expires < DateTimeOffset.UtcNow;

            if (isInvalidOrExpired)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "invalid_continuation",
                    "This picker page expired or belongs to a different query.");
            }

            return token!.Path;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_continuation",
                "Invalid picker continuation.");
        }
    }
}
