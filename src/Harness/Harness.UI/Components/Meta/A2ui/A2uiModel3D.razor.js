// A2UI Model3D: sandboxed three.js viewer iframes that survive Blazor re-renders.
//
// An <iframe> reloads whenever it is removed or moved, and Blazor recreates A2UI node DOM on every surface revision
// and entrance-wrapper toggle. So the inline iframe never lives in Blazor markup: each viewer is a JS-owned "host"
// appended once to the empty .meta-chat-feed__model3d-layer (MetaChatFeed) and positioned over the Blazor-owned slot.
// attach() on every render re-points the host at the current slot; release() (re-render) hides it; dispose() (node
// gone) and disposeSurface() (surface view gone) unload it. The iframe never fetches: the parent page fetches the
// same-origin preview URL and transfers the ArrayBuffer in.

const MAX_HOSTS = 8; // Chromium allows ~16 WebGL contexts; the modal needs one more.
const OVERLAYS = '.a2ui-popover__panel, .a2ui-modal__backdrop, .a2ui-model3d-modal';
const hosts = new Map(); // key -> { el, iframe, layer, slot, path, format, previewUrl, ready, state }
const viewers = new Map(); // iframe -> { ready, format, previewUrl, path, onReady?, onEscape?, host? }
const watched = new WeakSet();
let syncQueued = false;

window.addEventListener('message', (event) => {
    const data = event.data;
    if (!data || typeof data.type !== 'string' || !data.type.startsWith('dyson-model3d-'))
        return;
    for (const [iframe, viewer] of viewers) {
        if (!iframe.isConnected) {
            viewers.delete(iframe);
            continue;
        }
        if (event.source !== iframe.contentWindow)
            continue;
        if (data.type === 'dyson-model3d-ready') {
            viewer.ready = true;
            viewer.onReady?.();
            sendModel(iframe, viewer);
        } else if (data.type === 'dyson-model3d-status') {
            viewer.state = String(data.state || '');
            if (viewer.host)
                viewer.host.el.dataset.state = viewer.state;
        } else if (data.type === 'dyson-model3d-escape') {
            viewer.onEscape?.();
        }
        return;
    }
});

function absolute(url) {
    return new URL(url, document.baseURI).href;
}

function srcdoc(threeUrl, viewerUrl, textColor) {
    const scheme = getComputedStyle(document.documentElement).colorScheme || 'normal';
    const safeScheme = /^[a-z ]+$/.test(scheme) ? scheme : 'normal';
    const color = /^rgba?\([\d.,\s/%]+\)$/.test(textColor) ? textColor : '#888';
    const csp = `default-src 'none'; script-src ${location.origin}/lib/three/; style-src 'unsafe-inline'; `
        + "img-src blob: data:; connect-src blob: data:; media-src 'none'; object-src 'none'; frame-src 'none'; "
        + "base-uri 'none'; form-action 'none'";
    const esc = (s) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
    return '<!doctype html><html><head><meta charset="utf-8">'
        + `<meta http-equiv="Content-Security-Policy" content="${esc(csp)}">`
        + `<style>:root{color-scheme:${safeScheme}}html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}`
        + 'canvas{display:block;width:100%;height:100%;touch-action:none;cursor:grab}canvas:active{cursor:grabbing}'
        + '#status{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:12px;'
        + `text-align:center;font:13px system-ui,sans-serif;color:${color};pointer-events:none}#status[hidden]{display:none}</style>`
        + '</head><body><div id="status">Loading 3D model\u2026</div>'
        + `<script src="${esc(absolute(threeUrl))}"></script><script src="${esc(absolute(viewerUrl))}"></script>`
        + '</body></html>';
}

