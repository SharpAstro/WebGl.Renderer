namespace WebGl.Renderer;

/// <summary>
/// Command-stream opcodes. Every record is a fixed <see cref="CommandBuffer.SlotsPerRecord"/>
/// int32 slots: slot 0 = the opcode, slots 1..7 = payload (int32 or float32 bits, reinterpreted
/// per opcode). Fixed stride keeps the JS interpreter a flat indexed loop — no length-prefix
/// parsing, no per-record branching on size. Float payloads are stored via BitConverter
/// SingleToInt32Bits and recovered JS-side through a Float32Array view over the same buffer.
/// </summary>
public enum Opcode
{
    /// <summary>w, h (i32). Resizes the drawing buffer; JS recomputes and caches the ortho
    /// projection (with the GL Y-flip — .NET never sees NDC).</summary>
    SetViewport = 1,

    /// <summary>r, g, b, a (f32, 0..1). gl.clearColor + gl.clear(COLOR_BUFFER_BIT).</summary>
    Clear = 2,

    /// <summary>pipelineId (i32, a <see cref="PipelineId"/>). Looks up {program, blend state,
    /// vertex layout} in the JS pipeline table; gl.useProgram + blendFuncSeparate.</summary>
    UseProgram = 3,

    /// <summary>r, g, b, a (f32, 0..1). uniform4f(uColor) — the push-constant color analog.</summary>
    SetColor = 4,

    /// <summary>value (f32). uniform1f(uExtra) — innerRadius | halfWidth | sdfEdge,
    /// depending on the active pipeline.</summary>
    SetExtra = 5,

    /// <summary>pageId (i32). Binds the atlas page texture to unit 0 for subsequent draws.</summary>
    BindTexture = 6,

    /// <summary>pageId, dim (i32). Allocates a pageId-slotted WebGLTexture (RGBA8, dim²,
    /// LINEAR, CLAMP_TO_EDGE, no mips) — the ISdfAtlasBackend.OnPageCreated analog.</summary>
    CreatePage = 7,

    /// <summary>pageId (i32). gl.deleteTexture + removes the slot (indices above shift down,
    /// mirroring the atlas core's descending RemoveAt teardown).</summary>
    DestroyPage = 8,

    /// <summary>pageId, x, y, w, h, transferOffset, transferLength (i32). texSubImage2D from a
    /// view over the transfer buffer at [transferOffset, transferOffset+transferLength).</summary>
    UploadTexSubImage = 9,

    /// <summary>x, y, w, h (i32). gl.enable(SCISSOR_TEST) + gl.scissor. Y is given in top-left
    /// screen space; JS converts to GL's bottom-left origin using the cached viewport height.</summary>
    SetScissor = 10,

    /// <summary>gl.disable(SCISSOR_TEST).</summary>
    ClearScissor = 11,

    /// <summary>firstFloat, vertexCount (i32). firstFloat indexes the vertex stream in FLOATS
    /// (byte offset = firstFloat*4) — a raw float offset stays exact when pipelines with
    /// different strides (2f/4f/6f) interleave in one buffer, where a vertex-index encoding
    /// would need stride-divisibility invariants. JS sets the bound pipeline's attribute
    /// pointers at that byte offset and drawArrays(TRIANGLES, 0, vertexCount).</summary>
    Draw = 12,
}

/// <summary>
/// Pipeline identities, mirroring VkPipelineSet's pipeline objects. A JS-side table maps each id
/// to {compiled program, blend state, vertex stride/layout}. Ids are wire protocol — keep stable.
/// </summary>
public enum PipelineId
{
    /// <summary>Solid fill, pos(2f), standard over-blend.</summary>
    Flat = 0,
    /// <summary>Analytic circle/ring, pos+localUV(4f); uExtra = innerRadius.</summary>
    Ellipse = 1,
    /// <summary>GPU line-segment expansion, P0+P1+params(6f); uExtra = halfWidth.</summary>
    Stroke = 2,
    /// <summary>MTSDF text, pos+uv(4f); uExtra = sdfEdge (analytic AA half-band).</summary>
    Sdf = 3,
}
