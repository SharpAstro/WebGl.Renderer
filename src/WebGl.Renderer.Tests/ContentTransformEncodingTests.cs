using System.Numerics;
using DIR.Lib;
using Shouldly;
using WebGl.Renderer.Tests.Fakes;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// The content→device transform as it crosses the bridge. What this side owns is <b>which six numbers
/// get sent, and when</b>; the compose itself happens in <c>webgl-renderer.js</c>, because the
/// projection is JS-side (that is where the GL NDC Y-flip lives, and JS builds a projection at surface
/// creation before .NET has sent anything).
///
/// <para>So these tests pin the wire contract and the emission policy. The policy is the half with a
/// failure mode worth a test: emitting every frame would put a record in every stream of every
/// consumer that never rotates anything, and emitting eagerly would lose the command to the
/// <c>Clear()</c> that opens the next frame — the same trap the viewport already documents.</para>
/// </summary>
public sealed class ContentTransformEncodingTests
{
    private static (WebGlRenderer Renderer, FakeWebGlBridge Bridge) CreateRenderer()
    {
        var bridge = new FakeWebGlBridge();
        return (WebGlRenderer.Create(bridge, "test-canvas", 320, 240), bridge);
    }

    private static List<Cmd> Frame(WebGlRenderer renderer, FakeWebGlBridge bridge)
    {
        renderer.Clear(new RGBAColor32(0, 0, 0, 255));
        renderer.Present();
        return Cmd.Decode(bridge.Flushes[^1].Commands);
    }

