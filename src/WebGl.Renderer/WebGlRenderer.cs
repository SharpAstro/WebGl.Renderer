using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DIR.Lib;
using WebGl.Renderer.Interop;

namespace WebGl.Renderer;

/// <summary>
/// WebGL2 backend for <see cref="Renderer{TSurface}"/>, hosted in Blazor WebAssembly. Every draw
/// method synchronously appends to <see cref="WebGlContext"/>'s command/vertex streams; one
/// <see cref="Present"/> per frame hands both to the JS shim in a single interop call (plus a
/// separate atlas-sync call when glyph pages changed). Text renders through the shared
/// <see cref="SdfFontAtlas"/> core (DIR.Lib) with the same MTSDF shader math as the desktop
/// Vulkan renderer — our own managed rasterizer, no browser text APIs.
///
/// <para>Frame shape (mirrors the Chess.Web canvas pattern, render-on-demand, no RAF loop):
/// <c>Clear(bg)</c> → GameUI/caller issues draws → <c>Present()</c>.</para>
/// </summary>
public sealed partial class WebGlRenderer : Renderer<WebGlContext>
{
    private readonly IWebGlBridge _bridge;
    private readonly SdfFontAtlas _atlas;
    private readonly WebGlSdfAtlasBackend _atlasBackend;
    private readonly ManagedFontRasterizer _rasterizer;
    private readonly bool _ownsRasterizer;

    // Tracked as int so custom pipeline ids (which continue past the fixed PipelineId table)
    // share the same redundant-switch elision as the built-ins.
    private int? _activePipeline;
    private RGBAColor32? _activeColor;

    private WebGlRenderer(WebGlContext ctx, IWebGlBridge bridge,
        ManagedFontRasterizer rasterizer, bool ownsRasterizer, SdfGlyphDiskCache? diskCache)
        : base(ctx)
    {
        _bridge = bridge;
        _rasterizer = rasterizer;
        _ownsRasterizer = ownsRasterizer;
        // Backend before atlas: the core ctor allocates page 0 and fires OnPageCreated(0) inline.
        _atlasBackend = new WebGlSdfAtlasBackend(ctx);
        _atlas = new SdfFontAtlas(rasterizer,
            maxTextureDimension: ctx.MaxTextureSize,
            framesInFlight: 1,                 // no frame pipelining in WebGL
            backend: _atlasBackend,
            diskCache: diskCache,
            synchronousRasterize: true);       // browser WASM: no real thread pool without COOP/COEP
    }

    /// <summary>
    /// Async because <c>[JSImport]</c> requires its ES module loaded first (JSHost.ImportAsync);
    /// everything after that — including every render method — is synchronous.
    /// A pre-baked <c>.sdfg</c> written into <paramref name="diskCache"/>'s directory before this
    /// call gives zero-rasterization startup (the atlas bulk-loads it on first glyph use).
    /// </summary>
    [SupportedOSPlatform("browser")]
    public static async Task<WebGlRenderer> CreateAsync(string canvasId, uint width, uint height,
        ManagedFontRasterizer? rasterizer = null, SdfGlyphDiskCache? diskCache = null)
    {
        // Version-stamped import: the path is NOT fingerprinted by Blazor's asset pipeline, so
        // without the query a browser pairs a CACHED module from a previous package version with
        // the new assembly (e.g. "registerPipeline must be a Function but was undefined" on a
        // static host right after a deploy). The informational version changes per package build.
        var v = Uri.EscapeDataString(typeof(WebGlRenderer).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "0");
        await System.Runtime.InteropServices.JavaScript.JSHost.ImportAsync(
            JsWebGlBridge.ModuleName, $"../_content/WebGl.Renderer/webgl-renderer.js?v={v}");
        return Create(new JsWebGlBridge(), canvasId, width, height, rasterizer, diskCache);
    }

    /// <summary>Bridge-injected factory — the testability seam (tests pass a recording fake).</summary>
    internal static WebGlRenderer Create(IWebGlBridge bridge, string canvasId, uint width, uint height,
        ManagedFontRasterizer? rasterizer = null, SdfGlyphDiskCache? diskCache = null)
    {
        var surfaceId = bridge.InitContext(canvasId);
        var ctx = new WebGlContext(canvasId, surfaceId, width, height,
            bridge.GetMaxTextureSize(surfaceId), bridge.GetGlVersion(surfaceId));
        bridge.CompilePipelines(surfaceId,
            WebGlPipelines.VertexSources, WebGlPipelines.FragmentSources, WebGlPipelines.FloatsPerVertex);
        return new WebGlRenderer(ctx, bridge, rasterizer ?? new ManagedFontRasterizer(),
            ownsRasterizer: rasterizer is null, diskCache);
    }

    public override uint Width => Surface.Width;
    public override uint Height => Surface.Height;

    /// <summary>The shared atlas — exposed for prewarm/diagnostics (e.g. FrameStats).</summary>
    public SdfFontAtlas Atlas => _atlas;

    // ---- frame lifecycle -------------------------------------------------------------------------

