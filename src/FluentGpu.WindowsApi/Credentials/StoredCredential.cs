using System;

namespace FluentGpu.WindowsApi.Credentials;

/// <summary>
/// A generic credential read back from the Windows Credential Manager via
/// <see cref="CredentialStore.Enumerate(string)"/> — a managed snapshot independent of the native
/// <c>CREDENTIALW</c> block it was copied from.
/// </summary>
/// <param name="TargetName">The credential key (the target string supplied at store time, e.g. <c>"WAVEE/Spotify/{userId}"</c>).</param>
/// <param name="UserName">The account metadata stored alongside the secret; <see cref="string.Empty"/> if none was stored.</param>
/// <param name="Secret">
/// The raw secret bytes (UTF-8 when written through the string overload of <see cref="CredentialStore.Store(string,string,string,CredentialScope)"/>).
/// The caller owns this array and may zero it after use; an empty secret is <see cref="Array.Empty{T}"/>.
/// </param>
/// <param name="LastWritten">The UTC time the credential was last written, from <c>CREDENTIALW.LastWritten</c> (<see cref="DateTime.MinValue"/> if unset).</param>
public readonly record struct StoredCredential(
    string TargetName,
    string UserName,
    byte[] Secret,
    DateTime LastWritten);
