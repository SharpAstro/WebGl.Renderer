# Changelog

Release notes for WebGl.Renderer, one entry per `Major.Minor`, newest first.

The version NUMBER is not here: it lives in `src/Directory.Build.props` (`VersionMajorMinor`), and the
build job reads that property back rather than restating it, so a package can never declare a version
this file disagrees with. Bump it there and add the entry here, in the same commit.

## 1.26

Follows DIR.Lib to 8.3, with nothing to port -- the tab strip becomes a shared Layout tree
(TabStripTree), plus CompositeWidget&lt;TSurface&gt; and IconKind.Plus / Minus, all additive over the
8.0 this already declared.

Same reason as the 8.0 bump one entry down, and it is worth restating because it is the reason this
family bumps pins it does not need: a consumer pins 8.3, so a backend declaring 8.0 makes that
consumer's graph unify DIR.Lib upward BY VERSION rather than by intent. The resolve is right by
accident either way; declaring it is what makes it right on purpose.

## 1.25

A finger no longer costs a wasted interop crossing per move, and DIR.Lib goes to 8.0.
A touch reaches a consumer TWICE: once as the touch events webgl-canvas.js bridges, and once
as the pointer stream, which fires for touch as well. WebGlCanvas.HandlePointerMoveAsync
already discarded the second (IsBridgedTouch), so behaviour was right -- but it discarded it
only AFTER Blazor had serialized a full PointerEventArgs and crossed into .NET, which is the
whole cost of an event that was never going to be used. Measured in a Chrome trace of a real
touch session on a consumer: 812 pointermove dispatches, 0.657 s, ~0.81 ms each, every one
thrown away, about 5.6% of the main thread's busy time. The canvas now stops a touch-sourced
pointermove at the target, which works because Blazor DELEGATES (one listener per event type
on document) and uses the capture phase only for its non-bubbling set, which pointermove is
not in. Mouse and pen are untouched: they have no bridge, so the pointer stream IS their
input. Pure optimization, not a behaviour change -- if Blazor ever stopped delegating, the
events would reach .NET again and IsBridgedTouch would discard them exactly as before.
DIR.Lib 8.0 is a follow with nothing to port: its one break is TabBar becoming a widget, which
this backend references zero times. Taken now because a consumer already pins 8.0, and a
backend still declaring 7.29 makes that consumer's graph unify DIR.Lib by version rather than
by intent, which is the arrangement this family keeps paying for when a member lags.

## 1.24

DrawInstanced takes a firstInstance offset, so ONE persistent instance buffer can be drawn
as several independent ranges. Binary-breaking (an added optional parameter), source-compatible.
WebGL2 has no baseInstance draw argument -- that is desktop GL 4.2 and WebGL2 never picked it
up -- so it is expressed the only way the API allows: the per-instance attributes are pointed
at firstInstance * iStride before the draw, which is exactly equivalent for a divisor-1
attribute and costs nothing extra, since the draw re-binds those attributes anyway.
Motivating consumer: a spatially-chunked star field that submits only the sky regions a view
can see. Without an offset its only options were one draw over the whole buffer (which is
what TianWen's web atlas did -- 2.5M instances every frame regardless of the view) or a
separate buffer per chunk.

## 1.23

Follows DIR.Lib to 7.29, closing a two-minor lag: this backend sat on 7.27 while the rest of
the family moved to 7.28, so the package graph was unifying DIR.Lib by taking the highest
version rather than by intent -- the standing smell this repo's pin comment has warned about
since 1.12. Nothing backend-specific: 7.29's breaking surface is the widget layer (text-field
rendering, per-window settings, the typed dropdown) and this is a Renderer implementation,
which touches none of it.

## 1.21

Follows DIR.Lib to 7.21 with the rest of the family. Nothing backend-specific.

## 1.20

Follows DIR.Lib to 7.19 with the rest of the family, inheriting the three theme marks
from the shared painter with no code of its own. Taken in the same cut as
SdlVulkan.Renderer 7.14: the family lands together or it does not land.

## 1.19

No backend code change: the lockstep rebuild that re-pins DIR.Lib 7.14 -> 7.18, keeping
this the member that moves WITH the family rather than the one found two minors behind.
7.18's Layout.Content.Icon is painted by DIR.Lib's PixelWidgetBase, which this backend
inherits, so the icons arrive here for free.

## 1.18

Lockstep rebuild against DIR.Lib 7.14 (7.11 -> 7.14). No renderer code change. The whole range
is upstream text metrics, which this backend feels in full: a canvas is a pixel surface, and
MSDF text here comes off the shared SdfFontAtlas core.
DIR.Lib 7.12 is additive: TextInputRenderer takes a palette, the shape TabBar got in 7.10.
DIR.Lib 7.13 bounds the SDF atlas's rasterize retry, so a glyph that can never rasterize is
given up on instead of being re-claimed every frame and pinning IsDirty true -- worth more here
than anywhere, because a single-threaded WASM runtime has no second thread to hide a render that
never settles. It ALSO carried an unannounced measurement fix, only written up in 7.14's notes:
a whitespace advance now comes from the font rather than the 'n' glyph, and in DejaVu every
measured space had been 1.99x too wide, so space-padded text measures narrower from 7.13 on.
U+2007 FIGURE SPACE is the pad that keeps a column aligned, digit-width by definition.
DIR.Lib 7.14 makes TextFit.ShrinkToWidth return a size it actually measured, so a run fitted
with TextTrim.Shrink stops drawing a fraction of a pixel past its rect. Shrink is opt-in and
the default is End, so a consumer that never asks for it renders byte-identically.
Released alongside Console.Lib 4.20 and SdlVulkan.Renderer 7.11, moving with the family rather
than being found behind it -- the hazard 1.14 and 1.15 both closed, and 1.16 restated.

