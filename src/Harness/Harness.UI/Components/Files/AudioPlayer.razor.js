// Custom audio/video player behaviour for AudioPlayer.razor. Owns all playback state client-side so
// timeupdate / scrubbing never round-trips through the Blazor circuit.

function formatTime(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0)
        return "--:--";
    const total = Math.floor(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = String(total % 60).padStart(2, "0");
    return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}

// One player at a time across the page (A2UI surfaces and the file viewer share this module instance).
let current = null;

function setFill(range, fraction) {
    const pct = Number.isFinite(fraction) ? Math.min(Math.max(fraction, 0), 1) * 100 : 0;
    range.style.setProperty("--fill", `${pct}%`);
}

export function init(root) {
    const media = root.querySelector("audio, video");
    const play = root.querySelector('[data-role="play"]');
    const seek = root.querySelector('[data-role="seek"]');
    const elapsed = root.querySelector('[data-role="elapsed"]');
    const durationEl = root.querySelector('[data-role="duration"]');
    const loop = root.querySelector('[data-role="loop"]');
    const mute = root.querySelector('[data-role="mute"]');
    const volume = root.querySelector('[data-role="volume"]');
    const error = root.querySelector('[data-role="error"]');
    const errorText = root.querySelector('[data-role="error-text"]');
    // loop is absent in the Compact layout.
    if (!media || !play || !seek || !mute || !volume)
        return { dispose() { } };

    const isVideo = media.tagName === "VIDEO";
    const abort = new AbortController();
    const on = (target, type, handler) => target.addEventListener(type, handler, { signal: abort.signal });
    let scrubbing = false;

    const showError = (message) => {
        if (!error)
            return;
        // Write to the text span only so the "Open in default app" button survives.
        (errorText || error).textContent = message;
        error.hidden = false;
    };

    const renderPlaying = () => {
        const playing = !media.paused && !media.ended;
        root.toggleAttribute("data-playing", playing);
        if (playing && error)
            error.hidden = true;
        // A deferred remote player keeps its "Play (loads from host)" hint until the first press sets src.
        const label = playing ? "Pause" : (media.getAttribute("src") ? "Play" : play.dataset.playLabel || "Play");
        play.setAttribute("aria-label", label);
        play.title = label;
    };

    const renderTime = () => {
        const duration = media.duration;
        const finite = Number.isFinite(duration) && duration > 0;
        seek.disabled = !finite;
        if (finite)
            seek.max = String(duration);
        if (!scrubbing)
            seek.value = String(media.currentTime);
        const at = scrubbing ? Number(seek.value) : media.currentTime;
        elapsed.textContent = formatTime(at);
        durationEl.textContent = finite ? formatTime(duration) : "--:--";
        seek.setAttribute("aria-valuetext", `${formatTime(at)} of ${finite ? formatTime(duration) : "unknown"}`);
        setFill(seek, finite ? at / duration : 0);
    };

    const renderLoop = () => {
        if (!loop)
            return;
        loop.setAttribute("aria-pressed", String(media.loop));
        loop.title = media.loop ? "Loop: on" : "Loop: off";
    };

    const renderVolume = () => {
        const silent = media.muted || media.volume === 0;
        root.toggleAttribute("data-muted", silent);
        mute.setAttribute("aria-pressed", String(silent));
        mute.setAttribute("aria-label", silent ? "Unmute" : "Mute");
        mute.title = silent ? "Unmute" : "Mute";
        const shown = media.muted ? 0 : media.volume;
        volume.value = String(shown);
        volume.setAttribute("aria-valuetext", `${Math.round(shown * 100)}%`);
        setFill(volume, shown);
    };

    const togglePlay = () => {
        if (media.paused || media.ended) {
            // The only remote fetch: a deferred (url) player gets its src on the user's first press.
            if (!media.getAttribute("src") && media.dataset.src)
                media.src = media.dataset.src;
            const started = media.play();
            if (started && typeof started.catch === "function")
                started.catch((e) => showError(`Could not play: ${e && e.message ? e.message : e}`));
        } else {
            media.pause();
        }
    };

    on(play, "click", togglePlay);
    if (isVideo)
        on(media, "click", togglePlay);

    if (loop) {
        on(loop, "click", () => {
            media.loop = !media.loop;
            renderLoop();
        });
    }

    on(mute, "click", () => {
        if (media.muted || media.volume === 0) {
            media.muted = false;
            if (media.volume === 0)
                media.volume = 0.5;
        } else {
            media.muted = true;
        }
    });

    on(volume, "input", () => {
        const value = Number(volume.value);
        media.volume = value;
        media.muted = value === 0;
    });

    on(seek, "input", () => {
        scrubbing = true;
        media.currentTime = Number(seek.value);
        renderTime();
    });
    on(seek, "change", () => {
        scrubbing = false;
        renderTime();
    });

    on(media, "play", () => {
        if (current && current !== media)
            current.pause();
        current = media;
    });
    for (const type of ["play", "playing", "pause", "ended"])
        on(media, type, renderPlaying);
    for (const type of ["timeupdate", "durationchange", "loadedmetadata", "seeked", "emptied"])
        on(media, type, renderTime);
    on(media, "volumechange", renderVolume);
    on(media, "error", () => {
        renderPlaying();
        showError(media.dataset.src
            ? "This audio could not be played here (network error, blocked, or unsupported format)."
            : "This file could not be played here (unsupported format or codec).");
    });

    renderPlaying();
    renderTime();
    renderLoop();
    renderVolume();

    return {
        dispose() {
            abort.abort();
            if (current === media)
                current = null;
            try {
                media.pause();
                media.removeAttribute("src");
                media.load();
            } catch {
                // element already detached
            }
        },
    };
}