    private static Cmd? TransformIn(List<Cmd> cmds)
    {
        foreach (var c in cmds)
        {
            if (c.Op == Opcode.SetContentTransform)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// The six coefficients are exactly <c>ToMatrix3x2()</c>'s, in its own row-vector order. JS folds
    /// them in front of the projection assuming precisely that, so the order is wire protocol.
    /// </summary>
    [Fact]
    public void SettingATransform_SendsTheSixAffineCoefficients()
    {
        var (renderer, bridge) = CreateRenderer();
        var transform = ContentTransform.CenteredRotation(Rotation90.Half, 320, 240);

        renderer.ContentTransform = transform;
        var sent = TransformIn(Frame(renderer, bridge));

        sent.ShouldNotBeNull();
        var m = transform.ToMatrix3x2();
        sent.Value.SlotF(0).ShouldBe(m.M11);
        sent.Value.SlotF(1).ShouldBe(m.M12);
        sent.Value.SlotF(2).ShouldBe(m.M21);
        sent.Value.SlotF(3).ShouldBe(m.M22);
        sent.Value.SlotF(4).ShouldBe(m.M31);
        sent.Value.SlotF(5).ShouldBe(m.M32);
    }

    /// <summary>A 180° turn about the surface centre is the across-the-table flip, and it is the one
    /// concrete matrix worth spelling out rather than deriving: negate both axes, translate by the
    /// full extent.</summary>
    [Fact]
    public void TheAcrossTheTableFlip_IsANegationPlusAFullExtentTranslation()
    {
        var (renderer, bridge) = CreateRenderer();

        renderer.ContentTransform = ContentTransform.CenteredRotation(Rotation90.Half, 320, 240);
        var sent = TransformIn(Frame(renderer, bridge));

        sent.ShouldNotBeNull();
        sent.Value.SlotF(0).ShouldBe(-1f);   // m11
        sent.Value.SlotF(1).ShouldBe(0f);    // m12
        sent.Value.SlotF(2).ShouldBe(0f);    // m21
        sent.Value.SlotF(3).ShouldBe(-1f);   // m22
        sent.Value.SlotF(4).ShouldBe(320f);  // m31
        sent.Value.SlotF(5).ShouldBe(240f);  // m32
    }

    /// <summary>
    /// A consumer that never rotates anything must produce the byte stream it produced before this
    /// opcode existed — which is what lets the whole feature land without touching any existing host.
    /// </summary>
    [Fact]
    public void AConsumerThatNeverSetsOne_EmitsNothing()
    {
        var (renderer, bridge) = CreateRenderer();

        TransformIn(Frame(renderer, bridge)).ShouldBeNull();
    }

    /// <summary>Assigning the identity it already has is not a change, so it is not a command.</summary>
    [Fact]
    public void AssigningTheIdentityItAlreadyHas_EmitsNothing()
    {
        var (renderer, bridge) = CreateRenderer();

        renderer.ContentTransform = ContentTransform.Identity;

        TransformIn(Frame(renderer, bridge)).ShouldBeNull();
    }

    /// <summary>
    /// The steady state is "same as last frame": a host sets the transform from its render method, so
    /// re-sending an unchanged value would add a record to every frame forever.
    /// </summary>
    [Fact]
    public void ReassigningTheSameTransform_EmitsNothingTheSecondTime()
    {
        var (renderer, bridge) = CreateRenderer();
        var transform = ContentTransform.CenteredRotation(Rotation90.Half, 320, 240);

        renderer.ContentTransform = transform;
        TransformIn(Frame(renderer, bridge)).ShouldNotBeNull();

        renderer.ContentTransform = transform;
        TransformIn(Frame(renderer, bridge)).ShouldBeNull();
    }

    /// <summary>Going back to the identity is a change like any other — otherwise the frame would stay
    /// rotated after the host stopped asking for it.</summary>
    [Fact]
    public void ReturningToIdentity_IsSentLikeAnyOtherChange()
    {
        var (renderer, bridge) = CreateRenderer();

        renderer.ContentTransform = ContentTransform.CenteredRotation(Rotation90.Half, 320, 240);
        Frame(renderer, bridge);

        renderer.ContentTransform = ContentTransform.Identity;
        var sent = TransformIn(Frame(renderer, bridge));

        sent.ShouldNotBeNull();
        sent.Value.SlotF(0).ShouldBe(1f);
        sent.Value.SlotF(3).ShouldBe(1f);
        sent.Value.SlotF(4).ShouldBe(0f);
        sent.Value.SlotF(5).ShouldBe(0f);
    }

    /// <summary>
    /// Set outside a frame — which is the only way a host ever sets it — the command must survive the
    /// <c>Clear()</c> that opens the next frame. Emitting eagerly would not: <c>Clear()</c> resets the
    /// command stream, which is exactly why the viewport is deferred too.
    /// </summary>
    [Fact]
    public void SetBetweenFrames_SurvivesTheClearThatOpensTheNextOne()
    {
        var (renderer, bridge) = CreateRenderer();
        Frame(renderer, bridge);

        renderer.ContentTransform = ContentTransform.CenteredRotation(Rotation90.Half, 320, 240);
        var cmds = Frame(renderer, bridge);

        TransformIn(cmds).ShouldNotBeNull();
    }

    /// <summary>
    /// JS keeps the transform on the surface and re-folds it whenever the projection is rebuilt, so a
    /// resize does NOT require .NET to re-send it. If that ever stopped being true the frame would
    /// silently un-rotate on the first window resize, which is why it is stated here.
    /// </summary>
    [Fact]
    public void AResize_DoesNotResendTheTransform()
    {
        var (renderer, bridge) = CreateRenderer();
        renderer.ContentTransform = ContentTransform.CenteredRotation(Rotation90.Half, 320, 240);
        Frame(renderer, bridge);

        renderer.Resize(640, 480);
        var cmds = Frame(renderer, bridge);

        cmds.ShouldContain(c => c.Op == Opcode.SetViewport);
        TransformIn(cmds).ShouldBeNull();
    }

    /// <summary>The base contract still holds: the property reads back what was assigned, transform or
    /// not, because consumers compute their input inverse-mapping from it.</summary>
    [Fact]
    public void TheValueReadsBack_SoAHostCanInvertPointerEvents()
    {
        var (renderer, _) = CreateRenderer();
        var transform = new ContentTransform(Rotation90.Cw90, 2f, 10f, 20f);

        renderer.ContentTransform = transform;

        renderer.ContentTransform.ShouldBe(transform);
        var roundTripped = renderer.ContentTransform.Invert(transform.Apply(new Vector2(7, 9)));
        roundTripped.X.ShouldBe(7f, tolerance: 1e-4);
        roundTripped.Y.ShouldBe(9f, tolerance: 1e-4);
    }
}
