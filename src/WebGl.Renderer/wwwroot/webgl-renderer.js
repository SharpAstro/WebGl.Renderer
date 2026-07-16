// @ts-check
// WebGl.Renderer — the JS half of the command-buffer interop.
//
// .NET (WebGlRenderer) accumulates a per-frame command stream (fixed 8-int32 records; float
// payloads bit-cast into int slots) plus one raw vertex float stream (crossing as bytes), and
// hands both here in a single flush() call. This module owns all live GL state: contexts,
// compiled programs, atlas page textures, the shared dynamic VBO, and the cached projection
// matrix (which is where the GL-vs-Vulkan NDC Y-flip lives — the .NET side never sees it).
//
// MemoryView contract: the spans passed to flush()/syncAtlas() are views over WASM memory that
// are only valid for the duration of the call — everything is consumed synchronously, nothing
// retained. Opcode numbers mirror Opcode.cs exactly (wire protocol — keep in sync).
//
// Typed via @ts-check + JSDoc (no build step): run `npx tsc --noEmit` against the repo's
// jsconfig if you want the check in CI; the shipped file is this source, verbatim.

const SLOTS = 8; // ints per command record

const OP = {
  SetViewport: 1,
  Clear: 2,
  UseProgram: 3,
  SetColor: 4,
  SetExtra: 5,
  BindTexture: 6,
  CreatePage: 7,
  DestroyPage: 8,
  UploadTexSubImage: 9,
  SetScissor: 10,
  ClearScissor: 11,
  Draw: 12,
};

/**
 * Attribute layouts per PipelineId (index-matched with WebGlPipelines.FloatsPerVertex):
 * [location, size] pairs; stride = floatsPerVertex * 4 bytes.
 * @type {ReadonlyArray<ReadonlyArray<readonly [number, number]>>}
 */
const ATTRIBS = [
  [[0, 2]],                  // Flat:    aPos
  [[0, 2], [1, 2]],          // Ellipse: aPos, aLocalPos
  [[0, 2], [1, 2], [2, 2]],  // Stroke:  aP0, aP1, aParams
  [[0, 2], [1, 2]],          // Sdf:     aPos, aTexCoord
];

/**
 * A marshaled .NET MemoryView (has slice()), or a plain typed array from a test harness.
 * @typedef {{ slice?: () => Int32Array | Uint8Array, buffer?: ArrayBufferLike,
 *             byteOffset?: number, byteLength?: number, length?: number }} MemoryViewLike
 */

/**
 * @typedef {{ program: WebGLProgram,
 *             uProj: WebGLUniformLocation | null,
 *             uColor: WebGLUniformLocation | null,
 *             uExtra: WebGLUniformLocation | null,
 *             uTexture: WebGLUniformLocation | null,
 *             floatsPerVertex: number,
 *             attribs: ReadonlyArray<readonly [number, number]> }} Pipeline
 */

/**
 * @typedef {{ canvas: HTMLCanvasElement,
 *             gl: WebGL2RenderingContext,
 *             pipelines: Pipeline[],
 *             pages: (WebGLTexture | null)[],
 *             vbo: WebGLBuffer,
 *             proj: Float32Array,
 *             viewportH: number }} Surface
 */

/** @type {(Surface | null)[]} */
const surfaces = [];

/** @param {number} id @returns {Surface} */
function surface(id) {
  const s = surfaces[id];
  if (!s) throw new Error(`webgl-renderer: unknown surface ${id}`);
  return s;
}

// .NET MemoryView -> typed array. slice() copies the view's bytes out (the view itself is only
// valid during this call — never retain it); plain typed arrays pass through for test harnesses.
/** @param {MemoryViewLike} v @returns {Int32Array} */
function asInt32(v) {
  const c = typeof v.slice === "function" ? v.slice() : v;
  if (c instanceof Int32Array) return c;
  return new Int32Array(
    /** @type {ArrayBufferLike} */ (c.buffer ?? c), c.byteOffset ?? 0, (c.byteLength ?? 0) >> 2);
}

/** @param {MemoryViewLike} v @returns {Uint8Array} */
function asUint8(v) {
  const c = typeof v.slice === "function" ? v.slice() : v;
  if (c instanceof Uint8Array) return c;
  return new Uint8Array(
    /** @type {ArrayBufferLike} */ (c.buffer ?? c), c.byteOffset ?? 0, c.byteLength ?? c.length ?? 0);
}