    /// <summary>Start a frame: reset the draw streams, run the atlas's bounded drains, and record
    /// the clear. Call once per render, before any draw method.</summary>
    public void Clear(RGBAColor32 background)
    {
        Surface.BeginFrame();
        _activePipeline = null;
        _activeColor = null;
        _atlas.BeginFrame();
        Surface.Emit(Opcode.Clear, [
            WebGlContext.F(background.RedF), WebGlContext.F(background.GreenF),
            WebGlContext.F(background.BlueF), WebGlContext.F(background.AlphaF)]);
    }

    /// <summary>Flush the frame to the GPU: one atlas-sync interop call when glyph pages changed
    /// (for a pre-baked atlas: once, at startup), then one draw-stream interop call. Synchronous.</summary>
    public void Present()
    {
        var atlasDirty = _atlasBackend.SyncDirtyPages(_atlas);
        if (atlasDirty || Surface.AtlasCommands.Count > 0)
        {
            _bridge.SyncAtlas(Surface.SurfaceId,
                CollectionsMarshal.AsSpan(Surface.AtlasCommands),
                CollectionsMarshal.AsSpan(Surface.Transfer));
            Surface.ClearAtlasStream();
        }
        _bridge.Flush(Surface.SurfaceId,
            CollectionsMarshal.AsSpan(Surface.Commands),
            CollectionsMarshal.AsSpan(Surface.Vertices));
    }

    public override void Resize(uint width, uint height)
    {
        Surface.Resize(width, height);
        Surface.Emit(Opcode.SetViewport, [(int)width, (int)height]);
    }

    public override void Dispose()
    {
        _atlas.Dispose();   // fires OnPageDestroyed per page into the (now-moot) atlas stream
        _bridge.DisposeContext(Surface.SurfaceId);
        if (_ownsRasterizer) _rasterizer.Dispose();
    }

    // ---- pipeline/uniform state tracking ----------------------------------------------------------

    private void EnsurePipeline(PipelineId pipeline) => EnsurePipelineId((int)pipeline);

    private void EnsurePipelineId(int pipelineId)
    {
        if (_activePipeline == pipelineId) return;
        Surface.Emit(Opcode.UseProgram, [pipelineId]);
        _activePipeline = pipelineId;
        _activeColor = null; // uColor lives per program — rebind after a program switch
    }

    private void EnsureColor(RGBAColor32 color)
    {
        if (_activeColor == color) return;
        Surface.Emit(Opcode.SetColor, [
            WebGlContext.F(color.RedF), WebGlContext.F(color.GreenF),
            WebGlContext.F(color.BlueF), WebGlContext.F(color.AlphaF)]);
        _activeColor = color;
    }

    private void EmitDraw(ReadOnlySpan<float> verts, int floatsPerVertex)
    {
        var firstFloat = Surface.AppendVertices(verts);
        Surface.Emit(Opcode.Draw, [firstFloat, verts.Length / floatsPerVertex]);
    }

    // ---- solid shapes (Flat pipeline) ---------------------------------------------------------------

    private static void WriteQuad(Span<float> v, float x0, float y0, float x1, float y1)
    {
        v[0] = x0; v[1] = y0;
        v[2] = x1; v[3] = y0;
        v[4] = x1; v[5] = y1;
        v[6] = x0; v[7] = y0;
        v[8] = x1; v[9] = y1;
        v[10] = x0; v[11] = y1;
    }

    public override void FillRectangle(in RectInt rect, RGBAColor32 fillColor)
    {
        EnsurePipeline(PipelineId.Flat);
        EnsureColor(fillColor);
        Span<float> v = stackalloc float[12];
        WriteQuad(v, rect.UpperLeft.X, rect.UpperLeft.Y, rect.LowerRight.X, rect.LowerRight.Y);
        EmitDraw(v, 2);
    }

    /// <summary>Batches consecutive same-color rects into one vertex run + one draw — on WebGL the
    /// dominant cost is draw-call count, so runs matter more than they did under Vulkan.</summary>
    public override void FillRectangles(ReadOnlySpan<(RectInt Rect, RGBAColor32 Color)> rectangles)
    {
        if (rectangles.IsEmpty) return;
        EnsurePipeline(PipelineId.Flat);

        var i = 0;
        Span<float> quad = stackalloc float[12];
        while (i < rectangles.Length)
        {
            var runColor = rectangles[i].Color;
            var runStart = i;
            while (i < rectangles.Length && rectangles[i].Color == runColor) i++;

            EnsureColor(runColor);
            var firstFloat = Surface.Vertices.Count;
            for (var j = runStart; j < i; j++)
            {
                ref readonly var rect = ref rectangles[j].Rect;
                WriteQuad(quad, rect.UpperLeft.X, rect.UpperLeft.Y, rect.LowerRight.X, rect.LowerRight.Y);
                Surface.AppendVertices(quad);
            }
            Surface.Emit(Opcode.Draw, [firstFloat, (i - runStart) * 6]);
        }
    }

