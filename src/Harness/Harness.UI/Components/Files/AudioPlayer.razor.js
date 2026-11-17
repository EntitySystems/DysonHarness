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
    if (!media || !play || !seek || !loop || !mute || !volume)
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
        const label = playing ? "Pause" : "Play";
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
        const current = scrubbing ? Number(seek.value) : media.currentTime;
        elapsed.textContent = formatTime(current);
        durationEl.textContent = finite ? formatTime(duration) : "--:--";
        seek.setAttribute("aria-valuetext", `${formatTime(current)} of ${finite ? formatTime(duration) : "unknown"}`);
        setFill(seek, finite ? current / duration : 0);
    };

    const renderLoop = () => {
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

    on(loop, "click", () => {
        media.loop = !media.loop;
        renderLoop();
    });

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

    for (const type of ["play", "playing", "pause", "ended"])
        on(media, type, renderPlaying);
    for (const type of ["timeupdate", "durationchange", "loadedmetadata", "seeked", "emptied"])
        on(media, type, renderTime);
    on(media, "volumechange", renderVolume);
    on(media, "error", () => {
        renderPlaying();
        showError("This file could not be played here (unsupported format or codec).");
    });

    renderPlaying();
    renderTime();
    renderLoop();
    renderVolume();

    return {
        dispose() {
            abort.abort();
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
