using System;
using System.Collections.Generic;
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
/// A single custom-category destination in the taskbar Jump List — the same launch-command shape as
/// <see cref="JumpTask"/> (exe + arguments), published under a named category via
/// <see cref="JumpList.SetCategory"/>. Distinct from a user task only in where the shell draws it (a titled group
/// above the Tasks section, rather than the Tasks section itself).
/// </summary>
/// <param name="Title">The visible label of the item. Required.</param>
/// <param name="ExePath">Absolute path to the executable to launch — typically <see cref="Environment.ProcessPath"/>.</param>
/// <param name="Arguments">Command-line arguments passed to <paramref name="ExePath"/>. Used as the identity when
/// filtering against the user-removed list returned by <c>BeginList</c> — items the user pinned-off MUST NOT be
/// re-added in the same transaction or <c>CommitList</c>/<c>AppendCategory</c> fails.</param>
/// <param name="IconPath">Optional icon file path. Null = no icon.</param>
/// <param name="Description">Optional tooltip text. Null = none.</param>
/// <param name="IconIndex">The icon resource index within <paramref name="IconPath"/> (default 0).</param>
public readonly record struct JumpListItem(
    string Title,
    string ExePath,
    string Arguments,
    string? IconPath = null,
    string? Description = null,
    int IconIndex = 0);