/** Sandboxed viewer iframe (allow-scripts only: opaque origin, no parent access, no network but blob:/data:). */
function createViewer(container, threeUrl, viewerUrl, format, previewUrl, path, inline, textColor) {
    const iframe = document.createElement('iframe');
    iframe.setAttribute('sandbox', 'allow-scripts');
    iframe.referrerPolicy = 'no-referrer';
    iframe.title = '3D model viewer';
    iframe.style.cssText = 'display:block;width:100%;height:100%;border:0;background:transparent;color-scheme:inherit';
    iframe.srcdoc = srcdoc(threeUrl, viewerUrl, textColor);
    const viewer = { ready: false, format, previewUrl, path, inline, loadedPath: null };
    viewers.set(iframe, viewer);
    container.appendChild(iframe);
    return { iframe, viewer };
}

async function sendModel(iframe, viewer) {
    if (!viewer.ready || viewer.loadedPath === viewer.path)
        return;
    const path = viewer.path;
    viewer.loadedPath = path;
    iframe.contentWindow?.postMessage({ type: 'dyson-model3d-mode', inline: viewer.inline }, '*');
    let buffer = null;
    let error = null;
    try {
        const response = await fetch(viewer.previewUrl, { credentials: 'same-origin' });
        if (!response.ok)
            throw new Error(`HTTP ${response.status}`);
        buffer = await response.arrayBuffer();
    } catch (e) {
        error = 'Could not read the model file: ' + (e?.message || e);
    }
    if (viewer.path !== path || !iframe.isConnected)
        return; // a newer path (or an unload) won
    const message = { type: 'dyson-model3d-load', format: viewer.format, buffer, error };
    iframe.contentWindow?.postMessage(message, '*', buffer ? [buffer] : []);
}

function layerFor(slot) {
    return slot.closest('.meta-chat-feed__list')?.querySelector(':scope > .meta-chat-feed__model3d-layer') ?? null;
}

function watch(layer) {
    if (watched.has(layer))
        return;
    watched.add(layer);
    const list = layer.parentElement;
    new ResizeObserver(queueSync).observe(list);
    new MutationObserver(queueSync).observe(list, { childList: true, subtree: true });
    // Entrance and live-row animations translate the slot; re-measure when they settle.
    list.addEventListener('animationend', queueSync);
}

function queueSync() {
    if (syncQueued)
        return;
    syncQueued = true;
    requestAnimationFrame(sync);
}

// ponytail: O(live hosts) getBoundingClientRect per sync, capped at MAX_HOSTS. Upgrade path: IntersectionObserver to
// skip offscreen hosts.
function sync() {
    syncQueued = false;
    const overlay = document.querySelector(OVERLAYS) !== null;
    for (const [key, host] of hosts) {
        if (!host.layer.isConnected) {
            unload(key, host);
            continue;
        }
        if (host.inLayer) {
            host.layer.style.visibility = overlay ? 'hidden' : '';
            place(host);
        }
    }
}

function place(host) {
    const slot = host.slot;
    if (!slot || !slot.isConnected || host.layer === null) {
        host.el.style.display = 'none';
        return;
    }
    const s = slot.getBoundingClientRect();
    const l = host.layer.getBoundingClientRect();
    const style = host.el.style;
    style.display = '';
    style.top = `${s.top - l.top}px`;
    style.left = `${s.left - l.left}px`;
    style.width = `${s.width}px`;
    style.height = `${s.height}px`;
    slot.toggleAttribute('data-ready', host.viewer.ready);
}

function enter(host, slot) {
    if (matchMedia('(prefers-reduced-motion: reduce)').matches || !slot.closest('.a2ui-enter'))
        return;
    const delay = parseFloat(getComputedStyle(slot).getPropertyValue('--a2ui-enter-delay')) || 0;
    const easing = getComputedStyle(document.documentElement).getPropertyValue('--ease').trim() || 'ease-out';
    try {
        host.el.animate(
            [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }],
            { duration: 280, delay, easing, fill: 'backwards' });
    } catch {
        // Unparseable easing: no entrance, the viewer still shows.
    }
}

function unload(key, host) {
    host.iframe.src = 'about:blank';
    viewers.delete(host.iframe);
    host.el.remove();
    hosts.delete(key);
}

/**
 * Point the host for <key> at <slot>, creating it on first attach. Returns false past the live-host cap.
 * Called after every render of the Blazor slot.
 */
