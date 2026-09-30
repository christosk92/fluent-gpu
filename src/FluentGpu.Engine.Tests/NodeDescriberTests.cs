using System;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Node names for logs and evidence exports (docs/plans/evidence-diagnostics-implementation.md §A.5): the
/// nearest keyed ancestor-or-self and the child-index path below it; <c>#root</c> when nothing above is keyed;
/// <c>gone:&lt;index&gt;</c> for a node no longer live at that generation.</summary>
public sealed class NodeDescriberTests
{
    private static string Describe(SceneStore scene, NodeHandle n)
    {
        Span<char> buf = stackalloc char[128];
        int len = NodeDescriber.Describe(scene, (int)n.Raw.Index, n.Raw.Gen, buf);
        return new string(buf[..len]);
    }

    private static (SceneStore Scene, NodeHandle Root, NodeHandle Page, NodeHandle Band, NodeHandle Tab) Tree()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        var page = scene.CreateNode(3);
        scene.AppendChild(root, scene.CreateNode(1));   // sibling 0
        scene.AppendChild(root, page);                  // index 1
        var band = scene.CreateNode(1);
        scene.AppendChild(page, scene.CreateNode(2));   // 0
        scene.AppendChild(page, band);                  // 1
        var tab = scene.CreateNode(5);
        scene.AppendChild(band, scene.CreateNode(1));   // 0
        scene.AppendChild(band, scene.CreateNode(1));   // 1
        scene.AppendChild(band, tab);                   // 2
        return (scene, root, page, band, tab);
    }

    [Fact]
    public void DescribeNode_Keyed_IsItsKey()
    {
        var (scene, _, page, _, _) = Tree();
        scene.NoteDebugKey(page, "artist-page");
        Assert.Equal("artist-page", Describe(scene, page));
    }

    [Fact]
    public void DescribeNode_UnkeyedChild_IsTheNearestKeyedAncestorPlusTheIndexPath()
    {
        var (scene, _, page, _, tab) = Tree();
        scene.NoteDebugKey(page, "artist-page");
        Assert.Equal("artist-page/1/2", Describe(scene, tab));
    }

    [Fact]
    public void DescribeNode_NoKeyedAncestor_StartsAtTheRoot()
    {
        var (scene, root, _, band, _) = Tree();
        Assert.Equal("#root/1/1", Describe(scene, band));
        Assert.Equal("#root", Describe(scene, root));
    }

    [Fact]
    public void DescribeNode_Gone_WhenTheGenerationNoLongerLives()
    {
        var (scene, _, page, band, tab) = Tree();
        scene.NoteDebugKey(band, "band");
        int index = (int)tab.Raw.Index;
        scene.FreeSubtree(band);
        Assert.Equal("gone:" + index, Describe(scene, tab));
        var reused = scene.CreateNode(1);   // may reuse a freed slot: it must not inherit the freed node's key
        scene.AppendChild(page, reused);
        Assert.Null(scene.DebugKeyOf(reused));
    }

    [Fact]
    public void DescribeNode_TruncatesIntoASmallBuffer()
    {
        var (scene, _, page, _, tab) = Tree();
        scene.NoteDebugKey(page, "a-rather-long-page-key");
        Span<char> buf = stackalloc char[8];
        int len = NodeDescriber.Describe(scene, (int)tab.Raw.Index, tab.Raw.Gen, buf);
        Assert.Equal(8, len);
        Assert.Equal("a-rather", new string(buf[..len]));
    }

    [Fact]
    public void ElementTypeName_NamesTheMountedKind()
    {
        Assert.Equal("Box", NodeDescriber.ElementTypeName(1));
        Assert.Equal("Scroll", NodeDescriber.ElementTypeName(5));
        Assert.Equal("ListRow", NodeDescriber.ElementTypeName(17));
        Assert.Equal("T99", NodeDescriber.ElementTypeName(99));
    }
}
