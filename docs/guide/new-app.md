# Starting a new app

[← Guide index](./README.md)

Three ways to start, shortest first:

| You want | Use |
|---|---|
| A new app built against an engine **checkout** (you edit or pin the engine) | the `fluentgpu-app` template, below |
| An existing project that should consume a checkout | `FluentGpu.App.props` (import it from your `Directory.Build.props`) |
| An app built against the **published package** | `<PackageReference Include="FluentGpu" />` — [Consuming via NuGet](./consuming-via-nuget.md) |

## 1 · The `fluentgpu-app` template

```powershell
dotnet new install C:\src\fluent-gpu\templates\fluentgpu-app      # once per machine (re-run after a template change)
dotnet new fluentgpu-app -n MyApp --EnginePath C:\src\fluent-gpu
cd MyApp
dotnet run
```

| Parameter | Default | Meaning |
|---|---|---|
| `-n` / `--name` | the folder name | project, assembly and namespace name |
| `--EnginePath` | `..\fluent-gpu` | the engine checkout (the folder that holds `FluentGpu.App.props`). Absolute, or relative to the new project folder |
| `--GlobalJson` | `true` | add a `global.json` pinning the SDK band the engine builds with (10.0.300, `latestFeature`). Pass `false` inside a repo that already has one |

What you get:

| File | What it holds |
|---|---|
| `MyApp.csproj` | `OutputType=WinExe` + `TargetFramework`. Nothing else: the baseline comes from the props |
| `Directory.Build.props` | `EngineRoot` (with an optional git-ignored `EngineRoot.local.props` override), the import of `FluentGpu.App.props`, a one-sentence error when the checkout is missing, `TreatWarningsAsErrors` |
| `Program.cs` | `[STAThread] Main` → `FluentApp.Run(() => new App(), new AppOptions { Mica = true, CustomFrame = true, … })` |
| `App.cs` | the window root (a `TitleBar` over a `ScrollView` region) and one page with a counter |
| `.gitignore`, `global.json` | the usual, plus `EngineRoot.local.props` |

The root and the page set no `Fill`, so the Mica backdrop shows through. `CustomFrame = true` strips the OS caption;
the `TitleBar` draws the caption buttons and reports the drag band and buttons to the window itself.

**Moving the engine later.** Edit `EngineRoot` in `Directory.Build.props`, or pin it per machine/worktree without
touching tracked files:

```xml
<!-- EngineRoot.local.props (git-ignored) -->
<Project><PropertyGroup><EngineRoot>D:\work\fluent-gpu-feature\</EngineRoot></PropertyGroup></Project>
```

`-p:EngineRoot=...` on the command line wins over both.

**Checking the template** (the engine repo's gate, run by the orchestrator, not CI):
`powershell -File ops/scripts/check-app-template.ps1` installs the template into a private hive under `%TEMP%`,
instantiates it against the checkout, builds it Debug and checks the exe. Add `-Publish` to also NativeAOT-publish it,
and `-KeepTemp` to look at the output.

## 2 · `FluentGpu.App.props` / `FluentGpu.App.targets`

The two files at the engine repo root are the whole app baseline. Import only the props; it wires the targets itself:

```xml
<!-- Directory.Build.props of your app -->
<Project>
  <PropertyGroup>
    <EngineRoot>$(MSBuildThisFileDirectory)..\fluent-gpu\</EngineRoot>
  </PropertyGroup>
  <Import Project="$(EngineRoot)FluentGpu.App.props" />
</Project>
```

**The props** (evaluated before your csproj — every value is a default, `Condition="'$(X)' == ''"`, so anything you set
wins):

| Setting | Value | Why |
|---|---|---|
| `TargetFramework`, `LangVersion`, `Nullable`, `ImplicitUsings`, `AllowUnsafeBlocks`, `InvariantGlobalization` | `net10.0`, `14`, on, on, on, on | the engine's language baseline |
| `Deterministic`, `ServerGarbageCollection`, `ConcurrentGarbageCollection` | `true`, `false`, `true` | the engine's GC posture (workstation + background GC, the frame loop's assumption) |
| `ShouldUnsetParentConfigurationAndPlatform` | `false` | a solution build otherwise builds the out-of-tree engine references as Debug and copies Debug DLLs into a Release app |
| `NoWarn` | `+CA1416` | the app is a Windows composition root; the Win32 paths are intentional |
| `DefineConstants` | `+FLUENTGPU_DIAG` when `-p:FluentGpuDiag=true` | `[Conditional]` diagnostics are erased by the *calling* assembly, so the app needs the symbol too |
| `EngineRoot` | the folder of the props file itself | so importing the props from a checkout is enough; a relative value resolves against the project folder, the trailing slash is optional. `FluentGpuEngineRoot` is the normalised value |

**The targets** run after your csproj body (so `OutputType` is known) and before the SDK's own targets:

