# Avalonia Blazor integration: source alpha

This project is the separate integration boundary intended for a future
community-maintained package. It is not an official Avalonia or Microsoft
package. It is currently consumed through a project reference, not published to
NuGet. The native transport still depends on the unreleased implementation in
[AvaloniaUI/Avalonia.Controls.WebView#99](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/pull/99)
and this fork's isolated GTK cancellation fix.

## Public API and ownership

`AvaloniaBlazorWebViewManager` inherits the shipping Microsoft
`Microsoft.AspNetCore.Components.WebView.WebViewManager`. Its constructor accepts
a `NativeWebView`, `IServiceProvider`, `IFileProvider`, and optional host-page and
base-URI settings. It adds `DefaultAppBaseUri` and an `UnhandledException` event
for bootstrap/document and outbound transport failures. Microsoft's error
handling still applies to renderer and application JS interop failures.

Use the inherited `AddRootComponentAsync`, `RemoveRootComponentAsync`, `Navigate`,
`TryDispatchAsync`, and `DisposeAsync` contracts rather than a second renderer,
service-scope, or root-component abstraction. The dispatcher, response policy,
bridge envelope, document capability and bootstrap are implementation details,
not separately supported APIs.

| Owner | Responsibilities |
| --- | --- |
| Avalonia | Native engine creation, embedding, navigation, script execution and local response delivery |
| Microsoft Blazor | Renderer, IPC protocol, page-scoped services, root components and static-content lookup |
| This integration | Adapt the two public contracts; establish bootstrap, local-document policy and ordered cleanup |
| Application | Supply and retain the view, service provider and file provider; handle errors; await cleanup before native removal |

The integration owns neither the supplied view nor the caller's services/file
provider. Blazor owns the per-page scopes it creates. All setup, navigation and
root-component changes should run on Avalonia's UI thread; disposal dispatches
to that thread and concurrent callers await the same cleanup.

## Consumption

Reference this project from a Razor SDK desktop application, using
`-p:WebViewDesktopOnly=true` when building this fork without mobile workloads.
The [executable probe](../../samples/BlazorProbe) is the complete build/publish
example, including the Windows manifest and framework/RCL static assets.

The essential application-owned setup is:

```csharp
var view = new NativeWebView();
var manager = new AvaloniaBlazorWebViewManager(view, services, files);
manager.UnhandledException += OnHostFailure;
await manager.AddRootComponentAsync(typeof(MyRootComponent), "#app", ParameterView.Empty);
manager.Navigate("/");
window.Content = view;
```

Here `services` is configured with `AddBlazorWebView()` and logging; `files`
provides the application's `wwwroot`. The manager must exist before the native
adapter is created. Configure any native environment options before attaching
the view. The host page must contain a matching root selector and load:

```html
<base href="/">
<div id="app">Loading...</div>
<script src="_framework/blazor.webview.js" autostart="false"></script>
<script type="module" src="_avalonia/boot.js"></script>
```

The integration serves `_avalonia/boot.js` itself. Do not copy or configure its
private capability/transport protocol in application code. The external module
installs Microsoft's expected `window.external` bridge before `Blazor.start()`;
a new document-created-script API is not a prerequisite for this controlled
host page.

On window close or view replacement, defer native removal while cleanup runs:

```csharp
await manager.DisposeAsync();
window.Content = null;
```

The complete probe shows cancellation of the initial close request while
asynchronous cleanup finishes. Do not rely on an asynchronous visual-tree
detachment handler to finish before the native view is destroyed. Components
can make final JS calls during disposal, so the bridge must remain alive.

## Alpha scope and package gates

The verified baseline is .NET 10, Microsoft Blazor WebView 10.0.12, Avalonia
desktop 12.1.1, and the fork's PR-99-based native source. It covers default-origin
desktop hosting on Windows WebView2 x64, macOS WKWebView ARM64, and Linux GTK/WPE
x64/ARM64. See the [native evidence and limitations](../../samples/BlazorProbe/README.md).
The optional constructor settings are not a claim that arbitrary origins or
non-root application base paths have been validated.

Keep `IsPackable=false` until a compatible native dependency can be restored by
an external package consumer. A DLL or NuGet archive that silently assumes this
source checkout would not be an independently consumable alpha. Do not bundle
forked native assemblies under an apparently standalone integration package.
A future community package needs its own clearly unofficial identity and a
clean external-consumer build/publish/native test against its declared packages.

Before advertising broader support, validate native message provenance and the
supported local-content trust policy, repeated/error-path lifecycle behavior,
sandbox-enabled Linux and the intended ARM64 device/compositor. Native buffering
and provider cancellation remain separate transport concerns. The current CI
uses software graphics and disables WebKit's sandbox on ephemeral Linux runners.
Trimming/NativeAOT, mobile, arbitrary remote content and MAUI are not validated.

A future MAUI handler can map its properties, root components and packaging onto
this manager. It should not introduce a MAUI dependency into this project, or
confuse MAUI's separate `HybridWebView` protocol with Blazor rendering.

## Relationship to the proposed common WebView API

[ScarletKuro's proposal](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/issues/57#issuecomment-6289668633)
is aligned with this separation. Agree on capabilities and semantics rather than
requiring every engine to expose WebView2's method names or lifecycle shape:

| Proposed capability | Current integration and remaining gap |
| --- | --- |
| Initialize with environment options | Avalonia creates the adapter through its visual-tree lifecycle; `EnvironmentRequested` provides pre-creation options and deferrals. The manager subscribes before attachment. |
| Navigate and execute JavaScript | Existing `Navigate` and `InvokeScript` suffice. Native script APIs do not uniformly await JavaScript Promises; the native probe handles that explicitly. |
| Request filter and response synthesis | PR 99 supplies scheme registration, response status/headers/content and asynchronous deferrals. Our handler selects local requests by URI. A new filter API is not required for this consumer and would not make WebKit intercept arbitrary HTTP(S) responses. |
| Document-created scripts | Valuable for general hosting, but not a prerequisite for a controlled local host page that installs the bridge before explicitly starting Blazor. Any general API should specify ordering, origin and frame scope. |
| Send a string message | We serialize the string safely and invoke the installed receiver through an ordered asynchronous script queue. A native send API could improve the abstraction without being required for the current host. |
| Receive message with source URI | This is the important missing native-context capability. A document-declared URL and random per-document capability are not native source/frame provenance; unknown provenance must not be presented as an authenticated local URL. |
| Cancel navigation and handle new windows | Existing events provide the intended shape, but GTK needed the separately committed explicit native policy-decision correction. |
| Developer tools | Existing environment configuration enables development tools; a cross-platform programmatic open command was not needed for this prototype. |
| Dispose | Avalonia owns the native view lifetime. Blazor page teardown must be awaited first, including components' final JS interop; a synchronous-only host-disposal contract would be insufficient. |

No Microsoft Blazor source changes or public native COM interfaces were needed.
Keep fixes to the general transport in Avalonia and host-specific adaptation
here. Broader APIs can evolve separately without making every enhancement a
condition for merging the local-content primitive.