/** @param {string} canvasId @returns {number} surface id */
export function initContext(canvasId) {
  const canvas = /** @type {HTMLCanvasElement | null} */ (document.getElementById(canvasId));
  if (!canvas) throw new Error(`webgl-renderer: no canvas '${canvasId}'`);
  // antialias for polygon edges (MSDF text antialiases itself in-shader);
  // premultipliedAlpha:false matches the non-premultiplied blend factors below.
  const gl = canvas.getContext("webgl2", { antialias: true, premultipliedAlpha: false });
  if (!gl) throw new Error("webgl-renderer: WebGL2 unavailable");

  const vbo = gl.createBuffer();
  if (!vbo) throw new Error("webgl-renderer: createBuffer failed");

  /** @type {Surface} */
  const s = {
    canvas, gl,
    pipelines: [],
    pages: [],
    vbo,
    proj: new Float32Array(16),
    viewportH: canvas.height,
  };
  gl.enable(gl.BLEND);
  // Standard "over" with alpha-preserving alpha channel — mirrors VkPipelineSet's default:
  // color: SrcAlpha/OneMinusSrcAlpha, alpha: One/OneMinusSrcAlpha.
  gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
  gl.disable(gl.DEPTH_TEST);
  gl.disable(gl.CULL_FACE);

  surfaces.push(s);
  setViewport(s, canvas.width, canvas.height);
  return surfaces.length - 1;
}

/** @param {number} surfaceId @returns {number} */
export function getMaxTextureSize(surfaceId) {
  const s = surface(surfaceId);
  return /** @type {number} */ (s.gl.getParameter(s.gl.MAX_TEXTURE_SIZE));
}

/** @param {number} surfaceId @returns {string} */
export function getGlVersion(surfaceId) {
  const s = surface(surfaceId);
  return String(s.gl.getParameter(s.gl.VERSION));
}

/**
 * Compile every pipeline's program; index = PipelineId (wire order from WebGlPipelines).
 * @param {number} surfaceId
 * @param {string[]} vertexSources
 * @param {string[]} fragmentSources
 * @param {MemoryViewLike} floatsPerVertex
 */
export function compilePipelines(surfaceId, vertexSources, fragmentSources, floatsPerVertex) {
  const s = surface(surfaceId);
  const gl = s.gl;
  const fpv = asInt32(floatsPerVertex);
  for (let i = 0; i < vertexSources.length; i++) {
    const program = link(gl, vertexSources[i], fragmentSources[i]);
    s.pipelines.push({
      program,
      uProj: gl.getUniformLocation(program, "uProj"),
      uColor: gl.getUniformLocation(program, "uColor"),
      uExtra: gl.getUniformLocation(program, "uExtra"),
      uTexture: gl.getUniformLocation(program, "uTexture"),
      floatsPerVertex: fpv[i],
      attribs: ATTRIBS[i],
    });
  }
}

/**
 * @param {WebGL2RenderingContext} gl
 * @param {string} vsSource
 * @param {string} fsSource
 * @returns {WebGLProgram}
 */
function link(gl, vsSource, fsSource) {
  /** @param {number} type @param {string} src @returns {WebGLShader} */
  const compile = (type, src) => {
    const sh = gl.createShader(type);
    if (!sh) throw new Error("webgl-renderer: createShader failed");
    gl.shaderSource(sh, src);
    gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) {
      throw new Error(`webgl-renderer: shader compile failed: ${gl.getShaderInfoLog(sh)}\n${src}`);
    }
    return sh;
  };
  const program = gl.createProgram();
  if (!program) throw new Error("webgl-renderer: createProgram failed");
  gl.attachShader(program, compile(gl.VERTEX_SHADER, vsSource));
  gl.attachShader(program, compile(gl.FRAGMENT_SHADER, fsSource));
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    throw new Error(`webgl-renderer: program link failed: ${gl.getProgramInfoLog(program)}`);
  }
  return program;
}

/** @param {Surface} s @param {number} w @param {number} h */
function setViewport(s, w, h) {
  s.viewportH = h;
  s.gl.viewport(0, 0, w, h);
  // Screen-space ortho with the GL NDC Y-flip (Vulkan's NDC Y points down, m11 = 2/h, m31 = -1;
  // GL's Y points up so that row negates): column-major mat4.
  s.proj.set([
    2 / w, 0, 0, 0,
    0, -2 / h, 0, 0,
    0, 0, -1, 0,
    -1, 1, 0, 1,
  ]);
}

/** @param {Surface} s @param {Pipeline} p */
function applyPipelineUniforms(s, p) {
  const gl = s.gl;
  gl.useProgram(p.program);
  if (p.uProj) gl.uniformMatrix4fv(p.uProj, false, s.proj);
  if (p.uTexture) gl.uniform1i(p.uTexture, 0);
}

/**
 * Execute one frame's command stream against the vertex stream.
 * @param {number} surfaceId
 * @param {MemoryViewLike} commands
 * @param {MemoryViewLike} vertexBytes
 */
