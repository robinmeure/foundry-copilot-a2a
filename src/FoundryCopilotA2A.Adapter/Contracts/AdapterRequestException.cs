using A2A;

namespace FoundryCopilotA2A.Adapter;

/// <summary>Raised for conditions that should surface to the A2A caller as a defined error.</summary>
public sealed class AdapterRequestException : A2AException
{
    public AdapterRequestException(string message)
        : base(message, A2AErrorCode.InvalidParams)
    {
    }

    public AdapterRequestException(string message, Exception innerException)
        : base(message, innerException, A2AErrorCode.InternalError)
    {
    }
}
