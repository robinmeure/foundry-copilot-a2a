using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FoundryCopilotA2A.Cli;

internal static class A2ACorsCommands
{
    private const string Arm = "https://management.azure.com";
    private static readonly string[] Headers = ["A2A-Extensions", "X-A2A-Extensions"];

    public static async Task<int> RunAsync(
        CliContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("subscription-id", "resource-group", "service-name", "api-ids", "apply", "help");
        var subscription = arguments.Require("subscription-id");
        if (!Guid.TryParse(subscription, out _)) throw new CliException("--subscription-id must be a GUID.");
        var group = arguments.Require("resource-group");
        var service = arguments.Require("service-name");
        var ids = arguments.Require("api-ids").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0 || ids.Length > 50 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(id => id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            throw new CliException("--api-ids must list 1-50 distinct, explicit API identifiers.");
        }
        var token = await context.Processes.CaptureAsync("az",
            ["account", "get-access-token", "--subscription", subscription, "--resource", Arm,
                "--query", "accessToken", "--output", "tsv"], cancellationToken);
        var accessToken = token.StandardOutput.Trim();
        if (accessToken.Length == 0) throw new CliException("Azure CLI returned no ARM access token.");
        var baseUrl = $"{Arm}/subscriptions/{subscription}/resourceGroups/{Uri.EscapeDataString(group)}" +
            $"/providers/Microsoft.ApiManagement/service/{Uri.EscapeDataString(service)}/apis";
        await UpdateAsync(context.HttpClient, accessToken, baseUrl, ids, arguments.Has("apply"),
            context.Out, cancellationToken);
        return 0;
    }

    internal static async Task UpdateAsync(
        HttpClient client, string token, string baseUrl, string[] ids, bool apply,
        TextWriter output, CancellationToken cancellationToken)
    {
        // Read and validate every target before making any change. Never replace an API or
        // rebuild its policy from local configuration: the live policy is the source of truth.
        var changes = new List<(string Id, string Url, string ETag, string Policy, bool Changed)>();
        foreach (var id in ids)
        {
            var url = $"{baseUrl}/{Uri.EscapeDataString(id)}/policies/policy?api-version=2024-05-01&format=xml";
            var (current, etag) = await ReadAsync(client, token, url, id, cancellationToken);
            var patched = Patch(current);
            var changed = !Equivalent(current, patched);
            changes.Add((id, url, etag, patched, changed));
            output.WriteLine($"{id}: {(changed ? "needs extension headers" : "already current")}.");
        }
        if (!apply)
        {
            output.WriteLine($"Preview only: {changes.Count(change => change.Changed)} of {ids.Length} API policies would change.");
            return;
        }
        foreach (var change in changes)
        {
            if (!change.Changed) continue;
            using var request = Request(HttpMethod.Put, change.Url, token);
            request.Headers.TryAddWithoutValidation("If-Match", change.ETag);
            request.Content = JsonContent.Create(new { properties = new { format = "xml", value = change.Policy } });
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new CliException(
                    $"Policy update for '{change.Id}' failed with HTTP {(int)response.StatusCode}. " +
                    "Previously verified updates remain applied; rerun preview before retrying.");
            }
            var (saved, _) = await ReadAsync(client, token, change.Url, change.Id, cancellationToken);
            if (!Equivalent(saved, change.Policy))
            {
                throw new CliException($"Read-back verification failed for '{change.Id}'; stop and inspect its live policy.");
            }
            output.WriteLine($"{change.Id}: updated and verified; unrelated policy content preserved.");
        }
        output.WriteLine($"Verified {ids.Length} API policies.");
    }

    internal static string Patch(string xml)
    {
        var document = Parse(xml, preserveWhitespace: true);
        var inbound = document.Root?.Name == "policies" ? document.Root.Element("inbound") : null;
        var policies = inbound?.Elements("cors").ToArray() ?? [];
        if (policies.Length != 1)
        {
            throw new CliException("Expected exactly one API-level inbound CORS policy; refusing to alter inherited or ambiguous CORS.");
        }
        var cors = policies[0];
        var allowed = cors.Element("allowed-headers");
        var exposed = cors.Element("expose-headers");
        if (allowed is null)
        {
            throw new CliException("The CORS policy has no allowed-headers section; refusing to infer existing access rules.");
        }
        if (exposed is null)
        {
            exposed = new XElement("expose-headers");
            cors.Add(exposed);
        }
        foreach (var section in new[] { allowed, exposed })
        {
            foreach (var header in Headers)
            {
                if (!section.Elements("header").Any(element =>
                    element.Value.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)))
                {
                    section.Add(new XElement("header", header));
                }
            }
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    internal static bool Equivalent(string left, string right) =>
        XNode.DeepEquals(Parse(left), Parse(right));

    private static XDocument Parse(string xml, bool preserveWhitespace = false)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    IgnoreWhitespace = !preserveWhitespace
                });
            return XDocument.Load(reader, preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new CliException("The policy is not valid XML; it was not changed.");
        }
    }

    private static async Task<(string Policy, string ETag)> ReadAsync(
        HttpClient client, string token, string url, string id, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"Reading the existing policy for '{id}' failed with HTTP {(int)response.StatusCode}; no policy body was logged.");
        }
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrEmpty(etag) || etag == "*")
        {
            throw new CliException("The policy has no usable ETag; refusing an unguarded update.");
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var value = document.RootElement.GetProperty("properties").GetProperty("value").GetString();
        if (string.IsNullOrWhiteSpace(value)) throw new CliException("The existing policy is empty.");
        return (value, etag);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
