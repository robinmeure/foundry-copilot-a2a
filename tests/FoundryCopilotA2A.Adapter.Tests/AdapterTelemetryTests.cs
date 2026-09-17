using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class AdapterTelemetryTests : IDisposable
{
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public AdapterTelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AdapterTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AdapterTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
            {
                var copiedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copiedTags[tag.Key] = tag.Value;
                }

                _measurements.Enqueue(new Measurement(instrument.Name, value, copiedTags));
            });
        _meterListener.Start();
    }

    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
    }

    [Fact]
    public void CopilotStudioSignalsRecordMilestonesAndLowCardinalityMetrics()
    {
        using (var activity = AdapterTelemetry.StartActivity("telemetry.test.copilot"))
        {
            AdapterTelemetry.RecordCopilotStudioInvocation(
                activity,
                TimeSpan.FromMilliseconds(500),
                "telemetry-test-agent",
                completed: true);
            AdapterTelemetry.RecordCopilotStudioConversationStart(
                activity,
                TimeSpan.FromMilliseconds(200),
                "telemetry-test-agent",
                created: true);
            AdapterTelemetry.RecordCopilotStudioStreamMilestone(
                activity,
                AdapterTelemetry.StreamMilestone.FirstActivity,
                TimeSpan.FromMilliseconds(25),
                "telemetry-test-agent");
            AdapterTelemetry.RecordCopilotStudioStreamMilestone(
                activity,
                AdapterTelemetry.StreamMilestone.FirstProgress,
                TimeSpan.FromMilliseconds(75),
                "telemetry-test-agent");
            AdapterTelemetry.RecordCopilotStudioStreamMilestone(
                activity,
                AdapterTelemetry.StreamMilestone.FirstAnswer,
                TimeSpan.FromMilliseconds(125),
                "telemetry-test-agent");
            AdapterTelemetry.RecordCopilotStudioAnswerStream(
                activity,
                TimeSpan.FromMilliseconds(450),
                "telemetry-test-agent",
                completed: true);
        }

        var stoppedActivity = Assert.Single(
            _activities,
            item => item.DisplayName == "telemetry.test.copilot");
        Assert.Equal(true, stoppedActivity.GetTagItem("copilot_studio.invoke.completed"));
        Assert.Equal(
            200d,
            stoppedActivity.GetTagItem("copilot_studio.conversation.start.duration_ms"));
        Assert.Equal(
            25d,
            stoppedActivity.GetTagItem("copilot_studio.stream.time_to_first_activity_ms"));
        Assert.Equal(
            75d,
            stoppedActivity.GetTagItem("copilot_studio.stream.time_to_first_progress_ms"));
        Assert.Equal(
            125d,
            stoppedActivity.GetTagItem("copilot_studio.stream.time_to_first_answer_ms"));
        Assert.Equal(450d, stoppedActivity.GetTagItem("copilot_studio.stream.duration_ms"));
        Assert.Equal(
            [
                "copilot_studio.stream.first_activity",
                "copilot_studio.stream.first_progress",
                "copilot_studio.stream.first_answer"
            ],
            stoppedActivity.Events.Select(item => item.Name).ToArray());

        AssertMeasurement(
            "copilot_studio.client.invoke.duration",
            0.5,
            "copilot_studio.agent.id",
            "telemetry-test-agent");
        AssertMeasurement(
            "copilot_studio.client.conversation.start.duration",
            0.2,
            "copilot_studio.conversation.created",
            true);
        AssertMeasurement(
            "copilot_studio.client.answer_stream.time_to_first_activity",
            0.025,
            "copilot_studio.agent.id",
            "telemetry-test-agent");
        AssertMeasurement(
            "copilot_studio.client.answer_stream.time_to_first_progress",
            0.075,
            "copilot_studio.agent.id",
            "telemetry-test-agent");
        AssertMeasurement(
            "copilot_studio.client.answer_stream.time_to_first_answer",
            0.125,
            "copilot_studio.agent.id",
            "telemetry-test-agent");
        AssertMeasurement(
            "copilot_studio.client.answer_stream.duration",
            0.45,
            "copilot_studio.stream.completed",
            true);
    }

    [Fact]
    public void ApiManagementSignalsRecordCacheStageAndInvocationMetrics()
    {
        using (var activity = AdapterTelemetry.StartActivity("telemetry.test.apim"))
        {
            AdapterTelemetry.RecordApiManagementDiscoveryResolve(
                activity,
                TimeSpan.FromMilliseconds(800),
                "test_result");
            AdapterTelemetry.RecordApiManagementDiscoveryLockWait(
                activity,
                TimeSpan.FromMilliseconds(50));
            AdapterTelemetry.RecordApiManagementDiscoveryStage(
                activity,
                TimeSpan.FromMilliseconds(300),
                "test_stage",
                completed: true);
            AdapterTelemetry.RecordApiManagementDiscoveryRefresh(
                activity,
                TimeSpan.FromMilliseconds(700),
                candidateCount: 3,
                discoveredCount: 2,
                completed: true);
            AdapterTelemetry.RecordApiManagementA2AInvocation(
                activity,
                TimeSpan.FromMilliseconds(900),
                "telemetry-test-api",
                completed: true);
        }

        var stoppedActivity = Assert.Single(
            _activities,
            item => item.DisplayName == "telemetry.test.apim");
        Assert.Equal("test_result", stoppedActivity.GetTagItem("apim.discovery.cache.result"));
        Assert.Equal(50d, stoppedActivity.GetTagItem("apim.discovery.lock_wait_ms"));
        Assert.Equal(3, stoppedActivity.GetTagItem("apim.discovery.candidate.count"));
        Assert.Equal(2, stoppedActivity.GetTagItem("apim.discovery.agent.count"));
        Assert.Equal(1, stoppedActivity.GetTagItem("apim.discovery.skipped.count"));
        Assert.Contains(
            stoppedActivity.Events,
            item => item.Name == "apim.discovery.lock_acquired");

        AssertMeasurement(
            "apim.discovery.resolve.duration",
            0.8,
            "apim.discovery.cache.result",
            "test_result");
        AssertMeasurement(
            "apim.discovery.stage.duration",
            0.3,
            "apim.discovery.stage",
            "test_stage");
        AssertMeasurement(
            "apim.a2a.invoke.duration",
            0.9,
            "apim.api.id",
            "telemetry-test-api");
    }

    private void AssertMeasurement(
        string name,
        double expectedValue,
        string tagName,
        object expectedTagValue)
    {
        var measurement = Assert.Single(
            _measurements,
            item =>
                item.Name == name &&
                item.Tags.TryGetValue(tagName, out var value) &&
                Equals(value, expectedTagValue));
        Assert.Equal(expectedValue, measurement.Value, precision: 6);
    }

    private sealed record Measurement(
        string Name,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
