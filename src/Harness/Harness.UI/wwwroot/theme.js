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

/** Stick-to-bottom helpers for `.chat-panel__turns`. */
window.dysonChat = {
  isNearBottom: function (el, thresholdPx) {
    if (!el) return true;
    var threshold = typeof thresholdPx === "number" ? thresholdPx : 96;
    return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
  },
  scrollToBottom: function (el) {
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }
};
