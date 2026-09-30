# DRAFT — dotnet/runtime issue: JIT drops a whole-struct zero-init through a byref when a smaller zero store to the same address follows

> **Status: draft, not filed.** Prepared 2026-09-25 from the Wavee staged-row corruption (Wavee `FreshSlot`,
> `StagingLeaseTests`) and the engine audit (`src/FluentGpu.Engine/Foundation/FreshSlot.cs`,
> `src/FluentGpu.Engine.Tests/SlotZeroingTests.cs`). Everything below was reproduced on this machine; paste from
> "Title" down when filing at https://github.com/dotnet/runtime/issues/new (area-CodeGen-coreclr).

---

## Title

JIT: `x = default` through a byref is dropped when the next store zeroes a struct-typed first field of `x` (silent bad codegen, regression from .NET 8)

## Description

When a struct is zeroed through a reference (`ref S slot = ref array[i]; slot = default;`) and the next store writes a
zero value into the struct's first field, and that field is itself a struct (`slot.First = default;`), optimized code
keeps only the second, smaller zero store. The rest of the struct keeps whatever it held before. Nothing throws. The
program just reads stale data.

`Unsafe.InitBlockUnaligned(ref Unsafe.As<S, byte>(ref slot), 0, (uint)Unsafe.SizeOf<S>())` is dropped the same way.
`this = default;` followed by `First = default;` inside an (inlined) struct method is dropped too.

We found it in production code: a pooled append buffer (`ref T row = ref _a[Count++]; row = default; return ref row;`)
whose caller then ran an inlined `row.Init(id, …)`. The `id` copy started with a zero block at offset 0, either because
the argument was `default` or because a constructed struct temp was zero-initialised and forwarded. Reused rows kept
the previous tenant's fields, and those fields were offsets into a text arena. So the app persisted other rows' text as
image URLs, labels and dates.

### Minimal repro

```csharp
// net10.0 console app, dotnet run -c Release
using System;

struct Head { public long A, B, C; }                 // any struct-typed first field (12..128 bytes observed)
struct Slot { public Head First; public long Left; public int Kind; }

static class Program
{
    static readonly Slot[] s_slots = new Slot[64];

    static void Reuse(int i)
    {
        ref Slot slot = ref s_slots[i];
        slot = default;             // expected: all 40 bytes zeroed
        slot.First = default;       // a smaller zero store at the same address
    }

    static void Main()
    {
        for (int round = 0; round < 20_000; round++)
        {
            for (int i = 0; i < 40; i++) { s_slots[i].Left = 7; s_slots[i].Kind = 2; }   // previous tenant
            for (int i = 0; i < 40; i++) Reuse(i);
            for (int i = 0; i < 40; i++)
                if (s_slots[i].Left != 0 || s_slots[i].Kind != 0)
                {
                    Console.WriteLine($"STALE at round {round}, slot {i}: Left={s_slots[i].Left} Kind={s_slots[i].Kind}");
                    return;
                }
        }
        Console.WriteLine("clean");
    }
}
```

### Expected

`clean`. After `slot = default`, every field of the slot is zero, whatever `slot.First = default` does afterwards.

### Actual

| Runtime | Arch | Default (tiered) | `DOTNET_TieredCompilation=0` | `DOTNET_TieredPGO=0` | `DOTNET_TC_OnStackReplacement=0` |
|---|---|---|---|---|---|
| 10.0.8 | arm64 | STALE at round 161 | STALE at round 0 | clean | clean |
| 10.0.8 | x64 | STALE at round 161 | STALE at round 0 | clean | — |
| 11.0.0-preview.4.26230.115 | arm64 | STALE at round 161 | — | — | — |
| 11.0.0-preview.4.26230.115 | x64 | STALE at round 161 | — | — | — |
| 8.0.27 (same source, `net8.0`) | arm64 / x64 | clean | clean | — | — |

Machine: Windows 11 Pro 10.0.26340, ARM64 (the x64 rows use the x64 runtime under emulation). The SDK was
11.0.100-preview.4.26230.115, and the app was built for `net10.0` or `net8.0` as stated.

`DOTNET_TieredCompilation=0` makes it deterministic (round 0). The FullOpts code for `Reuse` keeps only the 24-byte
store of `First`, and the 40-byte zero of the whole slot is gone:

