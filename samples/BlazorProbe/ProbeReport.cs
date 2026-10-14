using System.Text.Json.Serialization;

namespace BlazorProbe;

internal sealed record ProbeReport(
    bool Passed, string? Adapter, string Runtime, List<string> Checks, string? Failure, double ElapsedSeconds);

[JsonSerializable(typeof(ProbeReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal partial class ProbeReportContext : JsonSerializerContext;
