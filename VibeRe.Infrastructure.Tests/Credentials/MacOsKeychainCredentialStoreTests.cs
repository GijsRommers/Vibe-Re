using VibeRe.Core.Auth;
using VibeRe.Infrastructure.Credentials;

namespace VibeRe.Infrastructure.Tests.Credentials;

public class MacOsKeychainCredentialStoreTests
{
    [Fact]
    public void Factory_selects_macOS_keychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.IsType<MacOsKeychainCredentialStore>(CredentialStoreFactory.CreateDefault());
    }

    [Fact]
    public async Task Keychain_round_trips_and_deletes_a_credential()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var store = new MacOsKeychainCredentialStore($"vibe-re-test-{Guid.NewGuid():N}");
        const string providerId = "test-provider";
        try
        {
            Assert.Null(await store.GetAsync(providerId));
            await store.SetAsync(new Credential(providerId, "test-secret with spaces"));
            Assert.Equal("test-secret with spaces", (await store.GetAsync(providerId))?.Secret);
            await store.SetAsync(new Credential(providerId, "replacement-secret"));
            Assert.Equal("replacement-secret", (await store.GetAsync(providerId))?.Secret);
            Assert.True(await store.DeleteAsync(providerId));
            Assert.Null(await store.GetAsync(providerId));
            Assert.False(await store.DeleteAsync(providerId));
        }
        finally
        {
            await store.DeleteAsync(providerId);
        }
    }
}
