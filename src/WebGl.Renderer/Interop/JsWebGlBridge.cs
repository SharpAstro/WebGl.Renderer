using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace WebGl.Renderer.Interop;

/// <summary>
/// The real JS boundary: thin wrappers over <c>[JSImport]</c>-generated shims into
/// <c>_content/WebGl.Renderer/webgl-renderer.js</c> (module name "webgl-renderer", imported by
/// <see cref="WebGlRenderer.CreateAsync"/> via <c>JSHost.ImportAsync</c> before any call here).
/// Spans marshal as MemoryViews — zero-copy views over WASM memory that the JS side fully
/// consumes before returning (never retained across calls; the GC may move the buffers after).
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class JsWebGlBridge : IWebGlBridge
{
    public const string ModuleName = "webgl-renderer";

    public int InitContext(string canvasId) => Js.InitContext(canvasId);
    public int GetMaxTextureSize(int surfaceId) => Js.GetMaxTextureSize(surfaceId);
    public string GetGlVersion(int surfaceId) => Js.GetGlVersion(surfaceId);

    public void CompilePipelines(int surfaceId, string[] vertexSources, string[] fragmentSources, int[] floatsPerVertex)
        => Js.CompilePipelines(surfaceId, vertexSources, fragmentSources, floatsPerVertex);

    public void Flush(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<float> vertices)
        => Js.Flush(surfaceId, commands, vertices);

    public void SyncAtlas(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<byte> transfer)
        => Js.SyncAtlas(surfaceId, commands, transfer);

    public int RegisterPipeline(int surfaceId, string vertexSource, string fragmentSource,
        ReadOnlySpan<int> attribTriples, int topology, int blend, string uniformBlockName)
        => Js.RegisterPipeline(surfaceId, vertexSource, fragmentSource,
            attribTriples, topology, blend, uniformBlockName);

    public int CreateBuffer(int surfaceId, ReadOnlySpan<byte> data)
        => Js.CreateBuffer(surfaceId, data);

    public void UpdateBuffer(int surfaceId, int bufferId, ReadOnlySpan<byte> data)
        => Js.UpdateBuffer(surfaceId, bufferId, data);

    public void DestroyBuffer(int surfaceId, int bufferId) => Js.DestroyBuffer(surfaceId, bufferId);

    public void SetUniformBlock(int surfaceId, int pipelineId, ReadOnlySpan<byte> data)
        => Js.SetUniformBlock(surfaceId, pipelineId, data);

    public void DisposeContext(int surfaceId) => Js.DisposeContext(surfaceId);

    private static partial class Js
    {
        [JSImport("initContext", ModuleName)]
        public static partial int InitContext(string canvasId);

        [JSImport("getMaxTextureSize", ModuleName)]
        public static partial int GetMaxTextureSize(int surfaceId);

        [JSImport("getGlVersion", ModuleName)]
        public static partial string GetGlVersion(int surfaceId);

        [JSImport("compilePipelines", ModuleName)]
        public static partial void CompilePipelines(int surfaceId,
            string[] vertexSources, string[] fragmentSources,
            [JSMarshalAs<JSType.MemoryView>] Span<int> floatsPerVertex);

        // MemoryView marshaling supports Span<byte>/<int>/<double> only — float vertices cross
        // the boundary as their byte view (bit-identical; JS rebuilds a Float32Array over them).
        [JSImport("flush", ModuleName)]
        public static partial void Flush(int surfaceId,
            [JSMarshalAs<JSType.MemoryView>] Span<int> commands,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> vertexBytes);

        [JSImport("syncAtlas", ModuleName)]
        public static partial void SyncAtlas(int surfaceId,
            [JSMarshalAs<JSType.MemoryView>] Span<int> commands,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> transfer);

        [JSImport("registerPipeline", ModuleName)]
        public static partial int RegisterPipeline(int surfaceId,
            string vertexSource, string fragmentSource,
            [JSMarshalAs<JSType.MemoryView>] Span<int> attribTriples,
            int topology, int blend, string uniformBlockName);

        [JSImport("createBuffer", ModuleName)]
        public static partial int CreateBuffer(int surfaceId,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

        [JSImport("updateBuffer", ModuleName)]
        public static partial void UpdateBuffer(int surfaceId, int bufferId,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

        [JSImport("destroyBuffer", ModuleName)]
        public static partial void DestroyBuffer(int surfaceId, int bufferId);

        [JSImport("setUniformBlock", ModuleName)]
        public static partial void SetUniformBlock(int surfaceId, int pipelineId,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

        [JSImport("disposeContext", ModuleName)]
        public static partial void DisposeContext(int surfaceId);

        // ReadOnlySpan -> Span adapters (JSImport MemoryView marshaling wants writable spans;
        // the JS side treats them as read-only by contract).
        public static void CompilePipelines(int surfaceId, string[] v, string[] f, int[] floats)
            => CompilePipelines(surfaceId, v, f, floats.AsSpan());

        public static void Flush(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<float> vertices)
            => Flush(surfaceId, UnsafeAsSpan(commands),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(UnsafeAsSpan(vertices)));

        public static void SyncAtlas(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<byte> transfer)
            => SyncAtlas(surfaceId, UnsafeAsSpan(commands), UnsafeAsSpan(transfer));

        public static int RegisterPipeline(int surfaceId, string vs, string fs,
            ReadOnlySpan<int> attribTriples, int topology, int blend, string uniformBlockName)
            => RegisterPipeline(surfaceId, vs, fs, UnsafeAsSpan(attribTriples), topology, blend, uniformBlockName);

        public static int CreateBuffer(int surfaceId, ReadOnlySpan<byte> data)
            => CreateBuffer(surfaceId, UnsafeAsSpan(data));

        public static void UpdateBuffer(int surfaceId, int bufferId, ReadOnlySpan<byte> data)
            => UpdateBuffer(surfaceId, bufferId, UnsafeAsSpan(data));

        public static void SetUniformBlock(int surfaceId, int pipelineId, ReadOnlySpan<byte> data)
            => SetUniformBlock(surfaceId, pipelineId, UnsafeAsSpan(data));

        private static Span<T> UnsafeAsSpan<T>(ReadOnlySpan<T> s)
            => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(s), s.Length);
    }
}
