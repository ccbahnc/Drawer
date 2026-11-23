using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Drawer.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Drawer;

internal static class WebsiteIcons
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly TimeSpan HomepageTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(3);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    });
    private static readonly SemaphoreSlim Downloads = new(3);
    private static readonly ConcurrentDictionary<string, Lazy<Task<IconPixels?>>> Pending = new();
    private static readonly Regex Link = new("<link\\b(?<attributes>(?:\"[^\"]*\"|'[^']*'|[^'\">])*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Attribute = new("(?<name>[\\w:-]+)(?:\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+)))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static async Task<IconPixels?> ReadAsync(string directory, Uri address)
    {
        if (!CanFetch(address)) return null;
        // Icon lookup is site-wide. Keep the shortcut's private path, query and fragment local.
        string origin = address.GetLeftPart(UriPartial.Authority);
        var page = new Uri(origin + "/");
        string cacheFile = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin))) + ".icon");
        var fetch = Pending.GetOrAdd(cacheFile, _ => new(() => ReadOrFetch(cacheFile, page)));
        try { return await fetch.Value.ConfigureAwait(false); }
        finally { Pending.TryRemove(new KeyValuePair<string, Lazy<Task<IconPixels?>>>(cacheFile, fetch)); }
    }

    private static async Task<IconPixels?> ReadOrFetch(string cacheFile, Uri page)
    {
        try
        {
            if (File.Exists(cacheFile))
            {
                using var stream = File.OpenRead(cacheFile);
                if (await ReadBounded(stream, prefixOnly: false, default).ConfigureAwait(false) is { } cached &&
                    await Task.Run(() => Decode(cached)).ConfigureAwait(false) is { } ready) return ready;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        await Downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            using var homepageDeadline = new CancellationTokenSource(HomepageTimeout);
            var html = await Download(page, prefixOnly: true, homepageDeadline.Token).ConfigureAwait(false);
            var finalPage = html?.Address ?? page;
            var candidates = html is { } head ? DeclaredIcons(finalPage, Encoding.UTF8.GetString(head.Bytes)) : Array.Empty<Uri>();
            using var iconDeadline = new CancellationTokenSource(IconTimeout);
            foreach (var uri in candidates.Append(new Uri(finalPage, "/favicon.ico")).Distinct())
            {
                if (iconDeadline.IsCancellationRequested) break;
                var image = await Download(uri, prefixOnly: false, iconDeadline.Token).ConfigureAwait(false);
                if (image is not { } response || await Task.Run(() => Decode(response.Bytes)).ConfigureAwait(false) is not { } icon) continue;
                await Cache(cacheFile, response.Bytes).ConfigureAwait(false);
                return icon;
            }
        }
        catch (OperationCanceledException) { }
        finally { Downloads.Release(); }
        return null;
    }

    private static IReadOnlyList<Uri> DeclaredIcons(Uri page, string html)
    {
        var icons = new List<Uri>();
        try
        {
            foreach (Match link in Link.Matches(html))
            {
                string? href = null, rel = null;
                foreach (Match attribute in Attribute.Matches(link.Groups["attributes"].Value))
                {
                    switch (attribute.Groups["name"].Value.ToLowerInvariant())
                    {
                        case "href": href ??= WebUtility.HtmlDecode(attribute.Groups["value"].Value); break;
                        case "rel": rel ??= attribute.Groups["value"].Value; break;
                    }
                }
                if (string.IsNullOrWhiteSpace(href) || rel == null ||
                    !rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("icon", StringComparer.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(page, href, out var address) || !SameSite(page, address) ||
                    address.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                icons.Add(address);
                if (icons.Count == 4) break;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return icons;
    }

    private static bool CanFetch(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0 || address.IsLoopback)
            return false;
        string host = address.IdnHost.TrimEnd('.');
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) &&
            !ip.IsIPv6LinkLocal && !(bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool SameSite(Uri first, Uri next) => CanFetch(next) &&
        string.Equals(first.IdnHost, next.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        (first.Scheme == next.Scheme && first.Port == next.Port ||
         first.Scheme == "http" && next.Scheme == "https" && first.IsDefaultPort && next.IsDefaultPort);

    private static async Task<(Uri Address, byte[] Bytes)?> Download(Uri address, bool prefixOnly, CancellationToken token)
    {
        var current = address;
        try
        {
            for (int redirect = 0; redirect <= 3; redirect++)
            {
                using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location &&
                    Uri.TryCreate(current, location, out var next) && SameSite(current, next)) { current = next; continue; }
                if (!response.IsSuccessStatusCode || (!prefixOnly && response.Content.Headers.ContentLength > MaximumBytes)) return null;
                using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await ReadBounded(content, prefixOnly, token).ConfigureAwait(false) is { } bytes ? (current, bytes) : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            PrototypeLog.Write($"website icon: {(prefixOnly ? "homepage" : "image")} unavailable ({ex.GetType().Name})");
        }
        return null;
    }

    private static async Task<byte[]?> ReadBounded(Stream stream, bool prefixOnly, CancellationToken token)
    {
        var bytes = new byte[MaximumBytes + (prefixOnly ? 0 : 1)];
        int length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
        return length > MaximumBytes ? null : bytes[..length];
    }

    private static async Task<IconPixels?> Decode(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            BitmapFrame? frame = null;
            for (uint i = 0; i < Math.Min(decoder.FrameCount, 8u); i++)
            {
                var candidate = await decoder.GetFrameAsync(i);
                if (candidate.PixelWidth is 0 or > 512 || candidate.PixelHeight is 0 or > 512) continue;
                if (frame == null || candidate.PixelWidth * candidate.PixelHeight > frame.PixelWidth * frame.PixelHeight) frame = candidate;
            }
            if (frame == null) return null;
            var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return new((int)frame.PixelWidth, (int)frame.PixelHeight, data.DetachPixelData());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static async Task Cache(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
