using System;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// A single user-task entry in the taskbar Jump List — a labeled command that relaunches the app's exe with arguments.
/// Composes with the v1 <see cref="FluentGpu.WindowsApi.Activation.ProtocolRegistrar"/>: a task's
/// <paramref name="Arguments"/> can carry a <c>wavee://…</c>-style deep link (or any CLI flag), so clicking the task
/// runs <c>ExePath Arguments</c>, which the running/cold-launched app parses via
/// <see cref="FluentGpu.WindowsApi.Activation.ActivationArgs"/>.
/// </summary>
/// <param name="Title">The visible label of the task (shown in the Jump List). Required.</param>
/// <param name="ExePath">Absolute path to the executable to launch — typically <see cref="Environment.ProcessPath"/>.</param>
/// <param name="Arguments">Command-line arguments passed to <paramref name="ExePath"/> (e.g. a deep-link URI or a flag
/// like <c>--play-liked</c>). May be empty.</param>
/// <param name="IconPath">Optional path to an icon file (<c>.ico</c>/<c>.exe</c>/<c>.dll</c>) for the task's glyph,
/// using the standard <c>"path,index"</c> resource convention via <see cref="IconIndex"/>. Null = no icon.</param>
/// <param name="Description">Optional tooltip text shown on hover. Null = none.</param>
/// <param name="IconIndex">The icon resource index within <paramref name="IconPath"/> (default 0). Ignored when
/// <paramref name="IconPath"/> is null.</param>
public readonly record struct JumpTask(
    string Title,
    string ExePath,
    string Arguments,
    string? IconPath = null,
    string? Description = null,
    int IconIndex = 0);