    public override void DrawRectangle(in RectInt rect, RGBAColor32 strokeColor, int strokeWidth)
    {
        EnsurePipeline(PipelineId.Flat);
        EnsureColor(strokeColor);
        float x0 = rect.UpperLeft.X, y0 = rect.UpperLeft.Y;
        float x1 = rect.LowerRight.X, y1 = rect.LowerRight.Y;
        var sw = strokeWidth;
        // Four bars in one 24-vertex run + one draw (mirrors VkRenderer.DrawRectangle).
        Span<float> v = stackalloc float[48];
        WriteQuad(v[..12], x0, y0, x1, y0 + sw);             // top
        WriteQuad(v[12..24], x0, y1 - sw, x1, y1);            // bottom
        WriteQuad(v[24..36], x0, y0 + sw, x0 + sw, y1 - sw);  // left
        WriteQuad(v[36..48], x1 - sw, y0 + sw, x1, y1 - sw);  // right
        EmitDraw(v, 2);
    }

    // ---- lines (Stroke pipeline: quad expansion in the vertex shader) --------------------------------

    public override void DrawLine(float x0, float y0, float x1, float y1, RGBAColor32 color, int thickness = 1)
    {
        EnsurePipeline(PipelineId.Stroke);
        EnsureColor(color);
        Surface.Emit(Opcode.SetExtra, [WebGlContext.F(thickness / 2f)]);
        // 6 verts × (P0, P1, params(side, end)); the VS does pos = mix(P0,P1,end) + normal*side*halfWidth.
        Span<float> v = stackalloc float[36];
        WriteStrokeVertex(v, 0, x0, y0, x1, y1, -1f, 0f);
        WriteStrokeVertex(v, 6, x0, y0, x1, y1, +1f, 0f);
        WriteStrokeVertex(v, 12, x0, y0, x1, y1, +1f, 1f);
        WriteStrokeVertex(v, 18, x0, y0, x1, y1, -1f, 0f);
        WriteStrokeVertex(v, 24, x0, y0, x1, y1, +1f, 1f);
        WriteStrokeVertex(v, 30, x0, y0, x1, y1, -1f, 1f);
        EmitDraw(v, 6);
    }

    private static void WriteStrokeVertex(Span<float> v, int at,
        float p0x, float p0y, float p1x, float p1y, float side, float end)
    {
        v[at] = p0x; v[at + 1] = p0y;
        v[at + 2] = p1x; v[at + 3] = p1y;
        v[at + 4] = side; v[at + 5] = end;
    }

    // ---- ellipses (Ellipse pipeline: analytic disc/ring in the fragment shader) ----------------------

    private void EmitEllipseQuad(in RectInt rect, RGBAColor32 color, float innerRadius)
    {
        EnsurePipeline(PipelineId.Ellipse);
        EnsureColor(color);
        Surface.Emit(Opcode.SetExtra, [WebGlContext.F(innerRadius)]);
        float x0 = rect.UpperLeft.X, y0 = rect.UpperLeft.Y;
        float x1 = rect.LowerRight.X, y1 = rect.LowerRight.Y;
        // 6 verts × (pos, localUV in [-1,1]); the FS discards outside the unit disc / inside the ring.
        Span<float> v = stackalloc float[24];
        v[0] = x0; v[1] = y0; v[2] = -1f; v[3] = -1f;
        v[4] = x1; v[5] = y0; v[6] = +1f; v[7] = -1f;
        v[8] = x1; v[9] = y1; v[10] = +1f; v[11] = +1f;
        v[12] = x0; v[13] = y0; v[14] = -1f; v[15] = -1f;
        v[16] = x1; v[17] = y1; v[18] = +1f; v[19] = +1f;
        v[20] = x0; v[21] = y1; v[22] = -1f; v[23] = +1f;
        EmitDraw(v, 4);
    }

    public override void FillEllipse(in RectInt rect, RGBAColor32 fillColor)
        => EmitEllipseQuad(in rect, fillColor, innerRadius: 0f);

    public override void DrawEllipse(in RectInt rect, RGBAColor32 strokeColor, float strokeWidth = 1f)
    {
        // Ring via normalized inner radius: outer radius (local units) is 1; the stroke eats
        // strokeWidth pixels of the semi-minor axis. Exact for circles, the same unit-circle
        // approximation for non-circular ellipses the Vulkan renderer uses.
        var semiMinor = Math.Min(rect.Width, rect.Height) * 0.5f;
        var innerRadius = semiMinor <= 0f ? 0f : Math.Max(0f, 1f - strokeWidth / semiMinor);
        EmitEllipseQuad(in rect, strokeColor, innerRadius);
    }

    // ---- clip (single-level by base-class contract) ---------------------------------------------------

    public override void PushClip(in RectInt rect)
        => Surface.Emit(Opcode.SetScissor,
            [rect.UpperLeft.X, rect.UpperLeft.Y, (int)rect.Width, (int)rect.Height]);

    public override void PopClip() => Surface.Emit(Opcode.ClearScissor, []);
}
