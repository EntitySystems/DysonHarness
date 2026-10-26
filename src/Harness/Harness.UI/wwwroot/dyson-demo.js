window.dysonDemo = (function () {
  var handler = null;
  var ref = null;
  function onKey(e) {
    if (!ref) return;
    if (e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey && (e.key === "j" || e.key === "J")) {
      e.preventDefault();
      ref.invokeMethodAsync("CancelFromHotkey");
    }
  }
  return {
    attach: function (dotNetRef) {
      if (handler) return;
      ref = dotNetRef;
      handler = onKey;
      document.addEventListener("keydown", handler);
    },
    detach: function () {
      if (!handler) return;
      document.removeEventListener("keydown", handler);
      handler = null;
      ref = null;
    }
  };
})();
