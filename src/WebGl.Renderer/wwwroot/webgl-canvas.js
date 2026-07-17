// @ts-check
// webgl-canvas.js — hi-dpi backing-buffer sizing + resize/dpr observation for WebGlCanvas.razor.
// Loaded by the component via IJSRuntime module import as
// ./_content/WebGl.Renderer/webgl-canvas.js (static web asset, same mechanism as
// webgl-renderer.js). One export pair: attach()/detach().
//
// Ownership: this module owns canvas.width/height from attach() through every subsequent report.
// For a WebGL-backed canvas, WebGlRenderer.Resize's SetViewport command (webgl-renderer.js) ALSO
// assigns canvas.width/height moments later — to the identical numbers, since the parent forwards
// the exact metrics reported here. See the ownership note in WebGlCanvas.razor.cs. For a CPU
// (2D putImageData) canvas there is no other writer at all.

/**
 * The .NET side of the callback: DotNetObjectReference<WebGlCanvas>.
 * @typedef {{ invokeMethodAsync(methodName: string, ...args: unknown[]): Promise<unknown> }} DotNetRef
 */

/**
 * @typedef {Object} Attachment
 * @property {DotNetRef} dotNetRef
 * @property {number} cap             - devicePixelRatio clamp (bounds backing-buffer memory)
 * @property {number} lastDpr         - the dpr of the most recent report (drives the matchMedia re-arm)
 * @property {ResizeObserver} ro
 * @property {MediaQueryList | null} mql
 * @property {() => void} onDprChange
 * @property {number} raf
 * @property {((e: PointerEvent) => void) | null} onPointerDown - capture-on-press listener (null when CapturePointer=false)
 */

/** @type {WeakMap<HTMLCanvasElement, Attachment>} */
const attachments = new WeakMap();

/**
 * Starts observing the canvas: sizes the backing buffer to the laid-out CSS box × dpr, reports
 * metrics to .NET immediately, then again on every layout or devicePixelRatio change.
 * @param {HTMLCanvasElement} canvas
 * @param {DotNetRef} dotNetRef
 * @param {number} maxDevicePixelRatio
 * @param {boolean} capturePointer - capture the pointer on a primary-button press, so the
 *        compatibility mousemove/mouseup keep retargeting to the canvas after the pointer
 *        leaves it (drags survive; the SDL mouse-capture analogue). Release is implicit on
 *        pointerup.
 */
export function attach(canvas, dotNetRef, maxDevicePixelRatio, capturePointer) {
  const cap = maxDevicePixelRatio > 0 ? maxDevicePixelRatio : 3;

  const report = () => {
    const rect = canvas.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, cap);
    const backingWidth = Math.max(1, Math.round(rect.width * dpr));
    const backingHeight = Math.max(1, Math.round(rect.height * dpr));
    canvas.width = backingWidth;
    canvas.height = backingHeight;
    state.lastDpr = dpr;
    return dotNetRef
      .invokeMethodAsync("OnCanvasMetricsAsync", backingWidth, backingHeight, rect.width, rect.height, dpr)
      .catch(() => { /* benign teardown race: component disposed while a report was in flight */ });
  };

  // `(resolution: Xdppx)` only fires `change` when the CURRENT ratio stops matching, so the query
  // must be rebuilt against the just-reported ratio after every report — that way the next change
  // (either direction: zoom, or dragging between differently-scaled monitors) is always caught.
  const armDprWatch = () => {
    state.mql?.removeEventListener("change", state.onDprChange);
    state.mql = window.matchMedia(`(resolution: ${state.lastDpr}dppx)`);
    state.mql.addEventListener("change", state.onDprChange);
  };

  /** @type {Attachment} */
  const state = {
    dotNetRef,
    cap,
    lastDpr: 1,
    // ResizeObserver fires on CSS layout-box changes (window resize, flex/grid reflow,
    // orientation change). Coalesce a burst (e.g. a drag-resize) into one report per frame.
    ro: new ResizeObserver(() => {
      cancelAnimationFrame(state.raf);
      state.raf = requestAnimationFrame(() => { report().then(armDprWatch); });
    }),
    mql: null,
    onDprChange: () => { report().then(armDprWatch); },
    raf: 0,
    onPointerDown: null,
  };

  if (capturePointer) {
    state.onPointerDown = (e) => {
      if (e.button === 0 && canvas.setPointerCapture) {
        try { canvas.setPointerCapture(e.pointerId); } catch { /* pointer already gone (e.g. pen lift) */ }
      }
    };
    canvas.addEventListener("pointerdown", state.onPointerDown);
  }

  state.ro.observe(canvas);
  attachments.set(canvas, state);
  report().then(armDprWatch); // seed lastDpr before building the first matchMedia query
}

/**
 * Stops observing the canvas and drops all listeners. Safe to call on an already-detached canvas.
 * @param {HTMLCanvasElement} canvas
 */
export function detach(canvas) {
  const state = attachments.get(canvas);
  if (!state) return;
  state.ro.disconnect();
  state.mql?.removeEventListener("change", state.onDprChange);
  if (state.onPointerDown) canvas.removeEventListener("pointerdown", state.onPointerDown);
  cancelAnimationFrame(state.raf);
  attachments.delete(canvas);
}
