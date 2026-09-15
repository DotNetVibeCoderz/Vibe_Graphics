using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using ThreeAppGen.Services;

namespace ThreeAppGen.Plugins;

/// <summary>Internet access: Tavily search and plain page scraping.</summary>
public sealed partial class WebPlugin(AppSettings settings, LogService log) : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    [KernelFunction, Description("Searches the internet with Tavily and returns titles, urls and snippets.")]
    public async Task<string> SearchInternet(
        [Description("Search query.")] string query,
        [Description("How many results to return, 1-10.")] int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(settings.TavilyApiKey))
        {
            return "Tavily is not configured. Add a Tavily API key in Settings to enable internet search.";
        }

        log.Info($"Jack searched the web for '{query}'");
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync("https://api.tavily.com/search", new
            {
                api_key = settings.TavilyApiKey,
                query,
                max_results = Math.Clamp(maxResults, 1, 10),
                search_depth = "basic",
                include_answer = true,
            });

            if (!response.IsSuccessStatusCode)
            {
                return $"Search failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            StringBuilder builder = new();

            if (document.RootElement.TryGetProperty("answer", out JsonElement answer) &&
                answer.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine($"Summary: {answer.GetString()}").AppendLine();
            }

            if (document.RootElement.TryGetProperty("results", out JsonElement results))
            {
                foreach (JsonElement result in results.EnumerateArray())
                {
                    builder
                        .AppendLine($"- {result.GetProperty("title").GetString()}")
                        .AppendLine($"  {result.GetProperty("url").GetString()}")
                        .AppendLine($"  {Shorten(result.GetProperty("content").GetString(), 400)}");
                }
            }

            return builder.Length == 0 ? "No results." : builder.ToString();
        }
        catch (Exception exception)
        {
            log.Error($"Search failed: {exception.Message}");
            return $"Search failed: {exception.Message}";
        }
    }

    [KernelFunction, Description("Downloads a web page and returns its readable text content.")]
    public async Task<string> ScrapeWebPage(
        [Description("Absolute URL to fetch.")] string url,
        [Description("Maximum characters to return.")] int maxCharacters = 8000)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return "Only absolute http(s) URLs can be fetched.";
        }

        log.Info($"Jack fetched {url}");
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Add("User-Agent", "ThreeAppGen/1.0 (+https://github.com/DotNetVibeCoderz/Vibe_Graphics)");
            using HttpResponseMessage response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return $"Fetch failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            }

            string html = await response.Content.ReadAsStringAsync();
            return Shorten(HtmlToText(html), Math.Clamp(maxCharacters, 500, 60_000));
        }
        catch (Exception exception)
        {
            log.Error($"Fetch failed: {exception.Message}");
            return $"Fetch failed: {exception.Message}";
        }
    }

    /// <summary>Strips scripts, styles and tags, then collapses the whitespace.</summary>
    private static string HtmlToText(string html)
    {
        string text = ScriptOrStyle().Replace(html, " ");
        text = Tags().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    private static string Shorten(string? text, int maxLength)
    {
        text ??= string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
