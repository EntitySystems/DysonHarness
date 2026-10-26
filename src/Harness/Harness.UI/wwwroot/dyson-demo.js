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
    openLatestVisualization: function () {
      var buttons = Array.prototype.filter.call(
        document.querySelectorAll(".meta-chat__actions button"),
        function (b) { return b.textContent.trim() === "View visualization"; });
      if (!buttons.length) return false;
      buttons[buttons.length - 1].click();
      return true;
    },
    detach: function () {
      if (!handler) return;
      document.removeEventListener("keydown", handler);
      handler = null;
      ref = null;
    }
  };
})();
