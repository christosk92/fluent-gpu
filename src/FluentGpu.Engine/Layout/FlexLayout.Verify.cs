using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Layout;

/// <summary>
/// Operation ultra-fast GPU engine, P4 — the DEBUG-only correctness oracles for incremental layout. Two of them:
/// <list type="number">
/// <item><b><c>FG_LAYOUT_VERIFY=1</c> parity oracle</b> — after a real solve, re-solve the SAME root from scratch with
/// every incremental short-circuit disabled (the cross-pass Measure ring, the Arrange early-out,
/// <see cref="TryResolveSizeStable"/>), compare every node's <c>Bounds</c>, then RESTORE the original rects. A
/// divergence is the signature of a missing dirty mark — the class of bug where the screen keeps last frame's geometry
/// forever and nothing throws.</item>
/// <item><b>the unmarked-LayoutInput tripwire</b> — every node the Arrange early-out SKIPS is re-hashed against the
/// signature recorded at its last real arrange (its whole subtree, since the early-out is a claim about the subtree,
/// not just the node). A mismatch means someone wrote a layout-affecting field without
/// <c>SceneStore.Mark(…, NodeFlags.LayoutDirty)</c>. Always counted in DEBUG
/// (<see cref="DiagUnmarkedLayoutWrites"/>, asserted by <c>gate.layout.dirty-mark-tripwire</c>); logged per-node only
/// under <c>FG_LAYOUT_VERIFY=1</c>.</item>
/// </list>
/// <para><b>Diagnostics only — never a behaviour switch.</b> Both are compiled out of Release entirely. The oracle
/// runs under a <c>_verifying</c> latch that suppresses every side effect a second solve would otherwise repeat
/// (OnBoundsChanged delivery, the <c>_arranged</c>/ArrangedValid columns, viewport <c>SetFrame</c> posts, scroll-bind
/// baking, realize/paint marks) and restores the diag counters, so an app with the variable set produces byte-identical
/// frames to one without it — only slower, plus stderr output when something is actually wrong.</para>
/// <para>Scope, stated honestly: the text measure cache is deliberately left LIVE across the re-solve (it is a pure
/// function of (text, style, maxWidth), so it cannot manufacture a layout divergence, and re-shaping every run twice
/// per frame would make the oracle unusable on a real page). The oracle targets the incremental machinery — the ring,
/// the early-out, the subtree-dirty propagation — which is what P4 introduced.</para>
/// </summary>
public sealed partial class FlexLayout
{
    // ── the verification latch ────────────────────────────────────────────────────────────────────────────────────
    // In DEBUG a plain field: the guarded sites are cold (viewport arrange, bounds-changed delivery, the early-out), so
    // a predictable never-taken branch there is cheaper than duplicating the whole solver. In Release it is a `const
    // false`, so every `if (!_verifying)` guard folds away and the oracle leaves no trace in the shipping solver at all.
#if DEBUG || FLUENTGPU_DIAG
    private bool _verifyingField;
    private bool Verifying => _verifyingField;
#else
    // A PROPERTY, not a const: a const would make every `if (Verifying) return;` guard body unreachable (CS0162, which
    // TreatWarningsAsErrors turns into a Release-only build break). The JIT folds this to the same nothing.
    private static bool Verifying => false;
#endif

    /// <summary>Nodes whose layout-affecting inputs changed while the Arrange early-out believed their subtree clean —
    /// i.e. a <c>LayoutInput</c> writer that skipped <c>Mark(LayoutDirty)</c>. Always 0 in Release (compiled out) and,
    /// in a correct DEBUG build, always 0 too. Per-frame; reset by <see cref="ResetFrameDiagCounters"/>.</summary>
    public int DiagUnmarkedLayoutWrites => _dUnmarkedLayoutWrites;
    private int _dUnmarkedLayoutWrites;

#if DEBUG || FLUENTGPU_DIAG
    /// <summary>Whether the DEBUG oracles are compiled in at all (false in Release — the whole class of check is
    /// <c>[Conditional]</c>-style erased, per the repo's "production safety == CI coverage" rule).</summary>
    public const bool VerifyCompiledIn = true;

    private static readonly bool s_layoutVerify = Diag.EnvFlag("FG_LAYOUT_VERIFY");

