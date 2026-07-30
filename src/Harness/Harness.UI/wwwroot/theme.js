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

/** Prevent textarea default for overlay nav keys while data-slash-open is set. */
window.dysonComposer = {
  attachSlashGuard: function (el) {
    if (!el || el._dysonSlashGuard) return;
    el._dysonSlashGuard = true;
    el.addEventListener("keydown", function (e) {
      if (el.getAttribute("data-slash-open") !== "1") return;
      if (e.key === "ArrowUp" || e.key === "ArrowDown" || e.key === "Escape") {
        e.preventDefault();
        return;
      }
      if (e.key === "Enter" && !e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey)
        e.preventDefault();
    });
  },

  openFileInput: function (shell) {
    if (!shell) return;
    var input = shell.querySelector(".composer-attach__input");
    if (input) input.click();
  },

  /**
   * Paste/drop → hidden InputFile (Blazor OpenReadStream), not base64 hub interop.
   * Capture-phase dragover/drop so textarea never triggers browser navigation.
   */
  attachFileCapture: function (el, dotNetRef) {
    if (!el || el._dysonImageCapture || !dotNetRef) return;
    el._dysonImageCapture = true;
    el._dysonImageDotNet = dotNetRef;

    function hasFilePayload(dt) {
      if (!dt || !dt.types) return false;
      for (var i = 0; i < dt.types.length; i++) {
        if (dt.types[i] === "Files") return true;
      }
      return false;
    }

    function forwardFilesToInput(fileList) {
      var input = el.querySelector(".composer-attach__input");
      if (!input || !fileList || !fileList.length) return;
      var dt = new DataTransfer();
      for (var i = 0; i < fileList.length; i++)
        dt.items.add(fileList[i]);
      input.files = dt.files;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }

    el._dysonPaste = function (e) {
      var items = e.clipboardData && e.clipboardData.items;
      if (!items) return;
      var dt = new DataTransfer();
      for (var i = 0; i < items.length; i++) {
        var item = items[i];
        if (item.kind !== "file") continue;
        var file = item.getAsFile();
        if (!file) continue;
        dt.items.add(file);
      }
      if (!dt.files.length) return;
      e.preventDefault();
      e.stopPropagation();
      forwardFilesToInput(dt.files);
    };

    el._dysonDragOver = function (e) {
      if (!hasFilePayload(e.dataTransfer)) return;
      e.preventDefault();
      e.stopPropagation();
      if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
      el._dysonImageDotNet.invokeMethodAsync("SetDragOver", true);
    };

    el._dysonDragLeave = function (e) {
      if (e.relatedTarget && el.contains(e.relatedTarget)) return;
      el._dysonImageDotNet.invokeMethodAsync("SetDragOver", false);
    };

    el._dysonDrop = function (e) {
      if (!hasFilePayload(e.dataTransfer)) return;
      // Always cancel default when Files are advertised (even if files.length is briefly 0).
      e.preventDefault();
      e.stopPropagation();
      el._dysonImageDotNet.invokeMethodAsync("SetDragOver", false);
      var files = e.dataTransfer && e.dataTransfer.files;
      if (!files || !files.length) return;
      forwardFilesToInput(files);
    };

    el.addEventListener("paste", el._dysonPaste, true);
    el.addEventListener("dragover", el._dysonDragOver, true);
    el.addEventListener("dragleave", el._dysonDragLeave, true);
    el.addEventListener("drop", el._dysonDrop, true);
  },

  /** Alias for existing Blazor call sites. */
  attachImageCapture: function (el, dotNetRef) {
    return this.attachFileCapture(el, dotNetRef);
  },

  detachFileCapture: function (el) {
    if (!el || !el._dysonImageCapture) return;
    el.removeEventListener("paste", el._dysonPaste, true);
    el.removeEventListener("dragover", el._dysonDragOver, true);
    el.removeEventListener("dragleave", el._dysonDragLeave, true);
    el.removeEventListener("drop", el._dysonDrop, true);
    el._dysonImageCapture = false;
    el._dysonImageDotNet = null;
  },

  detachImageCapture: function (el) {
    return this.detachFileCapture(el);
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
