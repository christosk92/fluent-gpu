# PlayReady native integration

Protected video uses an in-process Win32 native DLL plus managed wrappers in the Windows API layer.

## Native DLL

Build and output layout: [`ops/tools/playready-native/README.md`](../../ops/tools/playready-native/README.md).

```cmd
cd ops\tools\playready-native
build.cmd arm64
```

Produces `out/{arch}/FluentGpu.PlayReady.Native.dll`.

## Managed code

| Area | Path |
|---|---|
| PlayReady seam | `src/FluentGpu.WindowsApi/Media/PlayReady/` |
| Gallery harness | `src/FluentGpu.WindowsApp/` (protected-video test entry points) |

Design context: [`docs/design/subsystems/media-pipeline.md`](../design/subsystems/media-pipeline.md) (DRM / protected playback section).
