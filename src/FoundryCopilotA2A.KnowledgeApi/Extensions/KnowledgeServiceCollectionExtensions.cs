using Azure.Core;
using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.BackgroundServices;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat;
using FoundryCopilotA2A.KnowledgeApi.Services.Context;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Knowledge;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.KnowledgeApi.Stores;

namespace FoundryCopilotA2A.KnowledgeApi.Extensions;

/// <summary>Registers the process-wide knowledge modules and their replaceable interfaces.</summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeServices(this IServiceCollection services)
    {
        services.AddSingleton<TokenCredential>(provider =>
            CachedTokenCredential.CreateDefault(
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<IHostEnvironment>()));
        services.AddSingleton<AzureServices>();
        services.AddSingleton<AgentModelClient>();
        services.AddSingleton<IAgentModelClient>(provider => provider.GetRequiredService<AgentModelClient>());
        services.AddSingleton<DelegatedTokenProvider>();
        services.AddSingleton<A2aKnowledgeService>();
        services.AddSingleton<IA2aKnowledgeService>(provider => provider.GetRequiredService<A2aKnowledgeService>());
        services.AddSingleton<DocumentStore>();
        services.AddSingleton<IDocumentStore>(provider => provider.GetRequiredService<DocumentStore>());
        services.AddSingleton<ConversationHistoryStore>();
        services.AddSingleton<IConversationHistoryStore>(provider => provider.GetRequiredService<ConversationHistoryStore>());
        services.AddSingleton<SearchStore>();
        services.AddSingleton<ISearchStore>(provider => provider.GetRequiredService<SearchStore>());
        services.AddSingleton<DocumentRetrieval>();
        services.AddSingleton<IDocumentRetrieval>(provider => provider.GetRequiredService<DocumentRetrieval>());
        services.AddSingleton<GraphService>();
        services.AddSingleton<IGraphService>(provider => provider.GetRequiredService<GraphService>());
        services.AddSingleton<GraphSkill>();
        services.AddSingleton(provider => KnowledgeSkillCatalog.Load(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IKnowledgeSkillConnector, GraphSkillConnector>();
        services.AddSingleton<IKnowledgeSkillConnector, ApimA2aSkillConnector>();
        services.AddSingleton<AzureAiSearchConnector>();
        services.AddSingleton<IKnowledgeSkillConnector>(provider => provider.GetRequiredService<AzureAiSearchConnector>());
        services.AddSingleton<IKnowledgeSkillConnector, OfficeDocumentsConnector>();
        services.AddSingleton<KnowledgeSkillRegistry>();
        services.AddSingleton<GraphActions>();
        services.AddSingleton<GraphActionStore>();
        services.AddSingleton<GeneratedFileStore>();
        services.AddSingleton<UserContextService>();
        services.AddSingleton<ResponsesCoordinator>();
        services.AddSingleton<IResponsesCoordinator>(provider => provider.GetRequiredService<ResponsesCoordinator>());
        services.AddSingleton<KnowledgeService>();
        services.AddSingleton<IKnowledgeService>(provider => provider.GetRequiredService<KnowledgeService>());
        services.AddSingleton<ContinuationTokens>();
        services.AddScoped<KnowledgeAccessFilter>();
        services.AddDataProtection();
        services.AddHostedService<SkillValidationHostedService>();
        services.AddHostedService<TokenWarmupHostedService>();
        services.AddHostedService<CleanupWorker>();

        // Aliases share the same singleton state; tests can replace an interface without
        // creating a second document store, history lock pool, or orchestration instance.
        return services;
    }
}
