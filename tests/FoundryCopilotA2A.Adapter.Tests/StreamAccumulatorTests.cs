using System.Text.Json;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class StreamAccumulatorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothWireShapesPreserveAppendDeduplicationAndReplacement(bool legacy)
    {
        var accumulator = new A2AStreamAccumulator();

        Assert.Equal("Hello", Assert.Single(Artifact(accumulator, legacy, "Hello", append: false)).Text);
        Assert.Equal(" world", Assert.Single(Artifact(accumulator, legacy, " world", append: true)).Text);
        Assert.Empty(Artifact(accumulator, legacy, "Hello world", append: false));
        Assert.Empty(Artifact(accumulator, legacy, " Hello world ", append: false));

        var replacement = Assert.Single(Artifact(accumulator, legacy, "Corrected answer", append: false));
        Assert.True(replacement.ReplacesAnswer);
        Assert.Equal("Corrected answer", replacement.Text);
        Assert.Equal(2, accumulator.DeltaCount);
        Assert.Equal(2, accumulator.SuppressedCount);
        Assert.Equal(1, accumulator.ReplacedCount);
        Assert.Equal(5, accumulator.EventCount);
    }

    [Fact]
    public void CompletedStatusSuppliesAnAnswerOnlyWhenArtifactsHaveNotAlreadyAnswered()
    {
        using var completed = JsonDocument.Parse(
            """{"result":{"kind":"status-update","status":{"state":"completed","message":{"parts":[{"text":"Status answer"}]}}}}""");
        var empty = new A2AStreamAccumulator();
        Assert.Equal("Status answer", Assert.Single(empty.Consume(completed.RootElement)).Text);

        var answered = new A2AStreamAccumulator();
        Artifact(answered, legacy: true, "Artifact answer", append: false);
        Assert.Empty(answered.Consume(completed.RootElement));
    }

    [Fact]
    public void FailedStatusNeverTurnsItsMessageIntoASuccessfulAnswer()
    {
        using var failed = JsonDocument.Parse(
            """{"result":{"statusUpdate":{"status":{"state":"TASK_STATE_FAILED","message":{"parts":[{"text":"Backend unavailable"}]}}}}}""");
        var accumulator = new A2AStreamAccumulator();

        var error = Assert.Throws<AdapterRequestException>(() => accumulator.Consume(failed.RootElement).ToArray());

        Assert.Contains("Backend unavailable", error.Message);
        Assert.False(accumulator.HasAnswer);
    }

    private static A2AStreamDelta[] Artifact(
        A2AStreamAccumulator accumulator,
        bool legacy,
        string text,
        bool append)
    {
        var artifact = new { artifactId = "answer", parts = new[] { new { text } } };
        var result = legacy
            ? JsonSerializer.SerializeToElement(new { kind = "artifact-update", artifact, append })
            : JsonSerializer.SerializeToElement(new { artifactUpdate = new { artifact, append } });
        var envelope = JsonSerializer.SerializeToElement(new { result });
        return accumulator.Consume(envelope).ToArray();
    }
}
