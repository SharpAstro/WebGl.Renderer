using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using static WebGl.Renderer.Tests.Fakes.TestGeometry;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// End-to-end text encode over a REAL font and REAL SdfFontAtlas (pure CPU, synchronous
/// rasterize) with only the JS boundary faked: DrawText must produce an atlas upload plus an
/// Sdf-pipeline draw, and MeasureText must return coherent metrics — all on desktop .NET.
/// </summary>
public sealed class TextEncodingTests : IDisposable
{
    private static readonly string FontPath = Path.Combine("Fonts", "DejaVuSans.ttf");

    private readonly FakeWebGlBridge _bridge = new();
    private readonly WebGlRenderer _renderer;

    public TextEncodingTests() => _renderer = WebGlRenderer.Create(_bridge, "text-canvas", 400, 300);

    public void Dispose() => _renderer.Dispose();

    [Fact]
    public void DrawText_EncodesAtlasUploadAndSdfDraw()
    {
        _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        _renderer.DrawText("AB", FontPath, 32f, new RGBAColor32(255, 255, 255, 255),
            Rect(0, 0, 400, 300));
        _renderer.Present();

        // The two fresh glyphs rasterized inline (synchronousRasterize) into page 0's staging;
        // Present must upload the dirty region before the draw stream runs.
        var atlas = Cmd.Decode(_bridge.AtlasSyncs[^1].Commands);
        atlas.ShouldContain(c => c.Op == Opcode.CreatePage);
        var upload = atlas.Single(c => c.Op == Opcode.UploadTexSubImage);
        upload.Slots[0].ShouldBe(0);                                  // page 0
        upload.Slots[3].ShouldBe(SdfFontAtlas.DefaultInitialAtlasDim); // full-width scanlines
        upload.Slots[4].ShouldBeGreaterThan(0);                        // > 0 rows
        upload.Slots[6].ShouldBe(_bridge.AtlasSyncs[^1].Transfer.Length); // length matches transfer

        var cmds = Cmd.Decode(_bridge.Flushes[^1].Commands);
        var programs = cmds.Where(c => c.Op == Opcode.UseProgram).Select(c => c.Slots[0]).ToList();
        programs.ShouldContain((int)PipelineId.Sdf);
        cmds.ShouldContain(c => c.Op == Opcode.BindTexture && c.Slots[0] == 0);
        var draw = cmds.Last(c => c.Op == Opcode.Draw);
        draw.Slots[1].ShouldBe(12); // 2 glyphs × 6 verts
        // sdfEdge uniform carries the analytic AA half-band for this font size.
        var extra = cmds.Last(c => c.Op == Opcode.SetExtra);
        extra.SlotF(0).ShouldBe(_renderer.Atlas.ScreenPxHalfBand(32f), 1e-6);
    }

    [Fact]
    public void MeasureText_ReturnsCoherentMetrics_WithoutTouchingTheBridge()
    {
        var flushesBefore = _bridge.Flushes.Count;
        var (w1, h1) = _renderer.MeasureText("A", FontPath, 32f);
        var (w2, _) = _renderer.MeasureText("AA", FontPath, 32f);

        w1.ShouldBeGreaterThan(0f);
        h1.ShouldBeGreaterThan(0f);
        w2.ShouldBeGreaterThan(w1 * 1.5f, "two glyphs must be roughly double one");
        // Metrics come from the CPU atlas alone — zero interop traffic.
        _bridge.Flushes.Count.ShouldBe(flushesBefore);
    }

    [Fact]
    public void DrawText_SecondFrame_ReusesAtlasWithoutReupload()
    {
        for (var frame = 0; frame < 2; frame++)
        {
            _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
            _renderer.DrawText("AB", FontPath, 32f, new RGBAColor32(255, 255, 255, 255),
                Rect(0, 0, 400, 300));
            _renderer.Present();
        }

        // Frame 1 uploaded the glyphs; frame 2 draws from the resident page — steady state
        // carries no atlas traffic at all.
        _bridge.AtlasSyncs.Count.ShouldBe(1);
        Cmd.Decode(_bridge.Flushes[^1].Commands)
            .Last(c => c.Op == Opcode.Draw).Slots[1].ShouldBe(12);
    }
}
