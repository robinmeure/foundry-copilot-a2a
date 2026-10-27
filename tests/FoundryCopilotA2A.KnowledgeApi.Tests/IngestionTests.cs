using System.Globalization;
using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class IngestionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentImportsRespectQuotaAndReleaseAfterFailure(bool failFirst)
    {
        var caller = new Caller("local", "mock-user");
        var scope = caller.Scope(Guid.NewGuid().ToString());
        var otherScope = caller.Scope(Guid.NewGuid().ToString());
        while (Stripe(scope) == Stripe(otherScope))
        {
            otherScope = caller.Scope(Guid.NewGuid().ToString());
        }

        var search = new BlockingSearch(scope, failFirst);
        using var api = new Api(search);
        var documents = api.Services.GetRequiredService<IDocumentStore>();
        var knowledge = api.Services.GetRequiredService<IKnowledgeService>();
        for (var index = 0; index < 29; index++)
        {
            await documents.Commit(new DocumentRecord($"existing-{index}", scope, "existing.txt",
                DateTimeOffset.UtcNow.AddDays(1), 1, UserId: caller.User), default);
        }

        Task<DocumentRecord> Import(string target, CancellationToken ct = default) =>
            knowledge.Import(target, "new.txt", "New evidence"u8.ToArray(), null, null, null, ct, caller.User);

        var first = Record.ExceptionAsync(() => Import(scope));
        await search.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Record.ExceptionAsync(() => Import(scope));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelled = Import(scope, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await Import(otherScope).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            search.Release.TrySetResult();
        }

        var firstFailure = await first.WaitAsync(TimeSpan.FromSeconds(10));
        var secondFailure = await second.WaitAsync(TimeSpan.FromSeconds(10));
        if (failFirst)
        {
            Assert.IsType<HttpRequestException>(firstFailure);
            Assert.Null(secondFailure);
        }
        else
        {
            Assert.Null(firstFailure);
            var failure = Assert.IsType<ApiException>(secondFailure);
            Assert.Equal(409, failure.Status);
            Assert.Equal("document_quota", failure.Code);
        }
        Assert.Equal(30, (await documents.List(scope, default)).Count(document => document.Status == "Ready"));
        Assert.Single(await documents.List(otherScope, default));
    }

    private static int Stripe(string scope) =>
        int.Parse(scope.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) % 64;

    private sealed class Api(BlockingSearch search) : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(AppContext.BaseDirectory, "ingestion-tests-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<ISearchStore>(search)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class BlockingSearch(string scope, bool failFirst) : ISearchStore
    {
        private int calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Index(DocumentRecord document, Passage[] passages, CancellationToken ct)
        {
            if (document.Scope == scope && Interlocked.Increment(ref calls) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(ct);
                if (failFirst) throw new HttpRequestException("Simulated indexing failure.");
            }
        }

        public Task<Passage[]> Search(string scope, DocumentRecord[] allowed, string query, CancellationToken ct, string? userId = null) =>
            throw new NotSupportedException();

        public Task Delete(DocumentRecord document, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Passage[]> ReadDocument(string scope, string userId, DocumentRecord document, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
