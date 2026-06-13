using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.Windows;   // CREDENTIALW + FILETIME blittable structs (the P/Invokes are declared locally — see Native).

namespace FluentGpu.WindowsApi.Credentials;

/// <summary>
/// A thin wrapper over the Win32 Credential Manager (<c>advapi32</c> <c>CredWriteW</c>/<c>CredReadW</c>/
/// <c>CredDeleteW</c>/<c>CredEnumerateW</c>/<c>CredFree</c>) for storing application secrets — Spotify/OAuth refresh
/// tokens for the WAVEE workload — in the per-user Windows Credential Locker (DPAPI-encrypted at rest, keyed by the
/// signed-in user).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why raw CredMan and not PasswordVault / Windows Hello.</b> For a full-trust desktop app, the WinRT
/// <c>PasswordVault</c> reads and writes the <i>same</i> per-user locker as CredMan but, per Microsoft's own
/// maintainer, "doesn't work as expected for full-trust desktop apps" (WASDK Discussion #1840), and it costs a
/// WinRT/<c>ComWrappers</c> activation the repo's COM doctrine forbids on every path. <c>KeyCredentialManager</c>
/// (Windows Hello) is identity-sensitive and unreliable unpackaged. Raw CredMan is the only option that is
/// simultaneously identity-free (works packaged and unpackaged identically), AOT/trim-clean (flat blittable
/// P/Invoke, no <c>ComWrappers</c>/CsWinRT/reflection), and dependency-free. See
/// <c>docs/plans/windowsapi-implementation-research.md</c> §2.4.
/// </para>
/// <para>
/// <b>Key model.</b> A credential is keyed by <c>(TargetName, Type)</c>; this store fixes
/// <c>Type = CRED_TYPE_GENERIC</c> (the blob is application-defined, no auth-package restriction), so the application
/// target string is the whole key. WAVEE encodes multi-account by namespacing the target, e.g.
/// <c>"WAVEE/Spotify/{spotifyUserId}"</c>, and lists accounts with <see cref="Enumerate"/>.
/// </para>
/// <para>
/// <b>Secret encoding.</b> CredMan does not interpret a generic blob, so the secret rides as raw bytes. Callers that
/// hand a string in via <see cref="Store(string,string,string,CredentialScope)"/> get UTF-8 round-tripping; the
/// byte-span overload is pass-through for already-encoded material. <see cref="StoredCredential.Secret"/> is a
/// <c>byte[]</c> the caller can zero after use — <c>SecureString</c> is intentionally avoided (soft-deprecated, weak
/// on Windows, awkward under AOT).
/// </para>
/// <para>
/// <b>Persistence / roaming.</b> <see cref="CredentialScope.LocalMachine"/> (the default) survives reboot and this
/// user's other sessions on this machine. <see cref="CredentialScope.Roaming"/> (<c>CRED_PERSIST_ENTERPRISE</c>)
/// roams to other machines only if the account has a roam-able profile, and never for MSA generic creds — treat
/// cross-device availability as best-effort, do not build features assuming it.
/// </para>
/// <para>
/// <b>Security posture.</b> The locker is shared across all of this user's non-AppContainer processes — any same-user
/// process can read a generic credential it knows the target of. This is the same exposure PasswordVault has for
/// full-trust; it is encrypted at rest but not per-app sandboxed. Do not market it as isolated.
/// </para>
/// <para>
/// <b><c>CredFree</c> discipline.</b> <c>CredReadW</c> and <c>CredEnumerateW</c> allocate native memory the caller
/// owns; every success path frees it exactly once via <c>CredFree</c> in a <c>finally</c>, and never double-frees on
/// the enumerate path (one <c>CredFree</c> releases the whole returned array).
/// </para>
/// <para>
/// Canonical signatures: <c>wincred.h</c> (Windows SDK 10.0.26100.0) — <c>CredWriteW</c>:864, <c>CredReadW</c>:887,
/// <c>CredEnumerateW</c>:920, <c>CredDeleteW</c>:1007, <c>CredFree</c>:1399; <c>CREDENTIALW</c>:485. The
/// <see cref="CREDENTIALW"/> and <see cref="FILETIME"/> blittable structs are reused from
/// <c>TerraFX.Interop.Windows</c>, but the five entry points are declared locally in <see cref="Native"/> via
/// <c>[LibraryImport(... SetLastError = true)]</c>: TerraFX declares them <c>[DllImport(..., ExactSpelling = true)]</c>
/// <i>without</i> <c>SetLastError</c>, so <c>Marshal.GetLastPInvokeError()</c> would not reliably surface
/// <c>ERROR_NOT_FOUND</c> (CredMan reports failure detail only through <c>GetLastError</c>). The local declarations
/// also match the house P/Invoke style (<c>Win32Theme.cs</c>). Only the plain integer <c>#define</c>s TerraFX does not
/// project are restated as constants.
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw">CREDENTIALW (wincred.h)</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw">CredReadW</see></item>
/// <item><see href="https://github.com/microsoft/WindowsAppSDK/discussions/1840">WASDK Discussion #1840 (PasswordVault for full-trust)</see></item>
/// </list>
/// </para>
/// </remarks>
public static partial class CredentialStore
{
    // Plain wincred.h / winerror.h #defines. TerraFX exposes the CredMan P/Invokes and the CREDENTIALW struct but not
    // these integer constants, so they are restated from the SDK headers (10.0.26100.0).
    private const uint CRED_TYPE_GENERIC = 1;                  // wincred.h:442 — blob is application-defined.
    private const uint CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512; // wincred.h:455 — 2560 bytes.
    private const uint CRED_MAX_GENERIC_TARGET_NAME_LENGTH = 32767; // wincred.h:257.
    private const int ERROR_NOT_FOUND = 1168;                  // winerror.h:7563 — no such credential.

