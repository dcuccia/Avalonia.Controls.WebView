using System.Text.Json.Serialization;

namespace Avalonia.Controls.WebView.Blazor;

internal sealed record BridgeMessage(string Token, string Source, string Kind, string Message);

[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class BridgeJsonContext : JsonSerializerContext;
