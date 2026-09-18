using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using static WebGl.Renderer.Tests.Fakes.TestGeometry;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// Pins colour-glyph (COLR/CBDT emoji) routing: a colour glyph (Noto-COLRv1's U+1F5BC FRAME WITH
/// PICTURE -- the exact glyph TianWen's browser sky atlas draws after an object label) must route to
/// <see cref="WebGlColorGlyphAtlas"/> + the <see cref="PipelineId.ColorGlyph"/> pipeline, while a
/// monochrome outline glyph that happens to sit inside a Unicode "emoji" range (a Merida.ttf chess
/// piece, U+2654-265F) must stay on the MTSDF <see cref="PipelineId.Sdf"/> pipeline. Routing is
/// decided from <see cref="GlyphBitmap.IsColored"/> alone -- never a codepoint-range heuristic.
/// </summary>
public sealed class ColorGlyphTests : IDisposable
{
    // A SUBSET of Noto COLRv1 (U+1F5BC and U+1F4F7, COLRv1 layers intact, ~5 KB) rather than the 5 MB face, which
    // this repo would carry as a plain blob forever. Regenerate with
    //   python -m fontTools.subset Noto-COLRv1.ttf --unicodes=U+1F5BC,U+1F4F7 --output-file=Noto-COLRv1-subset.ttf
    private static readonly string EmojiFontPath = Path.Combine("Fonts", "Noto-COLRv1-subset.ttf");
    private static readonly string ChessFontPath = Path.Combine("Fonts", "Merida.ttf");

    // U+1F5BC FRAME WITH PICTURE: a colour glyph in the Noto COLRv1 face.
    private const string Picture = "\U0001F5BC";

    // White king: a plain MTSDF outline in Merida.ttf, despite sitting in the "emoji" Unicode block.
    private const string ChessKing = "♔";

    private readonly FakeWebGlBridge _bridge = new();
    private readonly WebGlRenderer _renderer;

    public ColorGlyphTests() => _renderer = WebGlRenderer.Create(_bridge, "color-glyph-canvas", 400, 300);

    public void Dispose() => _renderer.Dispose();

    [Fact]
    public void DrawText_ColorGlyph_RoutesToTheColorGlyphAtlasAndPipeline()
    {
        _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        _renderer.DrawText(Picture, EmojiFontPath, 32f, new RGBAColor32(255, 255, 255, 255),
            Rect(0, 0, 400, 300));
        _renderer.Present();

        // Synchronous rasterize: the glyph is packed into colour page 0 inline, and Present must
        // upload it (via the colour atlas's OWN opcodes) before the draw stream runs.
        var atlas = Cmd.Decode(_bridge.AtlasSyncs[^1].Commands);
        atlas.ShouldContain(c => c.Op == Opcode.CreateColorPage);
        var upload = atlas.Single(c => c.Op == Opcode.UploadColorTexSubImage);
        upload.Slots[0].ShouldBe(0); // colour page 0
        upload.Slots[6].ShouldBe(_bridge.AtlasSyncs[^1].Transfer.Length);

        var cmds = Cmd.Decode(_bridge.Flushes[^1].Commands);
        var programs = cmds.Where(c => c.Op == Opcode.UseProgram).Select(c => c.Slots[0]).ToList();
        programs.ShouldContain((int)PipelineId.ColorGlyph);
        cmds.ShouldContain(c => c.Op == Opcode.BindColorTexture && c.Slots[0] == 0);
        var draw = cmds.Last(c => c.Op == Opcode.Draw);
        draw.Slots[1].ShouldBe(6); // 1 colour glyph × 6 verts

        // Never rides the SDF atlas's own bind (a different page table).
        cmds.ShouldNotContain(c => c.Op == Opcode.BindTexture);
    }

