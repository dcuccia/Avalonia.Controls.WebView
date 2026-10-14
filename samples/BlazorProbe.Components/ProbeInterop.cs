using Microsoft.JSInterop;

namespace BlazorProbe.Components;

internal sealed class ProbeInterop(IJSRuntime runtime) : IAsyncDisposable
{
    private const string ModulePath = "./_content/BlazorProbe.Components/ProbePanel.razor.js";
    private IJSObjectReference? _module;

    public async Task<string> RoundTripAsync(DotNetObjectReference<ProbePanel> component)
    {
        _module = await runtime.InvokeAsync<IJSObjectReference>("import", ModulePath);
        return await _module.InvokeAsync<string>("roundTrip", component);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The native document may already be gone during page-context disposal.
            }
        }
    }
}
