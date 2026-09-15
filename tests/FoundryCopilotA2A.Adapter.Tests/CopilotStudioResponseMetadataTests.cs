using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class CopilotStudioResponseMetadataTests
{
    [Fact]
    public async Task TraceHandlerCapturesConversationIdResponseHeader()
    {
        using var traceStore = new SanitizedTraceStore(Options.Create(new AdapterOptions()));
        var responseMetadataAccessor = new CopilotStudioResponseMetadataAccessor();
        var httpContext = new DefaultHttpContext();
        httpContext.Items[AdapterConstants.SelectedAgentItem] = AgentCatalog.MockAgentId;
        var adapterOptions = Options.Create(new AdapterOptions
        {
            Backend = "Mock",
            AllowAnonymousDevelopmentMode = true
        });
        var metadataAccessor = new A2ARequestMetadataAccessor(
            new HttpContextAccessor { HttpContext = httpContext },
            adapterOptions,
            Options.Create(new AuthenticationOptions()),
            new AgentCatalog(
                adapterOptions,
                Options.Create(new CopilotStudioOptions()),
                Options.Create(new FoundryOptions())));
        using var handler = new CopilotStudioTraceHandler(
            traceStore,
            metadataAccessor,
            responseMetadataAccessor)
        {
            InnerHandler = new ConversationResponseHandler("conversation-from-header")
        };
        using var client = new HttpClient(handler);
        using var capture = responseMetadataAccessor.BeginCapture();

        using var response = await client.PostAsync(
            "https://example.test/conversations",
            new StringContent("{}"));

        Assert.Equal("conversation-from-header", capture.ConversationId);
    }

    [Fact]
    public async Task CaptureFlowsAcrossAsyncCallsWithoutLeakingAfterDisposal()
    {
        var accessor = new CopilotStudioResponseMetadataAccessor();

        using (var capture = accessor.BeginCapture())
        {
            await Task.Run(() => accessor.CaptureConversationId("conversation-1"));
            Assert.Equal("conversation-1", capture.ConversationId);
        }

        accessor.CaptureConversationId("conversation-2");
        using var nextCapture = accessor.BeginCapture();
        Assert.Null(nextCapture.ConversationId);
    }

    private sealed class ConversationResponseHandler(string conversationId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Headers.Add("x-ms-conversationid", conversationId);
            return Task.FromResult(response);
        }
    }
}
