// Custom audio player behaviour for AudioPlayer.razor. Owns all playback state client-side so
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
    const audio = root.querySelector("audio");
    const play = root.querySelector('[data-role="play"]');
    const seek = root.querySelector('[data-role="seek"]');
    const elapsed = root.querySelector('[data-role="elapsed"]');
    const durationEl = root.querySelector('[data-role="duration"]');
    const loop = root.querySelector('[data-role="loop"]');
    const mute = root.querySelector('[data-role="mute"]');
    const volume = root.querySelector('[data-role="volume"]');
    const error = root.querySelector('[data-role="error"]');
    if (!audio || !play || !seek || !loop || !mute || !volume)
        return { dispose() { } };

    const abort = new AbortController();
    const on = (target, type, handler) => target.addEventListener(type, handler, { signal: abort.signal });
    let scrubbing = false;

    const showError = (message) => {
        if (!error)
            return;
        error.textContent = message;
        error.hidden = false;
    };

    const renderPlaying = () => {
        const playing = !audio.paused && !audio.ended;
        root.toggleAttribute("data-playing", playing);
        if (playing && error)
            error.hidden = true;
        const label = playing ? "Pause" : "Play";
        play.setAttribute("aria-label", label);
        play.title = label;
    };

    const renderTime = () => {
        const duration = audio.duration;
        const finite = Number.isFinite(duration) && duration > 0;
        seek.disabled = !finite;
        if (finite)
            seek.max = String(duration);
        if (!scrubbing)
            seek.value = String(audio.currentTime);
        const current = scrubbing ? Number(seek.value) : audio.currentTime;
        elapsed.textContent = formatTime(current);
        durationEl.textContent = finite ? formatTime(duration) : "--:--";
        seek.setAttribute("aria-valuetext", `${formatTime(current)} of ${finite ? formatTime(duration) : "unknown"}`);
        setFill(seek, finite ? current / duration : 0);
    };

    const renderLoop = () => {
        loop.setAttribute("aria-pressed", String(audio.loop));
        loop.title = audio.loop ? "Loop: on" : "Loop: off";
    };

    const renderVolume = () => {
        const silent = audio.muted || audio.volume === 0;
        root.toggleAttribute("data-muted", silent);
        mute.setAttribute("aria-pressed", String(silent));
        mute.setAttribute("aria-label", silent ? "Unmute" : "Mute");
        mute.title = silent ? "Unmute" : "Mute";
        const shown = audio.muted ? 0 : audio.volume;
        volume.value = String(shown);
        volume.setAttribute("aria-valuetext", `${Math.round(shown * 100)}%`);
        setFill(volume, shown);
    };

    on(play, "click", () => {
        if (audio.paused || audio.ended) {
            const started = audio.play();
            if (started && typeof started.catch === "function")
                started.catch((e) => showError(`Could not play: ${e && e.message ? e.message : e}`));
        } else {
            audio.pause();
        }
    });

    on(loop, "click", () => {
        audio.loop = !audio.loop;
        renderLoop();
    });

    on(mute, "click", () => {
        if (audio.muted || audio.volume === 0) {
            audio.muted = false;
            if (audio.volume === 0)
                audio.volume = 0.5;
        } else {
            audio.muted = true;
        }
    });

    on(volume, "input", () => {
        const value = Number(volume.value);
        audio.volume = value;
        audio.muted = value === 0;
    });

    on(seek, "input", () => {
        scrubbing = true;
        audio.currentTime = Number(seek.value);
        renderTime();
    });
    on(seek, "change", () => {
        scrubbing = false;
        renderTime();
    });

    for (const type of ["play", "playing", "pause", "ended"])
        on(audio, type, renderPlaying);
    for (const type of ["timeupdate", "durationchange", "loadedmetadata", "seeked", "emptied"])
        on(audio, type, renderTime);
    on(audio, "volumechange", renderVolume);
    on(audio, "error", () => {
        renderPlaying();
        showError("This audio file could not be loaded or its format is not supported here.");
    });

    renderPlaying();
    renderTime();
    renderLoop();
    renderVolume();

    return {
        dispose() {
            abort.abort();
            try {
                audio.pause();
                audio.removeAttribute("src");
                audio.load();
            } catch {
                // element already detached
            }
        },
    };
}