| Setting | Effect |
|---|---|
| `PublishAot=true` | only for `WinExe`/`Exe`. A class library that imports the props stays a plain library (no NETSDK1207) |
| `PublishRuntimeIdentifier` | defaults to `$(NETCoreSdkRuntimeIdentifier)` — the architecture `dotnet` itself runs as, never `PROCESSOR_ARCHITECTURE` (an x64-emulated shell on an ARM64 machine reports AMD64 while the native SDK builds arm64). `-r` wins |
| Trim / AOT / single-file analyzers | off outside `Release` (they roughly halve an edit-build); Release and the AOT publish still check everything |
| Engine `ProjectReference`s | `FluentGpu.Engine`, `.Controls`, `.Windows`, `.WindowsApi` |
| `FluentGpu.SourceGen` | as an analyzer (never a compile reference), with the compiler-visible `FluentGpuLocBase` metadata and the `FluentGpuLocNamespace` / `FluentGpuLocHardcodedAssemblies` properties already declared |
| Missing checkout | one error naming the path, instead of a wall of NU1105 |
| vswhere fix | before the NativeAOT link (`SetupOSSpecificProps`/`LinkNative`), if `vswhere.exe` does not resolve on `PATH`, prepends `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to the MSBuild process `PATH` (Windows only) |

Opt-outs, set in your csproj or `Directory.Build.props`:

| Property | Effect |
|---|---|
| `FluentGpuReferenceWindowsApi=false` | no `FluentGpu.WindowsApi` (toasts, tray, storage, SMTC, pickers, …) |
| `FluentGpuReferenceSourceGen=false` | no source generators |
| `FluentGpuReferenceEngine=false` | no engine references at all (you wire them yourself) |
| `FluentGpuFixVsWhere=false` | leave `PATH` alone |
| `PublishAot=false` | a JIT publish |

If your project already sets `BeforeMicrosoftNETSdkTargets`, the props leaves it alone; import
`$(EngineRoot)FluentGpu.App.targets` from the file it names.

**Localization keys** need only the base-culture file and, optionally, the namespace:

```xml
<PropertyGroup><FluentGpuLocNamespace>MyApp</FluentGpuLocNamespace></PropertyGroup>
<ItemGroup><AdditionalFiles Include="assets\loc\en-US.json" FluentGpuLocBase="true" /></ItemGroup>
```

**Package consumers** get the safe subset of these defaults from the package itself (`buildTransitive/FluentGpu.props`:
CA1416 off, the Debug analyzer switches, the compiler-visible source-generator switches). PublishAot, the RID and the
vswhere fix stay yours to set.

## 3 · Publishing

```powershell
dotnet publish -c Release                     # NativeAOT, for the architecture of the dotnet you run
dotnet publish -c Release -r win-x64          # an explicit target
dotnet publish -c Release -r win-arm64
```

The result is one self-contained native `.exe` in `bin\Release\net10.0\<rid>\publish\`. `dotnet build`/`dotnet run`
stay JIT; ILC runs only at publish.

NativeAOT links with MSVC: install Visual Studio or the Build Tools with **Desktop development with C++**. For the
target architecture you need the matching MSVC tools *hosted on this machine's architecture*:

| Machine | Target | Component |
|---|---|---|
| x64 | win-x64 | MSVC x64/x86 build tools |
| x64 | win-arm64 | + MSVC ARM64 build tools |
| ARM64 | win-arm64 | MSVC ARM64 build tools |
| ARM64 | win-x64 | MSVC x64/x86 build tools (provides `HostARM64\x64\link.exe`) |

"Platform linker not found" on ARM64 with the tools installed is the `vswhere` lookup: the props' fix handles it; in a
project without the props, put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH`.

## 4 · Recipe: a small overlay window

A borderless, topmost, fixed-size widget (the `ClaudeUsageWidget` shape) combines the window shape with the
ergonomics pieces:

```csharp
FluentApp.Run(() => new OverlayHost { Child = Embed.Comp(() => new Widget()) }, new AppOptions
{
    Title = "My widget",
    Width = 340, Height = 120,
    Mica = true, Acrylic = true,            // desktop acrylic instead of Mica (live: FluentApp.SetWindowMaterialAcrylic)
    CustomFrame = true,                     // no OS caption
    Topmost = true,                         // live: FluentApp.SetTopmost
    ToolWindow = true,                      // no taskbar button, no Alt-Tab entry
    Resizable = false,                      // no sizing border, no maximize
    SizeToContent = SizeToContent.Height,   // the window height follows the content (live: FluentApp.SetSizeToContent)
});
```

| Need | API |
|---|---|
| Drag the window by its content | `new BoxEl { … }.WindowDragArea()`; interactive children `.NoWindowDrag()`. Right-click on a drag area raises `OnContextRequested` (system menu when nothing handles it) |
| A "…" menu that may be larger than the window | `IconButton.Menu(Icons.More, () => items)` — opens as a popup window when it does not fit; for a hand-rolled `IOverlayService.Open`, pass `PopupOptions.Menu` |
| Size to content | `AppOptions.SizeToContent` (`Width`/`Height`/`WidthAndHeight`); content that only `Grow`s measures to nothing and leaves that axis alone; the user resizing the frame switches back to `Manual` |
| Numbers that do not jitter | `new TextEl(value) { TextAlign = TextAlign.End, FontFeatures = FontFeatures.TabularFigures }` |
| Live data | `UseResource(ct => client.FetchAsync(ct), options: new ResourceOptions { RefetchIntervalMs = 60_000, RefetchWhileInactive = true })` — polls from the previous completion, backs off on errors, `res.Error` is a signal |
| A tray icon with a live badge | `var tray = new NotifyIcon(id, "My widget"); tray.SetIconSource(r => TrayIconArt.Glyph(r, Icons.Clock, badge: "42")); tray.Show();` (`FluentGpu.WindowsApi.Shell`) |
| Remembered position | `FluentApp.WindowBoundsPx` / `FluentApp.MoveWindowPx` with `FluentApp.CloseRequested`, stored through `AppDataStore` |

Design rationale and edge cases for each piece: `docs/plans/app-authoring-ergonomics-implementation.md` §0–§6.
