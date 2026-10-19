namespace VibeRe.Client.Application;

// Host-provided capabilities; shared presentation state does not inspect the operating system.
public sealed record ClientCapabilities(bool SupportsLocalServer);
