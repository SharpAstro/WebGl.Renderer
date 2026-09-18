using System.Runtime.InteropServices;
using DIR.Lib;

namespace WebGl.Renderer;

/// <summary>
/// Text rendering through the shared <see cref="SdfFontAtlas"/> — a port of VkRenderer.DrawText's
/// layout loop (line split → shape → two-pass metrics → baseline → per-page glyph quads). Every
/// outline glyph (chess's Merida pieces and DejaVuSans labels included) renders through the MTSDF
/// pipeline; a COLOUR glyph (COLR/CBDT emoji) routes instead to <see cref="WebGlColorGlyphAtlas"/>,
/// an RGBA bitmap atlas + the <see cref="PipelineId.ColorGlyph"/> pipeline, because an MTSDF field
/// has no single inside/outside to take a distance from.
///
/// <para><b>Routing is decided by the rasterizer, never by codepoint range.</b> Each shaped glyph's
/// identity is resolved once and offered to <see cref="WebGlColorGlyphAtlas.TryGetGlyph"/>, which
/// answers from <see cref="DIR.Lib.GlyphBitmap.IsColored"/> — the actual COLR/CBDT presence in the
/// font — and caches the decision per (font, glyph). A Unicode-range heuristic would be wrong here:
/// chess's piece codepoints U+2654–265F sit inside the "emoji" ranges but are plain MTSDF outlines
/// in Merida.ttf, and must stay on the Sdf pipeline.</para>
///
/// <para>A mixed run (ordinary text plus a colour glyph in one <see cref="DrawText"/> call) draws
/// correctly, but not by literally interleaving Sdf/ColorGlyph batches in source order the way
/// VkRenderer does for its Vulkan pipeline-bind constraint: both kinds of quad are accumulated into
/// per-page buckets during the SAME single layout pass, then emitted as two independent grouped
/// passes (all Sdf pages, then all colour pages) — cheaper here because the whole frame is one
/// command stream sent in a single interop call regardless of how many pipeline switches it
/// contains, and correct for any non-overlapping text layout (the only kind normal text produces).</para>
/// </summary>
public sealed partial class WebGlRenderer
{
    private readonly List<ShapedGlyph> _shapedLine = new(capacity: 64);
    // Per-atlas-page vertex buckets for the current DrawText call — each page needs its own
    // BindTexture, so quads group by page and draw as one run per non-empty page.
    private readonly List<List<float>> _sdfPageVertices = new();
    // Same grouping, for the colour-glyph atlas's OWN page table (BindColorTexture, not
    // BindTexture) and the ColorGlyph pipeline instead of Sdf.
    private readonly List<List<float>> _colorPageVertices = new();

    private SdfFontAtlas.GlyphInfo ResolveSdfGlyph(in ShapedGlyph sg, string fontFamily, float fontSize)
        => sg.Glyph is { } id
            ? _atlas.GetGlyphByGid(fontFamily, id.Gid, id.Type1Name)
            : _atlas.GetGlyph(fontFamily, fontSize, sg.Source);

    /// <summary>The one routing decision point (see the class doc): resolves <paramref name="sg"/>'s
    /// glyph identity and asks the colour-glyph atlas, returning false when it is not a colour
    /// glyph at all so the caller falls back to <see cref="ResolveSdfGlyph"/>.</summary>
    private bool TryResolveColorGlyph(in ShapedGlyph sg, string fontFamily, float fontSize,
        out WebGlColorGlyphAtlas.GlyphInfo info)
    {
        var id = sg.Glyph ?? _atlas.Rasterizer.ResolveGlyphIdentity(fontFamily, sg.Source, charCode: -1, GlyphMapHint.Auto);
        return _colorGlyphAtlas.TryGetGlyph(fontFamily, id.Gid, id.Type1Name, fontSize, out info);
    }

