using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// The clip seam, asserted through the emitted opcode stream.
/// <para>
/// This exists because of HOW the seam can break. DIR.Lib 7.27 moved the region bookkeeping into
/// <c>Renderer</c> and made <c>PushClip</c>/<c>PopClip</c> non-virtual, so a backend now implements
/// <c>ApplyClip</c>/<c>ClearClip</c>. A renderer left overriding the old pair does not fail loudly:
/// it either fails to load or, worse, compiles as a method that simply never runs, and the symptom is
/// content quietly escaping its panel rather than an exception naming the cause. The same shape cost
/// SdlVulkan.Renderer its <c>DrawTriangles</c> override one release earlier.
/// </para>
/// <para>
/// So these assert the OBSERVABLE consequence -- a scissor command in the stream -- rather than the
/// presence of a method. Intersection itself is DIR.Lib's to get right; what is pinned here is that
/// this backend is wired to it and receives the already-intersected rect.
/// </para>
/// </summary>
public sealed class ClipSeamTests
{
    private static (WebGlRenderer Renderer, FakeWebGlBridge Bridge) CreateRenderer()
    {
        var bridge = new FakeWebGlBridge();
        var renderer = WebGlRenderer.Create(bridge, "test-canvas", 320, 240);
        return (renderer, bridge);
    }

    private static List<Cmd> PresentAndDecode(WebGlRenderer renderer, FakeWebGlBridge bridge)
    {
        renderer.Present();
        return Cmd.Decode(bridge.Flushes[^1].Commands);
    }

    /// <summary>UpperLeft is the second ctor argument -- the corner order that catches people out.</summary>
    private static RectInt Rect(int x, int y, int w, int h) => new((x + w, y + h), (x, y));

    private static List<Cmd> Scissors(List<Cmd> cmds) =>
        cmds.FindAll(c => c.Op is Opcode.SetScissor or Opcode.ClearScissor);

    [Fact]
    public void PushClip_EmitsTheScissorRect()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));

        renderer.PushClip(Rect(10, 20, 100, 50));
        var set = Scissors(PresentAndDecode(renderer, bridge))[0];

        set.Op.ShouldBe(Opcode.SetScissor);
        set.Slots[0].ShouldBe(10);
        set.Slots[1].ShouldBe(20);
        set.Slots[2].ShouldBe(100);
        set.Slots[3].ShouldBe(50);
    }

    /// <summary>
    /// The behaviour change 7.27 bought: an inner clip states only its own bounds and cannot escape
    /// its parent's. Under the old single-level contract this second push would have REPLACED the
    /// first, so the inner region would have been the full 200-wide rect.
    /// </summary>
    [Fact]
    public void ANestedPush_ClipsToTheIntersection()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));

        renderer.PushClip(Rect(0, 0, 100, 100));
        renderer.PushClip(Rect(50, 50, 200, 200));   // overhangs the parent on both axes
        var inner = Scissors(PresentAndDecode(renderer, bridge))[^1];

        inner.Op.ShouldBe(Opcode.SetScissor);
        inner.Slots[0].ShouldBe(50);
        inner.Slots[1].ShouldBe(50);
        inner.Slots[2].ShouldBe(50);   // clipped by the parent's right edge, not the 200 asked for
        inner.Slots[3].ShouldBe(50);
    }

    /// <summary>
    /// Popping an inner clip restores the ENCLOSING one, not the whole surface. The old contract
    /// opened all the way up, so everything the outer panel painted after an inner pop escaped it.
    /// </summary>
    [Fact]
    public void PoppingAnInnerClip_RestoresTheEnclosingOne()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));

        renderer.PushClip(Rect(0, 0, 100, 100));
        renderer.PushClip(Rect(50, 50, 20, 20));
        renderer.PopClip();
        var restored = Scissors(PresentAndDecode(renderer, bridge))[^1];

        restored.Op.ShouldBe(Opcode.SetScissor);
        restored.Slots[2].ShouldBe(100);
        restored.Slots[3].ShouldBe(100);
    }

    [Fact]
    public void PoppingTheLastClip_OpensTheSurfaceBackUp()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));

        renderer.PushClip(Rect(10, 10, 50, 50));
        renderer.PopClip();

        Scissors(PresentAndDecode(renderer, bridge))[^1].Op.ShouldBe(Opcode.ClearScissor);
    }

    /// <summary>
    /// A widget that throws between its push and its pop must not clip every later frame to a rect
    /// nobody can name. The frame boundary rebuilds the command stream, so the base's stack has to be
    /// reset with it -- otherwise the depth keeps growing and the next frame opens already clipped.
    /// </summary>
    [Fact]
    public void AnUnbalancedPush_DoesNotLeakIntoTheNextFrame()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.PushClip(Rect(10, 10, 20, 20));   // no matching pop: the widget threw

        renderer.Clear(new RGBAColor32(0, 0, 0, 255));

        renderer.ClipDepth.ShouldBe(0);
        // A ClearScissor IS emitted here, and is the correct thing: the reset opens the surface back
        // up explicitly. What must not survive is a SetScissor confining the new frame.
        Scissors(PresentAndDecode(renderer, bridge))
            .ShouldAllBe(c => c.Op == Opcode.ClearScissor);
    }
}
