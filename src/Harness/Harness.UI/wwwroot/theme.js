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
