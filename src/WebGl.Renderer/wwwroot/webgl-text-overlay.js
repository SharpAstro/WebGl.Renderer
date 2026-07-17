// @ts-check
// webgl-text-overlay.js — DOM plumbing for CanvasTextOverlay.razor: a REAL <input> element
// positioned over a canvas-drawn text widget, so canvas UIs get native text entry (IME
// composition, clipboard, autocorrect, and — critically — the mobile soft keyboard, which only
// appears for a focused focusable element). The canvas cannot provide any of these itself.
//
// Division of labour: the .NET component owns visibility/value/rect decisions; this module owns
// the raw DOM events, because two of them need synchronous behaviour Blazor cannot provide —
// selective preventDefault on navigation keys (Blazor's @onkeydown:preventDefault is
// all-or-nothing) and reading selectionStart/End at input time.

/**
 * @typedef {{ invokeMethodAsync(methodName: string, ...args: unknown[]): Promise<unknown> }} DotNetRef
 * @typedef {Object} Attachment
 * @property {DotNetRef} dotNetRef
 * @property {boolean} suppressBlur - true while hide() runs, so a programmatic blur doesn't
 *           echo back into .NET as a user blur (which would double-deactivate).
 * @property {(e: InputEvent) => void} onInput
 * @property {(e: KeyboardEvent) => void} onKeyDown
 * @property {(e: FocusEvent) => void} onBlur
 */

/** @type {WeakMap<HTMLInputElement, Attachment>} */
const attachments = new WeakMap();

// Keys the canvas side needs for itself (suggestion navigation, commit, cancel, input cycling).
// Everything else stays native so the input edits itself (caret moves, Backspace, Ctrl+V, ...).
const forwardedKeys = new Set(["ArrowUp", "ArrowDown", "Enter", "Escape", "Tab"]);

/**
 * @param {HTMLInputElement} input
 * @param {DotNetRef} dotNetRef
 */
export function attach(input, dotNetRef) {
  /** @type {Attachment} */
  const state = {
    dotNetRef,
    suppressBlur: false,
    onInput: () => {
      dotNetRef
        .invokeMethodAsync("OnOverlayInput", input.value, input.selectionStart ?? input.value.length, input.selectionEnd ?? input.value.length)
        .catch(() => { /* teardown race */ });
    },
    onKeyDown: (e) => {
      // During IME composition the same keys steer the composition window — never steal them
      // (keyCode 229 is the legacy composition marker some browsers still report).
      if (e.isComposing || e.keyCode === 229) return;
      if (!forwardedKeys.has(e.key)) return;
      e.preventDefault();
      dotNetRef
        .invokeMethodAsync("OnOverlayKey", e.key, e.shiftKey, e.ctrlKey, e.altKey)
        .catch(() => { /* teardown race */ });
    },
    onBlur: () => {
      if (state.suppressBlur) return;
      dotNetRef.invokeMethodAsync("OnOverlayBlur").catch(() => { /* teardown race */ });
    },
  };

  input.addEventListener("input", state.onInput);
  input.addEventListener("keydown", state.onKeyDown);
  input.addEventListener("blur", state.onBlur);
  attachments.set(input, state);
}

/**
 * Shows the input over the given rect (CSS px, relative to the offset parent), seeds its value +
 * caret, and focuses it. Focus works when this runs inside (or shortly after) a user gesture —
 * the pointer press that activated the canvas widget.
 * @param {HTMLInputElement} input
 * @param {string} value
 * @param {number} caret
 * @param {number} x @param {number} y @param {number} w @param {number} h
 * @param {number} fontPx - 0 = derive from the rect height
 */
export function show(input, value, caret, x, y, w, h, fontPx) {
  setRect(input, x, y, w, h, fontPx);
  input.style.display = "block";
  input.value = value;
  input.focus({ preventScroll: true });
  const pos = Math.max(0, Math.min(caret, value.length));
  input.setSelectionRange(pos, pos);
}

/**
 * Repositions a visible input (the canvas relayouts every frame; the host calls this only when
 * the rect actually moved).
 * @param {HTMLInputElement} input
 * @param {number} x @param {number} y @param {number} w @param {number} h
 * @param {number} fontPx
 */
export function setRect(input, x, y, w, h, fontPx) {
  input.style.left = `${x}px`;
  input.style.top = `${y}px`;
  input.style.width = `${w}px`;
  input.style.height = `${h}px`;
  input.style.fontSize = `${fontPx > 0 ? fontPx : Math.max(10, h * 0.55)}px`;
}

/**
 * Replaces the value + caret from the .NET side (e.g. an autocomplete suggestion committed on the
 * canvas side) without generating an input event echo.
 * @param {HTMLInputElement} input
 * @param {string} value
 * @param {number} caret
 */
export function setValue(input, value, caret) {
  input.value = value;
  if (document.activeElement === input) {
    const pos = Math.max(0, Math.min(caret, value.length));
    input.setSelectionRange(pos, pos);
  }
}

/** @param {HTMLInputElement} input */
export function hide(input) {
  const state = attachments.get(input);
  if (state) state.suppressBlur = true;
  input.blur();
  input.style.display = "none";
  if (state) state.suppressBlur = false;
}

/** @param {HTMLInputElement} input */
export function detach(input) {
  const state = attachments.get(input);
  if (!state) return;
  input.removeEventListener("input", state.onInput);
  input.removeEventListener("keydown", state.onKeyDown);
  input.removeEventListener("blur", state.onBlur);
  attachments.delete(input);
}
