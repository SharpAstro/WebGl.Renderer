namespace WebGl.Renderer;

/// <summary>
/// GLSL ES 3.00 shader sources — transcribed 1:1 from SdlVulkan.Renderer's VkPipelineSet.cs
/// (GLSL 450). Mechanical transform per shader: <c>#version 300 es</c> + fragment
/// <c>precision highp float;</c> (highp, not mediump — the MTSDF smoothstep band lives in a
/// fraction of a texel around 0.5 and mediump banding is visible on mobile GPUs); the
/// push-constant block becomes three plain uniforms (uProj / uColor / uExtra); descriptor-set
/// sampler binding becomes a plain <c>uniform sampler2D</c>. Shader BODIES are byte-identical to
/// the Vulkan sources — the GL/Vulkan NDC Y-flip lives entirely in the projection matrix, which
/// the JS shim computes at SetViewport (m11 = -2/h, m31 = +1); .NET never sees the flip.
/// </summary>
public static class WebGlPipelines
{
    // --- Flat: solid-fill triangles (rects, lines-as-quads fallback, scrims) --------------------

    public const string FlatVertexSource = """
        #version 300 es
        layout(location = 0) in vec2 aPos;
        uniform mat4 uProj;
        void main() {
            gl_Position = uProj * vec4(aPos, 0.0, 1.0);
        }
        """;

    public const string FlatFragmentSource = """
        #version 300 es
        precision highp float;
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() {
            FragColor = uColor;
        }
        """;

    // --- Ellipse: analytic circle/ring fill+stroke via discard ----------------------------------
    // The single combined-predicate discard is kept from the Vulkan source (it worked around a
    // Mesa llvmpipe double-discard-with-MSAA SEGV; irrelevant to WebGL but harmless and proven).

    public const string EllipseVertexSource = """
        #version 300 es
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aLocalPos;
        uniform mat4 uProj;
        out vec2 vLocal;
        void main() {
            gl_Position = uProj * vec4(aPos, 0.0, 1.0);
            vLocal = aLocalPos;
        }
        """;

    public const string EllipseFragmentSource = """
        #version 300 es
        precision highp float;
        uniform vec4 uColor;
        uniform float uExtra; // innerRadius (0 = solid fill, >0 = ring)
        in vec2 vLocal;
        out vec4 FragColor;
        void main() {
            float dist = dot(vLocal, vLocal);
            float innerSq = uExtra * uExtra;
            // Outside the unit disc OR inside the inner ring -> discard. Single
            // statement avoids the llvmpipe double-discard-with-MSAA bug class.
            if (dist > 1.0 || dist < innerSq) discard;
            FragColor = uColor;
        }
        """;

    // --- Stroke: GPU line-segment expansion (screen-space quad built in the VS) -----------------
    // Operates in pixel space before the projection multiply, so the body is Y-flip-agnostic.

    public const string StrokeVertexSource = """
        #version 300 es
        layout(location = 0) in vec2 aP0;
        layout(location = 1) in vec2 aP1;
        layout(location = 2) in vec2 aParams;
        uniform mat4 uProj;
        uniform float uExtra; // halfWidth
        void main() {
            vec2 pos = mix(aP0, aP1, aParams.y);
            vec2 dir = aP1 - aP0;
            float len = length(dir);
            vec2 normal = len > 0.0001 ? vec2(-dir.y, dir.x) / len : vec2(0.0, 1.0);
            pos += normal * aParams.x * uExtra;
            gl_Position = uProj * vec4(pos, 0.0, 1.0);
        }
        """;

    public const string StrokeFragmentSource = """
        #version 300 es
        precision highp float;
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() {
            FragColor = uColor;
        }
        """;

    // --- Sdf: MTSDF text (median reconstruction + analytic AA band) -----------------------------
    // uExtra carries SdfFontAtlas.ScreenPxHalfBand(fontSize): the analytic half-band in field
    // units. fwidth() (core in ES 3.00) remains the fallback when the caller never set it —
    // but the analytic value avoids fwidth's derivative spikes at MSDF channel-switch seams
    // (the detached-gray-dash artifact under round glyphs the desktop renderer fixed in 6.22).

    public const string SdfVertexSource = """
        #version 300 es
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTexCoord;
        uniform mat4 uProj;
        out vec2 vTexCoord;
        void main() {
            gl_Position = uProj * vec4(aPos, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    public const string SdfFragmentSource = """
        #version 300 es
        precision highp float;
        uniform vec4 uColor;
        uniform float uExtra; // sdfEdge: analytic AA half-band; 0 = fwidth fallback
        uniform sampler2D uTexture;
        in vec2 vTexCoord;
        out vec4 FragColor;
        float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
        void main() {
            float dist = median(texture(uTexture, vTexCoord).rgb);
            // Half-width of the smoothstep band in distance units = half a screen pixel.
            // Analytic per-draw value when provided; fwidth fallback otherwise.
            float w = uExtra > 0.0 ? uExtra : fwidth(dist) * 0.5 + 1e-4;
            float alpha = smoothstep(0.5 - w, 0.5 + w, dist);
            if (alpha < 0.005) discard;
            FragColor = vec4(uColor.rgb, uColor.a * alpha);
        }
        """;

    /// <summary>Vertex-shader source per pipeline, ordered by <see cref="PipelineId"/> —
    /// the wire order CompilePipelines hands to the JS shim.</summary>
    public static readonly string[] VertexSources =
        [FlatVertexSource, EllipseVertexSource, StrokeVertexSource, SdfVertexSource];

    /// <summary>Fragment-shader source per pipeline, ordered by <see cref="PipelineId"/>.</summary>
    public static readonly string[] FragmentSources =
        [FlatFragmentSource, EllipseFragmentSource, StrokeFragmentSource, SdfFragmentSource];

    /// <summary>Floats per vertex, per pipeline, ordered by <see cref="PipelineId"/> —
    /// mirrored by the JS pipeline table's attribute layouts.</summary>
    public static readonly int[] FloatsPerVertex = [2, 4, 6, 4];
}
