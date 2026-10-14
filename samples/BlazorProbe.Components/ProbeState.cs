namespace BlazorProbe.Components;

public sealed class ProbeState
{
    public int RenderCount { get; set; }
    public int DisposeCount { get; set; }
    public string? LastResult { get; set; }
}
