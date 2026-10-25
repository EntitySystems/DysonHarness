/**
 * One-shot reading-speed reveal for a Meta Agent message that arrived live.
 *
 * The message lands in one piece as rendered markdown HTML. This wraps each grapheme
 * cluster in a span that fades in with a staggered animation-delay, then unwraps the
 * spans when the reveal finishes so a long message does not keep a span per character.
 * Blazor never declared those spans, so they survive its diffs; paint is a no-op once
 * the same text is wrapped.
 *
 * ponytail: one span per grapheme, capped. Past maxChars the post shows at once rather
 * than building thousands of nodes. Upgrade path: reveal a window of graphemes and
 * recycle spans if a long post should still animate.
 *
 * No timer per character. One setTimeout unmasks the trailing actions. Folding does not
 * wait on animationend — the blanket prefers-reduced-motion rule suppresses that event.
 */
window.dysonMetaReveal = {
  /** Fade per grapheme. Matches .dyson-meta-reveal__char in app.css. */
  fadeMs: 200,

  /**
   * Stagger between grapheme clusters, in ms.
   * 238 wpm × 1.2 × 2 (user asked to double) ≈ 572 wpm ≈ 55 chars/s ≈ 18 ms per char.
   * Keep in sync with MetaChatReveal.CharStaggerMs.
   */
  staggerMs: 18,

  /** Past this many graphemes the post shows at once. A 2s JS cap cannot cover a long reveal. */
  maxChars: 2500,

  paint: function (el, text) {
    if (!el) return;
    var value = typeof text === "string" ? text : "";
    if (el._dysonRevealText === value && (el._dysonRevealed || el._dysonRevealTimer)) return;

    this._clear(el);
    el._dysonRevealText = value;
    if (this._reduced()) {
      this._finish(el);
      return;
    }

    var nodes = [];
    var walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        if (!node.nodeValue || !/\S/.test(node.nodeValue)) return NodeFilter.FILTER_REJECT;
        var parent = node.parentNode;
        if (!parent || parent.closest("script, style")) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    var node;
    while ((node = walker.nextNode())) nodes.push(node);

    var count = 0;
    el._dysonChar = 0;
    for (var i = 0; i < nodes.length; i++) count += this._split(el, nodes[i]);

    if (!count || count > this.maxChars) {
      this._fold(el);
      el._dysonRevealText = value;
      this._finish(el);
      return;
    }

    this._mask(el, true);
    var self = this;
    var wait = ((count - 1) * this.staggerMs) + this.fadeMs;
    el._dysonRevealTimer = window.setTimeout(function () {
      el._dysonRevealTimer = 0;
      self._fold(el);
      self._finish(el);
    }, wait);
  },

  _clear: function (el) {
    if (el._dysonRevealTimer) {
      window.clearTimeout(el._dysonRevealTimer);
      el._dysonRevealTimer = 0;
    }
  },

  _graphemes: function (value) {
    if (typeof Intl !== "undefined" && typeof Intl.Segmenter === "function") {
      var segmenter = new Intl.Segmenter(undefined, { granularity: "grapheme" });
      var parts = [];
      var iterator = segmenter.segment(value)[Symbol.iterator]();
      var step;
      while (!(step = iterator.next()).done) parts.push(step.value.segment);
      return parts;
    }
    return Array.from(value);
  },

  _split: function (root, textNode) {
    var parts = this._graphemes(textNode.nodeValue);
    var made = 0;
    var parent = textNode.parentNode;
    var delay = root._dysonChar || 0;
    var frag = document.createDocumentFragment();
    for (var i = 0; i < parts.length; i++) {
      var part = parts[i];
      if (!part) continue;
      if (/^\s+$/.test(part)) {
        frag.appendChild(document.createTextNode(part));
        continue;
      }
      var span = document.createElement("span");
      span.className = "dyson-meta-reveal__char";
      span.style.animationDelay = (delay * this.staggerMs) + "ms";
      span.textContent = part;
      frag.appendChild(span);
      delay++;
      made++;
    }
    if (!made) return 0;
    root._dysonChar = delay;
    parent.replaceChild(frag, textNode);
    return made;
  },

  _fold: function (el) {
    var live = el.querySelectorAll(".dyson-meta-reveal__char");
    for (var i = 0; i < live.length; i++) {
      var span = live[i];
      var parent = span.parentNode;
      if (!parent) continue;
      parent.insertBefore(document.createTextNode(span.textContent), span);
      parent.removeChild(span);
    }
    if (live.length) el.normalize();
  },

  _finish: function (el) {
    el._dysonRevealed = true;
    this._mask(el, false);
  },

  _mask: function (el, hidden) {
    var msg = el.closest(".meta-chat__msg");
    if (!msg) return;
    var tail = msg.querySelectorAll(".meta-chat__question, .meta-chat__actions");
    for (var i = 0; i < tail.length; i++) {
      if (hidden) tail[i].setAttribute("hidden", "");
      else tail[i].removeAttribute("hidden");
    }
  },

  _reduced: function () {
    return !!(window.dysonUi && window.dysonUi.prefersReducedMotion());
  }
};
