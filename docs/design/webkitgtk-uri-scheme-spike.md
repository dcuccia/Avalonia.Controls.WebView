# Spike: WebKitGTK/WPE custom‑scheme local‑content serving for `NativeWebView`

> **Status: illustrative design spike — NOT wired into the build.** P/Invoke signatures are written to match the repo's existing style (`GtkInterop.cs`) and the public WebKitGTK C API, but they must be validated against the installed `libwebkit2gtk` version before use. The goal is a concrete, reviewable starting point to accompany the design discussion in **dcuccia/Avalonia.Controls.WebView#1**, and to mirror Microsoft's shipping WKWebView `IWKUrlSchemeHandler` approach (`dotnet/maui`) as closely as the GTK API allows.

## 1. Why a new mechanism (not the existing event)

Today `GtkWebViewAdapter` raises `WebResourceRequested` from the WebKit **`resource-load-started`** signal (`ResourceLoadStartedCallback`). That signal is **observational** — it fires once the network layer is already loading the resource, and (per the in‑code comment) headers there are effectively immutable. There is no way to *supply a response body* from it, so it cannot serve app‑local content.

Serving local content in WebKit (GTK **and** WPE, both WebKit‑based) is done with a **custom URI scheme handler**, registered on the `WebKitWebContext` **before** the web view is created:

```
webkit_web_context_register_uri_scheme(context, "app", callback, userData, destroy);
// ...later, inside the callback, when we have bytes:
webkit_uri_scheme_request_finish(request, gInputStream, length, contentType);
```

This is the exact structural analogue of MAUI's WKWebView handler
(`config.SetUrlSchemeHandler(new SchemeHandler(), "app")` →
`StartUrlSchemeTask` → `DidReceiveResponse` + `DidReceiveData` + `DidFinish`).

The two mechanisms are complementary:

| Concern | Mechanism | Can serve body? |
|---|---|---|
| Observe/inspect network requests | `resource-load-started` (existing) | ❌ |
| Serve app‑local content offline | custom‑scheme handler (this spike) | ✅ |

## 2. Proposed public surface (unified, emulates Microsoft)

A single interception surface plus a registered scheme list keeps parity with the existing event and with WebView2/Android (which already respond via their intercept). New types live in `Avalonia.Controls.WebView.Core`.

```csharp
// New response type (mirrors what Blazor's StaticContentProvider already produces).
public sealed class WebViewWebResourceResponse
{
    public required Stream Content { get; init; }
    public int StatusCode { get; init; } = 200;
    public string ReasonPhrase { get; init; } = "OK";
    public required string ContentType { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

// Extend the existing args so a handler can *fulfill* the request.
public sealed class WebResourceRequestedEventArgs : EventArgs
{
    public required WebViewWebResourceRequest Request { get; init; }

    // NEW:
    public void SetResponse(WebViewWebResourceResponse response);   // fulfill locally
    public IDisposable GetDeferral();                              // async providers (large wasm)
    internal WebViewWebResourceResponse? Response { get; }         // consumed by the adapter
}
```

Scheme registration must happen at context‑creation time on WebKit, so it is exposed through the existing `*EnvironmentRequested` seam:

```csharp
public sealed class GtkWebViewEnvironmentRequestedEventArgs : WebViewEnvironmentRequestedEventArgs
{
    // NEW: schemes that should be intercepted for local content (default: "app").
    public ICollection<string> LocalContentSchemes { get; } = new List<string> { "app" };
}
```

**Contract:** for a request whose scheme is in `LocalContentSchemes`, the adapter raises `WebResourceRequested`; if the handler calls `SetResponse(...)`, the adapter fulfils the native request with that stream; otherwise it errors the scheme request (or, for `http`‑filtered engines, lets the network proceed). Engines that respond via an event (WebView2 `CreateWebResourceResponse`, Android `ShouldInterceptRequest`) honour `SetResponse` in their existing intercept — so the surface is uniform across all five backends.

## 3. New P/Invoke (add to `GtkInterop.cs`)

Matches existing conventions (`LibWebKit = "libwebkit2gtk"`, `LibGio`, `LibGLib`; mix of `DllImport`/`LibraryImport`).

