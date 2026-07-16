using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using static WebGl.Renderer.Tests.Fakes.TestGeometry;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// Asserts the exact opcode/vertex streams the renderer encodes for known draw sequences —
/// the full renderer runs above a <see cref="FakeWebGlBridge"/>, no browser involved.
/// </summary>
public sealed class CommandBufferEncodingTests
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

    [Fact]
    public void Create_CompilesPipelinesAndReportsCaps()
    {
        var (renderer, bridge) = CreateRenderer();
        bridge.Calls.ShouldContain("init:test-canvas");
        bridge.Calls.ShouldContain("compile");
        bridge.CompiledVertexSources.ShouldBe(WebGlPipelines.VertexSources);
        renderer.Surface.MaxTextureSize.ShouldBe(4096);
        renderer.Width.ShouldBe(320u);
        renderer.Height.ShouldBe(240u);
    }

    [Fact]
    public void Clear_EncodesClearColor()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(255, 0, 0, 255));
        var cmds = PresentAndDecode(renderer, bridge);

        cmds[0].Op.ShouldBe(Opcode.Clear);
        cmds[0].SlotF(0).ShouldBe(1f);
        cmds[0].SlotF(1).ShouldBe(0f);
        cmds[0].SlotF(3).ShouldBe(1f);
    }

    [Fact]
    public void FillRectangle_EncodesProgramColorAndQuad()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.FillRectangle(Rect(10, 20, 30, 40), new RGBAColor32(0, 255, 0, 255));

        var cmds = PresentAndDecode(renderer, bridge);
        var verts = bridge.Flushes[^1].Vertices;

        cmds.Select(c => c.Op).ShouldBe([Opcode.Clear, Opcode.UseProgram, Opcode.SetColor, Opcode.Draw]);
        cmds[1].Slots[0].ShouldBe((int)PipelineId.Flat);
        cmds[3].Slots[0].ShouldBe(0);  // firstFloat
        cmds[3].Slots[1].ShouldBe(6);  // vertexCount
        // Two CCW-wound triangles over the rect [10,20)-[30,40).
        verts.ShouldBe([10f, 20f, 30f, 20f, 30f, 40f, 10f, 20f, 30f, 40f, 10f, 40f]);
    }

    [Fact]
    public void FillRectangles_BatchesSameColorRunsIntoOneDraw()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        var green = new RGBAColor32(0, 255, 0, 255);
        var red = new RGBAColor32(255, 0, 0, 255);
        renderer.FillRectangles(
        [
            (Rect(0, 0, 10, 10), green),
            (Rect(10, 10, 20, 20), green),
            (Rect(20, 20, 30, 30), red),
        ]);

        var cmds = PresentAndDecode(renderer, bridge);
        // Two same-color greens collapse into one SetColor + one 12-vertex Draw; red gets its own.
        cmds.Select(c => c.Op).ShouldBe(
            [Opcode.Clear, Opcode.UseProgram, Opcode.SetColor, Opcode.Draw, Opcode.SetColor, Opcode.Draw]);
        cmds[3].Slots[1].ShouldBe(12);
        cmds[5].Slots[1].ShouldBe(6);
    }

    [Fact]
    public void DrawLine_EncodesStrokeQuadWithHalfWidth()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.DrawLine(0f, 0f, 100f, 50f, new RGBAColor32(255, 255, 255, 255), thickness: 4);

        var cmds = PresentAndDecode(renderer, bridge);
        var verts = bridge.Flushes[^1].Vertices;

        cmds.Select(c => c.Op).ShouldBe(
            [Opcode.Clear, Opcode.UseProgram, Opcode.SetColor, Opcode.SetExtra, Opcode.Draw]);
        cmds[1].Slots[0].ShouldBe((int)PipelineId.Stroke);
        cmds[3].SlotF(0).ShouldBe(2f); // halfWidth = thickness/2
        cmds[4].Slots[1].ShouldBe(6);
        verts.Length.ShouldBe(36);     // 6 verts × (P0, P1, side+end)
        // Every vertex carries the segment endpoints.
        for (var v = 0; v < 6; v++)
        {
            verts[v * 6].ShouldBe(0f);
            verts[v * 6 + 2].ShouldBe(100f);
            verts[v * 6 + 3].ShouldBe(50f);
        }
    }

    [Fact]
    public void Ellipses_EncodeInnerRadius()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        var rect = Rect(0, 0, 100, 100); // 100×100 → semi-minor 50
        renderer.FillEllipse(rect, new RGBAColor32(255, 255, 255, 255));
        renderer.DrawEllipse(rect, new RGBAColor32(255, 255, 255, 255), strokeWidth: 5f);

        var cmds = PresentAndDecode(renderer, bridge);
        var extras = cmds.Where(c => c.Op == Opcode.SetExtra).ToList();
        extras.Count.ShouldBe(2);
        extras[0].SlotF(0).ShouldBe(0f);           // fill: solid disc
        extras[1].SlotF(0).ShouldBe(0.9f, 1e-4);   // ring: 1 - 5/50
    }

    [Fact]
    public void Clip_EncodesScissorRoundTrip()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.PushClip(Rect(20, 30, 60, 80));
        renderer.PopClip();

        var cmds = PresentAndDecode(renderer, bridge);
        cmds[1].Op.ShouldBe(Opcode.SetScissor);
        cmds[1].Slots.Take(4).ShouldBe([20, 30, 40, 50]);
        cmds[2].Op.ShouldBe(Opcode.ClearScissor);
    }

    [Fact]
    public void FirstPresent_SyncsAtlasPageZeroCreatedByCtor()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.Present();

        // The SdfFontAtlas ctor allocated page 0 → OnPageCreated(0) encoded CreatePage before any
        // draw; the first Present must deliver it via SyncAtlas ahead of the draw-stream flush.
        bridge.AtlasSyncs.Count.ShouldBe(1);
        var atlasCmds = Cmd.Decode(bridge.AtlasSyncs[0].Commands);
        atlasCmds[0].Op.ShouldBe(Opcode.CreatePage);
        atlasCmds[0].Slots[0].ShouldBe(0);
        atlasCmds[0].Slots[1].ShouldBe(SdfFontAtlas.DefaultInitialAtlasDim);
        bridge.Calls.IndexOf("syncAtlas").ShouldBeLessThan(bridge.Calls.IndexOf("flush"));
    }
}
