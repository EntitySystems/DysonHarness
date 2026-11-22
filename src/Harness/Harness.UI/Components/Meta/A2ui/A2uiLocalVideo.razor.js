// LocalVideo card: fill the duration pill from the browser's own metadata read; never crosses the circuit again.
function formatTime(seconds) {
    const total = Math.floor(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = String(total % 60).padStart(2, "0");
    return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}

export function init(thumb) {
    const video = thumb && thumb.querySelector("video");
    const pill = thumb && thumb.querySelector('[data-role="duration"]');
    if (!video || !pill)
        return;

    const render = () => {
        const d = video.duration;
        pill.textContent = Number.isFinite(d) && d > 0 ? formatTime(d) : "--:--";
    };
    const fail = () => {
        pill.textContent = "No preview";
        thumb.setAttribute("data-error", "");
    };
    video.addEventListener("loadedmetadata", render);
    video.addEventListener("durationchange", render);
    video.addEventListener("error", fail);
    if (video.error)
        fail();
    else if (video.readyState >= 1)
        render();
}
