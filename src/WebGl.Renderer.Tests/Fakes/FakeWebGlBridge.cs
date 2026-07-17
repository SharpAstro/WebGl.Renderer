using WebGl.Renderer.Interop;

namespace WebGl.Renderer.Tests.Fakes;

/// <summary>
/// Records everything crossing the (fake) JS boundary so tests can assert the exact command
/// stream, vertex floats, and atlas transfer bytes a draw sequence encodes — the whole renderer
/// above the bridge runs on desktop .NET with no browser.
/// </summary>
public sealed class FakeWebGlBridge : IWebGlBridge
{
    public int MaxTextureSize { get; init; } = 4096;

    public readonly List<string> Calls = new();
    public string[]? CompiledVertexSources;
    public string[]? CompiledFragmentSources;
    public readonly List<(int[] Commands, float[] Vertices)> Flushes = new();
    public readonly List<(int[] Commands, byte[] Transfer)> AtlasSyncs = new();

    public int InitContext(string canvasId)
    {
        Calls.Add($"init:{canvasId}");
        return 0;
    }

    public int GetMaxTextureSize(int surfaceId) => MaxTextureSize;
    public string GetGlVersion(int surfaceId) => "FakeGL 2.0";

    public void CompilePipelines(int surfaceId, string[] vertexSources, string[] fragmentSources, int[] floatsPerVertex)
    {
        Calls.Add("compile");
        CompiledVertexSources = vertexSources;
        CompiledFragmentSources = fragmentSources;
    }

    public void Flush(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<float> vertices)
    {
        Calls.Add("flush");
        Flushes.Add((commands.ToArray(), vertices.ToArray()));
    }

    public void SyncAtlas(int surfaceId, ReadOnlySpan<int> commands, ReadOnlySpan<byte> transfer)
    {
        Calls.Add("syncAtlas");
        AtlasSyncs.Add((commands.ToArray(), transfer.ToArray()));
    }

    public readonly List<(string Vs, string Fs, int[] AttribTriples, int Topology, int Blend, string BlockName)> RegisteredPipelines = new();
    public readonly List<byte[]?> Buffers = new();
    public readonly List<(int PipelineId, byte[] Data)> UniformBlocks = new();

    public int RegisterPipeline(int surfaceId, string vertexSource, string fragmentSource,
        ReadOnlySpan<int> attribTriples, int topology, int blend, string uniformBlockName)
    {
        Calls.Add("registerPipeline");
        RegisteredPipelines.Add((vertexSource, fragmentSource, attribTriples.ToArray(), topology, blend, uniformBlockName));
        // Ids continue past the fixed table, exactly like the JS shim's pipelines array.
        return 4 + RegisteredPipelines.Count - 1;
    }

    public int CreateBuffer(int surfaceId, ReadOnlySpan<byte> data)
    {
        Calls.Add("createBuffer");
        Buffers.Add(data.ToArray());
        return Buffers.Count - 1;
    }

    public void UpdateBuffer(int surfaceId, int bufferId, ReadOnlySpan<byte> data)
    {
        Calls.Add($"updateBuffer:{bufferId}");
        Buffers[bufferId] = data.ToArray();
    }

    public void DestroyBuffer(int surfaceId, int bufferId)
    {
        Calls.Add($"destroyBuffer:{bufferId}");
        Buffers[bufferId] = null;
    }

    public void SetUniformBlock(int surfaceId, int pipelineId, ReadOnlySpan<byte> data)
    {
        Calls.Add($"setUniformBlock:{pipelineId}");
        UniformBlocks.Add((pipelineId, data.ToArray()));
    }

    public void DisposeContext(int surfaceId) => Calls.Add("dispose");
}

/// <summary>One decoded fixed-stride command record.</summary>
public readonly record struct Cmd(Opcode Op, int[] Slots)
{
    public float SlotF(int i) => BitConverter.Int32BitsToSingle(Slots[i]);

    public static List<Cmd> Decode(int[] stream)
    {
        var result = new List<Cmd>();
        for (var i = 0; i < stream.Length; i += WebGlContext.SlotsPerRecord)
            result.Add(new Cmd((Opcode)stream[i], stream[(i + 1)..(i + WebGlContext.SlotsPerRecord)]));
        return result;
    }
}
