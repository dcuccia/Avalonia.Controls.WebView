using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Platform;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Avalonia.Controls.WebView.Blazor;

/// <summary>
/// Experimental, local-document-only Blazor host over NativeWebView's public API.
/// Attach before the control is added to the visual tree and dispose before removing it.
/// </summary>
public sealed class AvaloniaBlazorWebViewManager : WebViewManager
{
    private const string BootPath = "_avalonia/boot.js";
    private static readonly string s_bootScript = ReadBootScript();
    private readonly NativeWebView _webView;
    private readonly Uri _origin;
    private readonly string _hostPage;
    private readonly ILogger _logger;
    private string? _documentToken;
    private Uri? _navigationUri;
    private Task _sendQueue = Task.CompletedTask;
    private bool _disposed;

    public AvaloniaBlazorWebViewManager(
        NativeWebView webView,
        IServiceProvider services,
        IFileProvider files,
        string hostPage = "index.html",
        Uri? appBaseUri = null)
        : base(services, new AvaloniaBlazorDispatcher(),
            appBaseUri ?? DefaultAppBaseUri, files, new JSComponentConfigurationStore(), hostPage)
    {
        _webView = webView;
        _origin = appBaseUri ?? DefaultAppBaseUri;
        if (!_origin.IsAbsoluteUri || !_origin.AbsoluteUri.EndsWith('/') || _origin.Query.Length > 0
            || _origin.Fragment.Length > 0)
            throw new ArgumentException("The application base URI must be absolute and end with '/'.", nameof(appBaseUri));
        if (webView.AdapterInfo is not null)
            throw new InvalidOperationException("Attach the Blazor manager before the native adapter is created.");
        _hostPage = hostPage;
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<AvaloniaBlazorWebViewManager>();
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.WebResourceRequested += OnWebResourceRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NewWindowRequested += OnNewWindowRequested;
    }

    public static Uri DefaultAppBaseUri { get; } = OperatingSystem.IsAndroid()
        ? new("https://appassets.androidplatform.net/")
        : new("app://localhost/");

    public event EventHandler<Exception>? UnhandledException;