## 1.17

The canvas listens on POINTER events (@onpointerdown/move/up) instead of the mouse
compatibility ones. webgl-canvas.js has always called setPointerCapture on pointerdown, and
capture is defined over the pointer family, so the capture governed one event stream while
the app listened to another -- whether a drag that left the canvas kept being delivered was
left to per-browser compatibility-event behaviour. Now they are the same family.
TOUCH IS FILTERED OUT of the new handlers (PointerType == "touch"), which is load-bearing
rather than tidiness: pointer events fire for fingers too, and the JS touch bridge already
synthesises one-finger pan and two-finger pinch, so forwarding both would deliver every
touch drag twice and hand the pinch's first finger to the pan path as well.
Also NEW: @onpointercancel, reported as an up at the last position. There was no cancel path
at all, so a gesture the browser or the device took over (pen out of range, tab losing the
pointer) never sent an up and left the consumer's drag latched down indefinitely.
Consumer-visible: the three handlers now receive PointerEventArgs. It derives from
MouseEventArgs and CanvasPointerEventArgs still carries the base type, so consumer code
compiles unchanged; a consumer that wants the pointer type can now cast for it.

## 1.16

Lockstep rebuild against DIR.Lib 7.8 (7.5 -> 7.8; nothing in 7.6 or 7.7 touched this backend).
No renderer code change. 7.8 makes the PIXEL painter fit every text run to the rect the layout
engine arranged for it -- 7.7 had shipped Layout.Content.Text.Trim as a declaration only the
cell painter acted on, so on a GPU surface an over-wide run still drew over its neighbour.
TextTrim gains Shrink (scale down, keep every character) and None (draw whole and overhang, the
previous behaviour). BEHAVIOUR CHANGE upstream, not additive: a run left on the default
TextTrim.End now ellipsizes here where it used to overhang. Moved with the family this time
rather than being found two minors behind, which is the hazard 1.14 and 1.15 both closed.
Later in 1.16, no X.Y bump and NO value change: src/Directory.Build.props now STATES
AssemblyVersion = $(VersionMajorMinor).0.0 instead of leaving it to the SDK default. The default
resolved to the same 1.16.0.0, so nothing shipped differently -- the point is that a default is
fragile. The moment any csproj here sets its own AssemblyVersion it wins silently, and CI stamps
-p:Version and -p:FileVersion but never -p:AssemblyVersion, so nothing would correct it. That is
exactly how DIR.Lib shipped 6.4.0.0 for two majors and SdlVulkan.Renderer shipped 6.11.0.0. All
seven sibling repos now state the rule once, in the props file, with none left in any csproj.

## 1.15

Lockstep rebuild against DIR.Lib 7.5, and the version is no longer written here. 7.5 resolves
a font by its DECLARED family rather than by file identity, so every face in a family is
reachable and a run a face cannot cover falls back per run instead of per request; additive,
so this renderer paints byte-identically. Rebuilt because 1.14 left this the last sibling on
7.4 once DIR.Lib, Console.Lib and SdlVulkan.Renderer moved, which is the same split-DIR.Lib
hazard 1.14 itself set out to close.

## 1.14

Lockstep rebuild against DIR.Lib 7.4, no code change. This was the last sibling left on an
older DIR.Lib: 1.13 rebuilt against 7.0 while Console.Lib and SdlVulkan.Renderer have since
moved to 7.4, so a consumer holding both a desktop and a web renderer restored two DIR.Lib
versions and unified on the higher one by luck rather than by intent. 7.4 itself adds
PixelMeasureContext per-axis scales plus a CellAuthored factory, and PixelWidgetBase
Arrange/Paint/RenderLayout overloads taking the measure CONTEXT instead of a bare dpiScale;
both additive, with the scalar overloads delegating through an isotropic context, so this
renderer paints byte-identically.

## 1.13

Lockstep rebuild against DIR.Lib 7.0, where DeviceTransform became ContentTransform. A pure
rebuild: this backend never referenced the renamed property, so no source changed and 34/34
tests pass untouched. It is still worth cutting, because the alternative is a consumer on
DIR.Lib 7.0 resolving a WebGl.Renderer compiled against 6.21 and trusting that the two never
meet -- which happens to hold today and is not a thing to leave to luck.
NOT included: composing ContentTransform into uProj. VkRenderer folds the transform into its
projection (SdlVulkan.Renderer 7.0); this backend builds uProj JS-side in webgl-renderer.js
and ignores the transform, so a rotated or safe-area-offset frame stays Vulkan-only.
Re-pins DIR.Lib 6.21.* -> 7.0.*.

## 1.12

WebGlRenderer.FillRoundedRectangle: a GPU override of DIR.Lib 6.20's scanline
fallback. One rounded-box SDF quad per rect instead of one FillRectangle per row -- which
matters more here than on Vulkan, since every span would be its own command-buffer record
crossing into JS. New PipelineId.RoundRect (id 4) + shader pair; the box parameters ride
on VERTEX ATTRIBUTES because uExtra is a single float and a rounded box needs three.
Custom pipeline ids now start at 5, automatically: the JS side allocates them as
pipelines.length, so there was no constant to update.
Re-pins DIR.Lib 6.14.* -> 6.21.* (FillRoundedRectangle 6.20, Layout.Node.Radius 6.21).
