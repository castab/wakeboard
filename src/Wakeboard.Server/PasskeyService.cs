using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Wakeboard;

public sealed class PasskeyService(PasskeyStore store, AuthService auth)
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(2);
    private const string RegisterPurpose = "passkey-register";
    private const string LoginPurpose = "passkey-login";

    public async Task<PasskeyCeremonyResponse> BeginRegistrationAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        RequireEligibleOrigin(request);
        var rpId = PasskeyPolicy.DeriveRpId(request);
        var userHandle = await store.GetOrCreateUserHandleAsync(cancellationToken);
        var existing = await store.ListAsync(cancellationToken);
        var fido2 = BuildFido2(request, rpId);

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Name = "wakeboard", DisplayName = "Wakeboard", Id = Convert.FromBase64String(userHandle) },
            ExcludeCredentials = existing.Select(item => new PublicKeyCredentialDescriptor(Convert.FromBase64String(item.Id))).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Preferred, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var optionsJson = options.ToJson();
        var state = auth.SignState(RegisterPurpose, optionsJson, StateLifetime);
        return new PasskeyCeremonyResponse(ParseElement(optionsJson), state);
    }

    public async Task<PasskeyCredential> CompleteRegistrationAsync(HttpRequest request, string? state, JsonElement attestation, string? label, CancellationToken cancellationToken = default)
    {
        RequireEligibleOrigin(request);
        var rpId = PasskeyPolicy.DeriveRpId(request);
        if (!auth.TryVerifyState(RegisterPurpose, state, out var optionsJson))
            throw new ArgumentException("The registration request expired. Try again.");

        if (attestation.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid passkey response.");
        var options = CredentialCreateOptions.FromJson(optionsJson!);
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestation.GetRawText())
            ?? throw new ArgumentException("Invalid passkey response.");
        var existing = await store.ListAsync(cancellationToken);
        var fido2 = BuildFido2(request, rpId);

        RegisteredPublicKeyCredential result;
        try
        {
            result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = (parameters, _) =>
                    Task.FromResult(!existing.Any(item => item.Id == Convert.ToBase64String(parameters.CredentialId))),
            }, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ArgumentException("Could not register that passkey: " + error.Message);
        }

        var trimmedLabel = string.IsNullOrWhiteSpace(label) ? $"Passkey added {DateTimeOffset.UtcNow:yyyy-MM-dd}" : label.Trim();
        if (trimmedLabel.Length > 100) throw new ArgumentException("Passkey label is too long.");

        var credential = new PasskeyCredential
        {
            Id = Convert.ToBase64String(result.Id),
            PublicKey = Convert.ToBase64String(result.PublicKey),
            SignCount = result.SignCount,
            RpId = rpId,
            Label = trimmedLabel,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        return await store.AddAsync(credential, cancellationToken);
    }

    public async Task<PasskeyCeremonyResponse> BeginLoginAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        RequireEligibleOrigin(request);
        var rpId = PasskeyPolicy.DeriveRpId(request);
        var existing = await store.ListAsync(cancellationToken);
        var allowed = existing.Where(item => item.RpId == rpId)
            .Select(item => new PublicKeyCredentialDescriptor(Convert.FromBase64String(item.Id))).ToList();
        if (allowed.Count == 0) throw new KeyNotFoundException("No passkeys are registered for this address.");

        var fido2 = BuildFido2(request, rpId);
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = allowed, UserVerification = UserVerificationRequirement.Required });
        var optionsJson = options.ToJson();
        var state = auth.SignState(LoginPurpose, optionsJson, StateLifetime);
        return new PasskeyCeremonyResponse(ParseElement(optionsJson), state);
    }

    public async Task<bool> CompleteLoginAsync(HttpRequest request, string? state, JsonElement assertion, CancellationToken cancellationToken = default)
    {
        RequireEligibleOrigin(request);
        if (!auth.TryVerifyState(LoginPurpose, state, out var optionsJson)) return false;
        if (assertion.ValueKind != JsonValueKind.Object) return false;

        AuthenticatorAssertionRawResponse response;
        try
        {
            response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertion.GetRawText()) ?? throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException) { return false; }

        var rpId = PasskeyPolicy.DeriveRpId(request);
        var existing = await store.ListAsync(cancellationToken);
        var credentialId = Convert.ToBase64String(response.RawId);
        var stored = existing.FirstOrDefault(item => item.Id == credentialId && item.RpId == rpId);
        if (stored is null) return false;

        var options = AssertionOptions.FromJson(optionsJson!);
        var fido2 = BuildFido2(request, rpId);
        try
        {
            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = Convert.FromBase64String(stored.PublicKey),
                StoredSignatureCounter = stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
            }, cancellationToken);
            await store.UpdateSignCountAsync(stored.Id, result.SignCount, DateTimeOffset.UtcNow, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }

    private static void RequireEligibleOrigin(HttpRequest request)
    {
        if (!PasskeyPolicy.IsEligibleOrigin(request)) throw new ArgumentException("Passkeys require HTTPS or http://localhost.");
    }

    private static Fido2 BuildFido2(HttpRequest request, string rpId) => new(new Fido2Configuration
    {
        ServerDomain = rpId,
        ServerName = "Wakeboard",
        Origins = new HashSet<string> { $"{request.Scheme}://{request.Host}" },
    });

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
