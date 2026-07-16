using Shouldly;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// String-level sanity for the GLSL ES 3.00 sources (real compilation needs a browser GL context;
/// that's the milestone-1 harness's job). Guards the mechanical Vulkan→ES transform: version
/// pragma present, no leftover Vulkan-isms, fragment precision declared, braces balanced.
/// </summary>
public sealed class ShaderSourceSanityTests
{
    public static IEnumerable<TheoryDataRow<string, string>> AllSources()
    {
        for (var i = 0; i < WebGlPipelines.VertexSources.Length; i++)
        {
            yield return new($"{(PipelineId)i}.vert", WebGlPipelines.VertexSources[i]);
            yield return new($"{(PipelineId)i}.frag", WebGlPipelines.FragmentSources[i]);
        }
    }

    [Theory]
    [MemberData(nameof(AllSources))]
    public void Source_IsValidEs300(string name, string source)
    {
        source.TrimStart().ShouldStartWith("#version 300 es", customMessage: name);
        source.ShouldNotContain("push_constant", customMessage: name);
        source.ShouldNotContain("layout(set", customMessage: name);
        source.ShouldNotContain("#version 450", customMessage: name);
        source.Count(c => c == '{').ShouldBe(source.Count(c => c == '}'), name);
        if (name.EndsWith(".frag"))
        {
            source.ShouldContain("precision highp float;", customMessage: name);
            source.ShouldContain("out vec4 FragColor;", customMessage: name);
        }
    }

    [Fact]
    public void PipelineTables_AreIndexAligned()
    {
        WebGlPipelines.VertexSources.Length.ShouldBe(4);
        WebGlPipelines.FragmentSources.Length.ShouldBe(4);
        // Wire protocol with the JS ATTRIBS table — PipelineId order: Flat, Ellipse, Stroke, Sdf.
        WebGlPipelines.FloatsPerVertex.ShouldBe([2, 4, 6, 4]);
    }

    [Fact]
    public void SdfFragment_KeepsAnalyticBandWithFwidthFallback()
    {
        // The analytic sdfEdge band (uExtra) with fwidth fallback is the desktop renderer's
        // channel-seam artifact fix — losing either branch regresses text AA.
        WebGlPipelines.SdfFragmentSource.ShouldContain("uExtra > 0.0 ? uExtra : fwidth(dist)");
        WebGlPipelines.SdfFragmentSource.ShouldContain("float median(vec3 v)");
    }
}
