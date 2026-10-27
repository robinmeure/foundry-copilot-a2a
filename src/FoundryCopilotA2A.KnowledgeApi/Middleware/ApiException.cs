namespace FoundryCopilotA2A.KnowledgeApi.Middleware;

/// <summary>Stable API error with an HTTP status, machine-readable code, and client-safe message.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