```csharp
// --- WebKit URI scheme registration + fulfilment ---
[DllImport(LibWebKit)]
internal static extern void webkit_web_context_register_uri_scheme(
    IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme,
    IntPtr callback /* WebKitURISchemeRequestCallback */, IntPtr userData, IntPtr destroyNotify);

[DllImport(LibWebKit)]
internal static extern IntPtr webkit_uri_scheme_request_get_uri(IntPtr request);

[DllImport(LibWebKit)]
internal static extern IntPtr webkit_uri_scheme_request_get_web_view(IntPtr request);

[DllImport(LibWebKit)]
internal static extern void webkit_uri_scheme_request_finish(
    IntPtr request, IntPtr stream /* GInputStream* */, long streamLength,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string? contentType);

[DllImport(LibWebKit)]
internal static extern void webkit_uri_scheme_request_finish_error(IntPtr request, IntPtr error /* GError* */);

// --- Register the scheme as secure + CORS-enabled so fetch()/ES modules/Blazor work ---
[DllImport(LibWebKit)]
internal static extern IntPtr webkit_web_context_get_security_manager(IntPtr context);

[DllImport(LibWebKit)]
internal static extern void webkit_security_manager_register_uri_scheme_as_secure(
    IntPtr securityManager, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme);

[DllImport(LibWebKit)]
internal static extern void webkit_security_manager_register_uri_scheme_as_cors_enabled(
    IntPtr securityManager, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme);

// --- Wrap managed bytes as a GInputStream (GLib owns + frees the copy) ---
[DllImport(LibGio)]
internal static extern IntPtr g_memory_input_stream_new_from_data(IntPtr data, long len, IntPtr destroyNotify);

[DllImport(LibGLib)]
internal static extern IntPtr g_malloc(UIntPtr nBytes);
// g_free is passed as the GInputStream destroy notify:
internal static readonly IntPtr GFreeFuncPtr = NativeLibrary.GetExport(NativeLibrary.Load(LibGLib), "g_free");
```

## 4. `GtkUriSchemeHandler` — managed shape (mirrors MAUI `SchemeHandler`)

```csharp
namespace Avalonia.Controls;

internal static class GtkUriSchemeHandler
{
    // Registered per WebKitWebContext, before the web view is created.
    public static void Register(IntPtr context, GtkWebViewAdapter adapter, IEnumerable<string> schemes)
    {
        var security = GtkInterop.webkit_web_context_get_security_manager(context);
        foreach (var scheme in schemes)
        {
            // Keep 'adapter' reachable for the native callback via the same state table
            // GtkSignal uses elsewhere (GCHandle-backed), matching the repo pattern.
            var state = GtkSignal.AllocState(adapter);
            GtkInterop.webkit_web_context_register_uri_scheme(
                context, scheme, s_callbackPtr, state, IntPtr.Zero);

            // Make the custom scheme behave like https for the web platform.
            GtkInterop.webkit_security_manager_register_uri_scheme_as_secure(security, scheme);
            GtkInterop.webkit_security_manager_register_uri_scheme_as_cors_enabled(security, scheme);
        }
    }

    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_callback = &OnRequest;
    private static readonly IntPtr s_callbackPtr = (IntPtr)s_callback;

    // WebKitURISchemeRequestCallback: void (WebKitURISchemeRequest* request, gpointer userData)
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequest(IntPtr request, IntPtr userData)
    {
        if (!GtkSignal.TryGetState<GtkWebViewAdapter>(userData, out var adapter)
            || adapter.WebResourceRequested is not { } handler)
        {
            GtkInterop.webkit_uri_scheme_request_finish_error(request, IntPtr.Zero);
            return;
        }

        var uri = Marshal.PtrToStringUTF8(GtkInterop.webkit_uri_scheme_request_get_uri(request));
        if (uri is null) { GtkInterop.webkit_uri_scheme_request_finish_error(request, IntPtr.Zero); return; }

        var args = new WebResourceRequestedEventArgs
        {
            Request = new WebViewWebResourceRequest
            {
                Method = HttpMethod.Get,
                Uri = new Uri(uri),
                Headers = DictionaryNativeHttpRequestHeaders.ImmutableInstance,
            }
        };

        // The app (e.g. a WebViewManager or HybridRoot provider) fills the response.
        handler.Invoke(adapter, args);

        if (args.Response is not { } response)
        {
            GtkInterop.webkit_uri_scheme_request_finish_error(request, IntPtr.Zero); // 404-ish
            return;
        }

        // Copy managed bytes into GLib-owned memory, hand off as a GInputStream, then finish.
        // (finish() must occur on the GLib thread; adapter marshals via its RunOnGlibThread helper.)
        var bytes = ReadAll(response.Content);
        var native = GtkInterop.g_malloc((UIntPtr)bytes.Length);
        Marshal.Copy(bytes, 0, native, bytes.Length);
        var stream = GtkInterop.g_memory_input_stream_new_from_data(native, bytes.Length, GtkInterop.GFreeFuncPtr);

        GtkInterop.webkit_uri_scheme_request_finish(request, stream, bytes.Length, response.ContentType);
        // NOTE: g_object_unref(stream) after finish per GLib ownership rules (elided).
    }

    private static byte[] ReadAll(Stream s)
    {
        if (s is MemoryStream ms) return ms.ToArray();
        using var buffer = new MemoryStream();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }
}
```

