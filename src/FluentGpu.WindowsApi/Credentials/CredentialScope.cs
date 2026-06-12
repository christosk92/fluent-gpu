namespace FluentGpu.WindowsApi.Credentials;

/// <summary>
/// Persistence scope for a stored credential — maps 1:1 to the Win32 <c>CRED_PERSIST_*</c> values
/// (<c>wincred.h</c> 10.0.26100.0:461-463) written into <c>CREDENTIALW.Persist</c>.
/// </summary>
public enum CredentialScope
{
    /// <summary><c>CRED_PERSIST_SESSION</c> — lives only for the current logon session; gone at sign-out.</summary>
    Session = 1,

    /// <summary>
    /// <c>CRED_PERSIST_LOCAL_MACHINE</c> — survives reboot and this user's other sessions on this computer.
    /// The recommended default for persisting an OAuth refresh token.
    /// </summary>
    LocalMachine = 2,

    /// <summary>
    /// <c>CRED_PERSIST_ENTERPRISE</c> — like <see cref="LocalMachine"/>, and additionally roams to the user's
    /// other computers <i>if</i> the account has a roam-able profile. Best-effort: it persists only locally when
    /// there is no roaming profile, and MSA does not sync generic credentials across devices.
    /// </summary>
    Roaming = 3,
}
