window.dysonTheme = {
  get: function () {
    try {
      var raw = localStorage.getItem("dyson-theme");
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  },
  set: function (theme, accent) {
    localStorage.setItem("dyson-theme", JSON.stringify({ theme: theme, accent: accent }));
    document.documentElement.setAttribute("data-theme", theme);
    document.documentElement.setAttribute("data-accent", accent);
  },
  apply: function (theme, accent) {
    document.documentElement.setAttribute("data-theme", theme);
    document.documentElement.setAttribute("data-accent", accent);
  }
};

window.dysonWorkdir = {
  get: function () {
    try {
      return localStorage.getItem("dyson-workdir");
    } catch {
      return null;
    }
  },
  set: function (id) {
    try {
      if (id)
        localStorage.setItem("dyson-workdir", id);
      else
        localStorage.removeItem("dyson-workdir");
    } catch {
      // Ignore quota / private mode failures.
    }
  }
};

/** Stick-to-bottom helpers for `.chat-panel__turns`. Stick flag lives on the element so scroll can clear it synchronously (Blazor @onscroll is too late vs streaming AfterRender). */
window.dysonChat = {
  /** Default near-bottom threshold (px). Keep tight so a small upward scroll unsticks. */
  thresholdPx: 32,
  isNearBottom: function (el, thresholdPx) {
    if (!el) return true;
    var threshold = typeof thresholdPx === "number" ? thresholdPx : this.thresholdPx;
    return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
  },
  /** Bind a passive scroll listener once; updates el._dysonStick synchronously. */
  attach: function (el, thresholdPx) {
    if (!el || el._dysonChatBound) return;
    el._dysonChatBound = true;
    el._dysonStick = true;
    var threshold = typeof thresholdPx === "number" ? thresholdPx : this.thresholdPx;
    var self = this;
    el.addEventListener(
      "scroll",
      function () {
        el._dysonStick = self.isNearBottom(el, threshold);
      },
      { passive: true }
    );
  },
  setStick: function (el, value) {
    if (el) el._dysonStick = !!value;
  },
  /** Only scrolls when stick is true. Programmatic scroll keeps stick true via the listener. */
  scrollToBottom: function (el) {
    if (!el || el._dysonStick === false) return;
    el.scrollTop = el.scrollHeight;
  }
};

// ponytail: ceiling = off-DOM synthetic el; upgrade if stick logic grows branches.
(function () {
  var el = document.createElement("div");
  el.style.cssText = "position:absolute;left:-9999px;width:40px;height:40px;overflow:auto";
  el.innerHTML = "<div style='height:400px'></div>";
  document.documentElement.appendChild(el);
  window.dysonChat.attach(el, 32);
  window.dysonChat.scrollToBottom(el);
  console.assert(el._dysonStick === true, "dysonChat: stick after scrollToBottom");
  el.scrollTop = 0;
  el.dispatchEvent(new Event("scroll"));
  console.assert(el._dysonStick === false, "dysonChat: unstick on scroll away");
  var top = el.scrollTop;
  window.dysonChat.scrollToBottom(el);
  console.assert(el.scrollTop === top, "dysonChat: no auto-scroll while unstuck");
  el.remove();
})();
