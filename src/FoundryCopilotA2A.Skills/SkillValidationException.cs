namespace FoundryCopilotA2A.Skills;

/// <summary>Raised when one or more skill folders are invalid; every problem is reported at once.</summary>
public sealed class SkillValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid knowledge skills:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(error => "- " + error)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