    // The layout-input signature at each node's last REAL arrange (SetArrangedBounds). Superset of LayoutSig: it also
    // folds in the text inputs (TextStyle + the run itself), which LayoutSig deliberately omits because no ANCESTOR
    // reads them — the tripwire, unlike the ring, cares about this node's own measured size too.
    private ulong[] _verifySig = [];
    // Parity-oracle scratch: the rects the real (incremental) solve produced, restored verbatim after the compare.
    private RectF[] _verifySaved = [];
    private NodeHandle[] _verifyNodes = [];
    private int _verifyNodeCount;
    private NodeHandle[] _verifyWalk = new NodeHandle[64];

    /// <summary>Record the signature the Arrange early-out will later be checked against. Called from
    /// <c>SetArrangedBounds</c> — i.e. exactly when a node is genuinely (re)placed.</summary>
    private void NoteVerifySig(NodeHandle node)
    {
        uint i = node.Raw.Index;
        if (_verifySig.Length <= (int)i) System.Array.Resize(ref _verifySig, System.Math.Max((int)i + 1, System.Math.Max(16, _verifySig.Length * 2)));
        _verifySig[i] = VerifySig(node);
    }

    /// <summary>The Arrange early-out's tripwire: it just claimed this whole subtree is unchanged. Prove it — every
    /// descendant's layout-affecting inputs must still hash to what its last real arrange recorded.</summary>
    private void VerifyEarlyOutSubtree(NodeHandle node)
    {
        if (Verifying) return;   // the oracle's own re-solve never takes the early-out, but be explicit
        int depth = 0;
        _verifyWalk[depth++] = node;
        while (depth > 0)
        {
            var n = _verifyWalk[--depth];
            uint i = n.Raw.Index;
            if (i < (uint)_verifySig.Length && _verifySig[i] != 0)
            {
                ulong now = VerifySig(n);
                if (now != _verifySig[i])
                {
                    _dUnmarkedLayoutWrites++;
                    if (s_layoutVerify)
                        System.Console.Error.WriteLine(
                            $"[FG_LAYOUT_VERIFY] unmarked LayoutInput write: n#{i} changed under a clean Arrange early-out " +
                            $"rooted at n#{node.Raw.Index} (sig {_verifySig[i]:x} -> {now:x}). A writer skipped Mark(LayoutDirty).");
                    _verifySig[i] = now;   // report each divergence ONCE — a permanent mismatch must not spam every frame
                }
            }
            for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            {
                if (depth == _verifyWalk.Length) System.Array.Resize(ref _verifyWalk, _verifyWalk.Length * 2);
                _verifyWalk[depth++] = c;
            }
        }
    }

    /// <summary>LayoutSig plus the node's own text inputs — the tripwire's "did anything layout-affecting change".</summary>
    private ulong VerifySig(NodeHandle node)
    {
        ulong h = LayoutSig(node);
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint p = ref _scene.Paint(node);
        MixU(ref h, (uint)p.VisualKind);
        MixU(ref h, (uint)p.Text.Value);
        MixU(ref h, (uint)li.TextStyle.FontFamily.Value);
        MixF(ref h, li.TextStyle.SizeDip);
        MixU(ref h, li.TextStyle.Weight);
        MixU(ref h, (uint)li.TextStyle.Wrap);
        MixU(ref h, (uint)li.TextStyle.Trim);
        MixU(ref h, (uint)li.TextStyle.MaxLines);
        MixF(ref h, li.TextStyle.CharSpacing);
        MixF(ref h, li.TextStyle.LineHeight);
        MixU(ref h, (uint)li.TextStyle.SpanRunId);
        MixF(ref h, li.TextStyle.MinSizeDip);
        MixU(ref h, (uint)li.JustifySelf);
        MixU(ref h, li.MeasureUnboundedWidth ? 1u : 0u);
        return h;
    }

    /// <summary>Force ONE parity check right now, regardless of <c>FG_LAYOUT_VERIFY</c> — the oracle's own test hook
    /// (<c>gate.layout.parity-oracle</c>): it proves the machinery re-solves, compares and RESTORES, on a scene whose
    /// answer is already known to be right. Returns the number of node rects that diverged from a from-scratch solve
    /// (0 = clean), or -1 in a Release build where the oracle is compiled out.</summary>
    public int VerifyLayoutParityNow(NodeHandle root, Size2 window)
    {
        if (root.IsNull || !_scene.IsLive(root)) return 0;
        ref LayoutInput li = ref _scene.Layout(root);
        float w = float.IsNaN(li.Width) ? window.Width : li.Width;
        float h = float.IsNaN(li.Height) ? window.Height : li.Height;
        return VerifyParityCore(root, window.Width, 0f, 0f, w, h, "VerifyLayoutParityNow", log: false);
    }

