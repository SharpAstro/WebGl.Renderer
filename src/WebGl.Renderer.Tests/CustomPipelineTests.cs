using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// Pins the 1.3 extensibility seam: pipeline registration (layout triples, topology/blend wire
/// values, block name), persistent-buffer lifecycle, uniform-block upload, the DrawBuffer /
/// DrawInstanced record encoding, and the pipeline-switch interplay with the fixed pipelines.
/// </summary>
public sealed class CustomPipelineTests
{
    private static readonly CustomPipelineDescriptor StarLike = new(
        VertexSource: "#version 300 es\nvoid main(){}",
        FragmentSource: "#version 300 es\nprecision highp float;\nout vec4 o;\nvoid main(){o=vec4(1.0);}",
        Attribs:
        [
            new VertexAttrib(0, 2),                    // aCorner (per-vertex)
            new VertexAttrib(1, 3, PerInstance: true), // aUnitPos
            new VertexAttrib(2, 1, PerInstance: true), // aMagnitude
            new VertexAttrib(3, 1, PerInstance: true), // aBvColor
        ],
        Topology: PipelineTopology.Triangles,
        Blend: PipelineBlend.Additive,
        UniformBlockName: "SkyMapUBO");

    private static (WebGlRenderer Renderer, FakeWebGlBridge Bridge) CreateRenderer()
    {
        var bridge = new FakeWebGlBridge();
        var renderer = WebGlRenderer.Create(bridge, "test-canvas", 320, 240);
        return (renderer, bridge);
    }

    [Fact]
    public void RegisterPipeline_ForwardsLayoutAndReturnsIdPastFixedTable()
    {
        var (renderer, bridge) = CreateRenderer();

        var star = renderer.RegisterPipeline(StarLike);
        var line = renderer.RegisterPipeline(StarLike with
        {
            Attribs = [new VertexAttrib(0, 3)],
            Topology = PipelineTopology.Lines,
            Blend = PipelineBlend.AlphaOver,
        });

        star.Id.ShouldBe(4); // fixed table is 0..3
        line.Id.ShouldBe(5);

        var reg = bridge.RegisteredPipelines[0];
        reg.AttribTriples.ShouldBe([0, 2, 0, 1, 3, 1, 2, 1, 1, 3, 1, 1]);
        reg.Topology.ShouldBe((int)PipelineTopology.Triangles);
        reg.Blend.ShouldBe((int)PipelineBlend.Additive);
        reg.BlockName.ShouldBe("SkyMapUBO");
        bridge.RegisteredPipelines[1].Topology.ShouldBe((int)PipelineTopology.Lines);
    }

    [Fact]
    public void BufferLifecycle_CreateUpdateDestroy_RoundTripsBytes()
    {
        var (renderer, bridge) = CreateRenderer();

        var buffer = renderer.CreateBuffer([1f, 2f, 3f]);
        buffer.Id.ShouldBe(0);
        bridge.Buffers[0].ShouldNotBeNull().Length.ShouldBe(12);

        renderer.UpdateBuffer(buffer, [4f, 5f]);
        bridge.Buffers[0].ShouldNotBeNull().Length.ShouldBe(8);

        renderer.DestroyBuffer(buffer);
        bridge.Buffers[0].ShouldBeNull();
    }

    [Fact]
    public void SetUniformBlock_ForwardsBytesToThePipeline()
    {
        var (renderer, bridge) = CreateRenderer();
        var star = renderer.RegisterPipeline(StarLike);

        Span<byte> ubo = stackalloc byte[112];
        ubo[0] = 0xAB;
        renderer.SetUniformBlock(star, ubo);

        var (pipelineId, data) = bridge.UniformBlocks.ShouldHaveSingleItem();
        pipelineId.ShouldBe(star.Id);
        data.Length.ShouldBe(112);
        data[0].ShouldBe((byte)0xAB);
    }

    [Fact]
    public void DrawBufferAndInstanced_EncodeRecordsAgainstTheActivePipeline()
    {
        var (renderer, bridge) = CreateRenderer();
        var star = renderer.RegisterPipeline(StarLike);
        var quad = renderer.CreateBuffer([0f, 0f, 1f, 0f, 1f, 1f]);
        var stars = renderer.CreateBuffer(new float[5 * 3]);

        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.UsePipeline(star);
        renderer.DrawInstanced(quad, vertexCount: 6, instances: stars, instanceCount: 5);
        renderer.DrawBuffer(quad, first: 0, count: 3);
        renderer.Present();

        var cmds = Cmd.Decode(bridge.Flushes[^1].Commands);
        cmds[1].Op.ShouldBe(Opcode.UseProgram);
        cmds[1].Slots[0].ShouldBe(star.Id);
        cmds[2].Op.ShouldBe(Opcode.DrawInstanced);
        cmds[2].Slots[0].ShouldBe(quad.Id);
        cmds[2].Slots[1].ShouldBe(6);
        cmds[2].Slots[2].ShouldBe(stars.Id);
        cmds[2].Slots[3].ShouldBe(5);
        cmds[3].Op.ShouldBe(Opcode.DrawBuffer);
        cmds[3].Slots[0].ShouldBe(quad.Id);
        cmds[3].Slots[2].ShouldBe(3);
    }

    [Fact]
    public void UsePipeline_InterleavesWithFixedPipelines_SwitchingProgramsBothWays()
    {
        var (renderer, bridge) = CreateRenderer();
        var star = renderer.RegisterPipeline(StarLike);

        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.FillRectangle(new RectInt(new PointInt(0, 0), new PointInt(10, 10)), new RGBAColor32(255, 255, 255, 255));
        renderer.UsePipeline(star);
        renderer.SetPipelineColor(new RGBAColor32(0, 255, 0, 255));
        renderer.FillRectangle(new RectInt(new PointInt(0, 0), new PointInt(10, 10)), new RGBAColor32(255, 255, 255, 255));
        renderer.Present();

        var programs = Cmd.Decode(bridge.Flushes[^1].Commands)
            .Where(c => c.Op == Opcode.UseProgram).Select(c => c.Slots[0]).ToArray();
        programs.ShouldBe([(int)PipelineId.Flat, star.Id, (int)PipelineId.Flat]);
    }
}
