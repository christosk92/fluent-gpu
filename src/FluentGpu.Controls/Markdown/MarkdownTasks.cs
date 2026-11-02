using System;
using System.Text;

namespace FluentGpu.Controls;

/// <summary>
/// Source rewriting for task-list toggles (markdown.md §6.4). The viewer never owns a task's state: a box shows what
/// the SOURCE says, a click reports <c>(taskIndex, isChecked)</c> through <see cref="MarkdownOptions.OnTaskToggled"/>,
/// and the consumer writes the source back — <see cref="Toggle"/> flips the N-th task marker for it. It finds the
/// marker through the PARSER (<see cref="MarkdownListItem.MarkerOffset"/>, document order, parent before nested), so
/// it counts exactly the items the viewer draws as boxes: a <c>- [ ]</c> inside fenced or indented code, a code span
/// or any other non-task text never shifts the index, and CRLF sources keep their line endings.
/// </summary>
public static class MarkdownTasks
{
    /// <summary>Return <paramref name="source"/> with the <paramref name="taskIndex"/>-th task marker (0-based, document
    /// order — the index <see cref="MarkdownOptions.OnTaskToggled"/> reports) set to <c>[x]</c> or <c>[ ]</c>. The
    /// source is returned unchanged when there is no such task. Only the one character inside the brackets changes.</summary>
    public static string Toggle(string source, int taskIndex, bool isChecked, MarkdownOptions? options = null)
    {
        int at = MarkerOffset(source, taskIndex, options);
        if (at < 0) return source;
        char want = isChecked ? 'x' : ' ';
        if (source[at] == want || (isChecked && source[at] == 'X')) return source;
        var sb = new StringBuilder(source.Length);
        sb.Append(source, 0, at).Append(want).Append(source, at + 1, source.Length - at - 1);
        return sb.ToString();
    }

    /// <summary>The UTF-16 offset of the character INSIDE the brackets of the N-th task marker, or −1. Parses the
    /// source (a cold path) and walks the tree in document order.</summary>
    public static int MarkerOffset(string source, int taskIndex, MarkdownOptions? options = null)
    {
        if (string.IsNullOrEmpty(source) || taskIndex < 0) return -1;
        var doc = MarkdownParser.Parse(source, options);
        var item = FindTask(doc.Blocks, taskIndex);
        if (item is null) return -1;
        return VerifiedOffset(source, item.MarkerOffset);
    }

    /// <summary>The N-th task item of a document in document order (the viewer's and the parser's ordinal).</summary>
    public static MarkdownListItem? FindTask(MarkdownBlock[] blocks, int taskIndex)
    {
        foreach (var b in blocks)
        {
            if (b is MarkdownList l)
            {
                foreach (var it in l.Items)
                {
                    if (it.Checked is not null && it.TaskIndex == taskIndex) return it;
                    var inner = FindTask(it.Children, taskIndex);
                    if (inner is not null) return inner;
                }
            }
            else if (b is MarkdownQuote q)
            {
                var inner = FindTask(q.Children, taskIndex);
                if (inner is not null) return inner;
            }
        }
        return null;
    }

    /// <summary>The parser measures columns on tab-expanded lines; the offset is exact without tabs and lands on the
    /// right line with them. Confirm the bracket, else re-find the marker's `[` on that source line.</summary>
    private static int VerifiedOffset(string source, int at)
    {
        if (at <= 0 || at >= source.Length) return -1;
        if (source[at - 1] == '[' && at + 1 < source.Length && source[at + 1] == ']') return at;
        int lineStart = source.LastIndexOf('\n', Math.Min(at, source.Length - 1)) + 1;
        int lineEnd = source.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = source.Length;
        for (int i = lineStart; i + 2 < lineEnd; i++)
        {
            if (source[i] == '[' && source[i + 2] == ']' && (source[i + 1] == ' ' || source[i + 1] == 'x' || source[i + 1] == 'X'))
                return i + 1;
        }
        return -1;
    }
}
