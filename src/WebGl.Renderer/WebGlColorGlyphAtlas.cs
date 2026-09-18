using DIR.Lib;

namespace WebGl.Renderer;

/// <summary>
/// RGBA colour-glyph atlas: shelf-packed page(s) of rasterized COLR/CBDT emoji bitmaps, encoded to
/// the wire protocol's colour-glyph opcodes (<see cref="Opcode.CreateColorPage"/> /
/// <see cref="Opcode.UploadColorTexSubImage"/> / <see cref="Opcode.BindColorTexture"/>) — the
/// bitmap-atlas counterpart to <see cref="SdfFontAtlas"/>'s MTSDF pages, for the one class of glyph
/// an MTSDF field cannot represent (a colour glyph has no single inside/outside to take a distance
/// from).
///
/// <para>A glyph's colouredness is a font-structural fact, independent of size, so it is decided
/// once per (font, glyph identity) the first time this glyph is seen at ANY size
/// (<c>_isColored</c>): a plain outline glyph pays exactly one wasted RGBA rasterization (thrown
/// away in favour of the MTSDF atlas) the first time it is drawn, never again and never at a second
/// size — the routing rule this class exists for is "ask the rasterizer's <see cref="GlyphBitmap.IsColored"/>",
/// never a Unicode-range heuristic (chess piece codepoints U+2654-265F sit inside the "emoji" ranges
/// but are plain outlines). A colour glyph's PIXELS are cached per (font, glyph identity, rounded
/// pixel size) (<c>_glyphs</c>), because a bitmap is not resolution-independent — unlike the SDF
/// atlas, there is no fixed raster size a quad scales from.</para>
///
/// <para>Packing is a fixed-size shelf packer over one or more pages of <see cref="PageDimension"/>²
/// texels: a glyph that does not fit the current shelf starts a new row, a row that does not fit the
/// page starts a new page. No eviction and no per-page dirty-rect growth beyond a full-width
/// scanline range (mirroring <see cref="WebGlSdfAtlasBackend"/>'s v1 simplification) — a documented
/// future refinement, not needed for the handful of distinct emoji glyphs an app actually draws.</para>
/// </summary>
internal sealed class WebGlColorGlyphAtlas
{
    private const int BytesPerTexel = 4;
    private const int DefaultPageDim = 512;
    private const int MinPageDim = 64;

    /// <summary>One packed colour glyph: its page and normalized UV rect, plus display metrics in
    /// RASTER pixels — the caller scales by <see cref="GetGlyphScale"/>, exactly as
    /// <see cref="SdfFontAtlas.GlyphInfo"/> does for MTSDF glyphs. <see cref="Width"/> zero means
    /// "resolved, no ink" (still a colour-glyph identity, e.g. a blank/whitespace one) — the caller
    /// skips the quad but keeps the advance.</summary>
    public readonly record struct GlyphInfo(int Page, float U0, float V0, float U1, float V1,
        int Width, int Height, int BearingX, int BearingY, float AdvanceX);

    private readonly record struct IdentityKey(string Font, uint Gid, string? Type1Name);
    private readonly record struct SizedKey(IdentityKey Identity, int SizePx);

    private sealed class Page
    {
        public byte[] Staging = [];
        public int CursorX;
        public int CursorY;
        public int RowHeight;
        public int DirtyY0 = int.MaxValue;
        public int DirtyY1;

        public bool IsDirty => DirtyY0 < DirtyY1;
    }

    private readonly WebGlContext _ctx;
    private readonly ManagedFontRasterizer _rasterizer;
    private readonly int _pageDim;

    private readonly Dictionary<IdentityKey, bool> _isColored = new();
    private readonly Dictionary<SizedKey, GlyphInfo> _glyphs = new();
    private readonly List<Page> _pages = new();

    public WebGlColorGlyphAtlas(WebGlContext ctx, ManagedFontRasterizer rasterizer, int maxTextureDimension)
    {
        _ctx = ctx;
        _rasterizer = rasterizer;
        _pageDim = Math.Clamp(DefaultPageDim, MinPageDim, Math.Max(MinPageDim, maxTextureDimension));
    }

    public int PageCount => _pages.Count;
    public int PageDimension => _pageDim;

    /// <summary>The scale between the requested display size and the size this atlas actually
    /// rasterized at (whole pixels) — always close to 1, since colour glyphs raster at their own
    /// requested size rather than a fixed reference size the SDF atlas scales quads from.</summary>
    public static float GetGlyphScale(float fontSize) => fontSize / RoundSize(fontSize);

    private static int RoundSize(float fontSize) => Math.Max(1, (int)MathF.Round(fontSize));

