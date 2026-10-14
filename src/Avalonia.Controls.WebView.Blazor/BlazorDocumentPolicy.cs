namespace Avalonia.Controls.WebView.Blazor;

internal static class BlazorDocumentPolicy
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
        "frame-src 'none'; object-src 'none'; base-uri 'self'; form-action 'none'";

    internal static bool IsWithinOrigin(Uri origin, Uri uri) =>
        uri.IsAbsoluteUri && origin.IsBaseOf(uri);

    internal static bool IsDocumentRequest(WebViewWebResourceRequest request)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return false;
        return request.Headers.TryGetValue("Sec-Fetch-Dest", out var destination)
            ? destination.Equals("document", StringComparison.OrdinalIgnoreCase)
            : request.Headers.TryGetValue("Accept", out var accept)
              && accept.Split(',').Any(value =>
                  value.Split(';')[0].Trim().Equals("text/html", StringComparison.OrdinalIgnoreCase));
    }
}
