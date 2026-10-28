using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FoundryCopilotA2A.Adapter.Tests;

internal static class TestConfiguration
{
    public static IConfiguration From(AdapterOptions adapter)
    {
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Adapter = adapter }));
        return new ConfigurationBuilder().AddJsonStream(json).Build();
    }

    public static IConfiguration From(AuthenticationOptions authentication)
    {
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Authentication = authentication }));
        return new ConfigurationBuilder().AddJsonStream(json).Build();
    }
}
