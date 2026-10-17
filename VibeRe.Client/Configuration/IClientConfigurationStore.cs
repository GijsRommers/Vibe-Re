namespace VibeRe.Client.Configuration;

public interface IClientConfigurationStore
{
    Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update,
        CancellationToken cancellationToken = default);
}