    /// <summary>
    /// Resolves a glyph identity to a packed colour-glyph quad, returning false when the identity is
    /// not a colour glyph at all (the caller falls back to the MTSDF atlas). Rasterizes + packs on a
    /// cache miss — synchronous, matching this renderer's <c>synchronousRasterize: true</c> atlas.
    /// </summary>
    public bool TryGetGlyph(string fontFamily, uint gid, string? type1Name, float fontSize, out GlyphInfo info)
    {
        var identity = new IdentityKey(fontFamily, gid, type1Name);
        var sizePx = RoundSize(fontSize);

        if (_isColored.TryGetValue(identity, out var isColored))
        {
            if (!isColored) { info = default; return false; }

            var sizedKey = new SizedKey(identity, sizePx);
            if (_glyphs.TryGetValue(sizedKey, out info)) return true;

            // Known colour glyph, new size: rasterize + pack. No waste here -- these pixels are
            // needed for this size regardless of routing.
            var bitmap = Rasterize(fontFamily, gid, type1Name, sizePx);
            info = Pack(sizedKey, in bitmap);
            return true;
        }

        // First sight of this glyph identity, at any size: rasterize once to learn IsColored. A
        // non-colour result is thrown away here (the MTSDF atlas rasterizes its own copy) -- the
        // one-time cost the class doc above explains.
        var first = Rasterize(fontFamily, gid, type1Name, sizePx);
        _isColored[identity] = first.IsColored;
        if (!first.IsColored) { info = default; return false; }

        info = Pack(new SizedKey(identity, sizePx), in first);
        return true;
    }

    private GlyphBitmap Rasterize(string fontFamily, uint gid, string? type1Name, int sizePx)
        => type1Name is not null
            ? _rasterizer.RasterizeGlyphByType1Name(fontFamily, sizePx, type1Name)
            : _rasterizer.RasterizeGlyphByGid(fontFamily, sizePx, gid);

    private GlyphInfo Pack(SizedKey key, in GlyphBitmap bitmap)
    {
        if (bitmap.Width == 0 || bitmap.Height == 0)
        {
            // No ink but a real advance (matches SdfFontAtlas's blank-glyph handling) -- still a
            // colour-glyph identity, so the caller must not fall back to the SDF atlas for it.
            var blank = new GlyphInfo(0, 0f, 0f, 0f, 0f, 0, 0, bitmap.BearingX, bitmap.BearingY, bitmap.AdvanceX);
            _glyphs[key] = blank;
            return blank;
        }

        if (bitmap.Width > _pageDim || bitmap.Height > _pageDim)
        {
            // Pathologically large relative to the page (e.g. a huge display size on a device with a
            // small MaxTextureSize) -- draw nothing rather than corrupt the packer; the advance still
            // carries. Not expected in practice: emoji labels are modest display sizes.
            var oversize = new GlyphInfo(0, 0f, 0f, 0f, 0f, 0, 0, bitmap.BearingX, bitmap.BearingY, bitmap.AdvanceX);
            _glyphs[key] = oversize;
            return oversize;
        }

        var page = _pages.Count == 0 ? NewPage() : _pages[^1];
        if (page.CursorX + bitmap.Width > _pageDim)
        {
            page.CursorX = 0;
            page.CursorY += page.RowHeight + 1;
            page.RowHeight = 0;
        }
        if (page.CursorY + bitmap.Height > _pageDim)
            page = NewPage();

        var pageIndex = _pages.Count - 1;
        for (var row = 0; row < bitmap.Height; row++)
        {
            var srcOffset = row * bitmap.Width * BytesPerTexel;
            var dstOffset = ((page.CursorY + row) * _pageDim + page.CursorX) * BytesPerTexel;
            Buffer.BlockCopy(bitmap.Rgba, srcOffset, page.Staging, dstOffset, bitmap.Width * BytesPerTexel);
        }
        page.DirtyY0 = Math.Min(page.DirtyY0, page.CursorY);
        page.DirtyY1 = Math.Max(page.DirtyY1, page.CursorY + bitmap.Height);

        var info = new GlyphInfo(pageIndex,
            U0: page.CursorX / (float)_pageDim, V0: page.CursorY / (float)_pageDim,
            U1: (page.CursorX + bitmap.Width) / (float)_pageDim, V1: (page.CursorY + bitmap.Height) / (float)_pageDim,
            bitmap.Width, bitmap.Height, bitmap.BearingX, bitmap.BearingY, bitmap.AdvanceX);

        page.CursorX += bitmap.Width + 1;
        page.RowHeight = Math.Max(page.RowHeight, bitmap.Height);

        _glyphs[key] = info;
        return info;
    }

    private Page NewPage()
    {
        var page = new Page { Staging = new byte[_pageDim * _pageDim * BytesPerTexel] };
        _pages.Add(page);
        _ctx.EmitAtlas(Opcode.CreateColorPage, [_pages.Count - 1, _pageDim]);
        return page;
    }

    /// <summary>Uploads every page's dirty full-width scanline range, mirroring
    /// <see cref="WebGlSdfAtlasBackend.SyncDirtyPages"/>. Returns whether anything was uploaded, so
    /// the caller knows whether a <c>SyncAtlas</c> bridge call is needed this frame.</summary>
    public bool SyncDirtyPages()
    {
        var any = false;
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            if (!page.IsDirty) continue;

            var rowBytes = _pageDim * BytesPerTexel;
            var offset = _ctx.Transfer.Count;
            var height = page.DirtyY1 - page.DirtyY0;
            var length = height * rowBytes;
            _ctx.Transfer.AddRange(page.Staging.AsSpan(page.DirtyY0 * rowBytes, length));
            _ctx.EmitAtlas(Opcode.UploadColorTexSubImage, [i, 0, page.DirtyY0, _pageDim, height, offset, length]);

            page.DirtyY0 = int.MaxValue;
            page.DirtyY1 = 0;
            any = true;
        }
        return any;
    }
}
