using FoundryCopilotA2A.KnowledgeApi.Extensions;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Local settings override tracked defaults; non-empty environment values take precedence.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
builder.Configuration.AddInMemoryCollection(
    Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Select(entry => new KeyValuePair<string, string?>(
            entry.Key.ToString()!.Replace("__", ":", StringComparison.Ordinal),
            entry.Value?.ToString()))
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)));

builder.AddKnowledgeApi();
var app = builder.Build();

var configuration = app.Configuration;
var backend = configuration["Knowledge:Backend"];
if (backend is not ("Mock" or "Azure"))
{
    throw new InvalidOperationException("Knowledge:Backend must be Mock or Azure.");
}

if (backend == "Mock" && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Anonymous mock mode is restricted to Development.");
}

if (configuration.GetValue<int>("Knowledge:MaxFileBytes") is < 1 or > 20 * 1024 * 1024 ||
    configuration.GetValue<int>("Knowledge:RetentionDays") is < 1 or > 30)
{
    throw new InvalidOperationException("Upload bytes must be 1-20971520 and retention days 1-30.");
}

if (backend == "Azure" && configuration.GetValue<bool>("A2A:Enabled"))
{
    var authenticationMode = configuration["A2A:AuthenticationMode"];
    var delegatedScope = configuration["A2A:DelegatedScope"];
    if (authenticationMode is not ("OnBehalfOf" or "SameAudience"))
    {
        throw new InvalidOperationException("A2A:AuthenticationMode must be OnBehalfOf or SameAudience.");
    }

    if (authenticationMode == "SameAudience" && !string.IsNullOrEmpty(delegatedScope))
    {
        throw new InvalidOperationException("A2A:DelegatedScope must be empty for SameAudience.");
    }

    if (authenticationMode == "OnBehalfOf" && string.IsNullOrWhiteSpace(delegatedScope))
    {
        throw new InvalidOperationException("A2A:DelegatedScope is required for OnBehalfOf.");
    }

    var allowedAgentIds = configuration.GetSection("A2A:AllowedAgentIds").Get<string[]>() ?? [];
    if (allowedAgentIds.Length is < 1 or > 20 ||
        allowedAgentIds.Any(string.IsNullOrWhiteSpace) ||
        allowedAgentIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allowedAgentIds.Length)
    {
        throw new InvalidOperationException("A2A:AllowedAgentIds must list 1-20 distinct, read-only APIM knowledge agents.");
    }
}

app.UseExceptionHandler();
app.UseMiddleware<KnowledgeExceptionMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();

var controllers = app.MapControllers();
if (backend != "Mock")
{
    controllers.RequireAuthorization("delegated");
}

app.Run();

public partial class Program;