## 5. Wiring into `GtkWebViewAdapter` (one call site)

In the constructor, **after** the `context` is obtained and **before** `webkit_web_view_new_with_context(context)`:

```csharp
context = /* existing creation logic */;

// NEW: register local-content schemes on the context before the view exists.
GtkUriSchemeHandler.Register(context, this, args.LocalContentSchemes);

WebViewHandle = webkit_web_view_new_with_context(context);
```

Nothing else in the adapter changes; the existing `resource-load-started` observational path stays as‑is for network requests.

## 6. How the consumers plug in

- **BlazorWebView (downstream, `Avalonia.Controls.Maui`):** an `AvaloniaBlazorWebViewManager : WebViewManager` handles `WebResourceRequested` by calling the base `TryGetResponseContent(uri, …)` and, on success, `args.SetResponse(new WebViewWebResourceResponse { Content = stream, ContentType = headers["Content-Type"], … })`. This is the `PhotinoWebViewManager.HandleWebRequest` shape, 1:1.
- **HybridWebView (downstream):** the handler resolves `HybridRoot/<relativePath>` from the app package and calls `SetResponse(...)` — mirroring `HybridWebViewHandler.iOS.GetResponseBytesAsync`.
- **Navigation:** load `app://0.0.0.0/` via the existing `webkit_web_view_load_uri`.

## 7. WPE

WPE is WebKit‑based and exposes the same `webkit_web_context_register_uri_scheme` / `webkit_uri_scheme_request_finish` family, so `WpeWebViewAdapter` reuses `GtkUriSchemeHandler` (or a near‑copy) against the WPE context. Offscreen/headless composition is unaffected — this is purely the network/resource path.

## 8. Known gaps / follow‑ups for real implementation

1. **WebKit version drift:** GTK4 / WebKit 6.0 renames some context APIs and splits data management into `WebKitNetworkSession`; verify `register_uri_scheme` / `finish` signatures per `libwebkit2gtk-4.0` vs `-4.1` vs `-6.0`. The repo already probes `webkit_get_*_version()`.
2. **GLib threading & ownership:** `finish()` on the GLib thread; `g_object_unref` the stream after finish; confirm `g_free` destroy‑notify semantics for the copied buffer. Reuse the adapter's `RunOnGlibThread*` helpers; avoid the sync‑dispatch deadlock the existing code warns about.
3. **Streaming large assets:** replace the read‑all copy with a `GInputStream` that streams from the managed `Stream` (custom GInputStream subclass) + `GetDeferral()` for async providers.
4. **Range requests / HEAD:** `WebViewWebResourceRequest.Method` is currently hard‑coded to GET; wasm/media may want range support.
5. **Scheme security:** confirm `register_uri_scheme_as_secure` + `_as_cors_enabled` is sufficient for Blazor's `_framework` module loading and service‑worker‑free operation.

---

*Cross‑references:* design rationale and Microsoft prior‑art in **dcuccia/Avalonia.Controls.WebView#1**; templates in `dotnet/maui` `HybridWebViewHandler.iOS.cs` (`SchemeHandler`) and `dotnet/aspnetcore` `PhotinoWebViewManager.cs` / `StaticContentProvider.cs`.