```asm
; Program:Reuse(int) (FullOpts), x64, 10.0.8
       lea      rax, bword ptr [rax+8*rcx+0x10]
       vxorps   xmm0, xmm0, xmm0
       vmovdqu  xmmword ptr [rax], xmm0          ; bytes 0..15
       vmovdqu  xmmword ptr [rax+0x08], xmm0     ; bytes 8..23  -> only First (24 B) is zeroed
       add      rsp, 40
       ret

; Program:Reuse(int) (FullOpts), arm64, 10.0.8
       umaddl  x0, w0, w2, x3
       add     x0, x0, x1
       stp     xzr, xzr, [x0]                    ; bytes 0..15
       str     xzr, [x0, #0x10]                  ; bytes 16..23 -> only First (24 B) is zeroed
       ret     lr
```

### What does and does not trigger it (arm64, 10.0.8, same harness, ≥ 20,000 rounds)

| Second store after `slot = default` | Result |
|---|---|
| `slot.First = default` where `First` is a struct of 12, 20, 24, 32, 40, 44, 48, 56, 64 or 128 bytes (int fields) | **stale** |
| same, 16-byte `{long, long}` | **stale** on arm64, clean on x64 |
| `slot.First = 0` / `null` where `First` is `int`, `long` or `object` | clean |
| `First` is an 8-byte or 4-byte struct (`{int,int}`, `{float,float}`, `{int}`) | clean |
| a zero struct store at a NON-zero offset (`slot.Mid = default` at +12, `slot.Title = default` at +40) | clean |
| any non-zero store between the two (`slot = default; slot.C = x; slot.First = default;`) | clean |
| `slot.First = <non-zero runtime value>` | clean |
| element form: `arr[i] = default; arr[i].First = default;` | clean |
| `Unsafe.InitBlockUnaligned(ref …slot, 0, size)` then `slot.First = default` | **stale** |
| `this = default; First = default;` in a struct method | **stale** |
| zero in a callee that returns `ref slot` (inlined), caller writes `First = default` | **stale** |
| `arr.AsSpan(i, 1).Clear()` then `slot.First = default` | clean |
| `slot = default` in a `[MethodImpl(NoInlining)]` helper, caller writes `First = default` | clean |

This looks like a same-address, same-value "redundant/dead store" decision that doesn't compare store sizes. The later,
narrower zero store is taken to cover the earlier, wider one, and the wider one is removed.

### Impact

This is silent data corruption in ordinary pooled-buffer code: object pools, slabs and append lists that hand out
`ref T` after clearing it. It only shows after tier-up, so Debug builds and short tests never see it. It can hide
behind generic code, where the zero first-field store comes from an inlined `Init(default, …)` or from a zero-initialised
constructor temp that gets forwarded into the destination.

### Workarounds

- Clear through a span inside a non-inlined helper. Its store is opaque to the caller's JIT:

  ```csharp
  [MethodImpl(MethodImplOptions.NoInlining)]
  static ref T Fresh<T>(T[] a, int i) where T : struct { a.AsSpan(i, 1).Clear(); return ref a[i]; }
  ```

- Or put a non-zero store between the two, or zero the element with `arr[i] = default` rather than through a `ref`
  local. Both avoid this particular pattern, but neither is guaranteed.
- `DOTNET_TieredPGO=0` hides the tiered form in this repro, but not `TieredCompilation=0`. So it's diagnostic only, not
  a fix.

---

## Where this lives in our trees

- Engine helper: `src/FluentGpu.Engine/Foundation/FreshSlot.cs` (`FreshSlot.Of`). Wavee's staged-row lists use it,
  and the Wavee-local copy was deleted.
- Tests, which only bite in `-c Release`: `src/FluentGpu.Engine.Tests/SlotZeroingTests.cs` and Wavee
  `StagingLeaseTests` / `DecodeTests.A_recents_item_decoded_into_a_reused_buffer_keeps_nothing_from_the_previous_page`.
- Both repos' gates now run their unit tests in Release too (engine `CLAUDE.md`; Wavee `CLAUDE.md`,
  `docs/guide/releasing-wavee.md` §gates and the release script's `gates` check).
- Drop the helper only after a fixed runtime ships **and** the tests above stay green with a plain `row = default`
  in its place.
