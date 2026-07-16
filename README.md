# WebGl.Renderer

WebGL2 rendering backend for [DIR.Lib](https://github.com/SharpAstro/DIR.Lib)'s
`Renderer<TSurface>`, hosted in Blazor WebAssembly. The browser sibling of
[SdlVulkan.Renderer](https://github.com/SharpAstro/SdlVulkan.Renderer) — same drawing
contract, same MTSDF text pipeline, same shared `SdfFontAtlas` core.

## Architecture

- **Command-buffer interop, not chatty GL calls.** Every `Renderer` method synchronously appends
  fixed-stride opcode records + raw vertex floats to in-memory streams (`WebGlContext`); one
  `Present()` hands both to the JS shim (`wwwroot/webgl-renderer.js`) in a single `[JSImport]`
  call, and the shim replays them as `gl.*` calls. Atlas texture traffic rides a separate
  `SyncAtlas` call that fires only when glyph pages actually changed.
- **Text is our own rasterizer, end to end.** Glyphs come from DIR.Lib's managed
  `ManagedFontRasterizer` as MTSDF bitmaps, packed by the shared `SdfFontAtlas` core
  (`framesInFlight: 1`, `synchronousRasterize: true` for single-threaded WASM), uploaded via
  `texSubImage2D`, and drawn with the same `median()` + analytic-`sdfEdge` fragment shader as
  the desktop Vulkan renderer. No CSS fonts, no canvas text APIs.
- **The GL/Vulkan NDC Y-flip lives in the JS-side projection matrix** — shader bodies are
  byte-identical ports of VkPipelineSet's GLSL.
- Pre-bake a `.sdfg` glyph cache at build time (see `SdfGlyphDiskCache`), fetch it into the WASM
  in-memory FS, and startup performs zero rasterization.

## Usage (Blazor WASM)

```csharp
var renderer = await WebGlRenderer.CreateAsync("my-canvas", 760, 840);
renderer.Clear(background);
/* Renderer<WebGlContext> draw calls — FillRectangle, DrawText, ... */
renderer.Present();
```

The JS shim ships as a static web asset (`_content/WebGl.Renderer/webgl-renderer.js`) — no
consumer wiring beyond the NuGet reference.

## Tests

`src/WebGl.Renderer.Tests` runs headless on desktop .NET: command-stream encoding goldens,
shader-source sanity, and real-font text encoding over a fake JS bridge. Real-browser
compile/visual coverage happens in the consuming app (see the chess project's Chess.Web).
