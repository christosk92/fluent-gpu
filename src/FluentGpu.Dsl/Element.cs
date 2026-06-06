using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>Immutable description of a UI node (the "virtual DOM"). Cheap to build, never touches the scene directly.</summary>
public abstract record Element
{
    public string? Key { get; init; }

    /// <summary>Stable per-record-type id for integer type-dispatch in the reconciler (the source-gen'd ElementTypeId).</summary>
    public abstract ushort ElementTypeId { get; }
}

/// <summary>A box: container/layout node and/or a filled, optionally-clickable surface (VStack/HStack/Button/Box).</summary>
public sealed record BoxEl : Element
{
    public override ushort ElementTypeId => 1;

    public byte Direction { get; init; }          // 0 = row, 1 = column
    public float Gap { get; init; }
    public Edges4 Padding { get; init; }
    public ColorF Fill { get; init; }
    public CornerRadius4 Corners { get; init; }
    public Action? OnClick { get; init; }
    public Element[] Children { get; init; } = [];
}

/// <summary>A text run.</summary>
public sealed record TextEl(string Text) : Element
{
    public override ushort ElementTypeId => 2;

    public float Size { get; init; } = 14f;
    public bool Bold { get; init; }
    public ColorF Color { get; init; } = ColorF.FromRgba(0xE6, 0xE6, 0xE6);
}
