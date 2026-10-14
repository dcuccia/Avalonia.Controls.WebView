using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.WebView.Blazor;
using Avalonia.Platform;
using Avalonia.Threading;
using BlazorProbe.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace BlazorProbe;

internal static class Program
{
    internal static bool Smoke { get; private set; }
    internal static string? Backend { get; private set; }
    internal static string? ReportPath { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        Smoke = args.Contains("--smoke");
        Backend = Argument(args, "--backend");
        ReportPath = Argument(args, "--report");
        return AppBuilder.Configure<ProbeApp>().UsePlatformDetect()
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] }).LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1]
            : throw new ArgumentException($"Missing value for {name}.");
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Width = 900, Height = 600, Title = "Avalonia Blazor probe" };
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await RunAsync(window, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Yield();
        var checks = new List<string>();
        string? adapter = null;
        string? failure = null;
        var stopwatch = Stopwatch.StartNew();
        using var services = new ServiceCollection().AddBlazorWebView()
            .AddSingleton<ProbeState>()
            .AddLogging(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider();
        using var files = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        AvaloniaBlazorWebViewManager? manager = null;
        try
        {
            for (var cycle = 0; cycle < (Program.Smoke ? 2 : 1); cycle++)
            {
                var view = new NativeWebView { AdapterPreference = Preference() };
                var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                view.NavigationCompleted += (_, args) =>
                {
                    Console.WriteLine($"Navigation: {args.Request}, success={args.IsSuccess}");
                    if (args.Request is { } uri && AvaloniaBlazorWebViewManager.DefaultAppBaseUri.IsBaseOf(uri))
                    {
                        if (args.IsSuccess)
                            navigated.TrySetResult();
                        else
                            navigated.TrySetException(new InvalidOperationException($"Navigation failed: {uri}"));
                    }
                };
                var hostFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                manager = new AvaloniaBlazorWebViewManager(view, services, files);
                manager.UnhandledException += (_, ex) => hostFailure.TrySetResult(ex);
                view.EnvironmentRequested += (_, args) =>
                {
                    args.EnableDevTools = !Program.Smoke;
                    if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
                        windows.UserDataFolder = Path.Combine(Path.GetTempPath(), "avalonia-blazor-probe");
                };
                await manager.AddRootComponentAsync(typeof(ProbePanel), "#app", ParameterView.Empty);
                manager.Navigate(cycle == 0 ? "/" : "/deep/link");
                window.Content = view;
                await navigated.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await WaitAsync(view, "() => document.querySelector('#probe')?.dataset.ready === 'true'", hostFailure.Task);
                adapter = view.AdapterInfo?.Type.ToString();
                Check(adapter is not null, "native-adapter", checks);
                if (Program.Backend is not null)
                    Check(adapter == (Program.Backend == "gtk" ? "WebKitGtk" : "WpeWebKit"), "requested-backend", checks);
                Console.WriteLine($"Native adapter: {adapter}");
                if (!Program.Smoke)
                {
                    var closeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Closing(object? sender, WindowClosingEventArgs args)
                    {
                        args.Cancel = true;
                        closeRequested.TrySetResult();
                    }
                    window.Closing += Closing;
                    await closeRequested.Task;
                    await manager.DisposeAsync();
                    manager = null;
                    window.Closing -= Closing;
                    window.Close();
                    return;
                }
                Check(services.GetRequiredService<ProbeState>().LastResult == "js:dotnet:probe", "interop-roundtrip", checks);
                Check(await EvaluateAsync(view, "() => document.documentElement.dataset.initializer === 'loaded'"),
                    "rcl-initializer", checks);
                Check(await EvaluateAsync(view,
                    "() => getComputedStyle(document.querySelector('#rcl-marker')).color === 'rgb(0, 128, 0)'"),
                    "rcl-stylesheet", checks);
                Check(await EvaluateAsync(view,
                    "async () => (await (await fetch('/_content/BlazorProbe.Components/asset.json')).json()).value === 42"),
                    "rcl-json-asset", checks);
                Check(await EvaluateAsync(view,
                    "async () => { const r = await fetch('/missing.js'); return r.status === 404 && !(await r.text()).includes('<html'); }"),
                    "missing-asset-404", checks);
                Check(await EvaluateAsync(view,
                    "async () => { const r = await fetch('/deep/link', { headers: { Accept: 'text/html' } }); return r.status === 200 && (await r.text()).includes('id=\"app\"'); }"),
                    "document-fallback", checks);
                // A fetched document must not rotate the active IPC capability.
                await view.InvokeScript("document.querySelector('#increment').click();");
                await WaitAsync(view, "() => document.querySelector('#click-count')?.textContent === '1'", hostFailure.Task);
                checks.Add("blazor-dom-event");
                Check(await EvaluateAsync(view,
                    "async () => { try { await fetch('https://example.invalid/'); return false; } catch { return true; } }"),
                    "external-fetch-blocked", checks);
                await view.InvokeScript("invokeCSharpAction('{\"token\":\"forged\",\"source\":\"app://localhost/\",\"kind\":\"ipc\",\"message\":\"bad\"}');");
                await view.InvokeScript("document.querySelector('#increment').click();");
                await WaitAsync(view, "() => document.querySelector('#click-count')?.textContent === '2'", hostFailure.Task);
                checks.Add("forged-ipc-rejected");
                manager.Navigate("/deep/link");
                await WaitAsync(view, "() => location.pathname === '/deep/link' && document.querySelector('#probe')?.dataset.ready === 'true' && document.querySelector('#click-count')?.textContent === '0'", hostFailure.Task);
                checks.Add("deep-link-navigation");
                var renderCount = services.GetRequiredService<ProbeState>().RenderCount;
                Check(view.Refresh(), "refresh-requested", checks);
                await WaitAsync(view, "() => document.querySelector('#probe')?.dataset.ready === 'true'", hostFailure.Task,
                    () => services.GetRequiredService<ProbeState>().RenderCount > renderCount);
                checks.Add("reload");
                Console.WriteLine("Disposing Blazor host");
                await manager.DisposeAsync();
                manager = null;
                window.Content = null;
                checks.Add("host-disposed");
                Console.WriteLine("Blazor host disposed");
                await Task.Delay(150);
            }
            Check(services.GetRequiredService<ProbeState>().DisposeCount >= 4, "component-lifecycle", checks);
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            Console.Error.WriteLine(failure);
        }
        finally
        {
            if (Program.Smoke)
            {
                if (manager is not null)
                    await manager.DisposeAsync();
                var report = JsonSerializer.Serialize(new ProbeReport(
                    failure is null, adapter, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    checks, failure, stopwatch.Elapsed.TotalSeconds), ProbeReportContext.Default.ProbeReport);
                Console.WriteLine(report);
                if (Program.ReportPath is { } path)
                    File.WriteAllText(path, report);
                desktop.Shutdown(failure is null ? 0 : 1);
            }
        }
    }

    private static IReadOnlyList<WebViewAdapterType>? Preference() => Program.Backend switch
    {
        "gtk" => [WebViewAdapterType.WebKitGtk],
        "wpe" => [WebViewAdapterType.WpeWebKit],
        null when OperatingSystem.IsWindows() => [WebViewAdapterType.WebView2],
        null => null,
        _ => throw new ArgumentException("The backend must be gtk or wpe.")
    };

    private static void Check(bool success, string name, List<string> checks)
    {
        if (!success)
            throw new InvalidOperationException($"Probe check failed: {name}");
        checks.Add(name);
        Console.WriteLine($"PASS {name}");
    }

    private static async Task WaitAsync(NativeWebView view, string predicate, Task<Exception> failure,
        Func<bool>? managedPredicate = null)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (failure.IsCompleted)
                throw await failure;
            if (view.AdapterInfo is not null && (managedPredicate?.Invoke() ?? true)
                && await EvaluateAsync(view, predicate))
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Native Blazor probe did not satisfy {predicate}. Adapter: {view.AdapterInfo}");
    }

    private static async Task<bool> EvaluateAsync(NativeWebView view, string expression)
    {
        // Native InvokeScript APIs don't all await JS promises. Poll a plain string result.
        var id = "__probe" + Guid.NewGuid().ToString("N");
        await view.InvokeScript($"window.{id}='pending'; Promise.resolve().then({expression}).then(v=>window.{id}=v?'true':'false',e=>window.{id}='error:'+e);");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var value = await view.InvokeScript($"window.{id}");
            value = value?.Trim('"');
            if (value == "true" || value == "false")
            {
                await view.InvokeScript($"delete window.{id}");
                return value == "true";
            }
            if (value?.StartsWith("error:", StringComparison.Ordinal) == true)
                throw new InvalidOperationException(value);
            await Task.Delay(20);
        }
        throw new TimeoutException("Native script evaluation did not complete.");
    }
}