    /// <summary>
    /// Writes (creates or overwrites) a generic credential. The secret is encoded UTF-8 (CredMan treats a generic
    /// blob as opaque application bytes). Convenience overload of
    /// <see cref="Store(string,string,System.ReadOnlySpan{byte},CredentialScope)"/>.
    /// </summary>
    /// <param name="target">The credential key (e.g. <c>"WAVEE/Spotify/{userId}"</c>).</param>
    /// <param name="userName">Account display metadata stored alongside the secret (may be empty).</param>
    /// <param name="secret">The secret value; stored as its UTF-8 bytes.</param>
    /// <param name="scope">Persistence scope; defaults to <see cref="CredentialScope.LocalMachine"/>.</param>
    public static void Store(string target, string userName, string secret,
                             CredentialScope scope = CredentialScope.LocalMachine)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Store(target, userName, Encoding.UTF8.GetBytes(secret), scope);
    }

    /// <summary>
    /// Writes (creates or overwrites) a generic credential with an already-encoded secret blob (pass-through, no
    /// transcoding).
    /// </summary>
    /// <param name="target">The credential key (e.g. <c>"WAVEE/Spotify/{userId}"</c>).</param>
    /// <param name="userName">Account display metadata stored alongside the secret (may be empty).</param>
    /// <param name="secret">The raw secret bytes; must be ≤ <c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c> (2560).</param>
    /// <param name="scope">Persistence scope; defaults to <see cref="CredentialScope.LocalMachine"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="target"/> is empty or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="secret"/> exceeds the blob-size ceiling.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The OS rejected the write.</exception>
    public static unsafe void Store(string target, string userName, ReadOnlySpan<byte> secret,
                                    CredentialScope scope = CredentialScope.LocalMachine)
    {
        ValidateTarget(target);
        if (secret.Length > CRED_MAX_CREDENTIAL_BLOB_SIZE)
            throw new ArgumentOutOfRangeException(nameof(secret),
                $"Credential blob is {secret.Length} bytes; the maximum is {CRED_MAX_CREDENTIAL_BLOB_SIZE}. " +
                "Store an indirection (e.g. a DPAPI-protected file path) for larger payloads.");

        userName ??= string.Empty;

        // Pin the managed strings and the secret blob; CredWriteW copies everything synchronously, so the pins only
        // need to outlive the single call.
        fixed (char* pTarget = target)
        fixed (char* pUserName = userName)
        fixed (byte* pSecret = secret)
        {
            CREDENTIALW cred = default;
            cred.Type = CRED_TYPE_GENERIC;
            cred.TargetName = pTarget;
            cred.CredentialBlobSize = (uint)secret.Length;
            cred.CredentialBlob = pSecret;            // NULL-safe: an empty span pins to a null pointer with size 0.
            cred.Persist = (uint)scope;
            cred.UserName = userName.Length == 0 ? null : pUserName;

            if (Native.CredWriteW(&cred, 0) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                    $"CredWriteW failed for target '{target}'.");
        }
    }

    /// <summary>
    /// Reads a generic credential by target. Returns <see langword="false"/> (without throwing) when no such
    /// credential exists.
    /// </summary>
    /// <param name="target">The credential key used at <see cref="Store(string,string,string,CredentialScope)"/>.</param>
    /// <param name="userName">On success, the stored account metadata (empty string if none was stored).</param>
    /// <param name="secret">On success, the raw secret bytes (a fresh array the caller may zero after use).</param>
    /// <returns><see langword="true"/> if the credential was found and read.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The OS failed for a reason other than "not found".</exception>
    public static unsafe bool TryRetrieve(string target, out string userName, out byte[] secret)
    {
        ValidateTarget(target);
        userName = string.Empty;
        secret = Array.Empty<byte>();

        CREDENTIALW* pCred = null;
        fixed (char* pTarget = target)
        {
            if (Native.CredReadW(pTarget, CRED_TYPE_GENERIC, 0, &pCred) == 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ERROR_NOT_FOUND)
                    return false;
                throw new System.ComponentModel.Win32Exception(err, $"CredReadW failed for target '{target}'.");
            }
        }

        // CredReadW allocated a single block (the CREDENTIALW and its referenced buffers); free it exactly once.
        try
        {
            ReadCredential(pCred, out userName, out secret);
            return true;
        }
        finally
        {
            Native.CredFree(pCred);
        }
    }

    /// <summary>Deletes a generic credential by target.</summary>
    /// <param name="target">The credential key.</param>
    /// <returns><see langword="true"/> if a credential was deleted; <see langword="false"/> if none existed.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The OS failed for a reason other than "not found".</exception>
    public static unsafe bool Delete(string target)
    {
        ValidateTarget(target);
        fixed (char* pTarget = target)
        {
            if (Native.CredDeleteW(pTarget, CRED_TYPE_GENERIC, 0) != 0)
                return true;

            int err = Marshal.GetLastPInvokeError();
            if (err == ERROR_NOT_FOUND)
                return false;
            throw new System.ComponentModel.Win32Exception(err, $"CredDeleteW failed for target '{target}'.");
        }
    }

    /// <summary>
    /// Enumerates this user's generic credentials, optionally restricted by a target wildcard filter, returning the
    /// full <see cref="StoredCredential"/> (target, user, secret, last-written) for each. An empty list is returned
    /// when nothing matches.
    /// </summary>
    /// <param name="filter">
    /// A CredMan target filter (a single trailing <c>*</c> wildcard is supported, e.g. <c>"WAVEE/Spotify/*"</c>), or
    /// <see langword="null"/> to enumerate all of the user's credentials. Note CredMan's filter matches across all
    /// credential types; entries that are not <c>CRED_TYPE_GENERIC</c> are skipped so the result is consistent with
    /// what this store writes and reads.
    /// </param>
    public static unsafe IReadOnlyList<StoredCredential> Enumerate(string? filter = null)
    {
        uint count = 0;
        CREDENTIALW** pCreds = null;

        fixed (char* pFilter = filter)   // null string pins to a null pointer → "enumerate all".
        {
            if (Native.CredEnumerateW(pFilter, 0, &count, &pCreds) == 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ERROR_NOT_FOUND)
                    return Array.Empty<StoredCredential>();
                throw new System.ComponentModel.Win32Exception(err, "CredEnumerateW failed.");
            }
        }

        // CredEnumerateW returns ONE allocation: an array of CREDENTIALW* whose entries point inside the same block.
        // A single CredFree on the array pointer releases everything — do not free the entries individually.
        try
        {
            var result = new List<StoredCredential>((int)count);
            for (uint i = 0; i < count; i++)
            {
                CREDENTIALW* cred = pCreds[i];
                if (cred->Type != CRED_TYPE_GENERIC)
                    continue;
                ReadCredential(cred, out string user, out byte[] secret);
                result.Add(new StoredCredential(
                    TargetName: PtrToString(cred->TargetName),
                    UserName: user,
                    Secret: secret,
                    LastWritten: FileTimeToUtc(cred->LastWritten)));
            }
            return result;
        }
        finally
        {
            Native.CredFree(pCreds);
        }
    }

    /// <summary>
    /// Projects an OS-owned <c>CREDENTIALW*</c> into managed copies of its user name and secret blob. Pointer fields
    /// may be null (an absent user name / empty secret), which map to <see cref="string.Empty"/> /
    /// <see cref="Array.Empty{T}"/>. The copies are independent of the native block, so they remain valid after
    /// <c>CredFree</c>.
    /// </summary>
    private static unsafe void ReadCredential(CREDENTIALW* cred, out string userName, out byte[] secret)
    {
        userName = PtrToString(cred->UserName);

        uint blobSize = cred->CredentialBlobSize;
        if (cred->CredentialBlob == null || blobSize == 0)
        {
            secret = Array.Empty<byte>();
        }
        else
        {
            secret = new byte[blobSize];
            new ReadOnlySpan<byte>(cred->CredentialBlob, (int)blobSize).CopyTo(secret);
        }
    }

    /// <summary>Copies a null-terminated UTF-16 native string (or null) into a managed string.</summary>
    private static unsafe string PtrToString(char* p) => p == null ? string.Empty : new string(p);

    /// <summary>Converts a Win32 <c>FILETIME</c> (100 ns ticks since 1601-01-01 UTC) to a UTC <see cref="DateTime"/>.</summary>
    private static DateTime FileTimeToUtc(FILETIME ft)
    {
        long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        // A zero FILETIME (no timestamp) maps to the epoch rather than throwing from DateTime.FromFileTimeUtc.
        return ticks <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(ticks);
    }

    private static void ValidateTarget(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (target.Length >= CRED_MAX_GENERIC_TARGET_NAME_LENGTH)
            throw new ArgumentException(
                $"Credential target exceeds the {CRED_MAX_GENERIC_TARGET_NAME_LENGTH}-character limit.", nameof(target));
    }

    /// <summary>
    /// Local <c>advapi32</c> Credential Manager P/Invokes, declared <c>[LibraryImport(... SetLastError = true)]</c> so
    /// <c>Marshal.GetLastPInvokeError()</c> surfaces <c>ERROR_NOT_FOUND</c> on read/delete/enumerate (TerraFX's own
    /// declarations omit <c>SetLastError</c>; see the type remarks). The <c>CredW*</c> functions return a Win32
    /// <c>BOOL</c> (nonzero = success); blob and string buffers are passed as raw pointers — no string marshalling is
    /// requested because the caller pins <see cref="CREDENTIALW"/>'s <c>char*</c>/<c>byte*</c> fields directly.
    /// </summary>
    private static unsafe partial class Native
    {
        /// <summary><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew">CredWriteW</see> (wincred.h:864).</summary>
        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        internal static partial int CredWriteW(CREDENTIALW* credential, uint flags);

        /// <summary><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw">CredReadW</see> (wincred.h:887).</summary>
        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true)]
        internal static partial int CredReadW(char* targetName, uint type, uint flags, CREDENTIALW** credential);

        /// <summary><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credenumeratew">CredEnumerateW</see> (wincred.h:920).</summary>
        [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true)]
        internal static partial int CredEnumerateW(char* filter, uint flags, uint* count, CREDENTIALW*** credentials);

        /// <summary><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creddeletew">CredDeleteW</see> (wincred.h:1007).</summary>
        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true)]
        internal static partial int CredDeleteW(char* targetName, uint type, uint flags);

        /// <summary><see href="https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credfree">CredFree</see> (wincred.h:1399) — frees a buffer returned by <c>CredReadW</c>/<c>CredEnumerateW</c>.</summary>
        [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
        internal static partial void CredFree(void* buffer);
    }
}
