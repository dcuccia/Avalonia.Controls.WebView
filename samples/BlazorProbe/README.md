# Experimental Avalonia Blazor host

This fork exercises [AvaloniaUI/Avalonia.Controls.WebView#99](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/pull/99)
at `ce503516105e9661733707f0d34570ec02c59763`. Its upstream commits are preserved.
The Blazor integration is separate, experimental, not an official Avalonia
package, and not currently packaged for NuGet.

## Run

Install .NET 10 and the platform WebView runtime. Initialize the pinned build
dependency with `git submodule update --init --recursive`.

```powershell
dotnet run --project samples\BlazorProbe\BlazorProbe.csproj -p:WebViewDesktopOnly=true
```

For automated native integration evidence:

```powershell
dotnet run --project samples\BlazorProbe\BlazorProbe.csproj -p:WebViewDesktopOnly=true -- --smoke --report native-report.json
```

`--backend gtk` and `--backend wpe` select an exhaustive Linux adapter preference:
the probe fails rather than silently passing on another engine. Linux needs the
corresponding WebKitGTK 4.1 or WPE WebKit 2.0 runtime and a graphical session.
The GitHub workflow uses Xvfb and a session bus on Linux.

`WebViewDesktopOnly` restricts the existing source projects to .NET 10 so this
consumer does not require Android/browser workloads. It does not change their
normal multi-target build. Do not set `TargetFrameworks` globally for consumer
builds: it changes single-target projects into outer builds and can break NuGet
build-task imports.

## What is proved

The smoke executable renders real Razor components inside the native engine;
it is not a headless browser simulation. It checks:

- C# events and a .NET-to-JS-to-.NET round trip through a collocated RCL module.
- RCL CSS, JSON, JavaScript initializer and Microsoft framework assets.
- Missing assets remain 404s; document requests can fall back to the host page.
- Deep-link navigation and reload reattach the Blazor renderer.
- Forged bridge messages are rejected and external fetches are blocked.
- Two complete host creation/disposal cycles dispose component instances.

`--report` produces a JSON result with the actual adapter, runtime, checks and
failure details. A nonzero exit code indicates failure. The `Blazor native
probe` GitHub Actions workflow runs the published, self-contained application
on Windows WebView2, macOS WKWebView, and GTK/WPE on Linux x64 and ARM64. It also
runs the upstream response-contract tests and our document-policy tests.
Each job uploads its application and native report.

## Architecture

`src/Avalonia.Controls.WebView.Blazor` references Microsoft's shipping
`Microsoft.AspNetCore.Components.WebView` package. It supplies an Avalonia
dispatcher, a `WebViewManager` adapter, and a small JavaScript transport
bootstrap. It does not reimplement Blazor's renderer or IPC protocol, host an
HTTP server, or copy private WebView2 COM interfaces.

Create `AvaloniaBlazorWebViewManager` before the `NativeWebView` enters the
visual tree. Supply an `IServiceProvider` configured with `AddBlazorWebView()`
and an `IFileProvider` for the host's assets. Register root components and
navigate. Dispose the manager **before** destroying/removing the native view:
component disposal may make final JS interop calls, so the bridge stays alive
until Microsoft's page-context disposal completes.

The Razor SDK and Microsoft package provide the static-web-asset manifest in
development and copy framework/RCL assets into published `wwwroot`. The sample
uses the same manager in both modes; no absolute developer asset paths should
be needed in the published application.

`boot.js` necessarily implements Microsoft's global
`window.external.sendMessage/receiveMessage` transport contract. Application
interop remains in an exported, collocated `.razor.js` module with a typed C#
wrapper. The host page explicitly disables automatic Blazor startup until the
transport is installed.

## Trust boundary and limitations

The public WebView message event carries a body, **not authenticated native
frame/source metadata**. This host does not claim otherwise. It uses an
unpredictable document capability, rotates it on native navigation, validates
the document-declared application URL, rejects other messages, blocks external
navigation/new windows, and sends a restrictive CSP forbidding frames and
external connections. This is a **closed, trusted local-document experiment**,
not a security certification for untrusted content. Do not weaken the CSP or
load arbitrary plugins/remote scripts without revisiting the trust model.
Native message provenance remains a useful general-purpose upstream API.

GTK/WPE cannot replace arbitrary network responses; CSP is the enforcement
there. On engines that omit `Sec-Fetch-Dest`, document fallback uses `Accept:
text/html`, not file-extension guessing. WebKit response buffering and lack of
provider cancellation remain upstream constraints. A seekable `Stream` does
not imply constant-memory native transport.

The CI Linux smoke jobs explicitly disable WebKit's sandbox inside ephemeral
hosted runners. They prove integration, **not sandbox-enabled deployment**.
Sandbox, compositor/GPU behavior, memory budgets and actual device validation
remain deployment gates. CI ARM64 is architecture coverage, not a substitute
for the intended embedded device.

NativeAOT is intentionally not claimed. Normal publishing is the first
baseline; trimming/AOT must be investigated separately with retained assets,
component metadata and dynamic JS-invokable methods.

MAUI is not a dependency. A future MAUI `BlazorWebViewHandler` should reuse the
manager, dispatcher, file-provider and lifecycle contract rather than copy this
sample window. It still needs MAUI root-component/property mapping and asset
packaging. MAUI `HybridWebView` is a different protocol, and HTTP request bodies,
network access policy and cancellation may require additional general primitives.

No upstream issue comment or review is posted by the CI workflow.
