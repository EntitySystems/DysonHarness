// Image modal zoom/pan hooks. The math (fit, pan bounds, zoom steps) is Engine (DysonA2uiImageZoom); this module only measures,
// stores the bounds C# last sent, and moves the image with pointer capture. It never round-trips per pointer move.
// The transform is JS-owned: CSS reads --a2ui-zoom / --a2ui-pan-x / --a2ui-pan-y from the stage, and data-pannable /
// data-dragging are set here, so a Blazor re-render during a drag cannot snap the image back.
//
// ponytail: no mouse-wheel / ctrl+wheel / pinch zoom in v1 (slider, +/-/0 keys and double-click cover it). Upgrade path: a
// non-passive wheel listener that calls a [JSInvokable] Wheel(delta) and preventDefaults ctrl+wheel (else WebView page zoom).
const states = new WeakMap();

function write(s) {
    s.x = Math.min(s.maxX, Math.max(-s.maxX, s.x));
    s.y = Math.min(s.maxY, Math.max(-s.maxY, s.y));
    const style = s.stage.style;
    style.setProperty("--a2ui-zoom", String(s.zoom));
    style.setProperty("--a2ui-pan-x", s.x + "px");
    style.setProperty("--a2ui-pan-y", s.y + "px");
    if (s.maxX > 0 || s.maxY > 0)
        s.stage.setAttribute("data-pannable", "");
    else
        s.stage.removeAttribute("data-pannable");
}

function measure(s) {
    const img = s.stage.querySelector("img.a2ui-image-modal__img");
    if (!img || !img.complete || !img.naturalWidth)
        return;
    s.dotnet.invokeMethodAsync("Measured", img.naturalWidth, img.naturalHeight, s.stage.clientWidth, s.stage.clientHeight)
        .catch(() => { });
}

function endDrag(s) {
    if (!s.drag)
        return;
    s.drag = null;
    s.stage.removeAttribute("data-dragging");
}

export function attach(stage, dotnet, keyPanPx) {
    if (!stage || states.has(stage))
        return;
    const ac = new AbortController();
    const opts = { signal: ac.signal };
    const s = { stage, dotnet, ac, keyPan: keyPanPx, zoom: 1, x: 0, y: 0, maxX: 0, maxY: 0, drag: null, ro: null };
    states.set(stage, s);

    // load does not bubble: capture it so any img the modal renders (slide changes) is measured.
    stage.addEventListener("load", e => { if (e.target instanceof HTMLImageElement) measure(s); }, { capture: true, signal: ac.signal });
    s.ro = new ResizeObserver(() => measure(s));
    s.ro.observe(stage);

    stage.addEventListener("pointerdown", e => {
        stage.focus({ preventScroll: true });
        if (!stage.hasAttribute("data-pannable") || (e.pointerType === "mouse" && e.button !== 0))
            return;
        s.drag = { id: e.pointerId, px: e.clientX, py: e.clientY };
        stage.setPointerCapture(e.pointerId);
        stage.setAttribute("data-dragging", "");
    }, opts);
    stage.addEventListener("pointermove", e => {
        if (!s.drag || s.drag.id !== e.pointerId)
            return;
        s.x += e.clientX - s.drag.px;
        s.y += e.clientY - s.drag.py;
        s.drag.px = e.clientX;
        s.drag.py = e.clientY;
        write(s);
    }, opts);
    for (const type of ["pointerup", "pointercancel", "lostpointercapture"])
        stage.addEventListener(type, () => endDrag(s), opts);

    // Arrow keys pan only while zoomed and the stage has focus; otherwise they bubble and change slide.
    stage.addEventListener("keydown", e => {
        if (s.zoom <= 1 || e.ctrlKey || e.metaKey || e.altKey)
            return;
        const dx = e.key === "ArrowLeft" ? 1 : e.key === "ArrowRight" ? -1 : 0;
        const dy = e.key === "ArrowUp" ? 1 : e.key === "ArrowDown" ? -1 : 0;
        if ((dx === 0 && dy === 0) || (dx !== 0 && s.maxX <= 0) || (dy !== 0 && s.maxY <= 0))
            return;
        s.x += dx * s.keyPan;
        s.y += dy * s.keyPan;
        write(s);
        e.preventDefault();
        e.stopPropagation();
    }, opts);

    write(s);
    measure(s);
}

export function setView(stage, scale, maxX, maxY) {
    const s = states.get(stage);
    if (!s)
        return;
    s.zoom = scale;
    s.maxX = maxX;
    s.maxY = maxY;
    write(s);
}

export function reset(stage) {
    const s = states.get(stage);
    if (!s)
        return;
    endDrag(s);
    s.zoom = 1;
    s.x = 0;
    s.y = 0;
    s.maxX = 0;
    s.maxY = 0;
    write(s);
}

export function scrollThumb(strip, index) {
    const el = strip && strip.children[index];
    if (el)
        el.scrollIntoView({ block: "nearest", inline: "nearest" });
}

export function detach(stage) {
    const s = states.get(stage);
    if (!s)
        return;
    endDrag(s);
    s.ac.abort();
    if (s.ro)
        s.ro.disconnect();
    states.delete(stage);
}
