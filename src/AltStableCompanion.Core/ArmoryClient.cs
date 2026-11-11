using System.Net;
using System.Net.Http.Headers;

namespace AltStableCompanion.Core;

public enum ArmoryStatus
{
    /// <summary>A render came back: <see cref="ArmoryFetch.Body"/>.</summary>
    Ok,
    /// <summary>304: the render is the one this PC has.</summary>
    NotModified,
    /// <summary>403 or 404: no render for this character (yet).</summary>
    NoRender,
    /// <summary>429, a 5xx, or a <c>Retry-After</c>: everyone waits <see cref="ArmoryFetch.RetryAfter"/>.</summary>
    Busy,
    /// <summary>Anything else - a redirect, a timeout, too big, no network. Said on the row, tried again later.</summary>
    Failed,
}

/// <param name="Date">The CDN's clock when it answered, for the skew between it and this PC's.</param>
public sealed record ArmoryFetch(ArmoryStatus Status, byte[]? Body = null, string? ETag = null, string? LastModified = null,
    DateTimeOffset? Date = null, TimeSpan? RetryAfter = null, string? Problem = null);

/// <summary>
/// One GET of one render, with the limits the README promises: the render host only, no
/// redirects (a redirect is not the render: it went to a silhouette when measured), 30 seconds
/// for the whole request including the body, 5 MB at most, read as it arrives. Validators are
/// sent when the caller has the file they describe.
/// </summary>
public sealed class ArmoryClient(Func<HttpMessageHandler> handler, string version)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>How long everyone waits after a 429 or a 5xx that does not say.</summary>
    public static readonly TimeSpan DefaultBusy = TimeSpan.FromMinutes(15);

    /// <summary>The real network: no redirects followed, no cookies kept.</summary>
    public static Func<HttpMessageHandler> Real => () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    public async Task<ArmoryFetch> FetchAsync(string url, string? etag, string? lastModified, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != Armory.Host)
        {
            return new ArmoryFetch(ArmoryStatus.Failed, Problem: "not a render URL");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var http = new HttpClient(handler(), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AltStableCompanion/" + Safe(version));
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (lastModified is not null && DateTimeOffset.TryParse(lastModified, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var since))
        {
            request.Headers.IfModifiedSince = since;
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var date = response.Headers.Date;
            var code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotModified) return new ArmoryFetch(ArmoryStatus.NotModified, Date: date);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return new ArmoryFetch(ArmoryStatus.NoRender, Date: date);
            if (code == 429 || code >= 500)
            {
                return new ArmoryFetch(ArmoryStatus.Busy, Date: date, RetryAfter: RetryAfter(response, date) ?? DefaultBusy,
                    Problem: $"the armory answered {code}");
            }
            // A redirect is not followed (the handler is told not to) and is not the render.
            if (code != 200) return new ArmoryFetch(ArmoryStatus.Failed, Date: date, Problem: $"the armory answered {code}");
            if (response.Content.Headers.ContentLength is > MaxBytes) return new ArmoryFetch(ArmoryStatus.Failed, Problem: "the render is too big");
            var body = await ReadCapped(response.Content, deadline.Token);
            if (body is null) return new ArmoryFetch(ArmoryStatus.Failed, Problem: "the render is too big");
            return new ArmoryFetch(ArmoryStatus.Ok, body, response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"), date);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ArmoryFetch(ArmoryStatus.Failed, Problem: $"no answer within {Timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            return new ArmoryFetch(ArmoryStatus.Failed, Problem: ex.Message);
        }
    }

    private static async Task<byte[]?> ReadCapped(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset? date)
    {
        if (response.Headers.RetryAfter is not { } r) return null;
        var wait = r.Delta ?? (r.Date is { } at ? at - (date ?? DateTimeOffset.UtcNow) : null);
        return wait is { } w ? TimeSpan.FromSeconds(Math.Clamp(w.TotalSeconds, 60, 24 * 3600)) : null;
    }

    // "1.1.0+abc" is a valid product version; a dirty build's "-dirty" is too. Anything else is dropped.
    private static string Safe(string version)
    {
        var v = new string(version.TakeWhile(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+').ToArray());
        return v.Length == 0 ? "0" : v;
    }
}
