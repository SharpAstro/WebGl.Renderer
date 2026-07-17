namespace WebGl.Renderer.Interop;

/// <summary>
/// The JS boundary, abstracted so everything above it is testable on desktop .NET:
/// <see cref="JsWebGlBridge"/> is the real <c>[JSImport]</c> wrapper (browser-wasm only);
/// tests substitute a recording fake via the internal <see cref="WebGlRenderer"/> constructor.
/// All methods are synchronous — module loading (the only inherently async step) happens in
/// <see cref="WebGlRenderer.CreateAsync"/> before the bridge is ever called.
/// </summary>
internal interface IWebGlBridge
{
    /// <summary>Acquire a webgl2 context on the canvas and register it; returns the surface id.
    /// Throws when WebGL2 is unavailable (the caller surfaces the CPU-fallback story).</summary>
    int InitContext(string canvasId);

    int GetMaxTextureSize(int surfaceId);
    string GetGlVersion(int surfaceId);

    /// <summary>Compile every pipeline's program (index = <see cref="PipelineId"/>) and build the
    /// JS pipeline table (program + blend state + vertex layout, from floatsPerVertex).</summary>
    void CompilePipelines(int surfaceId, string[] vertexSources, string[] fragmentSources, int[] floatsPerVertex);

    /// <summary>Execute one frame's command stream against the vertex stream. Spans are consumed
    /// synchronously inside the call (MemoryView contract: JS must not retain them).</summary>
    void Flush(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<float> vertices);

    /// <summary>Execute an atlas-sync command stream (CreatePage/DestroyPage/UploadTexSubImage)
    /// against the transfer buffer. Off the per-frame hot path.</summary>
    void SyncAtlas(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<byte> transfer);

    /// <summary>Compile + register one consumer pipeline past the fixed table; returns its id.
    /// <paramref name="attribTriples"/> is the layout flattened as (location, floats, divisor)
    /// per attribute; <paramref name="topology"/>/<paramref name="blend"/> are the wire values of
    /// <see cref="PipelineTopology"/>/<see cref="PipelineBlend"/>.</summary>
    int RegisterPipeline(int surfaceId, string vertexSource, string fragmentSource,
        ReadOnlySpan<int> attribTriples, int topology, int blend, string uniformBlockName);

    /// <summary>Create a persistent GPU buffer (STATIC_DRAW) with the given contents; returns its id.</summary>
    int CreateBuffer(int surfaceId, ReadOnlySpan<byte> data);

    /// <summary>Replace a persistent buffer's contents (rare — geometry rebuilds, not per frame).</summary>
    void UpdateBuffer(int surfaceId, int bufferId, ReadOnlySpan<byte> data);

    /// <summary>Delete a persistent buffer.</summary>
    void DestroyBuffer(int surfaceId, int bufferId);

    /// <summary>Upload a registered pipeline's std140 uniform block (per-frame view state —
    /// the pan/zoom hot path uploads these ~112 bytes instead of any geometry).</summary>
    void SetUniformBlock(int surfaceId, int pipelineId, ReadOnlySpan<byte> data);

    /// <summary>Release the surface's GL objects and registry slot.</summary>
    void DisposeContext(int surfaceId);
}
