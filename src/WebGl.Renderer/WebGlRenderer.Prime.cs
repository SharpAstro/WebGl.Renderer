using System.Text;
using DIR.Lib;

namespace WebGl.Renderer;

public sealed partial class WebGlRenderer
{
    /// <summary>
    /// Warm the glyph pipeline before the first render: triggers the (synchronous, in
    /// <c>synchronousRasterize</c> mode) <c>.sdfg</c> bulk load for each font and drains every
    /// pending insert into the atlas staging, so the first frame draws entirely from resident
    /// glyphs instead of MSDF-rasterizing inline mid-DrawText — on an interpreted single-threaded
    /// WASM host that inline path costs seconds for a screenful of cold glyphs.
    /// The per-frame insert budgets apply per drain iteration; the bounded loop covers pre-baked
    /// sets far larger than any UI's working set. Call once, after CreateAsync, before rendering.
    /// </summary>
    public void PrimeFonts(params string[] fontPaths)
    {
        // First use of a font triggers EnsureFontLoadedFromDisk ('n' is also the reference
        // glyph whitespace advances derive from, so it's never wasted work).
        foreach (var f in fontPaths)
            _atlas.GetGlyph(f, SdfFontAtlas.SdfRasterSize, new Rune('n'));
        // Each BeginFrame drains up to the insert/byte budget; loop until the queues settle.
        // IsDirty also reflects un-uploaded dirty rects, so bound the loop rather than spin on it
        // (the rects upload on the next Present).
        for (var i = 0; i < 8 && _atlas.IsDirty; i++)
            _atlas.BeginFrame();
    }
}