    protected override void NavigateCore(Uri absoluteUri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!BlazorDocumentPolicy.IsWithinOrigin(_origin, absoluteUri))
            throw new InvalidOperationException("This experimental host only navigates within its application origin.");
        _webView.Navigate(absoluteUri);
    }

    protected override void SendMessage(string message)
    {
        if (!_disposed)
            _sendQueue = SendAfterAsync(_sendQueue, message);
    }

    private async Task SendAfterAsync(Task previous, string message)
    {
        try
        {
            await previous;
            if (!_disposed)
                await _webView.InvokeScript($"window.__avaloniaBlazorReceiveMessage({JsonSerializer.Serialize(message, BridgeJsonContext.Default.String)});");
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        if (args is WindowsWebView1EnvironmentRequestedEventArgs)
            throw new NotSupportedException("Blazor local-content hosting requires WebView2, not WebView1.");
        if (_origin.Scheme != Uri.UriSchemeHttp && _origin.Scheme != Uri.UriSchemeHttps)
            args.CustomSchemes.Add(_origin.Scheme);
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        if (args.Request is not { } uri || !BlazorDocumentPolicy.IsWithinOrigin(_origin, uri))
        {
            args.Cancel = true;
            _logger.LogWarning("Blocked navigation outside the Blazor application origin: {Uri}", args.Request);
        }
        else
        {
            // WebKit also reports fragment changes, which do not load a new document.
            if (!BlazorDocumentPolicy.IsFragmentNavigation(_navigationUri, uri))
                _documentToken = null;
            _navigationUri = uri;
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        _logger.LogWarning("Blocked a new window from the local Blazor document: {Uri}", args.Request);
    }

    private void OnWebResourceRequested(object? sender, WebResourceRequestedEventArgs args)
    {
        if (_disposed)
            return;
        if (!BlazorDocumentPolicy.IsWithinOrigin(_origin, args.Request.Uri))
        {
            // WebKit cannot override network responses. CSP is the enforcement there.
            args.Response = new() { StatusCode = 403 };
            _logger.LogWarning("Blocked a resource outside the Blazor application origin: {Uri}", args.Request.Uri);
            return;
        }

        var relative = _origin.MakeRelativeUri(args.Request.Uri).GetComponents(
            UriComponents.SerializationInfoString, UriFormat.Unescaped);
        var path = relative.Split('?', '#')[0];
        if (args.Request.Method != HttpMethod.Get && args.Request.Method != HttpMethod.Head)
        {
            args.Response = new() { StatusCode = 405, Headers = { ["Allow"] = "GET, HEAD" } };
            return;
        }

        if (path == BootPath)
        {
            if (_documentToken is not { } token)
            {
                args.Response = new() { StatusCode = 409 };
                _logger.LogWarning("The Blazor bootstrap was requested before an application document was served.");
                return;
            }
            var boot = TextResponse(s_bootScript.Replace("__DOCUMENT_TOKEN__", JsonSerializer.Serialize(token, BridgeJsonContext.Default.String)),
                "text/javascript");
            if (args.Request.Method == HttpMethod.Head)
            {
                boot.Headers["Content-Length"] = boot.Content!.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                boot.Content.Dispose();
                var head = new WebViewWebResourceResponse { StatusCode = boot.StatusCode, ReasonPhrase = boot.ReasonPhrase };
                foreach (var header in boot.Headers)
                    head.Headers[header.Key] = header.Value;
                boot = head;
            }
            args.Response = boot;
            return;
        }

        // Explicit host/root requests are documents even on engines that omit Accept.
        var document = path.Length == 0 || path == _hostPage || BlazorDocumentPolicy.IsDocumentRequest(args.Request);
        var lookup = document && path.Length == 0
            ? new Uri(_origin, _hostPage)
            : new Uri(args.Request.Uri.GetLeftPart(UriPartial.Path));
        if (!TryGetResponseContent(lookup.AbsoluteUri, document,
                out var status, out var reason, out var content, out var headers))
            throw new InvalidOperationException("The content provider rejected a request inside its application origin.");
        var response = new WebViewWebResourceResponse { StatusCode = status, ReasonPhrase = reason, Content = content };
        foreach (var header in headers)
            response.Headers[header.Key] = header.Value;
        response.Headers["Content-Security-Policy"] = BlazorDocumentPolicy.ContentSecurityPolicy;
        response.Headers["X-Content-Type-Options"] = "nosniff";

        if (document && status == 200 && args.Request.Method == HttpMethod.Get)
            _documentToken ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (args.Request.Method == HttpMethod.Head)
        {
            if (content.CanSeek)
                response.Headers["Content-Length"] = (content.Length - content.Position).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            content.Dispose();
            var head = new WebViewWebResourceResponse { StatusCode = status, ReasonPhrase = reason };
            foreach (var header in response.Headers)
                head.Headers[header.Key] = header.Value;
            response = head;
        }
        args.Response = response;
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        if (_disposed || args.Body is null)
            return;
        BridgeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(args.Body, BridgeJsonContext.Default.BridgeMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejected a non-protocol WebView message.");
            return;
        }
        if (message is null || _documentToken is null || message.Token != _documentToken
            || !Uri.TryCreate(message.Source, UriKind.Absolute, out var source)
            || !BlazorDocumentPolicy.IsWithinOrigin(_origin, source))
        {
            _logger.LogWarning("Rejected a WebView message without the current local-document capability.");
            return;
        }
        // Source is document-declared, not native frame provenance. The capability and
        // closed-document policy establish trust for this experimental host.
        if (message.Kind == "ipc")
            MessageReceived(source, message.Message);
        else if (message.Kind == "error")
            ReportFailure(new InvalidOperationException($"Blazor document error: {message.Message}"));
        else
            _logger.LogWarning("Rejected an unknown Blazor bridge message kind: {Kind}", message.Kind);
    }

    private void ReportFailure(Exception exception)
    {
        _logger.LogError(exception, "The Blazor WebView host failed.");
        UnhandledException?.Invoke(this, exception);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
            return;
        // Component disposal can make final JS interop calls. Keep the bridge alive
        // until Microsoft's page context has finished disposing.
        await base.DisposeAsyncCore();
        _disposed = true;
        _documentToken = null;
        _navigationUri = null;
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.WebResourceRequested -= OnWebResourceRequested;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        await _sendQueue;
    }

    private static WebViewWebResourceResponse TextResponse(string text, string mime) => new()
    {
        Content = new MemoryStream(Encoding.UTF8.GetBytes(text)),
        Headers = { ["Content-Type"] = mime + "; charset=utf-8", ["Cache-Control"] = "no-store" }
    };

    private static string ReadBootScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Avalonia.Controls.WebView.Blazor.boot.js")
            ?? throw new InvalidOperationException("The embedded Blazor bootstrap script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
