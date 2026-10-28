using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.Skills;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Skills;

/// <summary>
/// Turns one skill folder into tools. Connectors own the code (auth, validation, upstream contract);
/// skill folders own descriptions, instructions and declarative settings.
/// </summary>
public interface IKnowledgeSkillConnector
{
    string Name { get; }

    /// <summary>Connector-specific startup checks beyond the shared folder rules.</summary>
    IEnumerable<string> Validate(SkillDefinition skill);

    /// <summary>Whether the deployment configured the backend this skill needs.</summary>
    bool IsConfigured(SkillDefinition skill);

    ApiException NotConfigured(SkillDefinition skill);

    /// <summary>Tools for a turn in which the skill's source is selected; empty when the skill is unavailable.</summary>
    IReadOnlyList<KnowledgeSkillTool> GetTools(SkillDefinition skill, SkillScope scope);

    /// <summary>Code-owned safety rules returned with the skill's instructions; folders cannot weaken them.</summary>
    string? Guardrails(SkillDefinition skill, IReadOnlyList<KnowledgeSkillTool> tools);
}

/// <summary>The knowledge-source selection of one chat turn.</summary>
public sealed record SkillScope(IReadOnlyCollection<string> Sources, IReadOnlyCollection<string>? SelectedAgents);

/// <summary>Everything a skill tool may use while it runs; arguments are untrusted model output.</summary>
public sealed record SkillToolCall(
    HttpContext Context,
    SkillScope Scope,
    UserContext User,
    JsonElement Arguments,
    Func<string, CancellationToken, ValueTask> ReportProgress);

public sealed record KnowledgeSkillTool(
    string Name,
    string Description,
    JsonElement Parameters,
    Func<SkillToolCall, CancellationToken, Task<SkillToolResult>> InvokeAsync)
{
    /// <summary>Status line when the call starts; <c>null</c> uses the shared tool status text.</summary>
    public Func<JsonElement, TimeZoneInfo, string>? Started { get; init; }
    public string? Failed { get; init; }

    /// <summary>True for <c>graph_propose_*</c> tools, which count against the per-turn proposal budget.</summary>
    public bool StagesChange { get; init; }

    public static KnowledgeSkillTool FromDefinition(object definition, Func<SkillToolCall, CancellationToken, Task<SkillToolResult>> invoke)
    {
        var json = JsonSerializer.SerializeToElement(definition);
        return new KnowledgeSkillTool(
            json.GetProperty("name").GetString()!,
            json.GetProperty("description").GetString()!,
            json.GetProperty("parameters").Clone(),
            invoke);
    }
}

/// <summary>A tool outcome; the coordinator turns evidence into server-assigned citations.</summary>
public abstract record SkillToolResult(string Status);

public sealed record PassageToolResult(Passage[] Passages, string Coverage, bool Truncated, string Status) : SkillToolResult(Status);

public sealed record GraphItemsToolResult(GraphToolResult Result, string Status) : SkillToolResult(Status);

public sealed record ProposalToolResult(GraphActionProposal Proposal, string Status) : SkillToolResult(Status);

public sealed record DataToolResult(object Data, string Status) : SkillToolResult(Status);
