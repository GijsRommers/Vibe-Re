using Velopack;

namespace VibeRe.Updates;

public enum UpdateStage
{
    Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart, Applying, Unsupported, Error
}

public sealed record UpdateStatus(UpdateStage Stage, string Message, string? CurrentVersion = null,
    string? AvailableVersion = null, int? DownloadPercent = null)
{
    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Applying;
}

public sealed record UpdateCandidate(SemanticVersion Version, string Channel, string RuntimeIdentifier,
    string? ReleaseNotes = null);

/// <summary>The service controls consent and sequencing; the backend owns release selection and verification.</summary>
public interface IUpdateBackend
{
    SemanticVersion? CurrentVersion { get; }
    string RuntimeIdentifier { get; }
    string Channel { get; }
    string? UnavailableReason { get; }
    Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken cancellationToken);
    Task VerifyAsync(UpdateCandidate candidate, CancellationToken cancellationToken);
    void ApplyAndRestart(UpdateCandidate candidate);
}

public sealed class UpdateSafetyException(string message) : Exception(message);
