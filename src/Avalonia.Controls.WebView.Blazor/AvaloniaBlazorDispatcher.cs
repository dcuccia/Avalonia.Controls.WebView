using Microsoft.AspNetCore.Components;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace Avalonia.Controls.WebView.Blazor;

public sealed class AvaloniaBlazorDispatcher : Dispatcher
{
    public override bool CheckAccess() => UiDispatcher.UIThread.CheckAccess();

    public override Task InvokeAsync(Action workItem) =>
        UiDispatcher.UIThread.InvokeAsync(workItem).GetTask();

    public override Task InvokeAsync(Func<Task> workItem) =>
        UiDispatcher.UIThread.InvokeAsync(workItem);

    public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem) =>
        UiDispatcher.UIThread.InvokeAsync(workItem).GetTask();

    public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem) =>
        UiDispatcher.UIThread.InvokeAsync(workItem);
}