    public override (float Width, float Height) MeasureText(ReadOnlySpan<char> text, string fontFamily, float fontSize)
    {
        if (text.IsEmpty) return (0f, 0f);

        var glyphScale = _atlas.GetGlyphScale(fontSize);
        // Same shaper as DrawText, so measured width matches drawn advance exactly. '\n' measures
        // as a whitespace rune (advance from the 'n' reference glyph) — matching the desktop
        // renderer, which never splits lines here either.
        TextShaper.Shape(text, fontFamily, fontSize, _atlas.Rasterizer, _shapedLine);

        var width = 0f;
        var maxAscent = 0f;
        var maxDescent = 0f;
        foreach (ref readonly var sg in CollectionsMarshal.AsSpan(_shapedLine))
        {
            float advanceX, bearingY, height;
            if (TryResolveColorGlyph(in sg, fontFamily, fontSize, out var cg))
            {
                var cscale = WebGlColorGlyphAtlas.GetGlyphScale(fontSize);
                advanceX = cg.AdvanceX * cscale;
                bearingY = cg.BearingY * cscale;
                height = cg.Height * cscale;
            }
            else
            {
                var g = ResolveSdfGlyph(in sg, fontFamily, fontSize);
                advanceX = g.AdvanceX * glyphScale;
                bearingY = g.BearingY * glyphScale;
                height = g.Height * glyphScale;
            }
            width += advanceX + sg.XAdvanceAdjust;
            if (bearingY > maxAscent) maxAscent = bearingY;
            var descent = height - bearingY;
            if (descent > maxDescent) maxDescent = descent;
        }
        return (width, maxAscent + maxDescent);
    }

