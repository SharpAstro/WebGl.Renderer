using DIR.Lib;

namespace WebGl.Renderer;

/// <summary>
/// WebGL half of the shared <see cref="SdfFontAtlas"/>: translates page-lifecycle hooks and the
/// per-frame dirty-region pull into atlas-stream command records (CreatePage / DestroyPage /
/// UploadTexSubImage + transfer bytes). Holds no GL state — the JS shim owns the textures;
/// this class only encodes.
/// </summary>
internal sealed class WebGlSdfAtlasBackend(WebGlContext ctx) : ISdfAtlasBackend
{
    public void OnPageCreated(int pageIndex, int pageDimension)
        => ctx.EmitAtlas(Opcode.CreatePage, [pageIndex, pageDimension]);

    // WebGL has no frame pipelining — texSubImage2D/deleteTexture are safe immediately.
    public void OnPagesWillBeDestroyed() { }

    public void OnPageDestroyed(int pageIndex)
        => ctx.EmitAtlas(Opcode.DestroyPage, [pageIndex]);

    /// <summary>
    /// The backend flush loop (the WebGL analog of VkSdfFontAtlas.Flush): pull each page's dirty
    /// rect, append its pixels to the transfer buffer, encode one UploadTexSubImage per page.
    /// Uploads the FULL-WIDTH scanline range [Y0, Y1) rather than the tight dirty rect — one
    /// contiguous copy out of staging and one texSubImage2D, no per-row repack. For a pre-baked
    /// chess atlas this runs once at startup over ~one page; the tight-rect optimization is a
    /// documented future refinement for dynamic-text consumers.
    /// </summary>
    public bool SyncDirtyPages(SdfFontAtlas atlas)
    {
        var any = false;
        for (var i = 0; i < atlas.PageCount; i++)
        {
            if (!atlas.TryGetDirtyRegion(i, out var r)) continue;
            var rowBytes = atlas.PageDimension * SdfFontAtlas.BytesPerTexel;
            var offset = ctx.Transfer.Count;
            var length = r.Height * rowBytes;
            ctx.Transfer.AddRange(atlas.GetPageStaging(i).Slice(r.Y0 * rowBytes, length));
            ctx.EmitAtlas(Opcode.UploadTexSubImage,
                [i, 0, r.Y0, atlas.PageDimension, r.Height, offset, length]);
            atlas.MarkPageFlushed(i);
            any = true;
        }
        atlas.CompleteFlush(any);
        return any;
    }
}
