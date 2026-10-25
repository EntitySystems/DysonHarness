/**
 * One-shot reading-speed reveal for a Meta Agent message that arrived live.
 *
 * The message lands in one piece as rendered markdown HTML. This wraps each word in a
 * span that fades in with a staggered animation-delay, then unwraps the spans when the
 * reveal finishes so a long message does not keep a span per word. Blazor never declared
 * those spans, so they survive its diffs; paint is a no-op once the same text is wrapped.
 *
 * ponytail: word chunks, not grapheme spans. A per-character wrap is thousands of nodes
 * on a long post; words still read as a soft leading edge at the same overall rate.
 * Upgrade path: segment graphemes only when a word is one emoji/combining cluster.
 *
 * No timer per word. One setTimeout unmasks the trailing actions. Folding does not wait
 * on animationend — the blanket prefers-reduced-motion rule suppresses that event.
 */
window.dysonMetaReveal = {
  /** Fade per word. Matches .dyson-meta-reveal__word in app.css. */
  fadeMs: 200,

  /**
   * Stagger between words, in ms.
   * 238 wpm silent reading × 1.2 ≈ 286 wpm ≈ 4.76 words/s ≈ 210 ms/word.
   * Keep in sync with MetaChatReveal.WordStaggerMs.
   */
  staggerMs: 210,

  /** Past this many words the post shows at once. A 2s JS cap cannot cover a long reveal. */
  maxWords: 400,

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
    el._dysonWord = 0;
    for (var i = 0; i < nodes.length; i++) count += this._split(el, nodes[i]);

    if (!count || count > this.maxWords) {
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

  _split: function (root, textNode) {
    var parts = textNode.nodeValue.split(/(\s+)/);
    var made = 0;
    var parent = textNode.parentNode;
    var delay = root._dysonWord || 0;
    var frag = document.createDocumentFragment();
    for (var i = 0; i < parts.length; i++) {
      var part = parts[i];
      if (!part) continue;
      if (/^\s+$/.test(part)) {
        frag.appendChild(document.createTextNode(part));
        continue;
      }
      var span = document.createElement("span");
      span.className = "dyson-meta-reveal__word";
      span.style.animationDelay = (delay * this.staggerMs) + "ms";
      span.textContent = part;
      frag.appendChild(span);
      delay++;
      made++;
    }
    if (!made) return 0;
    root._dysonWord = delay;
    parent.replaceChild(frag, textNode);
    return made;
  },

  _fold: function (el) {
    var live = el.querySelectorAll(".dyson-meta-reveal__word");
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
