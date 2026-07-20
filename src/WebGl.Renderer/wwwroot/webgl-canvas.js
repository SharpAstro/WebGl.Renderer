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
 * @property {"none" | "pan" | "pinch" | "ended"} touchMode - current touch gesture
 * @property {number} pinchLastDist - inter-finger distance at the previous pinch step (px)
 * @property {((e: TouchEvent) => void) | null} onTouchStart
 * @property {((e: TouchEvent) => void) | null} onTouchMove
 * @property {((e: TouchEvent) => void) | null} onTouchEnd
 * @property {() => void} onFsChange - immediate re-measure on Fullscreen API transitions
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
    // Assign canvas.width/height ONLY on a real change. Writing either attribute -- even to its
    // current value -- resets the drawing buffer (clears it to transparent black) per the HTML spec.
    // The .NET OnCanvasMetricsAsync dedupes identical metrics and skips the repaint, so an
    // unconditional assign on a redundant ResizeObserver tick (e.g. the settling ticks an F11
    // fullscreen transition emits, which round to the same backing size) would blank the canvas with
    // no repaint to follow -- the F11 "black band". Guarding the assign keeps the last frame intact
    // when nothing changed; a genuine resize still clears + repaints via OnResized below.
    if (canvas.width !== backingWidth) canvas.width = backingWidth;
    if (canvas.height !== backingHeight) canvas.height = backingHeight;
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
    touchMode: "none",
    pinchLastDist: 0,
    onTouchStart: null,
    onTouchMove: null,
    onTouchEnd: null,
    // Fullscreen API transitions (element.requestFullscreen / Esc) re-lay-out the page; the
    // ResizeObserver above WILL catch that, but only on its next rAF-coalesced tick, which can leave
    // a visibly stale/letterboxed frame during the transition. Re-measure immediately instead.
    // (Plain F11 is browser-reserved: it fires no fullscreenchange and needs no handling here -- it
    // just resizes the viewport, which the ResizeObserver already handles like any window resize.)
    onFsChange: () => { report().then(armDprWatch); },
  };
  document.addEventListener("fullscreenchange", state.onFsChange);

  if (capturePointer) {
    state.onPointerDown = (e) => {
      if (e.button === 0 && canvas.setPointerCapture) {
        try { canvas.setPointerCapture(e.pointerId); } catch { /* pointer already gone (e.g. pen lift) */ }
      }
    };
    canvas.addEventListener("pointerdown", state.onPointerDown);
  }

  // ── Touch -> pointer/pinch bridge ──────────────────────────────────────────
  // touch-action:none (on the canvas) stops the page scrolling/zooming under the fingers, but it ALSO
  // suppresses the compatibility mouse events, so the Blazor @onmouse* bindings never fire for touch.
  // Bridge raw touch here: ONE finger drives the SAME pointer callbacks the mouse uses (pan/tap for
  // free), TWO fingers drive a relative-scale pinch (the SDL touch convention the sky-map consumer
  // expects: OnTouchPinch scale = current-distance / previous-distance, anchored at the finger
  // midpoint). All coordinates are mapped to backing space (getBoundingClientRect + reported dpr) so
  // the .NET side needs no further scaling.
  const invoke = (m, ...a) => dotNetRef.invokeMethodAsync(m, ...a).catch(() => { /* teardown race */ });
  const backing = (clientX, clientY) => {
    const rect = canvas.getBoundingClientRect();
    const dpr = state.lastDpr || 1;
    return [(clientX - rect.left) * dpr, (clientY - rect.top) * dpr];
  };
  const fingerDist = (ts) => Math.hypot(ts[0].clientX - ts[1].clientX, ts[0].clientY - ts[1].clientY);
  const fingerMid = (ts) => backing((ts[0].clientX + ts[1].clientX) / 2, (ts[0].clientY + ts[1].clientY) / 2);

  state.onTouchStart = (e) => {
    e.preventDefault();
    if (e.touches.length === 1) {
      state.touchMode = "pan";
      const [x, y] = backing(e.touches[0].clientX, e.touches[0].clientY);
      invoke("OnTouchPointerDownAsync", x, y);
    } else if (e.touches.length >= 2) {
      // Second finger: start a pinch. The sky-map handler undoes any pan the first finger began, so we
      // don't send a pointer-up. Baseline the distance so the first move reports a ~1 relative step.
      state.touchMode = "pinch";
      state.pinchLastDist = fingerDist(e.touches);
    }
  };

  state.onTouchMove = (e) => {
    e.preventDefault();
    if (state.touchMode === "pan" && e.touches.length === 1) {
      const [x, y] = backing(e.touches[0].clientX, e.touches[0].clientY);
      invoke("OnTouchPointerMoveAsync", x, y);
    } else if (state.touchMode === "pinch" && e.touches.length >= 2) {
      const dist = fingerDist(e.touches);
      if (state.pinchLastDist > 0) {
        const [mx, my] = fingerMid(e.touches);
        invoke("OnTouchPinchAsync", dist / state.pinchLastDist, mx, my);
      }
      state.pinchLastDist = dist;
    }
  };

  // Shared by touchend + touchcancel.
  state.onTouchEnd = (e) => {
    e.preventDefault();
    if (state.touchMode === "pinch" && e.touches.length < 2) {
      invoke("OnTouchPinchEndAsync");
      // A finger may remain (2 -> 1); do NOT resume pan mid-gesture (it would jump). Wait for all up.
      state.touchMode = e.touches.length === 0 ? "none" : "ended";
    } else if (state.touchMode === "pan" && e.touches.length === 0) {
      const t = e.changedTouches[0];
      const [x, y] = t ? backing(t.clientX, t.clientY) : [0, 0];
      invoke("OnTouchPointerUpAsync", x, y);
      state.touchMode = "none";
    } else if (e.touches.length === 0) {
      state.touchMode = "none";
    }
  };

  canvas.addEventListener("touchstart", state.onTouchStart, { passive: false });
  canvas.addEventListener("touchmove", state.onTouchMove, { passive: false });
  canvas.addEventListener("touchend", state.onTouchEnd, { passive: false });
  canvas.addEventListener("touchcancel", state.onTouchEnd, { passive: false });

  state.ro.observe(canvas);
  attachments.set(canvas, state);
  report().then(armDprWatch); // seed lastDpr before building the first matchMedia query
}

