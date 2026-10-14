using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls.WebView.Blazor;
using Xunit;

namespace Avalonia.Controls.WebView.Blazor.Tests;

public class DocumentPolicyTests
{
    [Theory]
    [InlineData("app://localhost/", true)]
    [InlineData("app://localhost/deep/link", true)]
    [InlineData("app://other/index.html", false)]
    [InlineData("https://localhost/index.html", false)]
    [InlineData("app://localhost.evil/index.html", false)]
    public void Origin_Is_Not_Just_A_Scheme_Check(string value, bool expected) =>
        Assert.Equal(expected, BlazorDocumentPolicy.IsWithinOrigin(new("app://localhost/"), new(value)));

    [Theory]
    [InlineData("app://localhost/", "app://localhost/#anchor", true)]
    [InlineData("app://localhost/#anchor", "app://localhost/", true)]
    [InlineData("app://localhost/#anchor", "app://localhost/#next", true)]
    [InlineData("app://localhost/#anchor", "app://localhost/#anchor", false)]
    [InlineData("app://localhost/", "app://localhost/page#anchor", false)]
    [InlineData("app://localhost/?v=1", "app://localhost/?v=2#anchor", false)]
    [InlineData("app://localhost/", "app://other/#anchor", false)]
    public void Only_Fragment_Changes_Preserve_The_Document(string current, string destination, bool expected) =>
        Assert.Equal(expected, BlazorDocumentPolicy.IsFragmentNavigation(new(current), new(destination)));

    [Theory]
    [InlineData("document", "text/html", "GET", true)]
    [InlineData("script", "text/html", "GET", false)]
    [InlineData("iframe", "text/html", "GET", false)]
    [InlineData(null, "text/html,application/xhtml+xml", "GET", true)]
    [InlineData(null, "text/html; q=0.8", "HEAD", true)]
    [InlineData(null, "*/*", "GET", false)]
    [InlineData(null, "text/html", "POST", false)]
    [InlineData(null, null, "GET", false)]
    public void Only_Document_Requests_Get_Host_Fallback(string? destination, string? accept, string method, bool expected)
    {
        var headers = new TestHeaders();
        if (destination is not null)
            headers.TrySet("Sec-Fetch-Dest", destination);
        if (accept is not null)
            headers.TrySet("Accept", accept);
        var request = new WebViewWebResourceRequest
        {
            Uri = new("app://localhost/deep/link"), Method = new(method), Headers = headers
        };
        Assert.Equal(expected, BlazorDocumentPolicy.IsDocumentRequest(request));
    }

    [Fact]
    public void Closed_Document_Policy_Forbids_Frames_And_External_Connections()
    {
        Assert.Contains("frame-src 'none'", BlazorDocumentPolicy.ContentSecurityPolicy);
        Assert.Contains("connect-src 'self'", BlazorDocumentPolicy.ContentSecurityPolicy);
        Assert.Contains("object-src 'none'", BlazorDocumentPolicy.ContentSecurityPolicy);
        Assert.DoesNotContain("unsafe-eval", BlazorDocumentPolicy.ContentSecurityPolicy);
    }

    private sealed class TestHeaders : WebViewWebRequestHeaders
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
        public override int Count => _headers.Count;
        public override bool TrySet(string name, string value) { _headers[name] = value; return true; }
        public override bool TryRemove(string name) => _headers.Remove(name);
        public override bool ContainsKey(string key) => _headers.ContainsKey(key);
        public override bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _headers.TryGetValue(key, out value);
        public override IEnumerable<string> Keys => _headers.Keys;
        public override IEnumerable<string> Values => _headers.Values;
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();
    }
}
