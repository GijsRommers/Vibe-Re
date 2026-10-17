namespace VibeRe.Client.Configuration;

public interface IServerConnectionManager
{
    Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task<ClientConfiguration> AddAsync(string name, string address, CancellationToken cancellationToken = default, bool startLocally = false);
    Task<ClientConfiguration> SelectAsync(string name, CancellationToken cancellationToken = default);
    Task<ClientConfiguration> RemoveAsync(string name, CancellationToken cancellationToken = default);
    Task<ClientConfiguration> UpdateAddressAsync(string name, string address, CancellationToken cancellationToken = default);
    Task<ClientConfiguration> SetLocalStartupAsync(string name, bool enabled, CancellationToken cancellationToken = default);
    Task<ClientConfiguration> SetTokenAsync(string name, string token, CancellationToken cancellationToken = default);
}