/// <summary>
/// The taskbar Jump List's custom user-tasks section, over <c>ICustomDestinationList</c> (the Windows 7+ API).
/// <see cref="SetTasks"/> rebuilds the entire user-tasks list; <see cref="Clear"/> removes the custom list. Each task is
/// an <c>IShellLinkW</c> (the same shell-link object a <c>.lnk</c> file wraps) whose visible label is written through
/// its <c>IPropertyStore</c> as <c>PKEY_Title</c> — the documented way to title a Jump List task that is a launch
/// command rather than a document. Flat call-OUT COM (hand-declared CLSIDs, <c>__uuidof&lt;T&gt;()</c> IIDs,
/// <c>iface-&gt;Method(...)</c> through TerraFX vtable structs); AOT-clean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading / apartment.</b> The Jump List COM objects are apartment-threaded; call <see cref="SetTasks"/> /
/// <see cref="Clear"/> on the <b>UI (STA) thread</b>. <see cref="EnsureSta"/> initializes COM (STA) on the calling
/// thread, tolerating the benign already-initialized results. These do not take a window handle — a Jump List is
/// per-application (keyed by AUMID), not per-window.
/// </para>
/// <para>
/// <b>AUMID.</b> If <c>aumid</c> is supplied, <c>ICustomDestinationList::SetAppID</c> targets that
/// application's Jump List — pass the SAME AUMID the app sets via
/// <c>SetCurrentProcessExplicitAppUserModelID</c> / the v1 toast registration, or the tasks attach to the wrong (or no)
/// taskbar group. If null, the list targets the process's current AUMID (the shell's default association for this exe).
/// </para>
/// <para>
/// <b>The BeginList → AddUserTasks → CommitList transaction.</b> <c>BeginList</c> opens an edit and reports how many
/// slots the shell will show (and which items the user has removed — honored implicitly here by simply not re-adding
/// removed items, which a full rebuild does not track; this is a deliberate simplicity choice for v1). The user tasks
/// are added as one <c>IObjectArray</c> (an <c>IObjectCollection</c> of links), then <c>CommitList</c> publishes the
/// list atomically. Any failure aborts via <c>AbortList</c> so a half-built list is never committed.
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-icustomdestinationlist">ICustomDestinationList</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/shell/nse-jumplist">Jump Lists (custom tasks)</see></item>
/// <item><c>PKEY_Title</c> = <c>{F29F85E0-4FF9-1068-AB91-08002B27B3D9}, 2</c> — the property-system Title key
/// (<c>propkey.h</c>, <c>PROPERTYKEY{ fmtid = FMTID_SummaryInformation, pid = PIDSI_TITLE(2) }</c>).</item>
/// <item>CLSIDs <c>CLSID_DestinationList</c>, <c>CLSID_EnumerableObjectCollection</c>, <c>CLSID_ShellLink</c> from the
/// Windows SDK <c>ShObjIdl_core.h</c> / <c>ShlObj_core.h</c> (TerraFX exposes the interfaces but not the coclass
/// CLSIDs as fields).</item>
/// </list>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.1")] // ICustomDestinationList shipped in Windows 7.
public static unsafe class JumpList
{
    // ── coclass CLSIDs (ShObjIdl_core.h / ShlObj_core.h); restated in the house style — TerraFX has no CLSID_* field. ──
    // CLSID_DestinationList {77F10CF0-3DB5-4966-B520-B7C54FD35ED6}
    private static readonly Guid CLSID_DestinationList =
        new(0x77F10CF0, 0x3DB5, 0x4966, 0xB5, 0x20, 0xB7, 0xC5, 0x4F, 0xD3, 0x5E, 0xD6);
    // CLSID_EnumerableObjectCollection {2D3468C1-36A7-43B6-AC24-D3F02FD9607A}
    private static readonly Guid CLSID_EnumerableObjectCollection =
        new(0x2D3468C1, 0x36A7, 0x43B6, 0xAC, 0x24, 0xD3, 0xF0, 0x2F, 0xD9, 0x60, 0x7A);
    // CLSID_ShellLink {00021401-0000-0000-C000-000000000046}
    private static readonly Guid CLSID_ShellLink =
        new(0x00021401, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    // PKEY_Title {F29F85E0-4FF9-1068-AB91-08002B27B3D9}, pid 2 (propkey.h). fmtid is FMTID_SummaryInformation; the title
    // is PIDSI_TITLE = 2. This labels a launch-command shell link in the Jump List.
    private static readonly Guid FMTID_SummaryInformation =
        new(0xF29F85E0, 0x4FF9, 0x1068, 0xAB, 0x91, 0x08, 0x00, 0x2B, 0x27, 0xB3, 0xD9);
    private const uint PIDSI_TITLE = 2;

    // S_FALSE (already-initialized STA, same model) is a positive HRESULT, so `hr < 0` already tolerates it; only the
    // changed-model result needs an explicit exemption.
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary>
    /// Replace the custom user-tasks section of the application's Jump List with <paramref name="tasks"/> (in order).
    /// Passing an empty <paramref name="tasks"/> commits an empty user-tasks list (use <see cref="Clear"/> to remove the
    /// custom list entirely). UI/STA thread.
    /// </summary>
    /// <param name="aumid">The target application's AUMID, or <see langword="null"/> to use the process's current AUMID.
    /// Must match the AUMID the app otherwise advertises.</param>
    /// <param name="tasks">The tasks to publish, in display order.</param>
    /// <exception cref="InvalidOperationException">A COM step failed; the partial list is aborted, not committed.</exception>
    public static void SetTasks(string? aumid = null, params JumpTask[] tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        EnsureSta();

        ICustomDestinationList* list = CreateDestinationList();
        bool listBegun = false;
        try
        {
            if (!string.IsNullOrEmpty(aumid))
                fixed (char* pAumid = aumid)
                    ThrowIfFailed(list->SetAppID(pAumid), "ICustomDestinationList.SetAppID");

            // BeginList opens the edit and hands back the (ignored here) removed-items array and the visible slot count.
            uint maxSlots = 0;
            Guid iidObjArray = __uuidof<IObjectArray>();
            IObjectArray* removed = null;
            ThrowIfFailed(list->BeginList(&maxSlots, &iidObjArray, (void**)&removed), "ICustomDestinationList.BeginList");
            listBegun = true;
            if (removed != null) removed->Release();   // we do not reconcile against user-removed items in v1.

            IObjectArray* taskArray = BuildTaskArray(tasks);
            try
            {
                ThrowIfFailed(list->AddUserTasks(taskArray), "ICustomDestinationList.AddUserTasks");
                ThrowIfFailed(list->CommitList(), "ICustomDestinationList.CommitList");
                listBegun = false;   // committed — no abort needed.
            }
            finally
            {
                if (taskArray != null) taskArray->Release();
            }
        }
        finally
        {
            if (listBegun) list->AbortList();   // never leave a half-built list open.
            list->Release();
        }
    }

    /// <summary>
    /// Remove the application's custom Jump List (user tasks and any custom categories). The shell falls back to its
    /// default Jump List for the app. UI/STA thread.
    /// </summary>
    /// <param name="aumid">The target AUMID, or <see langword="null"/> for the process's current AUMID.</param>
    /// <exception cref="InvalidOperationException">The delete failed.</exception>
    public static void Clear(string? aumid = null)
    {
        EnsureSta();
        ICustomDestinationList* list = CreateDestinationList();
        try
        {
            if (string.IsNullOrEmpty(aumid))
            {
                ThrowIfFailed(list->DeleteList(null), "ICustomDestinationList.DeleteList");
            }
            else
            {
                fixed (char* pAumid = aumid)
                    ThrowIfFailed(list->DeleteList(pAumid), "ICustomDestinationList.DeleteList");
            }
        }
        finally { list->Release(); }
    }

    // ── construction helpers ───────────────────────────────────────────────────────────────────────────────────────

    private static ICustomDestinationList* CreateDestinationList()
    {
        Guid clsid = CLSID_DestinationList;
        Guid iid = __uuidof<ICustomDestinationList>();
        ICustomDestinationList* list = null;
        ThrowIfFailed(
            CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&list),
            "CoCreateInstance(CLSID_DestinationList)");
        return list;
    }

