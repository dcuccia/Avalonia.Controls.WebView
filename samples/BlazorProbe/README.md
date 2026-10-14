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
The GitHub workflow uses Xvfb and a session bus on Linux. GTK runs on Ubuntu
24.04; WPE runs in Debian 13 containers because the required WPE packages are
not available in Ubuntu 24.04. Both execute natively on their runner architecture.
For GTK the probe uses Avalonia's existing `X11PlatformOptions.UseGLibMainLoop`
so the UI framework owns GLib integration. Start with `GDK_BACKEND=x11`, including
under WSL/Xvfb; do not let an inherited Wayland GDK setting initialize GTK before
the X11 adapter is created. No private event loop or forced process exit is used.

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
- Query-string asset lookup, HEAD body/length semantics and method rejection.
- Missing assets remain 404s; document requests can fall back to the host page.
- Deep-link navigation and reload reattach the Blazor renderer.
- Same-document anchor navigation preserves working interop.
- Forged bridge messages are rejected, external navigation is cancelled, and
  external fetches emit the expected CSP violation rather than just failing DNS.
- Two complete host creation/disposal cycles dispose every rendered component,
  including concurrent disposal callers while final JS interop is pending.

`--report` produces a JSON result with the actual adapter, runtime, checks and
failure details. A nonzero exit code indicates failure. The `Blazor native
probe` GitHub Actions workflow runs the published, self-contained application
on Windows WebView2, macOS WKWebView, and GTK/WPE on Linux x64 and ARM64. It also
runs the upstream response-contract tests and our document-policy tests.
Each job uploads its application and native report.

### Verified baseline

[Run 38628511676](https://github.com/dcuccia/Avalonia.Controls.WebView/actions/runs/38628511676)
passed all six native jobs on 2026-10-14 at code commit `0140f91`.
Each ran the published app through both host cycles and exited successfully;
a passing JSON report alone was not treated as proof of process teardown.

| Host | Native architecture | Result |
| --- | --- | --- |
| Windows / WebView2 | x64 | Passed |
| macOS / WKWebView | ARM64 | Passed |
| Ubuntu 24.04 / GTK | x64 and ARM64 | Passed |
| Debian 13 / WPE | x64 and ARM64 | Passed |

The Linux jobs use software graphics, Xvfb and the sandbox setting described
below. These results establish that baseline, not unrestricted deployment support.

The probe exposed a GTK cancellation defect independent of Blazor: returning
`TRUE` from `decide-policy` without making a decision lets WebKit default to
`use`, even when the managed event was cancelled. This fork calls
`webkit_policy_decision_ignore` for cancelled navigation and handled new-window
requests, following [WebKit's policy contract](https://webkitgtk.org/reference/webkit2gtk/stable/signal.WebView.decide-policy.html).
The smoke checks both cancellation and continued interop in the original page.

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
Concurrent disposal callers share one UI-dispatched cleanup operation; a second
caller cannot disconnect the bridge while the first is still disposing components.

The Razor SDK and Microsoft package provide the static-web-asset manifest in
development and copy framework/RCL assets into published `wwwroot`. The sample
uses the same manager in both modes; no absolute developer asset paths should
be needed in the published application.

`boot.js` necessarily implements Microsoft's global
`window.external.sendMessage/receiveMessage` transport contract. Application
interop remains in an exported, collocated `.razor.js` module with a typed C#
wrapper. The host page explicitly disables automatic Blazor startup until the
transport is installed.

### OSS implementation lineage

Prefer the maintained implementation over a parallel framework:

| Surface | Reference and reuse |
| --- | --- |
| Renderer, IPC, DI page scopes, static assets | [ASP.NET Core `WebViewManager`](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Components/WebView/WebView/src/WebViewManager.cs), directly inherited through the shipping 10.0.12 package |
| Dispatcher adaptation | [MAUI `MauiDispatcher`](https://github.com/dotnet/maui/blob/main/src/BlazorWebView/src/Maui/MauiDispatcher.cs); our adapter similarly maps each overload to the UI framework's existing dispatcher |
| WebKit bridge initialization | [MAUI Apple host](https://github.com/dotnet/maui/blob/main/src/BlazorWebView/src/Maui/iOS/BlazorWebViewHandler.iOS.cs); explicitly provide `window.external`, which WebKit does not supply, before `Blazor.start()` |
| Non-Windows host contract | [MAUI Tizen manager](https://github.com/dotnet/maui/blob/main/src/BlazorWebView/src/Maui/Tizen/TizenWebViewManager.cs), using the same manager/dispatcher/file-provider layering |
| URI-to-file lookup | [MAUI query normalization](https://github.com/dotnet/maui/blob/main/src/BlazorWebView/src/SharedSource/QueryStringHelper.cs); we use the standard `Uri` API to exclude query and fragment without duplicating that internal helper |
| Native engines and UI lifetime | Existing Avalonia `NativeWebView`, desktop lifetime, dispatcher, and adapter implementation; GTK uses the public [GLib-loop option](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.X11/X11Platform.cs), not an additional native engine wrapper |

These are architecture/protocol references, not wholesale source copies.
Platform-specific handlers are not interchangeable: Tizen is a useful
Linux-based precedent, not desktop GTK/WPE support. Native provenance, request
cancellation and response behavior must be checked independently rather than
copied blindly from a platform handler.

The Linux/native comparison also covered [Microsoft Playwright's process launcher](https://github.com/microsoft/playwright/blob/main/packages/utils/processLauncher.ts)
and [SkiaSharp's native-object ownership](https://github.com/mono/SkiaSharp/blob/main/binding/SkiaSharp/SKObject.cs).
The transferable patterns are explicit resource ownership, awaited cleanup,
native prerequisite/launch diagnostics, and checking actual process termination
rather than treating a completed application report as proof of clean shutdown.
Playwright's separate browser-process termination strategies do not belong in
this in-process host; SkiaSharp's native-handle wrappers are not needed in glue
that already delegates native ownership to Avalonia. Neither adds an application
dependency. ASP.NET Core remains the primary source for Blazor semantics;
Avalonia remains the owner of rendering and native event-loop integration.

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
