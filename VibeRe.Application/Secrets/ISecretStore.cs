namespace VibeRe.Application.Secrets;

public interface ISecretStore
{
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}