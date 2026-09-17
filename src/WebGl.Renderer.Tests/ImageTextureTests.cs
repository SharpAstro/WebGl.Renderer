using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// Pins the consumer-texture seam (1.33): a URL and both wrap modes reach the bridge, the handle is the
/// bridge's id, a bind is its OWN record (never the atlas page bind, whose ids index a different table),
/// and a destroy forwards the id.
/// </summary>
public sealed class ImageTextureTests
{
    private static readonly CustomPipelineDescriptor Sampled = new(
        VertexSource: "#version 300 es\nlayout(location = 0) in vec2 aPos;\nvoid main(){gl_Position=vec4(aPos,0.0,1.0);}",
        FragmentSource: "#version 300 es\nprecision highp float;\nuniform sampler2D uTexture;\nout vec4 o;\nvoid main(){o=texture(uTexture, vec2(0.5));}",
        Attribs: [new VertexAttrib(0, 2)],
        Blend: PipelineBlend.Additive);

    private static (WebGlRenderer Renderer, FakeWebGlBridge Bridge) CreateRenderer()
    {
        var bridge = new FakeWebGlBridge();
        return (WebGlRenderer.Create(bridge, "test-canvas", 320, 240), bridge);
    }

    [Fact]
    public async Task LoadTextureAsync_ForwardsTheUrlAndBothWrapModes()
    {
        var (renderer, bridge) = CreateRenderer();

        var sky = await renderer.LoadTextureAsync("milkyway.png", wrapS: TextureWrap.Repeat, wrapT: TextureWrap.ClampToEdge);
        var map = await renderer.LoadTextureAsync("dust.png");

        sky.Id.ShouldBe(0);
        map.Id.ShouldBe(1);
        bridge.LoadedTextures[0].ShouldBe(("milkyway.png", (int)TextureWrap.Repeat, (int)TextureWrap.ClampToEdge));
        bridge.LoadedTextures[1].ShouldBe(("dust.png", (int)TextureWrap.ClampToEdge, (int)TextureWrap.ClampToEdge),
            "clamp is the default on both axes");
    }

    [Fact]
    public async Task BindTexture_EmitsItsOwnRecordBetweenThePipelineAndTheDraw()
    {
        var (renderer, bridge) = CreateRenderer();
        var pipeline = renderer.RegisterPipeline(Sampled);
        var quad = renderer.CreateBuffer([-1f, -1f, 1f, -1f, 1f, 1f]);
        await renderer.LoadTextureAsync("first.png");
        var texture = await renderer.LoadTextureAsync("second.png");

        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.UsePipeline(pipeline);
        renderer.BindTexture(texture);
        renderer.DrawBuffer(quad, first: 0, count: 3);
        renderer.Present();

        var cmds = Cmd.Decode(bridge.Flushes[^1].Commands);
        var use = cmds.FindIndex(c => c.Op == Opcode.UseProgram && c.Slots[0] == pipeline.Id);
        var bind = cmds.FindIndex(c => c.Op == Opcode.BindImageTexture);
        var draw = cmds.FindIndex(c => c.Op == Opcode.DrawBuffer);
        use.ShouldBeGreaterThanOrEqualTo(0);
        bind.ShouldBeGreaterThan(use);
        draw.ShouldBeGreaterThan(bind);
        cmds[bind].Slots[0].ShouldBe(texture.Id);
        cmds.ShouldNotContain(c => c.Op == Opcode.BindTexture,
            "a consumer texture must not ride the atlas page bind: its id would index the page table");
    }

    [Fact]
    public async Task DestroyTexture_ForwardsTheId()
    {
        var (renderer, bridge) = CreateRenderer();
        await renderer.LoadTextureAsync("a.png");
        var b = await renderer.LoadTextureAsync("b.png");

        renderer.DestroyTexture(b);

        bridge.DestroyedTextures.ShouldBe([b.Id]);
    }
}
