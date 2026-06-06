namespace FluentGpu.Foundation;

/// <summary>4-byte interned string id. Id 0 = empty. No <see cref="string"/> reaches the paint path.</summary>
public readonly record struct StringId(int Value)
{
    public bool IsEmpty => Value == 0;
    public static StringId Empty => default;
}

/// <summary>Process/window-lifetime string interner. Lives in Foundation so Render can resolve without depending on Scene.</summary>
public sealed class StringTable
{
    private readonly Dictionary<string, int> _map = new(StringComparer.Ordinal);
    private readonly List<string> _list = new() { "" };

    public StringId Intern(string? s)
    {
        if (string.IsNullOrEmpty(s)) return StringId.Empty;
        if (_map.TryGetValue(s, out int id)) return new StringId(id);
        id = _list.Count;
        _list.Add(s);
        _map[s] = id;
        return new StringId(id);
    }

    public string Resolve(StringId id)
        => (uint)id.Value < (uint)_list.Count ? _list[id.Value] : "";
}
