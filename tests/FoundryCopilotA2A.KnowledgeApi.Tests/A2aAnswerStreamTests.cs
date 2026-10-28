using System.Text.Json;
using A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class A2aAnswerStreamTests
{
    [Fact]
    public void AppendedChunksAccumulateAndANonAppendChunkReplacesTheArtifact()
    {
        var stream = new A2aAnswerStream();
        stream.Apply(Artifact("answer", append: false, Part.FromText("Hello ")));
        stream.Apply(Artifact("answer", append: true, Part.FromText("world")));
        Assert.Equal("Hello world", stream.Answer);

        stream.Apply(Artifact("answer", append: false, Part.FromText("Hello, world [1]")));
        Assert.Equal("Hello, world [1]", stream.Answer);
    }

    [Fact]
    public void InformativePartsAreProgressNotAnswerAndRepeatsAreDropped()
    {
        var stream = new A2aAnswerStream();
        var first = stream.Apply(Artifact("answer", append: false, Informative("Searching   through\nsources")));
        var repeat = stream.Apply(Artifact("answer", append: true, Informative("Searching through sources")));
        stream.Apply(Artifact("answer", append: true, Part.FromText("Answer")));

        Assert.Equal(["Searching through sources"], first);
        Assert.Empty(repeat);
        Assert.Equal("Answer", stream.Answer);
    }

    [Fact]
    public void ACitationOnlyChunkDoesNotClearTheAnswer()
    {
        var stream = new A2aAnswerStream();
        stream.Apply(Artifact("answer", append: false, Part.FromText("Answer [1]")));
        stream.Apply(Artifact("answer", append: false, Citations("s1", "javascript:alert(1)")));

        Assert.Equal("Answer [1]", stream.Answer);
        var source = Assert.Single(stream.Sources);
        Assert.Null(source.Url);
        Assert.Equal(["Quote"], source.Quotes);
    }

    [Theory]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.InputRequired)]
    public void TheLastStatusDecidesTheOutcome(TaskState state)
    {
        var stream = new A2aAnswerStream();
        stream.Apply(Status(TaskState.Working, "Planning"));
        Assert.Equal(TaskState.Working, stream.State);

        var progress = stream.Apply(Status(state, "Final status text is not progress"));

        Assert.Equal(state, stream.State);
        Assert.Empty(progress);
    }

    [Fact]
    public void ProgressIsCapped()
    {
        var stream = new A2aAnswerStream();
        var lines = Enumerable.Range(0, A2aAnswerStream.MaxProgressLines + 10)
            .SelectMany(index => stream.Apply(Status(TaskState.Working, $"Step {index}")))
            .ToArray();
        Assert.Equal(A2aAnswerStream.MaxProgressLines, lines.Length);
    }

    private static StreamResponse Artifact(string id, bool append, params Part[] parts) => new()
    {
        ArtifactUpdate = new TaskArtifactUpdateEvent
        {
            TaskId = "task",
            ContextId = "context",
            Append = append,
            Artifact = new Artifact { ArtifactId = id, Parts = [.. parts] }
        }
    };

    private static StreamResponse Status(TaskState state, string text) => new()
    {
        StatusUpdate = new TaskStatusUpdateEvent
        {
            TaskId = "task",
            ContextId = "context",
            Status = new A2A.TaskStatus
            {
                State = state,
                Message = new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"), Parts = [Part.FromText(text)] }
            }
        }
    };

    private static Part Informative(string text)
    {
        var part = Part.FromText(text);
        part.Metadata = new Dictionary<string, JsonElement> { ["isInformative"] = JsonSerializer.SerializeToElement(true) };
        return part;
    }

    private static Part Citations(string sourceId, string url) => Part.FromData(JsonSerializer.SerializeToElement(
        new Dictionary<string, object>
        {
            [A2aAnswerStream.CitationExtensionUri] = new
            {
                schemaVersion = "1",
                sources = new[] { new { id = sourceId, title = "Source", url, originatingAgent = new { id = "a", name = "A" } } },
                citations = new[] { new { sourceId, quote = "Quote" } }
            }
        }));
}
