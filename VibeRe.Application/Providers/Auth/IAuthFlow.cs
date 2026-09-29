namespace VibeRe.Application.Providers.Auth;

public interface IAuthFlow
{
    string ProviderName { get; }
    
    Task<AuthStart>
}