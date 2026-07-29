/**
 * Capture-phase click interception for chat/markdown http(s) links.
 * Opens via DysonUiHost.OpenExternalChatUrlAsync (OS default browser), not the WebView.
 * Scoped to markdown containers; settings / in-app NavLinks are left alone.
 */
window.dysonChatLinks = {
  _handler: null,
  _selector:
    ".turn-block__body, .turn-block__user-prompt, .turn-block__instruction, .file-viewer-modal__markdown",

  install: function (dotNetRef) {
    this.uninstall();
    if (!dotNetRef) return;

    var self = this;
    this._handler = function (e) {
      var a = e.target && e.target.closest ? e.target.closest("a[href]") : null;
      if (!a) return;
      if (!a.closest(self._selector)) return;

      var href = (a.getAttribute("href") || "").trim();
      if (!/^https?:\/\//i.test(href)) return;

      e.preventDefault();
      e.stopPropagation();
      dotNetRef.invokeMethodAsync("OpenExternalChatUrlAsync", href);
    };

    document.addEventListener("click", this._handler, true);
  },

  uninstall: function () {
    if (!this._handler) return;
    document.removeEventListener("click", this._handler, true);
    this._handler = null;
  },
};