    /// <summary>The <c>FG_LAYOUT_VERIFY=1</c> parity oracle. <paramref name="availW"/>/<paramref name="x"/>/… replay
    /// exactly the arguments the real solve used.</summary>
    private void VerifyParity(NodeHandle root, float availW, float x, float y, float w, float h, string site)
    {
        if (!s_layoutVerify || Verifying || root.IsNull || !_scene.IsLive(root)) return;
        VerifyParityCore(root, availW, x, y, w, h, site, log: true);
    }

    private int VerifyParityCore(NodeHandle root, float availW, float x, float y, float w, float h, string site, bool log)
    {
        if (Verifying) return 0;

        CollectVerifyNodes(root);
        for (int i = 0; i < _verifyNodeCount; i++) _verifySaved[i] = _scene.Bounds(_verifyNodes[i]);

        int m0 = _dMeasure, a0 = _dArrange, th0 = _dTextHit, tm0 = _dTextMiss, mh0 = _dMeasureMemoHit, ov0 = _dOverflow;
        _verifyingField = true;
        try
        {
            BeginMeasurePass();
            Measure(root, availW);
            Arrange(root, x, y, w, h);
        }
        finally
        {
            _verifyingField = false;
            _dMeasure = m0; _dArrange = a0; _dTextHit = th0; _dTextMiss = tm0; _dMeasureMemoHit = mh0; _dOverflow = ov0;
        }

        int mismatches = 0;
        for (int i = 0; i < _verifyNodeCount; i++)
        {
            var node = _verifyNodes[i];
            RectF fresh = _scene.Bounds(node), had = _verifySaved[i];
            if (Near(fresh.X, had.X) && Near(fresh.Y, had.Y) && Near(fresh.W, had.W) && Near(fresh.H, had.H)) continue;
            if (log && mismatches < 16)
                System.Console.Error.WriteLine(
                    $"[FG_LAYOUT_VERIFY] {site}: n#{node.Raw.Index} incremental={had} fromScratch={fresh}");
            mismatches++;
        }
        // Restore unconditionally: the oracle observes, it never decides. Even a genuine divergence leaves the frame
        // byte-identical to a run without the variable set — the report is the whole output.
        for (int i = 0; i < _verifyNodeCount; i++) _scene.Bounds(_verifyNodes[i]) = _verifySaved[i];

        if (log && mismatches > 0)
            System.Console.Error.WriteLine(
                $"[FG_LAYOUT_VERIFY] {site}: {mismatches}/{_verifyNodeCount} node rects diverge from a from-scratch solve " +
                "— an incremental short-circuit trusted a subtree that had actually changed (a missing Mark(LayoutDirty)).");
        return mismatches;

        static bool Near(float a, float b) => a == b || System.MathF.Abs(a - b) <= 0.01f
            || (float.IsNaN(a) && float.IsNaN(b));
    }

    private void CollectVerifyNodes(NodeHandle root)
    {
        _verifyNodeCount = 0;
        int depth = 0;
        _verifyWalk[depth++] = root;
        while (depth > 0)
        {
            var n = _verifyWalk[--depth];
            if (_verifyNodes.Length == _verifyNodeCount)
            {
                System.Array.Resize(ref _verifyNodes, System.Math.Max(64, _verifyNodes.Length * 2));
                System.Array.Resize(ref _verifySaved, _verifyNodes.Length);
            }
            _verifyNodes[_verifyNodeCount++] = n;
            for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            {
                if (depth == _verifyWalk.Length) System.Array.Resize(ref _verifyWalk, _verifyWalk.Length * 2);
                _verifyWalk[depth++] = c;
            }
        }
        if (_verifySaved.Length < _verifyNodes.Length) System.Array.Resize(ref _verifySaved, _verifyNodes.Length);
    }
#else
    /// <summary>Whether the DEBUG oracles are compiled in at all (false in Release).</summary>
    public const bool VerifyCompiledIn = false;

    /// <summary>Release build: the oracle is compiled out entirely — always -1 ("not available"), never a silent 0.</summary>
    public int VerifyLayoutParityNow(NodeHandle root, Size2 window) => -1;

    private void NoteVerifySig(NodeHandle node) { }
    private void VerifyEarlyOutSubtree(NodeHandle node) { }
    private void VerifyParity(NodeHandle root, float availW, float x, float y, float w, float h, string site) { }
#endif
}
