using System.Diagnostics;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class NativeTraceTests
{
    [Fact]
    public void RepeatedRequestsCannotGrowOneTraceBeyondItsRequestLimit()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions { MaxCacheEntries = 1 }));
        var traceId = ActivityTraceId.CreateRandom();

        for (var index = 0; index < 5_000; index++)
        {
            using var request = source.StartActivity("request", ActivityKind.Server,
                new ActivityContext(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))!;
            request.SetTag("a2a.runtime", true);
            Assert.Equal(index < SanitizedTraceStore.MaxRequestsPerTrace,
                store.Register(request, "tenant|user", "specialist"));
            if (index == SanitizedTraceStore.MaxRequestsPerTrace)
            {
                using var rejectedChild = source.StartActivity("rejected-child")!;
                store.RecordHttpExchange(rejectedChild,
                    new TraceHttpRequest("POST", "https://specialist.example/a2a",
                        new Dictionary<string, string>(), null), null, null);
                rejectedChild.Stop();
                store.Record(rejectedChild);
            }
            request.Stop();
            store.Record(request);
        }

        Assert.True(store.TryGet(traceId.ToHexString(), "tenant|user", out var trace));
        Assert.True(trace!.Complete);
        Assert.True(trace.Truncated);
        Assert.Equal(SanitizedTraceStore.MaxRequestsPerTrace, trace.Spans.Count);
    }

    [Fact]
    public void SpanLimitPreservesCompletionAndReportsTruncation()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()));
        using var entry = source.StartActivity("entry", ActivityKind.Server)!;
        entry.SetTag("a2a.runtime", true);
        Assert.True(store.Register(entry, "tenant|user", "orchestrator"));

        for (var index = 0; index < SanitizedTraceStore.MaxSpansPerTrace + 10; index++)
        {
            using var child = source.StartActivity("child")!;
            child.Stop();
            store.Record(child);
        }
        entry.Stop();
        store.Record(entry);

        Assert.True(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out var trace));
        Assert.True(trace!.Complete);
        Assert.True(trace.Truncated);
        Assert.Equal(SanitizedTraceStore.MaxSpansPerTrace, trace.Spans.Count);
        Assert.Contains(trace.Spans, span => span.Name == "entry");
    }

    [Fact]
    public void ConcurrentHttpRecordingCannotExceedTheSpanLimit()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()));
        using var entry = source.StartActivity("entry", ActivityKind.Server)!;
        entry.SetTag("a2a.runtime", true);
        Assert.True(store.Register(entry, "tenant|user", "orchestrator"));
        var request = new TraceHttpRequest("POST", "https://specialist.example/a2a",
            new Dictionary<string, string>(), null);

        Parallel.For(0, SanitizedTraceStore.MaxSpansPerTrace * 2, _ =>
        {
            using var child = source.StartActivity("http", ActivityKind.Client)!;
            store.RecordHttpExchange(child, request, new TraceHttpResponse(200, null), null);
            child.Stop();
            store.Record(child);
        });
        entry.Stop();
        store.Record(entry);

        Assert.True(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out var trace));
        Assert.True(trace!.Complete);
        Assert.True(trace.Truncated);
        Assert.Equal(SanitizedTraceStore.MaxSpansPerTrace, trace.Spans.Count);
        Assert.Equal(SanitizedTraceStore.MaxSpansPerTrace - 1, trace.Spans.Count(span => span.Http is not null));
    }

    [Fact]
    public void ConcurrentCallbacksCannotExceedTheRequestLimit()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()));
        var traceId = ActivityTraceId.CreateRandom();
        var accepted = 0;

        Parallel.For(0, SanitizedTraceStore.MaxRequestsPerTrace * 4, _ =>
        {
            using var request = source.StartActivity("callback", ActivityKind.Server,
                new ActivityContext(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))!;
            request.SetTag("a2a.runtime", true);
            if (store.Register(request, "tenant|user", "specialist"))
            {
                Interlocked.Increment(ref accepted);
            }
            request.Stop();
            store.Record(request);
        });

        Assert.Equal(SanitizedTraceStore.MaxRequestsPerTrace, accepted);
        Assert.True(store.TryGet(traceId.ToHexString(), "tenant|user", out var trace));
        Assert.True(trace!.Complete);
        Assert.True(trace.Truncated);
        Assert.Equal(accepted, trace.Spans.Count);
    }

    [Fact]
    public void ReadingAndMergingCallbacksCannotExtendTheAbsoluteLifetime()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        var clock = new TestClock();
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()), clock);
        using var entry = source.StartActivity("entry", ActivityKind.Server)!;
        entry.SetTag("a2a.runtime", true);
        Assert.True(store.Register(entry, "tenant|user", "orchestrator"));
        entry.Stop();
        store.Record(entry);

        for (var minute = 1; minute < 15; minute++)
        {
            clock.UtcNow += TimeSpan.FromMinutes(1);
            Assert.True(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out _));
            using var callback = source.StartActivity("callback", ActivityKind.Server,
                new ActivityContext(entry.TraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))!;
            callback.SetTag("a2a.runtime", true);
            Assert.True(store.Register(callback, "tenant|user", "specialist"));
            callback.Stop();
            store.Record(callback);
        }

        clock.UtcNow += TimeSpan.FromMinutes(1);
        Assert.False(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out _));
        store.Record(entry);
        Assert.False(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out _));
    }

    [Fact]
    public void ExpiredRequestsCannotWriteIntoAReplacementTraceBuffer()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        var clock = new TestClock();
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()), clock);
        using var expired = source.StartActivity("expired", ActivityKind.Server)!;
        expired.SetTag("a2a.runtime", true);
        Assert.True(store.Register(expired, "tenant|user", "orchestrator"));
        expired.Stop();
        store.Record(expired);

        clock.UtcNow += TimeSpan.FromMinutes(15);
        using var replacement = source.StartActivity("replacement", ActivityKind.Server,
            new ActivityContext(expired.TraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))!;
        replacement.SetTag("a2a.runtime", true);
        Assert.True(store.Register(replacement, "tenant|user", "specialist"));
        store.Record(expired);
        store.RecordHttpExchange(expired,
            new TraceHttpRequest("POST", "https://specialist.example/a2a",
                new Dictionary<string, string>(), null), null, null);
        replacement.Stop();
        store.Record(replacement);

        Assert.True(store.TryGet(replacement.TraceId.ToHexString(), "tenant|user", out var trace));
        Assert.True(trace!.Complete);
        Assert.False(trace.Truncated);
        Assert.Equal("replacement", Assert.Single(trace.Spans).Name);
    }

    [Fact]
    public void NativeCallbackPreservesEntrySpansAndWaitsForBothRequests()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()));
        using var entry = source.StartActivity("entry", ActivityKind.Server)!;
        entry.SetTag("a2a.runtime", true);
        Assert.True(store.Register(entry, "tenant|user", "orchestrator"));
        using (var beforeCallback = source.StartActivity("before-callback")!)
        {
            beforeCallback.Stop();
            store.Record(beforeCallback);
        }
        using var callback = source.StartActivity("callback", ActivityKind.Server,
            new ActivityContext(entry.TraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))!;
        callback.SetTag("a2a.runtime", true);
        Assert.True(store.Register(callback, "tenant|user", "specialist"));
        callback.Stop();
        store.Record(callback);
        store.Record(callback);

        Assert.True(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out var partial));
        Assert.False(partial!.Complete);
        Assert.Contains(partial.Spans, span => span.Name == "before-callback");
        Assert.Contains(partial.Spans, span => span.Name == "callback");

        entry.Stop();
        store.Record(entry);
        Assert.True(store.TryGet(entry.TraceId.ToHexString(), "tenant|user", out var complete));
        Assert.True(complete!.Complete);
        Assert.Equal(3, complete.Spans.Count);
    }

    [Fact]
    public void ASecondCallerCannotReplaceOrPolluteAnExistingTrace()
    {
        using var source = new ActivitySource($"native-trace-{Guid.NewGuid():N}");
        using var listener = Listen(source);
        using var store = new SanitizedTraceStore(Options.Create(new AdapterOptions()));
        using var entry = source.StartActivity("entry", ActivityKind.Server)!;
        entry.SetTag("a2a.runtime", true);
        Assert.True(store.Register(entry, "tenant|first", "orchestrator"));
        using var other = source.StartActivity("other-caller", ActivityKind.Server,
            new ActivityContext(entry.TraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))!;
        other.SetTag("a2a.runtime", true);
        Assert.False(store.Register(other, "tenant|second", "specialist"));
        other.Stop();
        store.Record(other);
        entry.Stop();
        store.Record(entry);

        Assert.False(store.TryGet(entry.TraceId.ToHexString(), "tenant|second", out _));
        Assert.True(store.TryGet(entry.TraceId.ToHexString(), "tenant|first", out var trace));
        Assert.True(trace!.Complete);
        Assert.Equal("entry", Assert.Single(trace.Spans).Name);
        Assert.DoesNotContain(trace.Spans.SelectMany(span => span.Attributes.Keys),
            key => key.Contains("trace.owner", StringComparison.Ordinal));
    }

    private static ActivityListener Listen(ActivitySource source)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(source, candidate),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
