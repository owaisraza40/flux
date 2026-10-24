using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Flux.App.Services;

public sealed record UpdateInfo(string Version, string Url);

public static class UpdateService
{
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Flux/1.0.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await client.GetAsync("https://api.github.com/repos/owaisraza40/flux/releases/latest", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var tag = json.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            if (!System.Version.TryParse(tag, out var remote) || remote <= current) return null;
            var url = json.RootElement.GetProperty("html_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/owaisraza40/flux/releases/", StringComparison.Ordinal)) return null;
            return new UpdateInfo(tag!, uri.AbsoluteUri);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException) { return null; }
    }
}
