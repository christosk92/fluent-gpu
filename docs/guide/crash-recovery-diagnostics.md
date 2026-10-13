# Recovery and teardown evidence

The render loop checks pending stop, park and recovery requests before every top-level wait. Events only nudge the
loop; their coalescing cannot erase a request. Recovery still takes priority over a pending park, and ownership only
passes to the UI at the existing park acknowledgement. A failed recovery also acknowledges completion so the UI can
observe the failure without becoming permanently blocked.

D3D12 teardown retains healthy fence retirement. Device disposal signals a fresh fence; target disposal waits only
for that target's submit/present horizon. Confirmed removal, including the fence's `UINT64_MAX` sentinel and removal
during signal/event registration/wait, bypasses normal retirement and permits resource release. Borrowers still
release before shared owners. Fence waits poll removal every 100 ms in both async and single-thread hosts. A healthy
fence retains its existing retirement requirement however long completion takes; polling does not impose a new
shutdown deadline or force a device reset. Unexpected native failures propagate with their operation and HRESULT. Completed device
disposal is idempotent.

The Windows run boundary captures a frame-loop failure before disposing its owners in the established reverse order.
If cleanup also fails, the original exception and stack remain primary, cleanup failures are logged and attached as
`FluentGpu.CleanupFailures` in the exception's data, and the remaining CPU-only owners are disposed. A clean frame
loop still propagates its first cleanup failure. Resource-specific disposal retains its safety gates; this boundary
does not retry or force GPU release after a drain failure.
Live host/window references are cleared before disposal starts, including on failure; custom exception metadata and
diagnostic sinks are best-effort and cannot replace the primary exception.

Release builds retain these cold `Diag.Line` checkpoints:

| Category | Ordered phases |
| --- | --- |
| `d3d12.recovery` | `recover-start`, `recover-end` or `recover-failed`; `submit-start`, `submit-end` for the first following submission |
| `d3d12.teardown` | `target-drain`, `target-released`; `device-drain`, `device-release`, `device-released` |

`recovery` counts recovery attempts. `target` and `primary` identify the submission target; `reason` is a 32-bit
device-loss HRESULT. The application's log prefix supplies process/session correlation. A missing matching end phase
locates the failing stage; `submit-end` proves queue submission and fence signalling returned, not that a frame was
presented or every GPU command finished. No checkpoint runs on subsequent steady-state submissions.

Behavioral regressions live in `RenderThreadRecoveryControlTests`, `GpuTeardownFenceTests` and `RunLifetimeTests`. Threaded tests use
barriers to keep recovery and park requests pending, exercise both successful and throwing recoveries, and verify
publication/resume/another park and shutdown. Native-fence policy tests use deterministic completion/removal facts;
GPU loss/resize probes remain necessary to validate the native backend. These fixes do not establish a cause or
resolution for historical AMD/NVIDIA access violations or production fail-fast exits.