/**
 * Toggles Fullscreen-API fullscreen for the canvas's PARENT element (the position:relative host
 * container that also anchors any DOM overlays -- fullscreening the wrapper keeps those overlays
 * anchored and avoids the replaced-element letterboxing a bare <canvas> fullscreen target gets
 * from the UA :fullscreen object-fit rules). Returns true when now fullscreen.
 * @param {HTMLCanvasElement} canvas
 * @returns {Promise<boolean>}
 */
export async function toggleFullscreen(canvas) {
  if (document.fullscreenElement) {
    await document.exitFullscreen().catch(() => { /* already exited */ });
    return false;
  }
  const target = canvas.parentElement ?? canvas;
  try {
    await target.requestFullscreen();
    return true;
  } catch {
    return false; // denied (no user gesture / iframe policy) -- caller stays windowed
  }
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
  document.removeEventListener("fullscreenchange", state.onFsChange);
  if (state.onPointerDown) canvas.removeEventListener("pointerdown", state.onPointerDown);
  if (state.onTouchStart) {
    canvas.removeEventListener("touchstart", state.onTouchStart);
    canvas.removeEventListener("touchmove", state.onTouchMove);
    canvas.removeEventListener("touchend", state.onTouchEnd);
    canvas.removeEventListener("touchcancel", state.onTouchEnd);
  }
  cancelAnimationFrame(state.raf);
  attachments.delete(canvas);
}
