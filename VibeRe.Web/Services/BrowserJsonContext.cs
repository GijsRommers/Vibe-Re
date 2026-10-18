using System.Text.Json.Serialization;
using VibeRe.Contracts;

namespace VibeRe.Web.Services;

// Explicit metadata keeps browser HTTP and SignalR payloads safe under publish trimming.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AuthStatusReply))]
[JsonSerializable(typeof(BrowserLoginReply))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(AccessStatusReply))]
[JsonSerializable(typeof(BeginPairingRequest))]
[JsonSerializable(typeof(PairingReply))]
[JsonSerializable(typeof(RedeemPairingRequest))]
[JsonSerializable(typeof(ApprovePairingRequest))]
[JsonSerializable(typeof(DeviceReply[]))]
[JsonSerializable(typeof(BrowserCsrfReply))]
[JsonSerializable(typeof(BrowserErrorReply))]
internal partial class BrowserJsonContext : JsonSerializerContext;

internal sealed record BrowserCsrfReply(string RequestToken);
internal sealed record BrowserErrorReply(string Message);
