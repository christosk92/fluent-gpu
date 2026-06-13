using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Forms;

/// <summary>A registered field as the form sees it: its true validity, its async-pending state, and its node (for
/// focus-first-error). Implemented over a <see cref="Field{T}"/>'s memos so the form stays non-generic.</summary>
public interface IFieldEntry
{
    /// <summary>Subscribes the reader to the field's ungated validity.</summary>
    bool IsValidNow();
    /// <summary>Subscribes the reader to the field's async-pending state.</summary>
    bool IsValidatingNow();
    /// <summary>Reads the field's true validity WITHOUT subscribing (for the imperative submit walk).</summary>
    bool PeekValid();
    /// <summary>The control's node, for focus-first-error (may be <see cref="NodeHandle.Null"/> if unpublished).</summary>
    NodeHandle Root { get; }
}

/// <summary>
/// The form context value (the Blazor <c>EditContext</c> analogue, signal-backed): it aggregates the validity of the
/// fields registered under it, gates submit, and drives focus-first-error. It is signal-native — there is no error
/// dictionary and no change event: <see cref="IsValid"/> is a derived conjunction <see cref="Memo{T}"/> over the
/// registered fields, re-folding whenever a field registers/deregisters (a <see cref="Membership"/> bump) or any
/// field's validity changes.
/// </summary>
public sealed class FormScope
{
    /// <summary>The form currently being built by a <c>UseForm()</c> call in the running <c>Render()</c>. A
    /// <c>UseField()</c> in the same component auto-joins it; cleared at the end of the render. (Context-resolution by
    /// scene-ancestor walk cannot see a sibling provider in the same component, so this thread-local is the join path.)</summary>
    [ThreadStatic] internal static FormScope? Building;

    /// <summary>For a NESTED subtree (a child component that owns fields), wrap it in
    /// <c>Ctx.Provide(FormScope.Context, scope, child)</c>; those fields then resolve the scope via <c>UseContext</c>.</summary>
    public static readonly Context<FormScope?> Context = new(null);

    // ── Wired once by UseForm from public hooks (so FormScope itself needs no ReactiveRuntime access). ──
    internal readonly List<IFieldEntry> Fields = new();
    internal Signal<int> Membership = null!;        // bumped on register/deregister so the conjunction memos re-fold
    /// <summary>Flips true on the first <see cref="Validate"/> — every gated field reveals its errors at once.</summary>
    public Signal<bool> SubmitAttempted = null!;
    /// <summary>Derived: true iff every registered field is valid (drives submit-button enabling).</summary>
    public Memo<bool> IsValid = null!;
    /// <summary>Derived: true iff any registered field has an async check pending.</summary>
    public Memo<bool> IsValidating = null!;
    internal Action<Action>? Post;                  // cross-thread UI poster (UseField/UseForm wire it)
    internal InputHooks? Hooks;                     // focus seam for focus-first-error

    /// <summary>The number of fields currently registered (diagnostics / tests — verifies deregistration on unmount).</summary>
    public int FieldCount => Fields.Count;

    internal bool ComputeIsValid()
    {
        _ = Membership.Value;                       // re-subscribe when the field set changes
        var f = Fields;
        for (int i = 0; i < f.Count; i++) if (!f[i].IsValidNow()) return false;
        return true;
    }

    internal bool ComputeIsValidating()
    {
        _ = Membership.Value;
        var f = Fields;
        for (int i = 0; i < f.Count; i++) if (f[i].IsValidatingNow()) return true;
        return false;
    }

    /// <summary>Register a field (called by <c>UseField</c> at mount). Returns a handle the hook disposes on unmount, so
    /// a field leaving the tree no longer counts toward the form (no leak — the Blazor <c>FieldState</c> leak fixed).</summary>
    public IDisposable Register(IFieldEntry entry)
    {
        Fields.Add(entry);
        Membership.Value = Membership.Peek() + 1;
        return new Registration(this, entry);
    }

    /// <summary>Attempt submit: reveal every field's errors (flip <see cref="SubmitAttempted"/>), find the first invalid
    /// field, post a focus-move to it (deferred to the next frame — never a synchronous flush inside an input handler),
    /// and return whether the form is valid. Validity is read via the ungated per-field memos, so it reflects the true
    /// state regardless of touched/submit gating.</summary>
    public bool Validate()
    {
        SubmitAttempted.Value = true;               // deferred write — reveals gated errors on the next flush
        IFieldEntry? firstInvalid = null;
        var f = Fields;
        for (int i = 0; i < f.Count; i++)
            if (!f[i].PeekValid()) { firstInvalid = f[i]; break; }

        if (firstInvalid is not null && Post is not null && Hooks?.FocusNode is { } focus)
        {
            NodeHandle target = firstInvalid.Root;
            if (!target.IsNull) Post(() => focus(target, true));
        }
        return firstInvalid is null;
    }

    private sealed class Registration : IDisposable
    {
        private FormScope? _scope;
        private readonly IFieldEntry _entry;
        public Registration(FormScope scope, IFieldEntry entry) { _scope = scope; _entry = entry; }
        public void Dispose()
        {
            if (_scope is null) return;
            _scope.Fields.Remove(_entry);
            _scope.Membership.Value = _scope.Membership.Peek() + 1;
            _scope = null;
        }
    }
}

/// <summary>The concrete <see cref="IFieldEntry"/> backed by a <see cref="Field{T}"/>'s memos + node signal.</summary>
internal sealed class FieldEntry<T> : IFieldEntry
{
    private readonly Memo<bool> _isValid;
    private readonly Memo<bool> _isValidating;
    private readonly Signal<NodeHandle> _node;
    public FieldEntry(Field<T> field) { _isValid = field.IsValid; _isValidating = field.IsValidating; _node = field.Node; }
    public bool IsValidNow() => _isValid.Value;
    public bool IsValidatingNow() => _isValidating.Value;
    public bool PeekValid() => _isValid.Peek();
    public NodeHandle Root => _node.Peek();
}
