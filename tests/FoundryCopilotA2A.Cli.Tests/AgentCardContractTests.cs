using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class AgentCardContractTests
{
    [Theory]
    [InlineData("""{"url":"https://gateway.example/a2a"}""", true)]
    [InlineData("""{"supportedInterfaces":[{"url":"https://gateway.example/a2a"}]}""", true)]
    [InlineData("""{"url":"https://gateway.example/a2a","supportedInterfaces":[{"url":"https://gateway.example/a2a"}]}""", true)]
    [InlineData("""{"url":"https://other.example/a2a","supportedInterfaces":[{"url":"https://gateway.example/a2a"}]}""", false)]
    [InlineData("""{"url":"https://gateway.example/a2a","supportedInterfaces":[{"url":"https://other.example/a2a"}]}""", false)]
    [InlineData("""{"url":"https://gateway.example/a2a","supportedInterfaces":[]}""", false)]
    [InlineData("""{"url":"https://other.example/a2a"}""", false)]
    [InlineData("""{"url":null}""", false)]
    [InlineData("""{"url":42}""", false)]
    [InlineData("""{"supportedInterfaces":[null,42,{"url":42}]}""", false)]
    [InlineData("""{"supportedInterfaces":null}""", false)]
    [InlineData("""{"supportedInterfaces":"https://gateway.example/a2a"}""", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    public void BothCardVersionsMustRemainBoundToTheExpectedRuntime(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AgentCardContract.AdvertisesRuntime(
            document.RootElement, url => url == "https://gateway.example/a2a"));
    }
}