export function attach(key, slot, previewUrl, format, path, threeUrl, viewerUrl) {
    if (!slot)
        return true;
    for (const [k, h] of hosts) {
        if (!h.layer.isConnected)
            unload(k, h);
    }

    const layer = layerFor(slot);
    let host = hosts.get(key);
    if (host && host.layer !== (layer ?? slot)) {
        unload(key, host);
        host = undefined;
    }

    if (!host) {
        if (hosts.size >= MAX_HOSTS)
            return false;
        const el = document.createElement('div');
        el.className = 'a2ui-model3d-host';
        el.style.cssText = layer
            ? 'position:absolute;pointer-events:auto;display:none;border-radius:inherit;overflow:hidden'
            : 'position:absolute;inset:0;pointer-events:auto';
        const textColor = getComputedStyle(slot).color;
        const { iframe, viewer } = createViewer(el, threeUrl, viewerUrl, format, previewUrl, path, true, textColor);
        host = { el, iframe, viewer, layer: layer ?? slot, inLayer: layer !== null, slot: null };
        viewer.host = host;
        viewer.onReady = () => host.slot && host.inLayer && place(host);
        hosts.set(key, host);
        if (layer) {
            layer.appendChild(el);
            watch(layer);
        } else {
            // Fallback outside the feed: lives in the slot, so a re-render reloads it.
            slot.style.position = 'relative';
            slot.appendChild(el);
        }
        enter(host, slot);
    }

    host.slot = slot;
    // Always the newest preview URL: a re-rendered instance registers its own id and the old one is released.
    host.viewer.previewUrl = previewUrl;
    if (host.viewer.path !== path) {
        host.viewer.path = path;
        host.viewer.format = format;
        sendModel(host.iframe, host.viewer);
    }
    if (layer) {
        layer.style.visibility = document.querySelector(OVERLAYS) ? 'hidden' : '';
        place(host);
    }
    return true;
}

/** Re-render: the slot is going away but the node is still a Model3D. Hide the host; keep the iframe. */
export function release(key, slot) {
    const host = hosts.get(key);
    if (host && host.slot === slot) {
        host.slot = null;
        host.el.style.display = 'none';
    }
}

/** Node removed or replaced: unload the iframe. */
export function dispose(key) {
    const host = hosts.get(key);
    if (host)
        unload(key, host);
}

/**
 * Surface view disposed (session switch, page leave, feed item removed): unload every host under the prefix whose
 * slot is gone. A host a new surface-view instance already re-attached (question-card/plain branch swap) stays.
 */
export function disposeSurface(prefix) {
    // ponytail: the replacement instance attaches in its own after-render call, which can land after this one, so
    // the check waits a beat (the host is already hidden by sync). Upgrade path: a surface-level attach count.
    setTimeout(() => {
        for (const [key, host] of hosts) {
            if (key.startsWith(prefix) && !host.slot?.isConnected)
                unload(key, host);
        }
    }, 1500);
}

/** Modal viewer: zoom/pan on, lighting from the two sliders posted to this iframe only (no circuit round trip). */
export function wireModal(stage, temp, intensity, tempLabel, intensityLabel, previewUrl, format, path, threeUrl, viewerUrl) {
    if (!stage || stage.querySelector('iframe'))
        return;
    const { iframe, viewer } = createViewer(stage, threeUrl, viewerUrl, format, previewUrl, path, false,
        getComputedStyle(stage).color);
    const post = () => {
        const kelvin = Number(temp.value);
        const k = Number(intensity.value);
        tempLabel.textContent = `${kelvin} K`;
        intensityLabel.textContent = `${k.toFixed(2)}\u00d7`;
        if (viewer.ready)
            iframe.contentWindow?.postMessage({ type: 'dyson-model3d-light', kelvin, intensity: k }, '*');
    };
    temp.addEventListener('input', post);
    intensity.addEventListener('input', post);
    viewer.onReady = post;
    viewer.onEscape = () => stage.closest('.confirm-dialog-backdrop')
        ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    queueSync(); // hide the inline layer while the modal is up
}