    [Fact]
    public void DrawText_ChessPieceInsideTheEmojiRange_StaysOnTheSdfPipeline()
    {
        _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        _renderer.DrawText(ChessKing, ChessFontPath, 32f, new RGBAColor32(255, 255, 255, 255),
            Rect(0, 0, 400, 300));
        _renderer.Present();

        // Only the SDF atlas's own page 0 (created eagerly by the SdfFontAtlas ctor) is created --
        // no colour page at all, because the routing check found this glyph is not colour.
        var atlas = Cmd.Decode(_bridge.AtlasSyncs[^1].Commands);
        atlas.ShouldNotContain(c => c.Op == Opcode.CreateColorPage);
        atlas.ShouldNotContain(c => c.Op == Opcode.UploadColorTexSubImage);

        var cmds = Cmd.Decode(_bridge.Flushes[^1].Commands);
        cmds.ShouldNotContain(c => c.Op == Opcode.BindColorTexture);
        cmds.Where(c => c.Op == Opcode.UseProgram).Select(c => c.Slots[0])
            .ShouldNotContain((int)PipelineId.ColorGlyph);
        cmds.Where(c => c.Op == Opcode.UseProgram).Select(c => c.Slots[0])
            .ShouldContain((int)PipelineId.Sdf);
        cmds.Last(c => c.Op == Opcode.Draw).Slots[1].ShouldBe(6); // 1 glyph × 6 verts, on Sdf
    }

    [Fact]
    public void DrawText_ColorGlyph_TheDrawCarriesTheCallersAlpha()
    {
        _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        _renderer.DrawText(Picture, EmojiFontPath, 32f, new RGBAColor32(10, 20, 30, 128),
            Rect(0, 0, 400, 300));
        _renderer.Present();

        var cmds = Cmd.Decode(_bridge.Flushes[^1].Commands);
        var useColorGlyph = cmds.FindIndex(c => c.Op == Opcode.UseProgram && c.Slots[0] == (int)PipelineId.ColorGlyph);
        useColorGlyph.ShouldBeGreaterThanOrEqualTo(0);
        // uColor lives per program, so SetColor is rebound immediately after the program switch --
        // the fragment shader multiplies the glyph's own alpha by uColor.a, fading the whole glyph
        // with the label it was drawn in.
        var setColor = cmds[(useColorGlyph + 1)..].First(c => c.Op == Opcode.SetColor);
        setColor.SlotF(3).ShouldBe(128f / 255f, 1e-6);
    }

    [Fact]
    public void MeasureText_ColorGlyph_WidthMatchesTheAdvanceDrawTextActuallyUses()
    {
        // Two copies of the same glyph, left/top aligned so pen math has no centering offset: the
        // x-distance between the two quads' first vertex IS the advance DrawText used for the first
        // glyph, read straight out of the encoded vertex stream (ground truth, not a re-derivation).
        _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        _renderer.DrawText(Picture + Picture, EmojiFontPath, 32f, new RGBAColor32(255, 255, 255, 255),
            Rect(0, 0, 400, 300), TextAlign.Near, TextAlign.Near);
        _renderer.Present();

        var verts = _bridge.Flushes[^1].Vertices;
        var cmds = Cmd.Decode(_bridge.Flushes[^1].Commands);
        var draw = cmds.Last(c => c.Op == Opcode.Draw);
        draw.Slots[1].ShouldBe(12); // 2 glyphs × 6 verts

        // Vertex layout is pos(2f)+uv(2f); glyph 2's first vertex starts 6 verts (24 floats) later.
        var firstFloat = draw.Slots[0];
        var glyph1X0 = verts[firstFloat];
        var glyph2X0 = verts[firstFloat + 6 * 4];
        var advanceUsed = glyph2X0 - glyph1X0;

        var (measuredWidth, _) = _renderer.MeasureText(Picture, EmojiFontPath, 32f);
        advanceUsed.ShouldBe(measuredWidth, 1e-3);
    }

    [Fact]
    public void DrawText_SameColorGlyphSecondFrame_ReusesTheAtlasWithoutReupload()
    {
        for (var frame = 0; frame < 2; frame++)
        {
            _renderer.Clear(new RGBAColor32(0, 0, 0, 255));
            _renderer.DrawText(Picture, EmojiFontPath, 32f, new RGBAColor32(255, 255, 255, 255),
                Rect(0, 0, 400, 300));
            _renderer.Present();
        }

        // Frame 1 packed + uploaded the glyph; frame 2 draws from the resident page -- one upload
        // total, cached per (font, glyph, size) exactly as the class doc promises.
        var uploads = _bridge.AtlasSyncs
            .SelectMany(sync => Cmd.Decode(sync.Commands))
            .Count(c => c.Op == Opcode.UploadColorTexSubImage);
        uploads.ShouldBe(1);
        Cmd.Decode(_bridge.Flushes[^1].Commands)
            .Last(c => c.Op == Opcode.Draw).Slots[1].ShouldBe(6);
    }
}