export function flush(surfaceId, commands, vertexBytes) {
  const s = surface(surfaceId);
  const gl = s.gl;
  const cmds = asInt32(commands);
  const cmdsF = new Float32Array(cmds.buffer, cmds.byteOffset, cmds.length);
  const vBytes = asUint8(vertexBytes);
  const verts = new Float32Array(vBytes.buffer, vBytes.byteOffset, vBytes.byteLength >> 2);

  gl.bindBuffer(gl.ARRAY_BUFFER, s.vbo);
  gl.bufferData(gl.ARRAY_BUFFER, verts, gl.DYNAMIC_DRAW);

  /** @type {Pipeline | null} */
  let pipeline = null;
  const n = (cmds.length / SLOTS) | 0;
  for (let i = 0; i < n; i++) {
    const b = i * SLOTS;
    switch (cmds[b]) {
      case OP.SetViewport: {
        const w = cmds[b + 1], h = cmds[b + 2];
        s.canvas.width = w;
        s.canvas.height = h;
        setViewport(s, w, h);
        if (pipeline) applyPipelineUniforms(s, pipeline); // re-push the rebuilt projection
        break;
      }
      case OP.Clear:
        gl.clearColor(cmdsF[b + 1], cmdsF[b + 2], cmdsF[b + 3], cmdsF[b + 4]);
        gl.clear(gl.COLOR_BUFFER_BIT);
        break;
      case OP.UseProgram:
        pipeline = s.pipelines[cmds[b + 1]];
        applyPipelineUniforms(s, pipeline);
        break;
      case OP.SetColor:
        if (pipeline?.uColor)
          gl.uniform4f(pipeline.uColor, cmdsF[b + 1], cmdsF[b + 2], cmdsF[b + 3], cmdsF[b + 4]);
        break;
      case OP.SetExtra:
        if (pipeline?.uExtra) gl.uniform1f(pipeline.uExtra, cmdsF[b + 1]);
        break;
      case OP.BindTexture:
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, s.pages[cmds[b + 1]]);
        break;
      case OP.SetScissor: {
        // Command carries top-left-origin screen coords; GL scissor is bottom-left-origin.
        const x = cmds[b + 1], y = cmds[b + 2], w = cmds[b + 3], h = cmds[b + 4];
        gl.enable(gl.SCISSOR_TEST);
        gl.scissor(x, s.viewportH - y - h, w, h);
        break;
      }
      case OP.ClearScissor:
        gl.disable(gl.SCISSOR_TEST);
        break;
      case OP.Draw: {
        if (!pipeline) throw new Error("webgl-renderer: Draw before UseProgram");
        const firstFloat = cmds[b + 1], count = cmds[b + 2];
        const stride = pipeline.floatsPerVertex * 4;
        const base = firstFloat * 4;
        let attrOffset = 0;
        for (const [loc, size] of pipeline.attribs) {
          gl.enableVertexAttribArray(loc);
          gl.vertexAttribPointer(loc, size, gl.FLOAT, false, stride, base + attrOffset);
          attrOffset += size * 4;
        }
        gl.drawArrays(gl.TRIANGLES, 0, count);
        break;
      }
      default:
        throw new Error(`webgl-renderer: unknown opcode ${cmds[b]} at record ${i}`);
    }
  }
}

/**
 * Execute an atlas-sync stream (page lifecycle + texel uploads) against the transfer buffer.
 * @param {number} surfaceId
 * @param {MemoryViewLike} commands
 * @param {MemoryViewLike} transfer
 */
export function syncAtlas(surfaceId, commands, transfer) {
  const s = surface(surfaceId);
  const gl = s.gl;
  const cmds = asInt32(commands);
  const bytes = asUint8(transfer);

  const n = (cmds.length / SLOTS) | 0;
  for (let i = 0; i < n; i++) {
    const b = i * SLOTS;
    switch (cmds[b]) {
      case OP.CreatePage: {
        const pageId = cmds[b + 1], dim = cmds[b + 2];
        const tex = gl.createTexture();
        if (!tex) throw new Error("webgl-renderer: createTexture failed");
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, dim, dim, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        s.pages[pageId] = tex;
        break;
      }
      case OP.DestroyPage: {
        const pageId = cmds[b + 1];
        gl.deleteTexture(s.pages[pageId]);
        // Descending teardown from the atlas core keeps splice index-consistent.
        s.pages.splice(pageId, 1);
        break;
      }
      case OP.UploadTexSubImage: {
        const pageId = cmds[b + 1], x = cmds[b + 2], y = cmds[b + 3];
        const w = cmds[b + 4], h = cmds[b + 5];
        const off = cmds[b + 6], len = cmds[b + 7];
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, s.pages[pageId]);
        gl.texSubImage2D(gl.TEXTURE_2D, 0, x, y, w, h, gl.RGBA, gl.UNSIGNED_BYTE,
          bytes.subarray(off, off + len));
        break;
      }
      default:
        throw new Error(`webgl-renderer: unexpected atlas opcode ${cmds[b]} at record ${i}`);
    }
  }
}

/** @param {number} surfaceId */
export function disposeContext(surfaceId) {
  const s = surfaces[surfaceId];
  if (!s) return;
  const gl = s.gl;
  for (const t of s.pages) if (t) gl.deleteTexture(t);
  for (const p of s.pipelines) gl.deleteProgram(p.program);
  gl.deleteBuffer(s.vbo);
  surfaces[surfaceId] = null;
}
