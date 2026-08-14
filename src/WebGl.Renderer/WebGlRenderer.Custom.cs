using System.Runtime.InteropServices;
using DIR.Lib;

namespace WebGl.Renderer;

/// <summary>
/// Consumer-pipeline extensibility (1.3): register custom GLSL ES 3.00 pipelines past the fixed
/// four, upload persistent GPU geometry once, and draw it (optionally instanced) inside the
/// normal frame stream — the seam a GPU sky map / particle field / custom effect builds on.
/// General primitives only; domain pipelines (shaders, geometry, UBO layouts) live in consumers.
/// </summary>
public sealed partial class WebGlRenderer
{
    /// <summary>
    /// Compiles and registers a custom pipeline; the returned handle selects it via
    /// <see cref="UsePipeline"/>. Registration is immediate (one bridge call) — do it at load
    /// time, not per frame.
    /// </summary>
    public PipelineHandle RegisterPipeline(CustomPipelineDescriptor descriptor)
    {
        var triples = new int[descriptor.Attribs.Length * 3];
        for (var i = 0; i < descriptor.Attribs.Length; i++)
        {
            var a = descriptor.Attribs[i];
            triples[i * 3] = a.Location;
            triples[i * 3 + 1] = a.Floats;
            triples[i * 3 + 2] = a.PerInstance ? 1 : 0;
        }
        var id = _bridge.RegisterPipeline(Surface.SurfaceId,
            descriptor.VertexSource, descriptor.FragmentSource,
            triples, (int)descriptor.Topology, (int)descriptor.Blend, descriptor.UniformBlockName);
        return new PipelineHandle(id);
    }

    /// <summary>Creates a persistent GPU buffer from float data (STATIC_DRAW). Geometry uploads
    /// once here; frames reference it via <see cref="DrawBuffer"/>/<see cref="DrawInstanced"/>.</summary>
    public GpuBufferHandle CreateBuffer(ReadOnlySpan<float> data)
        => new(_bridge.CreateBuffer(Surface.SurfaceId, MemoryMarshal.AsBytes(data)));

    /// <summary>Replaces a persistent buffer's contents (rare — a geometry rebuild, never per frame).</summary>
    public void UpdateBuffer(GpuBufferHandle buffer, ReadOnlySpan<float> data)
        => _bridge.UpdateBuffer(Surface.SurfaceId, buffer.Id, MemoryMarshal.AsBytes(data));

    /// <summary>Deletes a persistent buffer. The handle must not be drawn afterwards.</summary>
    public void DestroyBuffer(GpuBufferHandle buffer)
        => _bridge.DestroyBuffer(Surface.SurfaceId, buffer.Id);

    /// <summary>
    /// Uploads the pipeline's std140 uniform block (the per-frame view state; e.g. a sky map's
    /// 112-byte view matrix + viewport block). Executes immediately — call before
    /// <see cref="Present"/> and the frame's draws see the new values; the pan/zoom hot path is
    /// exactly this call plus the unchanged command stream.
    /// </summary>
    public void SetUniformBlock(PipelineHandle pipeline, ReadOnlySpan<byte> data)
        => _bridge.SetUniformBlock(Surface.SurfaceId, pipeline.Id, data);

    /// <summary>Selects a custom pipeline for subsequent draw records (the custom analog of the
    /// internal fixed-pipeline switch; also applies its blend state).</summary>
    public void UsePipeline(PipelineHandle pipeline) => EnsurePipelineId(pipeline.Id);

    /// <summary>Sets the active pipeline's <c>uColor</c> uniform (the push-constant color analog
    /// — e.g. a line set's color), when the shader declares it.</summary>
    public void SetPipelineColor(RGBAColor32 color) => EnsureColor(color);

    /// <summary>Draws <paramref name="count"/> vertices from a persistent buffer with the active
    /// custom pipeline's per-vertex layout and topology, starting at <paramref name="first"/>.</summary>
    public void DrawBuffer(GpuBufferHandle buffer, int first, int count)
        => Surface.Emit(Opcode.DrawBuffer, [buffer.Id, first, count]);

    /// <summary>Instanced draw: per-vertex attributes from <paramref name="vertices"/> (divisor 0),
    /// per-instance attributes from <paramref name="instances"/> (divisor 1), both persistent.
    ///
    /// <para><paramref name="firstInstance"/> draws the instance range
    /// <c>[firstInstance, firstInstance + instanceCount)</c>, so ONE persistent buffer can hold
    /// several independently-drawn groups. WebGL2 has no <c>baseInstance</c> parameter (that is a
    /// desktop-GL 4.2 feature), so this is expressed the only way the API allows: by pointing the
    /// per-instance attributes at a byte offset before the draw. The cost is the attribute
    /// re-binding the draw already does, which is why a caller can afford one draw per group.</para>
    ///
    /// <para>The motivating consumer is a spatially-chunked star field, where the buffer is grouped
    /// by sky region so a view can submit only the regions it can see.</para>
    /// </summary>
    public void DrawInstanced(GpuBufferHandle vertices, int vertexCount, GpuBufferHandle instances, int instanceCount, int firstInstance = 0)
        => Surface.Emit(Opcode.DrawInstanced, [vertices.Id, vertexCount, instances.Id, instanceCount, firstInstance]);
}
