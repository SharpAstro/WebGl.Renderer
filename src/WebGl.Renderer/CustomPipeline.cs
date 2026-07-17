namespace WebGl.Renderer;

/// <summary>Primitive topology for a custom pipeline's draws. Values are wire protocol
/// (webgl-renderer.js maps them to GL modes) — keep stable.</summary>
public enum PipelineTopology
{
    Triangles = 0,
    Lines = 1,
    LineStrip = 2,
}

/// <summary>Blend state applied while a custom pipeline is active. Values are wire protocol.</summary>
public enum PipelineBlend
{
    /// <summary>Standard "over": SrcAlpha/OneMinusSrcAlpha color, One/OneMinusSrcAlpha alpha —
    /// the fixed pipelines' state.</summary>
    AlphaOver = 0,

    /// <summary>Additive One/One (color and alpha) for premultiplied emissive output — e.g.
    /// star fields, where overlapping glows must sum instead of occlude.</summary>
    Additive = 1,
}

/// <summary>
/// One vertex attribute of a custom pipeline: a shader <c>location</c>, its width in floats,
/// and whether it advances per instance (divisor 1) instead of per vertex. Attributes with the
/// same <see cref="PerInstance"/> flag are interleaved in declaration order into one buffer;
/// stride is the sum of that group's float widths.
/// </summary>
public readonly record struct VertexAttrib(int Location, int Floats, bool PerInstance = false);

/// <summary>Handle to a pipeline registered via <see cref="WebGlRenderer.RegisterPipeline"/>.
/// Ids continue past the fixed <see cref="PipelineId"/> table (first custom id is 4).</summary>
public readonly record struct PipelineHandle(int Id);

/// <summary>Handle to a persistent GPU buffer created via
/// <see cref="WebGlRenderer.CreateBuffer"/>. Lives until destroyed or the surface disposes.</summary>
public readonly record struct GpuBufferHandle(int Id);

/// <summary>
/// Everything needed to compile and register a consumer-defined pipeline: GLSL ES 3.00 shader
/// pair (<c>#version 300 es</c>; the module raw-string convention applies — ASCII only), the
/// attribute layout (split per-vertex / per-instance by <see cref="VertexAttrib.PerInstance"/>),
/// topology, blend, and the optional std140 uniform-block name fed by
/// <see cref="WebGlRenderer.SetUniformBlock"/>. The stock <c>uColor</c>/<c>uExtra</c> uniforms
/// keep working when declared (the <see cref="Opcode.SetColor"/>/<see cref="Opcode.SetExtra"/>
/// records resolve them per program), and <c>uProj</c> is pushed when declared — a pipeline
/// doing its own projection simply omits it.
/// </summary>
public sealed record CustomPipelineDescriptor(
    string VertexSource,
    string FragmentSource,
    VertexAttrib[] Attribs,
    PipelineTopology Topology = PipelineTopology.Triangles,
    PipelineBlend Blend = PipelineBlend.AlphaOver,
    string UniformBlockName = "UBO");
