namespace FoundryCopilotA2A.KnowledgeApi.Services.Skills;

/// <summary>Fails startup on an invalid skill folder instead of on the first chat turn.</summary>
public sealed class SkillValidationHostedService(KnowledgeSkillRegistry registry, ILogger<SkillValidationHostedService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Loaded {Count} knowledge skills: {Skills}", registry.Skills.Count,
            string.Join(", ", registry.Skills.Select(skill => skill.Name)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
