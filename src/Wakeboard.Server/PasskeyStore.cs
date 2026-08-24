using System.Security.Cryptography;

namespace Wakeboard;

public sealed class PasskeyStore(AppPaths paths)
{
    private const int MaxCredentials = 20;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<PasskeyCredential>> ListAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return SettingsStore.Load(paths).Passkeys; }
        finally { gate.Release(); }
    }

    public async Task<string> GetOrCreateUserHandleAsync(CancellationToken cancellationToken = default)
    {
        string? handle = null;
        await UpdateAsync(settings =>
        {
            settings.PasskeyUserHandle ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            handle = settings.PasskeyUserHandle;
        }, cancellationToken);
        return handle!;
    }

    public async Task<PasskeyCredential> AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(settings =>
        {
            if (settings.Passkeys.Count >= MaxCredentials) throw new ArgumentException($"You can register at most {MaxCredentials} passkeys.");
            if (settings.Passkeys.Any(item => item.Id == credential.Id)) throw new ArgumentException("That passkey is already registered.");
            settings.Passkeys.Add(credential);
        }, cancellationToken);
        return credential;
    }

    public async Task UpdateSignCountAsync(string credentialId, uint signCount, DateTimeOffset lastUsedAt, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(settings =>
        {
            var credential = settings.Passkeys.FirstOrDefault(item => item.Id == credentialId)
                ?? throw new KeyNotFoundException("Passkey not found.");
            credential.SignCount = signCount;
            credential.LastUsedAt = lastUsedAt;
        }, cancellationToken);
    }

    public async Task DeleteAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(settings =>
        {
            var index = settings.Passkeys.FindIndex(item => item.Id == credentialId);
            if (index < 0) throw new KeyNotFoundException("Passkey not found.");
            settings.Passkeys.RemoveAt(index);
        }, cancellationToken);
    }

    private async Task UpdateAsync(Action<AppSettings> mutation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = SettingsStore.Load(paths);
            mutation(settings);
            SettingsStore.Save(paths, settings);
        }
        finally { gate.Release(); }
    }
}
