# CLAUDE.md

Guidance for Claude Code in this repository.

## Build & Test

```bash
dotnet build src/WebGl.Renderer/WebGl.Renderer.csproj -c Release
dotnet test src/WebGl.Renderer.Tests/WebGl.Renderer.Tests.csproj
```

Local builds use the DIR.Lib sibling checkout (`../DIR.Lib`) via the `UseLocalDirLib` switch;
CI restores DIR.Lib from NuGet. Verify pin changes with `-p:UseLocalDirLib=false`.

## Architecture (one screen)

- `WebGlRenderer : Renderer<WebGlContext>` (DIR.Lib contract). Draw methods append to
  `WebGlContext`'s command/vertex streams; `Present()` flushes via ONE `[JSImport]` call.
  `WebGlRenderer.Text.cs` ports VkRenderer's DrawText layout loop (MSDF-only, no bitmap atlas).
- `Opcode.cs` + `wwwroot/webgl-renderer.js` are a WIRE PROTOCOL — fixed 8-int32 records, float
  payloads bit-cast into int slots, opcode numbers and the JS `ATTRIBS` table must stay in sync
  with `WebGlPipelines.FloatsPerVertex`.
- `WebGlPipelines.cs`: GLSL ES 3.00 sources transcribed from SdlVulkan.Renderer's
  VkPipelineSet.cs. Shader BODIES stay byte-identical to the Vulkan sources; the NDC Y-flip
  lives in the JS-side projection matrix (`setViewport`), never in shaders or .NET.
- `WebGlSdfAtlasBackend : ISdfAtlasBackend` (DIR.Lib SdfFontAtlas core): encodes
  CreatePage/DestroyPage from lifecycle hooks; `SyncDirtyPages` pulls dirty rects and uploads
  FULL-WIDTH scanline ranges (deliberate v1 simplification).
- `Interop/IWebGlBridge` is the testability seam: `JsWebGlBridge` = real `[JSImport]`
  (browser-only), tests inject `FakeWebGlBridge` via `WebGlRenderer.Create`.

## Constraints learned the hard way (do not regress)

- Atlas config MUST be `framesInFlight: 1, synchronousRasterize: true` — browser WASM without
  COOP/COEP (e.g. GitHub Pages) has no real thread pool; `Task.Run` rasterization stalls.
- MemoryView spans passed to the bridge are only valid during the call — JS consumes
  synchronously, never retains.
- `Draw` records carry a FLOAT offset (byte = ×4), not a vertex index — mixed strides
  (2f/4f/6f) share one VBO.
- Sdf fragment shader keeps BOTH the analytic `uExtra` band and the `fwidth` fallback
  (channel-seam artifact fix, see SdlVulkan.Renderer 6.22 history).

## Release

CI publishes `X.Y.<run_number>` to NuGet on main push.

**A release bump is ONE line:** `<VersionMajorMinor>` in `src/Directory.Build.props`. Everything else
derives from it and must not be hand-edited — `VersionPrefix` (guarded on empty so CI's `-p:Version`
wins), `AssemblyVersion` as `$(VersionMajorMinor).0.0`, and the workflow's `VERSION_PREFIX`, which the
build job RESOLVES by reading the property back (`dotnet msbuild src/Directory.Build.props
-getProperty:VersionMajorMinor`) rather than restating it. So CI cannot stamp a version the packages
disagree with, and **no csproj declares a `VersionPrefix` of its own** — one that did would silently
win. (This doc used to say the workflow and the csproj "must bump together"; neither of those two
places holds a number any more.)
The matching release note goes in [CHANGELOG.md](CHANGELOG.md) at the repo root, newest first, one
`## Major.Minor` section each. (It used to live in a comment block in the workflow's `env:`; nothing
ever read it there, and it had grown to 120 of that file's 199 lines.)
Consumed by the chess repo via floating pin — see chess's `release-lib` skill for the chain.
