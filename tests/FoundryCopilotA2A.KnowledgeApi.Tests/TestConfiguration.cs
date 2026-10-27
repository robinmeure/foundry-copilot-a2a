using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

internal static class TestConfiguration
{
    public static IConfigurationRoot Create(
        string? backend = null,
        string? directory = null,
        Dictionary<string, string?>? values = null)
    {
        var overrides = values is null ? new Dictionary<string, string?>() : new(values);
        if (backend is not null)
        {
            overrides["Knowledge:Backend"] = backend;
        }
        if (directory is not null)
        {
            overrides["Knowledge:DataDirectory"] = directory;
        }

        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(overrides)
            .Build();
    }

    public static void ConfigureMock(IWebHostBuilder builder, string directory) =>
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:Backend"] = "Mock",
                ["Knowledge:DataDirectory"] = directory
            }));
}
