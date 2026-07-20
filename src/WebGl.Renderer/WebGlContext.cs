namespace WebGl.Renderer;

/// <summary>
/// The <c>TSurface</c> for <see cref="WebGlRenderer"/>: the JS-boundary staging area. Holds the
/// per-frame command/vertex buffers plus the (rare) atlas-sync command/transfer buffers, and the
/// capabilities fetched once at init. Has NO drawing knowledge — shape/text logic lives in
/// <see cref="WebGlRenderer"/>, exactly as <c>RgbaImage</c> holds pixels for the software
/// renderer without knowing what a rectangle is.
/// </summary>
public sealed class WebGlContext
{
    /// <summary>Fixed int32 slots per command record (opcode + 7 payload slots).</summary>
    public const int SlotsPerRecord = 8;

    public string CanvasId { get; }

    /// <summary>JS-side registry handle (canvas + gl context + program/texture tables).</summary>
    public int SurfaceId { get; }

    /// <summary>gl.getParameter(MAX_TEXTURE_SIZE), fetched once at init — feeds the atlas core's
    /// maxTextureDimension.</summary>
    public int MaxTextureSize { get; }

    /// <summary>Diagnostics only (e.g. "WebGL 2.0 (OpenGL ES 3.0 Chromium)").</summary>
    public string GlVersion { get; }

    public uint Width { get; private set; }
    public uint Height { get; private set; }

    // Set by Resize() when the surface size changes; consumed (re-emitted as a SetViewport command)
    // by the NEXT BeginFrame(). See the Resize/BeginFrame comments for why the viewport can't be
    // emitted from Resize() directly.
    private bool _viewportDirty;

    // Per-frame draw stream, reset by BeginFrame(). Ints and floats grow independently;
    // Draw records index the vertex stream in vertex units.
    internal readonly List<int> Commands = new(capacity: 64 * SlotsPerRecord);
    internal readonly List<float> Vertices = new(capacity: 1024);

    // Atlas-sync stream — flushed via a separate SyncAtlas bridge call only when the atlas
    // reports dirty (for a pre-baked chess board: once, at startup). Kept apart from the draw
    // stream so the steady-state per-frame flush carries no texture traffic.
    internal readonly List<int> AtlasCommands = new(capacity: 8 * SlotsPerRecord);
    internal readonly List<byte> Transfer = new();

    internal WebGlContext(string canvasId, int surfaceId, uint width, uint height,
        int maxTextureSize, string glVersion)
    {
        CanvasId = canvasId;
        SurfaceId = surfaceId;
        Width = width;
        Height = height;
        MaxTextureSize = maxTextureSize;
        GlVersion = glVersion;
    }

    internal void BeginFrame()
    {
        Commands.Clear();
        Vertices.Clear();
        // Re-emit the viewport as the FIRST command of a frame after a resize. Resize() cannot emit it
        // directly: the consumer's documented pattern is Resize() then a render pass that opens with
        // Clear(), and Clear() -> BeginFrame() resets the command stream, which would discard a
        // viewport command emitted by Resize() -- leaving gl.viewport + projection at the old size
        // while the drawing buffer grew (content renders through a stale viewport: the F11 "black
        // band"). Emitting HERE, after the reset, guarantees it survives to Present(). GL viewport
        // state is sticky across frames, so this only fires when the size actually changed.
        if (_viewportDirty)
        {
            Emit(Opcode.SetViewport, [(int)Width, (int)Height]);
            _viewportDirty = false;
        }
    }

    internal void ClearAtlasStream()
    {
        AtlasCommands.Clear();
        Transfer.Clear();
    }

    internal void Resize(uint width, uint height)
    {
        if (Width == width && Height == height) return;
        Width = width;
        Height = height;
        _viewportDirty = true;
    }

    /// <summary>Append one fixed-stride command record to the draw stream. Missing slots pad with 0.</summary>
    internal void Emit(Opcode op, ReadOnlySpan<int> slots) => EmitTo(Commands, op, slots);

    /// <summary>Append one command record to the atlas-sync stream.</summary>
    internal void EmitAtlas(Opcode op, ReadOnlySpan<int> slots) => EmitTo(AtlasCommands, op, slots);

    private static void EmitTo(List<int> stream, Opcode op, ReadOnlySpan<int> slots)
    {
        stream.Add((int)op);
        for (var i = 0; i < SlotsPerRecord - 1; i++)
            stream.Add(i < slots.Length ? slots[i] : 0);
    }

    /// <summary>Float payload slots ride the int stream bit-exactly; JS reads them back through a
    /// Float32Array view over the same buffer.</summary>
    internal static int F(float value) => BitConverter.SingleToInt32Bits(value);

    /// <summary>Append raw vertex floats; returns the run's first FLOAT index (the Draw record's
    /// firstFloat slot — byte offset = index*4 JS-side, exact for any pipeline stride).</summary>
    internal int AppendVertices(ReadOnlySpan<float> v)
    {
        var firstFloat = Vertices.Count;
        for (var i = 0; i < v.Length; i++) Vertices.Add(v[i]);
        return firstFloat;
    }
}
