/** Draggable tools-column width for `.turn-block__content`. Reports percent to Blazor. */
window.dysonToolsCol = {
  minPercent: 12,
  maxPercent: 50,

  clamp: function (pct) {
    var min = this.minPercent;
    var max = this.maxPercent;
    if (pct < min) return min;
    if (pct > max) return max;
    return pct;
  },

  percentFromPointer: function (content, clientX) {
    if (!content) return 30;
    var rect = content.getBoundingClientRect();
    if (rect.width <= 0) return 30;
    var fromRight = ((rect.right - clientX) / rect.width) * 100;
    return this.clamp(fromRight);
  },

  applyCssVar: function (content, pct) {
    var panel = content && content.closest ? content.closest(".chat-panel") : null;
    if (panel)
      panel.style.setProperty("--tools-col-width", pct + "%");
  },

  attach: function (handle, content, dotNetRef) {
    if (!handle || !content || !dotNetRef || handle._dysonToolsColBound) return;
    handle._dysonToolsColBound = true;

    var self = this;
    var dragging = false;

    function onPointerDown(e) {
      if (e.button !== 0 && e.pointerType === "mouse") return;
      dragging = true;
      handle.setPointerCapture(e.pointerId);
      handle.classList.add("is-dragging");
      e.preventDefault();
      var pct = self.percentFromPointer(content, e.clientX);
      self.applyCssVar(content, pct);
      dotNetRef.invokeMethodAsync("OnToolsColWidth", pct);
    }

    function onPointerMove(e) {
      if (!dragging) return;
      var pct = self.percentFromPointer(content, e.clientX);
      self.applyCssVar(content, pct);
      dotNetRef.invokeMethodAsync("OnToolsColWidth", pct);
    }

    function endDrag(e) {
      if (!dragging) return;
      dragging = false;
      handle.classList.remove("is-dragging");
      try {
        handle.releasePointerCapture(e.pointerId);
      } catch {
        // Already released.
      }
      var pct = self.percentFromPointer(content, e.clientX);
      self.applyCssVar(content, pct);
      dotNetRef.invokeMethodAsync("OnToolsColWidth", pct).then(function () {
        return dotNetRef.invokeMethodAsync("OnToolsColResizeEnd");
      });
    }

    handle._dysonToolsColOnDown = onPointerDown;
    handle._dysonToolsColOnMove = onPointerMove;
    handle._dysonToolsColOnUp = endDrag;
    handle.addEventListener("pointerdown", onPointerDown);
    handle.addEventListener("pointermove", onPointerMove);
    handle.addEventListener("pointerup", endDrag);
    handle.addEventListener("pointercancel", endDrag);
  },

  detach: function (handle) {
    if (!handle || !handle._dysonToolsColBound) return;
    handle.removeEventListener("pointerdown", handle._dysonToolsColOnDown);
    handle.removeEventListener("pointermove", handle._dysonToolsColOnMove);
    handle.removeEventListener("pointerup", handle._dysonToolsColOnUp);
    handle.removeEventListener("pointercancel", handle._dysonToolsColOnUp);
    handle._dysonToolsColBound = false;
    handle._dysonToolsColOnDown = null;
    handle._dysonToolsColOnMove = null;
    handle._dysonToolsColOnUp = null;
  }
};

// ponytail: ceiling = synthetic rect math; upgrade if clamp rules grow.
(function () {
  var api = window.dysonToolsCol;
  console.assert(api.clamp(5) === 12, "dysonToolsCol: clamp min");
  console.assert(api.clamp(60) === 50, "dysonToolsCol: clamp max");
  console.assert(api.clamp(30) === 30, "dysonToolsCol: clamp mid");
  var content = {
    getBoundingClientRect: function () {
      return { left: 0, right: 200, width: 200 };
    }
  };
  // Pointer at x=140 → 60px from right → 30%
  console.assert(api.percentFromPointer(content, 140) === 30, "dysonToolsCol: percentFromPointer");
})();