/// <summary>
/// The taskbar Jump List's custom user-tasks section and custom categories, over <c>ICustomDestinationList</c>
/// (the Windows 7+ API). <see cref="SetTasks"/> rebuilds the user-tasks list; <see cref="SetCategory"/> rebuilds
/// tasks plus one named custom category in a single Begin/Commit transaction (a Jump List cannot be edited
/// incrementally — every publish is a full rebuild). <see cref="Clear"/> removes the custom list. Each entry is
/// an <c>IShellLinkW</c> whose visible label is written through its <c>IPropertyStore</c> as <c>PKEY_Title</c>.
/// Flat call-OUT COM (hand-declared CLSIDs, <c>__uuidof&lt;T&gt;()</c> IIDs, <c>iface-&gt;Method(...)</c> through
/// TerraFX vtable structs); AOT-clean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading / apartment.</b> The Jump List COM objects are apartment-threaded; call <see cref="SetTasks"/> /
/// <see cref="SetCategory"/> / <see cref="Clear"/> on the <b>UI (STA) thread</b>. <see cref="EnsureSta"/> initializes
/// COM (STA) on the calling thread, tolerating the benign already-initialized results. These do not take a window
/// handle — a Jump List is per-application (keyed by AUMID), not per-window.
/// </para>
/// <para>
/// <b>AUMID.</b> If <c>aumid</c> is supplied, <c>ICustomDestinationList::SetAppID</c> targets that
/// application's Jump List — pass the SAME AUMID the app sets via
/// <c>SetCurrentProcessExplicitAppUserModelID</c> / the v1 toast registration, or the tasks attach to the wrong (or no)
/// taskbar group. If null, the list targets the process's current AUMID (the shell's default association for this exe).
/// </para>
/// <para>
/// <b>The BeginList → AddUserTasks / AppendCategory → CommitList transaction.</b> <c>BeginList</c> opens an edit,
/// reports the visible slot count, and returns the <c>IObjectArray</c> of destinations the user has removed. Those
/// items MUST NOT be re-added in the same transaction or <c>AppendCategory</c>/<c>CommitList</c> fails — this type
/// filters category items (and user tasks) against that list by comparing the shell-link <c>GetArguments</c> string
/// to <see cref="JumpListItem.Arguments"/> / <see cref="JumpTask.Arguments"/>. User tasks are added as one
/// <c>IObjectArray</c>, then (for <see cref="SetCategory"/>) one custom category via <c>AppendCategory</c>, then
/// <c>CommitList</c> publishes atomically. Any failure aborts via <c>AbortList</c> so a half-built list is never
/// committed. Because <c>BeginList</c> starts a fresh list, a caller that wants to keep tasks when publishing a
/// category must pass them to <see cref="SetCategory"/> in the same call.
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

    // GetArguments buffer: shell-link arguments can exceed MAX_PATH; 4 KiB is the documented INFOTIPSIZE-class ceiling
    // used by explorer for Jump List argument compares.
    private const int ArgumentsBufferChars = 4096;

    // S_FALSE (already-initialized STA, same model) is a positive HRESULT, so `hr < 0` already tolerates it; only the
    // changed-model result needs an explicit exemption.
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary>
    /// Replace the custom user-tasks section of the application's Jump List with <paramref name="tasks"/> (in order).
    /// Passing an empty <paramref name="tasks"/> commits an empty user-tasks list (use <see cref="Clear"/> to remove the
    /// custom list entirely). UI/STA thread. Tasks whose arguments match an item the user removed are skipped (the
    /// <c>BeginList</c> removed-items contract — re-adding them fails the commit).
    /// </summary>
    /// <param name="aumid">The target application's AUMID, or <see langword="null"/> to use the process's current AUMID.
    /// Must match the AUMID the app otherwise advertises.</param>
    /// <param name="tasks">The tasks to publish, in display order.</param>
    /// <exception cref="InvalidOperationException">A COM step failed; the partial list is aborted, not committed.</exception>
    public static void SetTasks(string? aumid = null, params JumpTask[] tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        Commit(aumid, tasks, categoryTitle: null, categoryItems: null);
    }

    /// <summary>
    /// Rebuild the Jump List with one named custom category and, optionally, the user-tasks section, in a single
    /// <c>BeginList</c>/<c>CommitList</c> transaction. Category items (and tasks) whose arguments match a
    /// user-removed destination are filtered out — re-adding them makes <c>AppendCategory</c>/<c>CommitList</c> fail.
    /// Because a Jump List cannot be patched in place, pass any tasks that should survive in the same call; omitting
    /// them clears the Tasks section. UI/STA thread.
    /// </summary>
    /// <param name="categoryTitle">The visible category heading (e.g. "Recent albums"). Required.</param>
    /// <param name="items">The category destinations, in display order. Empty is legal (the category is then omitted).</param>
    /// <param name="tasks">Optional user tasks published in the same transaction. Null or empty = no Tasks section.</param>
    /// <param name="aumid">The target AUMID, or <see langword="null"/> for the process's current AUMID.</param>
    /// <exception cref="InvalidOperationException">A COM step failed; the partial list is aborted, not committed.</exception>
    public static void SetCategory(string categoryTitle, JumpListItem[] items, JumpTask[]? tasks = null, string? aumid = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(categoryTitle);
        ArgumentNullException.ThrowIfNull(items);
        Commit(aumid, tasks ?? [], categoryTitle, items);
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

    // ── transaction ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One BeginList → (AddUserTasks) → (AppendCategory) → CommitList. <paramref name="categoryTitle"/> null skips
    /// the category; <paramref name="categoryItems"/> is ignored in that case. Removed destinations from
    /// <c>BeginList</c> are collected by argument string and applied as a filter on both arrays.
    /// </summary>
    private static void Commit(string? aumid, JumpTask[] tasks, string? categoryTitle, JumpListItem[]? categoryItems)
    {
        EnsureSta();

        ICustomDestinationList* list = CreateDestinationList();
        bool listBegun = false;
        IObjectArray* removed = null;
        try
        {
            if (!string.IsNullOrEmpty(aumid))
                fixed (char* pAumid = aumid)
                    ThrowIfFailed(list->SetAppID(pAumid), "ICustomDestinationList.SetAppID");

            uint maxSlots = 0;
            Guid iidObjArray = __uuidof<IObjectArray>();
            ThrowIfFailed(list->BeginList(&maxSlots, &iidObjArray, (void**)&removed), "ICustomDestinationList.BeginList");
            listBegun = true;

            HashSet<string> removedArgs = CollectRemovedArguments(removed);

            IObjectArray* taskArray = BuildLinkArray(tasks, removedArgs);
            try
            {
                ThrowIfFailed(list->AddUserTasks(taskArray), "ICustomDestinationList.AddUserTasks");
            }
            finally
            {
                if (taskArray != null) taskArray->Release();
            }

            if (categoryTitle is not null && categoryItems is not null)
            {
                IObjectArray* catArray = BuildLinkArray(categoryItems, removedArgs);
                try
                {
                    uint count = 0;
                    catArray->GetCount(&count);
                    if (count > 0)
                    {
                        fixed (char* pTitle = categoryTitle)
                            ThrowIfFailed(list->AppendCategory(pTitle, catArray), "ICustomDestinationList.AppendCategory");
                    }
                }
                finally
                {
                    if (catArray != null) catArray->Release();
                }
            }

            ThrowIfFailed(list->CommitList(), "ICustomDestinationList.CommitList");
            listBegun = false;   // committed — no abort needed.
        }
        finally
        {
            if (removed != null) removed->Release();
            if (listBegun) list->AbortList();   // never leave a half-built list open.
            list->Release();
        }
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

    /// <summary>
    /// Walk the <c>BeginList</c> removed-items array, QIing each as <c>IShellLinkW</c> and reading
    /// <c>GetArguments</c>. Non-link destinations (e.g. <c>IShellItem</c> documents) are skipped — this pillar
    /// publishes launch-command links, so argument-string identity is the matching key.
    /// </summary>
    private static HashSet<string> CollectRemovedArguments(IObjectArray* removed)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (removed == null)
            return set;

        uint count = 0;
        if (removed->GetCount(&count).FAILED || count == 0)
            return set;

        Guid iidLink = __uuidof<IShellLinkW>();
        char* buf = stackalloc char[ArgumentsBufferChars];
        for (uint i = 0; i < count; i++)
        {
            IShellLinkW* link = null;
            if (removed->GetAt(i, &iidLink, (void**)&link).FAILED || link == null)
                continue;
            try
            {
                if (link->GetArguments(buf, ArgumentsBufferChars).SUCCEEDED)
                    set.Add(new string(buf));
            }
            finally { link->Release(); }
        }
        return set;
    }

    /// <summary>Build an <c>IObjectCollection</c> of titled <c>IShellLinkW</c>s for user tasks, skipping any whose
    /// arguments are in <paramref name="removedArgs"/>. Caller releases the returned <c>IObjectArray</c>.</summary>
    private static IObjectArray* BuildLinkArray(JumpTask[] tasks, HashSet<string> removedArgs)
    {
        IObjectCollection* collection = CreateCollection();
        try
        {
            foreach (JumpTask task in tasks)
            {
                if (removedArgs.Contains(task.Arguments ?? string.Empty))
                    continue;
                AddLink(collection, task.Title, task.ExePath, task.Arguments, task.IconPath, task.Description, task.IconIndex);
            }
            return QiObjectArray(collection);
        }
        finally { collection->Release(); }
    }

    /// <summary>Build an <c>IObjectCollection</c> of titled <c>IShellLinkW</c>s for a custom category, skipping any
    /// whose arguments are in <paramref name="removedArgs"/>. Caller releases the returned <c>IObjectArray</c>.</summary>
    private static IObjectArray* BuildLinkArray(JumpListItem[] items, HashSet<string> removedArgs)
    {
        IObjectCollection* collection = CreateCollection();
        try
        {
            foreach (JumpListItem item in items)
            {
                if (removedArgs.Contains(item.Arguments ?? string.Empty))
                    continue;
                AddLink(collection, item.Title, item.ExePath, item.Arguments, item.IconPath, item.Description, item.IconIndex);
            }
            return QiObjectArray(collection);
        }
        finally { collection->Release(); }
    }

    private static IObjectCollection* CreateCollection()
    {
        Guid clsidColl = CLSID_EnumerableObjectCollection;
        Guid iidColl = __uuidof<IObjectCollection>();
        IObjectCollection* collection = null;
        ThrowIfFailed(
            CoCreateInstance(&clsidColl, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iidColl, (void**)&collection),
            "CoCreateInstance(CLSID_EnumerableObjectCollection)");
        return collection;
    }

    private static IObjectArray* QiObjectArray(IObjectCollection* collection)
    {
        IObjectArray* array = null;
        Guid iidArray = __uuidof<IObjectArray>();
        ThrowIfFailed(collection->QueryInterface(&iidArray, (void**)&array), "QI IObjectArray");
        return array;
    }

    private static void AddLink(
        IObjectCollection* collection,
        string title, string exePath, string? arguments, string? iconPath, string? description, int iconIndex)
    {
        IShellLinkW* link = CreateLink(title, exePath, arguments, iconPath, description, iconIndex);
        try
        {
            ThrowIfFailed(collection->AddObject((IUnknown*)link), "IObjectCollection.AddObject");
        }
        finally { if (link != null) link->Release(); }
    }

    /// <summary>Create one <c>IShellLinkW</c>: exe path, arguments, optional icon/description, and the visible
    /// title written through the link's <c>IPropertyStore</c> as <c>PKEY_Title</c>. Shared by tasks and category
    /// items. Caller releases the returned link.</summary>
    private static IShellLinkW* CreateLink(
        string title, string exePath, string? arguments, string? iconPath, string? description, int iconIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(exePath);

        Guid clsid = CLSID_ShellLink;
        Guid iid = __uuidof<IShellLinkW>();
        IShellLinkW* link = null;
        ThrowIfFailed(
            CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&link),
            "CoCreateInstance(CLSID_ShellLink)");
        try
        {
            fixed (char* pExe = exePath)
                ThrowIfFailed(link->SetPath(pExe), "IShellLinkW.SetPath");

            fixed (char* pArgs = arguments ?? string.Empty)
                ThrowIfFailed(link->SetArguments(pArgs), "IShellLinkW.SetArguments");

            if (!string.IsNullOrEmpty(description))
                fixed (char* pDesc = description)
                    ThrowIfFailed(link->SetDescription(pDesc), "IShellLinkW.SetDescription");

            if (!string.IsNullOrEmpty(iconPath))
                fixed (char* pIcon = iconPath)
                    ThrowIfFailed(link->SetIconLocation(pIcon, iconIndex), "IShellLinkW.SetIconLocation");

            SetLinkTitle(link, title);
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
