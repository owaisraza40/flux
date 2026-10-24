using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flux.Core;

internal static class StateStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static async Task<T?> ReadAsync<T>(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return default;
        var info = new FileInfo(path);
        if (info.Length > maxBytes) throw new InvalidDataException("The saved state exceeds the supported size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken).ConfigureAwait(false);
    }
}