    public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize,
        RGBAColor32 fontColor, in RectInt layout, TextAlign horizAlignment = TextAlign.Center,
        TextAlign vertAlignment = TextAlign.Near)
    {
        if (text.IsEmpty) return;

        var lineCount = text.Count('\n') + 1;
        var glyphScale = _atlas.GetGlyphScale(fontSize);
        var lineHeight = TextBaseline.LineHeight(fontSize);
        var totalHeight = lineCount * lineHeight;

        var layoutX = (float)layout.UpperLeft.X;
        var layoutY = (float)layout.UpperLeft.Y;
        var layoutW = (float)layout.Width;
        var layoutH = (float)layout.Height;

        var startY = vertAlignment switch
        {
            TextAlign.Center => layoutY + (layoutH - totalHeight) / 2f,
            TextAlign.Far => layoutY + layoutH - totalHeight,
            _ => layoutY
        };

        foreach (var bucket in _sdfPageVertices) bucket.Clear();
        foreach (var bucket in _colorPageVertices) bucket.Clear();
        var anyGlyphs = false;
        var anyColorGlyphs = false;

        var remaining = text;
        for (var lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            var nl = remaining.IndexOf('\n');
            var line = nl < 0 ? remaining : remaining[..nl];
            if (nl >= 0) remaining = remaining[(nl + 1)..];
            if (line.IsEmpty) continue;

            TextShaper.Shape(line, fontFamily, fontSize, _atlas.Rasterizer, _shapedLine);

            // Pass 1: visual metrics (scaled from the SDF raster size to display size) — the same
            // ink-box math the desktop renderer uses, so alignment is backend-identical.
            var advanceSum = 0f;
            var firstBearingX = 0f;
            var lastRightEdge = 0f;
            var maxAscent = 0f;
            var maxDescent = 0f;
            var first = true;
            foreach (ref readonly var sg in CollectionsMarshal.AsSpan(_shapedLine))
            {
                float scaledBearingX, scaledBearingY, scaledWidth, scaledHeight, scaledAdvance;
                if (TryResolveColorGlyph(in sg, fontFamily, fontSize, out var cg))
                {
                    var cscale = WebGlColorGlyphAtlas.GetGlyphScale(fontSize);
                    scaledBearingX = cg.BearingX * cscale;
                    scaledBearingY = cg.BearingY * cscale;
                    scaledWidth = cg.Width * cscale;
                    scaledHeight = cg.Height * cscale;
                    scaledAdvance = cg.AdvanceX * cscale;
                }
                else
                {
                    var g = ResolveSdfGlyph(in sg, fontFamily, fontSize);
                    scaledBearingX = g.BearingX * glyphScale;
                    scaledBearingY = g.BearingY * glyphScale;
                    scaledWidth = g.Width * glyphScale;
                    scaledHeight = g.Height * glyphScale;
                    scaledAdvance = g.AdvanceX * glyphScale;
                }
                if (first && scaledWidth > 0) { firstBearingX = scaledBearingX; first = false; }
                if (scaledWidth > 0) { lastRightEdge = advanceSum + sg.XOffset + scaledBearingX + scaledWidth; }
                if (scaledBearingY > maxAscent) maxAscent = scaledBearingY;
                var descent = scaledHeight - scaledBearingY;
                if (descent > maxDescent) maxDescent = descent;
                advanceSum += scaledAdvance + sg.XAdvanceAdjust;
            }
            var visualWidth = first ? advanceSum : lastRightEdge - firstBearingX;

            var penX = horizAlignment switch
            {
                TextAlign.Center => layoutX + (layoutW - visualWidth) / 2f - firstBearingX,
                TextAlign.Far => layoutX + layoutW - visualWidth - firstBearingX,
                _ => layoutX
            };
            var penY = startY + lineIdx * lineHeight;

            // The FACE's metrics, not this run's ink -- the formula itself now lives in
            // DIR.Lib.TextBaseline rather than being restated here, which is what let the four copies
            // drift. Note the DOM overlay (CanvasTextLayer) already centres by CSS line box, so this
            // also brings the rastered text into agreement with the selectable text drawn over it.
            var faceMetrics = _atlas.Rasterizer.GetVerticalMetrics(fontFamily, fontSize);
            var (baseAscent, baseDescent) = faceMetrics is { } fm
                ? (fm.Ascent, fm.Descent)
                : (maxAscent, maxDescent);
            var baseline = penY + TextBaseline.WithinLine(lineHeight, baseAscent, baseDescent);

            // Pass 2: emit glyph quads into per-page buckets (one bucket list per atlas, routed by
            // the same TryResolveColorGlyph check pass 1 used).
            foreach (ref readonly var sg in CollectionsMarshal.AsSpan(_shapedLine))
            {
                if (TryResolveColorGlyph(in sg, fontFamily, fontSize, out var colorGlyph))
                {
                    var cscale = WebGlColorGlyphAtlas.GetGlyphScale(fontSize);
                    if (colorGlyph.Width > 0)
                    {
                        // Colour-glyph bitmaps pack tight (no SDF spread padding), so the ink
                        // top-left IS the quad top-left -- unlike AddGlyphQuad below.
                        AddColorGlyphQuad(in colorGlyph, cscale,
                            inkX: penX + colorGlyph.BearingX * cscale + sg.XOffset,
                            inkY: baseline - colorGlyph.BearingY * cscale - sg.YOffset);
                        anyGlyphs = true;
                        anyColorGlyphs = true;
                    }
                    penX += colorGlyph.AdvanceX * cscale + sg.XAdvanceAdjust;
                    continue;
                }

                var glyph = ResolveSdfGlyph(in sg, fontFamily, fontSize);
                if (glyph.Width > 0)
                {
                    AddGlyphQuad(in glyph, glyphScale,
                        // BearingX/Y on the SDF atlas are to the TEXTURE edges (incl. spread
                        // padding); the ink sits `pad` inside. Position the ink, then shift the
                        // quad back out by pad when writing vertices.
                        inkX: penX + glyph.BearingX * glyphScale + glyph.Spread * glyphScale + sg.XOffset,
                        inkY: baseline - glyph.BearingY * glyphScale + glyph.Spread * glyphScale - sg.YOffset);
                    anyGlyphs = true;
                }
                penX += glyph.AdvanceX * glyphScale + sg.XAdvanceAdjust;
            }
        }

        if (!anyGlyphs) return;

        // One pipeline/color/edge setup, then one BindTexture + Draw per non-empty Sdf page.
        EnsurePipeline(PipelineId.Sdf);
        EnsureColor(fontColor);
        Surface.Emit(Opcode.SetExtra, [WebGlContext.F(_atlas.ScreenPxHalfBand(fontSize))]);
        for (var page = 0; page < _sdfPageVertices.Count; page++)
        {
            var bucket = _sdfPageVertices[page];
            if (bucket.Count == 0) continue;
            Surface.Emit(Opcode.BindTexture, [page]);
            var firstFloat = Surface.AppendVertices(CollectionsMarshal.AsSpan(bucket));
            Surface.Emit(Opcode.Draw, [firstFloat, bucket.Count / 4]);
        }

        // Second grouped pass: the ColorGlyph pipeline + one BindColorTexture + Draw per non-empty
        // colour page. Gated on anyColorGlyphs (unlike the Sdf block above) so the overwhelmingly
        // common case -- a DrawText call with no emoji at all -- emits no ColorGlyph-pipeline
        // records: DrawText runs every frame for every UI label, and a pipeline/color switch that
        // never draws anything is two wasted commands paid on every one of them.
        if (anyColorGlyphs)
        {
            EnsurePipeline(PipelineId.ColorGlyph);
            EnsureColor(fontColor);
            for (var page = 0; page < _colorPageVertices.Count; page++)
            {
                var bucket = _colorPageVertices[page];
                if (bucket.Count == 0) continue;
                Surface.Emit(Opcode.BindColorTexture, [page]);
                var firstFloat = Surface.AppendVertices(CollectionsMarshal.AsSpan(bucket));
                Surface.Emit(Opcode.Draw, [firstFloat, bucket.Count / 4]);
            }
        }
    }

    /// <summary>Append one glyph's 6-vertex textured quad (pos+uv) to its page bucket — the
    /// non-rotated quad math from VkRenderer.AddBatchedSdfGlyph, xScale = 1.</summary>
    private void AddGlyphQuad(in SdfFontAtlas.GlyphInfo glyph, float glyphScale, float inkX, float inkY)
    {
        _atlas.DecodePage(in glyph, out var page, out var lv0, out var lv1);

        var w = glyph.Width * glyphScale;    // SDF texture width (ink + 2*spread)
        var h = glyph.Height * glyphScale;   // SDF texture height (ink + 2*spread)
        var pad = glyph.Spread * glyphScale;

        // Quad top-left = ink top-left shifted by (-pad, -pad) so the ink inside the SDF texture
        // lands exactly at (inkX, inkY).
        var x0 = inkX - pad;
        var y0 = inkY - pad;
        var x1 = x0 + w;
        var y1 = y0 + h;

        while (_sdfPageVertices.Count <= page)
            _sdfPageVertices.Add(new List<float>(24 * 16));
        var bucket = _sdfPageVertices[page];

        Span<float> v =
        [
            x0, y0, glyph.U0, lv0,
            x1, y0, glyph.U1, lv0,
            x1, y1, glyph.U1, lv1,
            x0, y0, glyph.U0, lv0,
            x1, y1, glyph.U1, lv1,
            x0, y1, glyph.U0, lv1,
        ];
        bucket.AddRange(v);
    }

    /// <summary>Append one colour glyph's 6-vertex textured quad (pos+uv) to its page bucket. Unlike
    /// <see cref="AddGlyphQuad"/> there is no spread/padding and no virtual multi-page V decode: a
    /// <see cref="WebGlColorGlyphAtlas"/> page is a single real texture and its U0..V1 are already
    /// page-local, and the bitmap packs tight, so the ink top-left IS the quad top-left.</summary>
    private void AddColorGlyphQuad(in WebGlColorGlyphAtlas.GlyphInfo glyph, float glyphScale, float inkX, float inkY)
    {
        var w = glyph.Width * glyphScale;
        var h = glyph.Height * glyphScale;

        var x0 = inkX;
        var y0 = inkY;
        var x1 = x0 + w;
        var y1 = y0 + h;

        while (_colorPageVertices.Count <= glyph.Page)
            _colorPageVertices.Add(new List<float>(24 * 16));
        var bucket = _colorPageVertices[glyph.Page];

        Span<float> v =
        [
            x0, y0, glyph.U0, glyph.V0,
            x1, y0, glyph.U1, glyph.V0,
            x1, y1, glyph.U1, glyph.V1,
            x0, y0, glyph.U0, glyph.V0,
            x1, y1, glyph.U1, glyph.V1,
            x0, y1, glyph.U0, glyph.V1,
        ];
        bucket.AddRange(v);
    }
}
