# WebGlCanvas 1.2 — full input surface

Status: PLANNED (2026-07-17). Prereq for TianWen.UI.Web's migration off its hand-rolled canvas
helpers; see `tianwen/docs/plans/web-showcase.md` ("WebGlCanvas 1.1 assessment") for why 1.1 is
not migratable for drag-based consumers.

## Motivation

1.1's `<WebGlCanvas>` owns metrics/resize/dpr but only half the input story: it binds `@onclick`
(fires on RELEASE — cannot start a drag) and `@onkeydown`. The planner in TianWen.UI.Web needs
true press/move/release (handoff-divider drag, scrollbar drag, hover follower), wheel scroll, and
drag-survives-leaving-the-canvas. Those all live today as consumer-side JS helpers + dpr math that
this component exists to subsume.

## API changes

### 1. `OnPointerDown` rebinds `@onclick` -> `@onmousedown` (semantic fix)

The name already says *Down*; 1.1 binding it to `click` was a compromise. Click-semantics
consumers move to the new `OnPointerUp`. Chess (the only 1.1 consumer) selects a square per
press after the rebind — arguably better UX; if release-semantics is preferred, it's a
one-line switch to `OnPointerUp`. Called out in the release notes; consumers pin minor
wildcards (`1.1.*`) so nobody gets the rebind silently.

### 2. New event parameters (additive)

| Parameter | Binding | Args |
|---|---|---|
| `OnPointerMove` | `@onmousemove` | `CanvasPointerEventArgs` (backing-space X/Y) |
| `OnPointerUp` | `@onmouseup` | `CanvasPointerEventArgs` |
| `OnWheel` | `@onwheel` | new `CanvasWheelEventArgs(X, Y, DeltaY, DeltaMode, Original)` |

- All coordinates mapped to backing space exactly like 1.1's `OnPointerDown`
  (`OffsetX/Y x DevicePixelRatio`, rounded) via one shared `MapToBacking(MouseEventArgs)` helper.
- Wheel deltas are forwarded RAW (`DeltaY` + `DeltaMode` + `Original`); sign/scale conventions
  (e.g. TianWen's SDL positive-is-up `-DeltaY/100`) are consumer policy, not component policy.

### 3. Why compat mouse events, not pointer events

`MouseEventArgs.Detail` carries the click count (double-click select-all in TianWen's text
inputs); the Pointer Events spec pins `pointerdown.detail` at 0. The `Original` event already
rides `CanvasPointerEventArgs`, so Detail/Buttons/modifiers stay available. Pointer capture (below)
retargets the compatibility mouse events to the capturing element identically to pointer events —
the pattern is proven in production by TianWen's `tianwenDragCapture` helper.

### 4. Built-in pointer capture

`webgl-canvas.js` `attach()` adds a `pointerdown` listener: primary button ->
`canvas.setPointerCapture(e.pointerId)` (try/catch; release is implicit on pointerup). New
parameter `CapturePointer` (default **true**) threads through `attach()`; the listener is stored
on the `Attachment` record so `detach()` removes it. Effect: `mousemove`/`mouseup` keep
retargeting to the canvas after the pointer leaves it — the SDL mouse-capture analogue, without
which swipe-style drags drop mid-gesture.

Documented caveat: during a captured drag `OffsetX/Y` can go negative / beyond the canvas;
consumers clamp (TianWen's slider/scrollbar math already does).

### 5. `AutoFocus` parameter (default false)

Focus the canvas (existing `FocusAsync`) right after the first metrics report, so keyboard input
works without a click. Replaces TianWen's `tianwenFocus` helper.

## Non-goals (deferred)

- Touch gestures / pinch (`DIR.Lib` `InputEvent.Pinch`) — 1.3 candidate, needs pointer-event
  multi-touch tracking JS-side.
- Move-event raf-throttling — per-event Blazor interop is fine at planner scale (measured);
  revisit only with data.
- TextInput synthesis — stays consumer-side (`OnKeyDown` + `e.Key.Length == 1`).

## Implementation sketch

- `WebGlCanvas.razor`: add the three bindings + `@onwheel`; markup-only rule unchanged.
- `WebGlCanvas.razor.cs`: `HandlePointerMoveAsync`/`UpAsync`/`WheelAsync` over the shared
  `MapToBacking` helper; new `CanvasWheelEventArgs` record next to `CanvasPointerEventArgs`.
- `webgl-canvas.js`: capture listener in `attach(canvas, dotNetRef, maxDpr, capturePointer)`.
- Version: csproj `VersionPrefix` 1.1.0 -> **1.2.0** + workflow `VERSION_PREFIX`
  `1.2.${{ github.run_number }}` (patch segment stays CI-reserved).
- DIR.Lib pin unchanged (6.11.*).

## Consumer migrations (each in its own repo, after 1.2.x is on NuGet)

1. **chess** (pilot re-validation): repin `1.2.*`; decide press-vs-release select after the
   `OnPointerDown` rebind; no new events needed.
2. **tianwen** (`TianWen.UI.Web/Pages/Planner.razor`): replace the raw `<canvas>` + handlers with
   `<WebGlCanvas Id="planner" TabIndex="0" AutoFocus OnReady/OnResized/OnPointerDown/OnPointerMove/
   OnPointerUp/OnWheel/OnKeyDown>`; DELETE `tianwenCanvasMetrics`/`tianwenWatchResize`/
   `tianwenDragCapture` + `MeasureCanvasAsync`/`OnBrowserResize`/`_resizePending`/`ToCanvas` and
   all `_dpr` mouse scaling. Init ordering: font staging must finish before renderer creation, and
   `OnReady` can fire during `OnInitializedAsync`'s font await -> buffer the first metrics in a
   `TaskCompletionSource<CanvasMetrics>` (the `Chess.Web/Pages/Play.razor` pattern). `OnResized` =
   `_webGl.Resize(m.BackingWidth, m.BackingHeight)` + re-render. The
   `PlannerSliderInteraction`/`HitTestAndDispatch` wiring is unchanged — only the coordinate
   plumbing moves into the component.

## Verification checklist

- chess local run (`UseLocalSiblings=true`): board plays; square select feel after the rebind.
- tianwen local run (:5099): divider grab, click-to-place, drag OUT of the canvas + release
  outside (capture), hover follower, target-list scrollbar drag, wheel scroll, double-click
  select-all in the search input, keyboard shortcuts, F5 persistence.
- Release dance per the SharpAstro flow: bump + push WebGl.Renderer, wait for NuGet `1.2.x`,
  THEN repin + push consumers (never push a consumer referencing an unpublished version).
