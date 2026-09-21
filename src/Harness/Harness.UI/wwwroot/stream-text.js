/**
 * Per-chunk fade for streaming assistant / reasoning text.
 *
 * There is no CSS-only path: `StreamingPreview` materializes a fresh string per read and Blazor
 * emits a single `UpdateText` that reassigns one text node's `textContent`, so there are no
 * per-token elements to animate. Instead `TurnBlock` declares the streaming div with *no* text
 * content and this module owns its children: each delta is appended as a
 * `<span class="dyson-stream-chunk">` that fades in. Blazor never re-renders children it did not
 * declare, so the appended spans survive its diffs.
 *
 * The caret span IS declared by Blazor and sits inside the same div, so chunks insert before it.
 *
 * Faded-in chunks are folded back into a single text node so a long stream does not grow the DOM
 * one span per delta. Folding is driven by timestamps, never `animationend`: the blanket
 * prefers-reduced-motion rule in app.css suppresses that event entirely.
 */
window.dysonStreamText = {
  /** Keep in sync with the `.dyson-stream-chunk` animation duration (--motion-slow) in app.css. */
  fadeMs: 320,

  /**
   * Appends whatever `text` adds beyond what is already painted into `el`.
   * Safe to call on every render — a no-op when nothing changed.
   */
  paint: function (el, text) {
    if (!el) return;

    var value = typeof text === "string" ? text : "";
    var painted = el._dysonPainted || "";

    // Growth is the normal case. Anything else (a rewritten preview, a restarted stream, a
    // recycled element) is repainted from scratch rather than guessed at.
    if (value.length < painted.length || value.lastIndexOf(painted, 0) !== 0) {
      this.reset(el);
      painted = "";
    }

    var suffix = value.slice(painted.length);
    el._dysonPainted = value;
    this._fold(el);
    if (!suffix) return;

    var span = document.createElement("span");
    span.className = "dyson-stream-chunk";
    span.textContent = suffix;
    span._dysonAt = Date.now();
    el.insertBefore(span, el.querySelector(".turn-block__caret"));
    (el._dysonChunks || (el._dysonChunks = [])).push(span);
  },

  /** Drops everything this module painted into `el`, leaving Blazor's own children alone. */
  reset: function (el) {
    if (!el) return;

    var caret = el.querySelector(".turn-block__caret");
    var node = el.firstChild;
    while (node) {
      var next = node.nextSibling;
      if (node !== caret) el.removeChild(node);
      node = next;
    }

    el._dysonPainted = "";
    el._dysonChunks = [];
    el._dysonSettled = null;
  },

  /** Zero under reduced motion: nothing fades, so chunks may fold on the next paint. */
  _fadeWindow: function () {
    if (this._reduced === undefined)
      this._reduced = !!(window.dysonUi && window.dysonUi.prefersReducedMotion());
    return this._reduced ? 0 : this.fadeMs;
  },

  /** Merges chunks whose fade is over into one leading text node (oldest first, order kept). */
  _fold: function (el) {
    var chunks = el._dysonChunks;
    if (!chunks || !chunks.length) return;

    var cutoff = Date.now() - this._fadeWindow();
    while (chunks.length && chunks[0]._dysonAt <= cutoff) {
      var span = chunks.shift();
      if (!el._dysonSettled || !el._dysonSettled.isConnected) {
        el._dysonSettled = document.createTextNode("");
        el.insertBefore(el._dysonSettled, el.firstChild);
      }
      el._dysonSettled.appendData(span.textContent);
      if (span.parentNode === el) el.removeChild(span);
    }
  }
};
