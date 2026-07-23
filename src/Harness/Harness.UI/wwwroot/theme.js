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
