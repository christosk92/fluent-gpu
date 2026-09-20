using System;
using System.Text;
using FluentGpu.Foundation;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class StringTableUtf8InternTests
{
    [Fact]
    public void Utf8SpanInternsToTheSameIdAsTheEquivalentChars()
    {
        var table = new StringTable();
        const string text = "hello utf8 intern";

        StringId fromChars = table.Intern(text.AsSpan());
        StringId fromUtf8 = table.Intern(Encoding.UTF8.GetBytes(text));

        Assert.Equal(fromChars, fromUtf8);
        Assert.Equal(text, table.Resolve(fromUtf8));
    }

    [Fact]
    public void RepeatedUtf8InternOfKnownContentReturnsTheSameIdAndAllocatesNothing()
    {
        var table = new StringTable();
        byte[] bytes = Encoding.UTF8.GetBytes("repeated hit content");

        // Warm-up: the first call registers the string (allocates); a second warm-up call keeps
        // any one-time JIT/dictionary-growth cost out of the measured window below.
        StringId first = table.Intern(bytes);
        StringId warm = table.Intern(bytes);
        Assert.Equal(first, warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        StringId hit = table.Intern(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(first, hit);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Utf8SpanLongerThan256BytesRoundTrips()
    {
        var table = new StringTable();
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.Append("wavee-fluentgpu-stringtable-");
        string text = sb.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Assert.True(bytes.Length > 256);   // exercises the ArrayPool<char> rental path, not the stackalloc one

        StringId id = table.Intern(bytes);

        Assert.Equal(text, table.Resolve(id));
        Assert.Equal(id, table.Intern(bytes));
        Assert.Equal(id, table.Intern(text.AsSpan()));
    }

    [Fact]
    public void EmptyUtf8SpanReturnsTheSameIdAsTheEmptyString()
    {
        var table = new StringTable();

        StringId fromEmptyString = table.Intern("");
        StringId fromEmptyUtf8 = table.Intern(ReadOnlySpan<byte>.Empty);

        Assert.Equal(StringId.Empty, fromEmptyString);
        Assert.Equal(StringId.Empty, fromEmptyUtf8);
    }
}
