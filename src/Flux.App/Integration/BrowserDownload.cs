using System.Text.Json;
using System.IO;

namespace Flux.App.Integration;

/// <summary>Browser data is deliberately limited to download metadata, never local actions or paths.</summary>
public sealed class BrowserDownload
{
    public string Url { get; set; } = "";
    public string? Cookies { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? FileName { get; set; }
    public string? PageTitle { get; set; }
    public bool IsMedia { get; set; }

    public static BrowserDownload Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A download must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name is not ("type" or "url" or "cookies" or "referrer" or "userAgent" or "fileName" or "filename" or "isMedia" or "pageTitle"))
                throw new InvalidDataException("Unknown or duplicate browser download property.");
        if (element.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "capture"))
            throw new InvalidDataException("Unsupported capture type.");
        if (element.TryGetProperty("fileName", out _) && element.TryGetProperty("filename", out _))
            throw new InvalidDataException("Specify only one filename property.");
        var result = new BrowserDownload
        {
            Url = Read(element, "url", NativeBridge.MaximumUrlCharacters, required: true)!,
            Cookies = Read(element, "cookies", 16_384),
            Referrer = Read(element, "referrer", NativeBridge.MaximumUrlCharacters),
            UserAgent = Read(element, "userAgent", 2048),
            FileName = Read(element, element.TryGetProperty("filename", out _) ? "filename" : "fileName", 512),
            PageTitle = Read(element, "pageTitle", 1024),
            IsMedia = element.TryGetProperty("isMedia", out var media) && media.ValueKind == JsonValueKind.True
        };
        result.Url = NativeBridge.NormalizeUrls([result.Url])[0];
        if (result.Referrer is { Length: > 0 }) result.Referrer = NativeBridge.NormalizeUrls([result.Referrer])[0];
        if (result.Cookies?.IndexOfAny(['\r', '\n', '\0']) >= 0 || result.UserAgent?.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("Browser headers cannot contain line breaks.");
        if (element.TryGetProperty("isMedia", out media) && media.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("isMedia must be a boolean.");
        return result;
    }

    private static string? Read(JsonElement element, string name, int maximum, bool required = false)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new InvalidDataException($"Missing {name}.");
            return null;
        }
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} must be text.");
        var value = property.GetString()!;
        if (value.Length > maximum || value.Contains('\0')) throw new InvalidDataException($"{name} is invalid or too long.");
        return value;
    }
}