    /// <summary>Build an <c>IObjectCollection</c> of titled <c>IShellLinkW</c>s and return it QI'd as
    /// <c>IObjectArray</c> (what <c>AddUserTasks</c> consumes). The caller releases the returned array.</summary>
    private static IObjectArray* BuildTaskArray(JumpTask[] tasks)
    {
        Guid clsidColl = CLSID_EnumerableObjectCollection;
        Guid iidColl = __uuidof<IObjectCollection>();
        IObjectCollection* collection = null;
        ThrowIfFailed(
            CoCreateInstance(&clsidColl, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iidColl, (void**)&collection),
            "CoCreateInstance(CLSID_EnumerableObjectCollection)");
        try
        {
            foreach (JumpTask task in tasks)
            {
                IShellLinkW* link = CreateTaskLink(task);
                try
                {
                    // AddObject takes IUnknown*; the link is added by reference (the collection AddRefs it).
                    ThrowIfFailed(collection->AddObject((IUnknown*)link), "IObjectCollection.AddObject");
                }
                finally { if (link != null) link->Release(); }
            }

            IObjectArray* array = null;
            Guid iidArray = __uuidof<IObjectArray>();
            ThrowIfFailed(collection->QueryInterface(&iidArray, (void**)&array), "QI IObjectArray");
            return array;
        }
        finally { collection->Release(); }
    }

    /// <summary>Create one <c>IShellLinkW</c> for a task: exe path, arguments, optional icon/description, and the visible
    /// title written through the link's <c>IPropertyStore</c> as <c>PKEY_Title</c>. Caller releases the returned link.</summary>
    private static IShellLinkW* CreateTaskLink(JumpTask task)
    {
        ArgumentException.ThrowIfNullOrEmpty(task.Title);
        ArgumentException.ThrowIfNullOrEmpty(task.ExePath);

        Guid clsid = CLSID_ShellLink;
        Guid iid = __uuidof<IShellLinkW>();
        IShellLinkW* link = null;
        ThrowIfFailed(
            CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&link),
            "CoCreateInstance(CLSID_ShellLink)");
        try
        {
            fixed (char* pExe = task.ExePath)
                ThrowIfFailed(link->SetPath(pExe), "IShellLinkW.SetPath");

            fixed (char* pArgs = task.Arguments ?? string.Empty)
                ThrowIfFailed(link->SetArguments(pArgs), "IShellLinkW.SetArguments");

            if (!string.IsNullOrEmpty(task.Description))
                fixed (char* pDesc = task.Description)
                    ThrowIfFailed(link->SetDescription(pDesc), "IShellLinkW.SetDescription");

            if (!string.IsNullOrEmpty(task.IconPath))
                fixed (char* pIcon = task.IconPath)
                    ThrowIfFailed(link->SetIconLocation(pIcon, task.IconIndex), "IShellLinkW.SetIconLocation");

            SetLinkTitle(link, task.Title);
            return link;
        }
        catch
        {
            link->Release();
            throw;
        }
    }

    /// <summary>
    /// Write the link's display title via its <c>IPropertyStore</c> (<c>IShellLinkW</c> QIs to <c>IPropertyStore</c>):
    /// set <c>PKEY_Title</c> to a string <c>PROPVARIANT</c> built with <c>InitPropVariantFromString</c>, then
    /// <c>Commit</c>. The <c>PROPVARIANT</c> is always cleared with <c>PropVariantClear</c> (it owns a heap copy of the
    /// string after <c>InitPropVariantFromString</c>).
    /// </summary>
    private static void SetLinkTitle(IShellLinkW* link, string title)
    {
        IPropertyStore* store = null;
        Guid iid = __uuidof<IPropertyStore>();
        ThrowIfFailed(link->QueryInterface(&iid, (void**)&store), "QI IPropertyStore");
        try
        {
            PROPERTYKEY key = default;
            key.fmtid = FMTID_SummaryInformation;
            key.pid = PIDSI_TITLE;

            PROPVARIANT pv = default;
            fixed (char* pTitle = title)
                ThrowIfFailed(InitPropVariantFromString(pTitle, &pv), "InitPropVariantFromString(Title)");
            try
            {
                ThrowIfFailed(store->SetValue(&key, &pv), "IPropertyStore.SetValue(PKEY_Title)");
                ThrowIfFailed(store->Commit(), "IPropertyStore.Commit");
            }
            finally
            {
                PropVariantClear(&pv);
            }
        }
        finally { store->Release(); }
    }

    // ── apartment ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ensure the calling thread is in an STA (the Jump List COM objects require it). Tolerates benign
    /// already-initialized results; does not balance with <c>CoUninitialize</c> (the host owns apartment lifetime).</summary>
    private static void EnsureSta()
    {
        int hr = (int)CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
            ThrowIfFailed(hr, "CoInitializeEx(APARTMENTTHREADED)");
    }

    private static void ThrowIfFailed(HRESULT hr, string what)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{what} failed (0x{(uint)hr:X8}).");
    }
}
