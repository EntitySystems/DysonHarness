/**
 * Capture-phase click interception for chat/markdown links.
 * http(s): open via DysonUiHost.OpenExternalChatUrlAsync (OS default browser).
 * Relative/other: preventDefault only — never navigate the Blazor WebView / SPA.
 * Scoped to markdown containers; settings / in-app NavLinks are left alone.
 */
window.dysonChatLinks = {
  _handler: null,
  _selector:
    ".turn-block__body, .turn-block__user-prompt, .turn-block__instruction, .turn-block__display-info, .file-viewer-modal__markdown",

  install: function (dotNetRef) {
    this.uninstall();
    if (!dotNetRef) return;

    var self = this;
    this._handler = function (e) {
      var a = e.target && e.target.closest ? e.target.closest("a[href]") : null;
      if (!a) return;
      if (!a.closest(self._selector)) return;

      var href = (a.getAttribute("href") || "").trim();
      if (!href) return;

      // Never let markdown links replace the Blazor app (relative → localhost SPA nav).
      e.preventDefault();
      e.stopPropagation();

      if (/^https?:\/\//i.test(href)) {
        dotNetRef.invokeMethodAsync("OpenExternalChatUrlAsync", href);
      }
    };

    document.addEventListener("click", this._handler, true);
  },

  uninstall: function () {
    if (!this._handler) return;
    document.removeEventListener("click", this._handler, true);
    this._handler = null;
  },
};
