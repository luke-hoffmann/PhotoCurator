using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;

namespace PhotoCurator.Core.Server;

/// <summary>
/// The app's web UI, embedded in the assembly as a flat "Assets.&lt;file&gt;" resource set.
/// Only embedded files can ever be served, so the allowlist is simply the embedded resource list.
/// </summary>
static class StaticAssets
{
    const string Prefix = "Assets.";

    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png"
    };

    static readonly Assembly Source = typeof(StaticAssets).Assembly;

    static readonly HashSet<string> Allowed = new(
        Source.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..]),
        StringComparer.Ordinal);

    sealed record Asset(byte[] Bytes, string ETag);

    static readonly ConcurrentDictionary<string, Asset?> Cache = new(StringComparer.Ordinal);

    public static bool TryGet(string requestedName, out byte[] bytes, out string contentType, out string etag)
    {
        bytes = Array.Empty<byte>();
        contentType = "application/octet-stream";
        etag = "";
        if (!Allowed.Contains(requestedName)) return false;

        var asset = Cache.GetOrAdd(requestedName, static name =>
        {
            using var stream = Source.GetManifestResourceStream(Prefix + name);
            if (stream is null) return null;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var data = memory.ToArray();
            return new Asset(data, "\"" + Convert.ToHexString(SHA256.HashData(data))[..16] + "\"");
        });
        if (asset is null) return false;

        bytes = asset.Bytes;
        etag = asset.ETag;
        contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(requestedName), "application/octet-stream");
        return true;
    }
}
