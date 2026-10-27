using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;

/// <summary>Centralizes authenticated Azure requests, extraction polling and embedding generation.</summary>
public sealed class AzureServices(
    IHttpClientFactory factory,
    TokenCredential credential,
    IConfiguration config)
{
    private const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";
    private const string DocumentIntelligenceApiVersion = "2024-11-30";
    private const int MaxExtractedPages = 300;
    private const int MaxGetRetryAttempts = 2;
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AccessToken> tokenCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim tokenRefreshLock = new(1, 1);

    public async Task<HttpResponseMessage> Send(
        string url,
        string scope,
        HttpMethod method,
        object? body,
        CancellationToken ct,
        byte[]? bytes = null,
        bool allowNotFound = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = await CreateAuthenticatedRequestAsync(
                url,
                scope,
                method,
                body,
                bytes,
                ct);

            var response = await factory.CreateClient("knowledge").SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;

            var isRetryableGet =
                method == HttpMethod.Get &&
                (statusCode == StatusCodes.Status429TooManyRequests || statusCode >= StatusCodes.Status500InternalServerError) &&
                attempt < MaxGetRetryAttempts;

            if (isRetryableGet)
            {
                var delay = GetPollDelay(response);
                response.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            var isAllowedNotFound = allowNotFound && response.StatusCode == HttpStatusCode.NotFound;
            if (!response.IsSuccessStatusCode && !isAllowedNotFound)
            {
                response.Dispose();
                var errorStatus = statusCode == StatusCodes.Status429TooManyRequests
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status502BadGateway;

                throw new ApiException(
                    errorStatus,
                    "azure_service_error",
                    $"Azure service {new Uri(url).Host} returned HTTP {statusCode}. Check deployment, file format/protection, RBAC, quotas and connectivity.");
            }

            return response;
        }
    }

    public string Endpoint(string section) =>
        config[$"{section}:Endpoint"]!.TrimEnd('/');

    public async Task<float[]> Embed(string text, CancellationToken ct)
    {
        var dimensions = config.GetValue("OpenAI:EmbeddingDimensions", 3072);
        var payload = new
        {
            model = config["OpenAI:EmbeddingDeployment"],
            input = text,
            dimensions
        };

        using var response = await Send(
            $"{Endpoint("OpenAI")}/openai/v1/embeddings",
            CognitiveServicesScope,
            HttpMethod.Post,
            payload,
            ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var vector = json.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(element => element.GetSingle())
            .ToArray();

        if (vector.Length != dimensions || vector.Any(value => !float.IsFinite(value)))
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "embedding_mismatch",
                "Embedding dimensions do not match the configured Search vector field.");
        }

        return vector;
    }

    public async Task<ExtractedPage[]> Extract(byte[] bytes, CancellationToken ct)
    {
        var endpoint = Endpoint("DocumentIntelligence");
        var modelId = Uri.EscapeDataString(config["DocumentIntelligence:ModelId"] ?? "prebuilt-read");
        var analyzeUrl =
            $"{endpoint}/documentintelligence/documentModels/{modelId}:analyze" +
            $"?api-version={DocumentIntelligenceApiVersion}&stringIndexType=utf16CodeUnit";

        using var submission = await Send(
            analyzeUrl,
            CognitiveServicesScope,
            HttpMethod.Post,
            body: null,
            ct: ct,
            bytes: bytes);

        var operationLocation = submission.Headers.GetValues("Operation-Location").Single();
        ValidateOperationHost(endpoint, operationLocation);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var pollingToken = deadline.Token;
        var delay = GetPollDelay(submission);

        while (true)
        {
            await Task.Delay(delay, pollingToken);

            using var pollResponse = await Send(
                operationLocation,
                CognitiveServicesScope,
                HttpMethod.Get,
                body: null,
                ct: pollingToken);

            await using var stream = await pollResponse.Content.ReadAsStreamAsync(pollingToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: pollingToken);

            var status = json.RootElement.GetProperty("status").GetString();
            if (status == "failed")
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity,
                    "extraction_failed",
                    "Document analysis failed. Check file protection and format.");
            }

            if (status != "succeeded")
            {
                delay = GetPollDelay(pollResponse);
                continue;
            }

            var analyzeResult = json.RootElement.GetProperty("analyzeResult");
            var pages = analyzeResult
                .GetProperty("pages")
                .EnumerateArray()
                .Select(page => new ExtractedPage(
                    Number: page.GetProperty("pageNumber").GetInt32(),
                    Text: ExtractPageText(page, analyzeResult)))
                .ToArray();

            using var deletion = await Send(
                operationLocation,
                CognitiveServicesScope,
                HttpMethod.Delete,
                body: null,
                ct: pollingToken);

            if (pages.Length > MaxExtractedPages)
            {
                throw new ApiException(
                    StatusCodes.Status413PayloadTooLarge,
                    "page_limit",
                    $"The document exceeds {MaxExtractedPages} pages.");
            }

            return pages;
        }
    }

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        string url,
        string scope,
        HttpMethod method,
        object? body,
        byte[]? bytes,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        var accessToken = await GetAccessTokenAsync(scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        if (bytes is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async ValueTask<AccessToken> GetAccessTokenAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        if (tokenCache.TryGetValue(scope, out var cachedToken) && IsTokenValid(cachedToken))
        {
            return cachedToken;
        }

        await tokenRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (tokenCache.TryGetValue(scope, out cachedToken) && IsTokenValid(cachedToken))
            {
                return cachedToken;
            }

            var refreshedToken = await credential.GetTokenAsync(
                new TokenRequestContext([scope]),
                cancellationToken);
            tokenCache[scope] = refreshedToken;
            return refreshedToken;
        }
        finally
        {
            tokenRefreshLock.Release();
        }
    }

    private static bool IsTokenValid(AccessToken token) =>
        token.ExpiresOn - DateTimeOffset.UtcNow > TokenRefreshBuffer;

    private static void ValidateOperationHost(string configuredEndpoint, string operationLocation)
    {
        var operationUri = new Uri(operationLocation);
        var endpointUri = new Uri(configuredEndpoint);

        if (operationUri.GetLeftPart(UriPartial.Authority) != endpointUri.GetLeftPart(UriPartial.Authority))
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "invalid_operation",
                "The analysis service returned an unexpected polling host.");
        }
    }

    private static TimeSpan GetPollDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delaySeconds = (retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow))?.TotalSeconds ?? 2;
        return TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 1, 60));
    }

    private static string ExtractPageText(JsonElement page, JsonElement analyzeResult)
    {
        if (page.TryGetProperty("lines", out var lines) && lines.GetArrayLength() > 0)
        {
            var lineContents = lines
                .EnumerateArray()
                .Select(line => line.GetProperty("content").GetString());

            return string.Join("\n", lineContents);
        }

        if (!page.TryGetProperty("spans", out var spans) ||
            !analyzeResult.TryGetProperty("content", out var contentElement))
        {
            return string.Empty;
        }

        var fullText = contentElement.GetString() ?? string.Empty;
        var spanTexts = spans.EnumerateArray().Select(span =>
        {
            var offset = span.GetProperty("offset").GetInt32();
            var length = span.GetProperty("length").GetInt32();

            if (offset < 0 || length < 0 || offset > fullText.Length - length)
            {
                throw new ApiException(
                    StatusCodes.Status502BadGateway,
                    "invalid_extraction_span",
                    "Document analysis returned invalid content offsets.");
            }

            return fullText.Substring(offset, length);
        });

        return string.Join("\n", spanTexts);
    }
}
