// Auto-sizes a visualization iframe to its sandboxed document.
// The sandbox is allow-scripts only (no allow-same-origin), so the parent cannot read
// contentDocument. The document posts its height; this writes it onto the iframe.
const watches = new WeakMap();

export function watch(iframe, dotnet) {
  if (!iframe) return;
  const onMessage = event => {
    if (event.source !== iframe.contentWindow) return;
    const data = event.data;
    if (!data || data.type !== "dyson-viz-height") return;
    const height = Number(data.height);
    if (!Number.isFinite(height) || height <= 0) return;
    iframe.style.height = Math.ceil(height) + "px";
    dotnet.invokeMethodAsync("SetHeight", Math.ceil(height)).catch(() => {});
  };
  window.addEventListener("message", onMessage);
  watches.set(iframe, onMessage);
}

export function unwatch(iframe) {
  const onMessage = watches.get(iframe);
  if (!onMessage) return;
  window.removeEventListener("message", onMessage);
  watches.delete(iframe);
}
