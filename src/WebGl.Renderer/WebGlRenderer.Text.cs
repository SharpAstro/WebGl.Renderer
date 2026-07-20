using System.Runtime.InteropServices;
using DIR.Lib;

namespace WebGl.Renderer;

/// <summary>
/// Text rendering through the shared <see cref="SdfFontAtlas"/> — a port of VkRenderer.DrawText's
/// layout loop (line split → shape → two-pass metrics → baseline → per-page glyph quads), MSDF
/// path only. There is no bitmap/color-glyph atlas in v1: every glyph renders through the MTSDF
/// pipeline, which covers all outline fonts (chess's Merida pieces and DejaVuSans labels
/// included). COLR/CBDT color glyphs are out of scope until a consumer needs them — routing
/// should then use the rasterizer's IsColored result, never Unicode-range heuristics (chess's
/// piece codepoints U+2654–265F sit inside the "emoji" ranges but are plain outlines).
/// </summary>
public sealed partial class WebGlRenderer
{
    private readonly List<ShapedGlyph> _shapedLine = new(capacity: 64);
    // Per-atlas-page vertex buckets for the current DrawText call — each page needs its own
    // BindTexture, so quads group by page and draw as one run per non-empty page.
    private readonly List<List<float>> _sdfPageVertices = new();

    private SdfFontAtlas.GlyphInfo ResolveSdfGlyph(in ShapedGlyph sg, string fontFamily, float fontSize)
        => sg.Glyph is { } id
            ? _atlas.GetGlyphByGid(fontFamily, id.Gid, id.Type1Name)
            : _atlas.GetGlyph(fontFamily, fontSize, sg.Source);

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
            var g = ResolveSdfGlyph(in sg, fontFamily, fontSize);
            width += g.AdvanceX * glyphScale + sg.XAdvanceAdjust;
            var bearingY = g.BearingY * glyphScale;
            if (bearingY > maxAscent) maxAscent = bearingY;
            var descent = g.Height * glyphScale - bearingY;
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
        var lineHeight = fontSize * 1.3f;
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
        var anyGlyphs = false;

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
                var g = ResolveSdfGlyph(in sg, fontFamily, fontSize);
                var scaledBearingX = g.BearingX * glyphScale;
                var scaledBearingY = g.BearingY * glyphScale;
                var scaledWidth = g.Width * glyphScale;
                var scaledHeight = g.Height * glyphScale;
                if (first && scaledWidth > 0) { firstBearingX = scaledBearingX; first = false; }
                if (scaledWidth > 0) { lastRightEdge = advanceSum + sg.XOffset + scaledBearingX + scaledWidth; }
                if (scaledBearingY > maxAscent) maxAscent = scaledBearingY;
                var descent = scaledHeight - scaledBearingY;
                if (descent > maxDescent) maxDescent = descent;
                advanceSum += g.AdvanceX * glyphScale + sg.XAdvanceAdjust;
            }
            var visualWidth = first ? advanceSum : lastRightEdge - firstBearingX;

            var penX = horizAlignment switch
            {
                TextAlign.Center => layoutX + (layoutW - visualWidth) / 2f - firstBearingX,
                TextAlign.Far => layoutX + layoutW - visualWidth - firstBearingX,
                _ => layoutX
            };
            var penY = startY + lineIdx * lineHeight;

            // Visual centering on the line's actual ascent/descent — identical formula across
            // RgbaImageRenderer / VkRenderer / here.
            var baseline = penY + (lineHeight + maxAscent - maxDescent) / 2f;

            // Pass 2: emit glyph quads into per-page buckets.
            foreach (ref readonly var sg in CollectionsMarshal.AsSpan(_shapedLine))
            {
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

        // One pipeline/color/edge setup, then one BindTexture + Draw per non-empty page.
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
}
