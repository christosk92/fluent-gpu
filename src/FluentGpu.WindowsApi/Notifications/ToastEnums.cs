using System.Runtime.Versioning;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The toast <c>scenario</c> attribute — it controls how aggressively the platform presents the toast (sound,
/// on-screen dwell, whether it stays until dismissed). Values map 1:1 to the strings WASDK's
/// <c>AppNotificationBuilder::GetScenario()</c> emits (<c>dev/AppNotifications/AppNotificationBuilder/AppNotificationBuilder.cpp:267-283</c>);
/// <see cref="Default"/> emits no attribute.
/// </summary>
/// <remarks>See <see href="https://learn.microsoft.com/en-us/uwp/schemas/tiles/toastschema/element-toast">toast schema — toast element</see>.</remarks>
public enum ToastScenario
{
    /// <summary>Normal toast (no <c>scenario</c> attribute emitted).</summary>
    Default = 0,

    /// <summary><c>scenario='reminder'</c> — stays on screen until the user dismisses or acts on it.</summary>
    Reminder = 1,

    /// <summary><c>scenario='alarm'</c> — like Reminder, plus a looping alarm sound by default.</summary>
    Alarm = 2,

    /// <summary><c>scenario='incomingCall'</c> — full-screen-style call presentation with call-styled buttons.</summary>
    IncomingCall = 3,

    /// <summary><c>scenario='urgent'</c> — high-priority presentation (Windows 10 2004+).</summary>
    Urgent = 4,
}

/// <summary>
/// A built-in Windows notification sound, mapped to its <c>ms-winsoundevent:Notification.*</c> URI exactly as WASDK's
/// <c>GetWinSoundEventString</c> does (<c>dev/AppNotifications/AppNotificationBuilder/AppNotificationBuilderUtility.h:42-97</c>).
/// The <c>Looping*</c> values are only honored by the platform when the toast also sets a looping scenario
/// (<see cref="ToastScenario.Alarm"/> / <see cref="ToastScenario.IncomingCall"/>); a non-looping toast plays them once.
/// </summary>
/// <remarks>See <see href="https://learn.microsoft.com/en-us/uwp/schemas/tiles/toastschema/element-audio">toast schema — audio element</see>.</remarks>
public enum ToastSound
{
    /// <summary>The default notification sound (<c>ms-winsoundevent:Notification.Default</c>).</summary>
    Default = 0,

    /// <summary><c>Notification.IM</c>.</summary>
    IM,

    /// <summary><c>Notification.Mail</c>.</summary>
    Mail,

    /// <summary><c>Notification.Reminder</c>.</summary>
    Reminder,

    /// <summary><c>Notification.SMS</c>.</summary>
    SMS,

    /// <summary><c>Notification.Looping.Alarm</c> (looping; needs a looping scenario).</summary>
    LoopingAlarm,

    /// <summary><c>Notification.Looping.Call</c> (looping; needs a looping scenario).</summary>
    LoopingCall,
}

/// <summary>
/// The platform's reported delivery setting for an AUMID's toasts — the value behind
/// <c>IToastNotifier::get_Setting</c> (projected from <c>TerraFX.Interop.WinRT.NotificationSetting</c>). A
/// successful <c>Show</c> (S_OK) does NOT imply a visible banner: the platform may have accepted-and-suppressed the
/// toast when this is anything other than <see cref="Enabled"/> (e.g. the user turned the app's toasts off, or
/// Focus Assist is on). The spike that proved the WinRT path hit exactly this — <c>Show</c> returned S_OK while the box
/// had toasts disabled, so nothing painted (docs/plans/windowsapi-implementation-research.md §2.1, spike pitfall #2).
/// Surface this so callers can distinguish "suppressed by policy" from "failed".
/// </summary>
/// <remarks>See <see href="https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.notificationsetting">NotificationSetting</see>.</remarks>
[SupportedOSPlatform("windows6.1")]
public enum ToastDeliverySetting
{
    /// <summary>Toasts are enabled for this AUMID — a <c>Show</c> should paint a banner.</summary>
    Enabled = 0,

    /// <summary>The user turned this app's toasts off in Settings.</summary>
    DisabledForApplication = 1,

    /// <summary>The user turned off all toasts for this Windows user.</summary>
    DisabledForUser = 2,

    /// <summary>Group Policy disabled toasts.</summary>
    DisabledByGroupPolicy = 3,

    /// <summary>The app manifest opted out of toasts.</summary>
    DisabledByManifest = 4,

    /// <summary>The setting could not be read (e.g. <c>get_Setting</c> failed); treat as "unknown, may be suppressed".</summary>
    Unknown = 255,
}
