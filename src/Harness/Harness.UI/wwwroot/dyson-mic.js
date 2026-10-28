// Meta Agent composer push-to-talk: record the mic, return a 16 kHz mono 16-bit WAV.
// ponytail: ScriptProcessorNode is deprecated but needs no module file; upgrade path is an AudioWorklet.
window.dysonMic = (function () {
  var TARGET_RATE = 16000;
  var state = null;

  function release(s) {
    try { if (s.proc) s.proc.disconnect(); } catch (e) {}
    try { if (s.source) s.source.disconnect(); } catch (e) {}
    if (s.stream) s.stream.getTracks().forEach(function (t) { t.stop(); });
    if (s.ctx) return s.ctx.close().catch(function () {});
    return Promise.resolve();
  }

  function downsample(chunks, rate) {
    var length = 0;
    for (var i = 0; i < chunks.length; i++) length += chunks[i].length;
    var ratio = rate / TARGET_RATE;
    var outLength = Math.floor(length / ratio);
    var out = new Int16Array(outLength);
    var all = new Float32Array(length);
    var offset = 0;
    for (var j = 0; j < chunks.length; j++) { all.set(chunks[j], offset); offset += chunks[j].length; }
    for (var k = 0; k < outLength; k++) {
      // Box filter over the source window: cheap anti-aliasing for speech.
      var start = Math.floor(k * ratio), end = Math.min(length, Math.floor((k + 1) * ratio));
      var sum = 0, n = 0;
      for (var m = start; m < end; m++) { sum += all[m]; n++; }
      var v = n ? sum / n : 0;
      v = Math.max(-1, Math.min(1, v));
      out[k] = v < 0 ? v * 0x8000 : v * 0x7fff;
    }
    return out;
  }

  function wav(samples) {
    var buffer = new ArrayBuffer(44 + samples.length * 2);
    var view = new DataView(buffer);
    function text(at, s) { for (var i = 0; i < s.length; i++) view.setUint8(at + i, s.charCodeAt(i)); }
    text(0, "RIFF"); view.setUint32(4, 36 + samples.length * 2, true); text(8, "WAVE");
    text(12, "fmt "); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, TARGET_RATE, true); view.setUint32(28, TARGET_RATE * 2, true);
    view.setUint16(32, 2, true); view.setUint16(34, 16, true);
    text(36, "data"); view.setUint32(40, samples.length * 2, true);
    for (var i = 0; i < samples.length; i++) view.setInt16(44 + i * 2, samples[i], true);
    return new Uint8Array(buffer);
  }

  return {
    /** Resolves true once recording; false when the mic is unavailable or denied. */
    start: async function () {
      if (state) return true;
      var s = { chunks: [], aborted: false };
      state = s;
      try {
        s.stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
        if (s.aborted) { await release(s); return false; }
        s.ctx = new AudioContext();
        s.rate = s.ctx.sampleRate;
        s.source = s.ctx.createMediaStreamSource(s.stream);
        s.proc = s.ctx.createScriptProcessor(4096, 1, 1);
        s.proc.onaudioprocess = function (e) { s.chunks.push(new Float32Array(e.inputBuffer.getChannelData(0))); };
        s.source.connect(s.proc);
        s.proc.connect(s.ctx.destination);
        return true;
      } catch (e) {
        console.warn("dysonMic.start", e);
        if (state === s) state = null;
        await release(s);
        return false;
      }
    },

    /** Stops and returns the WAV bytes (null when nothing was recorded). */
    stop: async function () {
      var s = state;
      state = null;
      if (!s) return null;
      s.aborted = true;
      await release(s);
      if (!s.rate || !s.chunks.length) return null;
      return wav(downsample(s.chunks, s.rate));
    },

    cancel: function () {
      var s = state;
      state = null;
      if (s) { s.aborted = true; release(s); }
    },

    caret: function (el) { return el ? el.selectionStart : null; },

    setCaret: function (el, pos) {
      if (!el) return;
      // After the Blazor render that wrote the new value.
      requestAnimationFrame(function () {
        el.focus();
        el.setSelectionRange(pos, pos);
      });
    },

    /** Hold Ctrl+M in the textarea. Repeat is ignored; nothing while the slash list is open. */
    attachHotkey: function (el, dotNetRef) {
      if (!el || !dotNetRef) return;
      el._dysonMicRef = dotNetRef;
      if (el._dysonMicHotkey) return;
      el._dysonMicHotkey = true;
      var held = false;
      function call(name) {
        var ref = el._dysonMicRef;
        if (ref) ref.invokeMethodAsync(name).catch(function () {});
      }
      el.addEventListener("keydown", function (e) {
        if (!el._dysonMicRef || !e.ctrlKey || e.altKey || e.metaKey || (e.key !== "m" && e.key !== "M")) return;
        e.preventDefault();
        if (e.repeat || held || el.getAttribute("data-slash-open") === "1") return;
        held = true;
        call("MicDown");
      });
      el.addEventListener("keyup", function (e) {
        if (!held || (e.key !== "m" && e.key !== "M" && e.key !== "Control")) return;
        held = false;
        call("MicUp");
      });
      el.addEventListener("blur", function () {
        if (!held) return;
        held = false;
        call("MicUp");
      });
    },

    detachHotkey: function (el) {
      if (el) el._dysonMicRef = null;
    },

    /**
     * Voice-mode push to talk: Ctrl+M on the window, capture phase, one active ref.
     * stopImmediatePropagation so the composer attachHotkey never sees the key.
     */
    attachVoiceHotkey: function (dotNetRef) {
      if (!dotNetRef) return;
      this.detachVoiceHotkey();
      var hook = { ref: dotNetRef, held: false };
      function call(name) {
        hook.ref.invokeMethodAsync(name).catch(function () {});
      }
      function isM(e) {
        return e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && (e.key === "m" || e.key === "M");
      }
      function onKeyDown(e) {
        if (!isM(e)) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        if (e.repeat || hook.held) return;
        hook.held = true;
        call("PttDown");
      }
      function onKeyUp(e) {
        if (!hook.held) return;
        if (e.key !== "m" && e.key !== "M" && e.key !== "Control") return;
        hook.held = false;
        call("PttUp");
      }
      function release() {
        if (!hook.held) return;
        hook.held = false;
        call("PttUp");
      }
      function onVisibility() {
        if (document.visibilityState === "hidden") release();
      }
      hook.down = onKeyDown;
      hook.up = onKeyUp;
      hook.blur = release;
      hook.visibility = onVisibility;
      window.addEventListener("keydown", onKeyDown, true);
      window.addEventListener("keyup", onKeyUp, true);
      window.addEventListener("blur", release, true);
      document.addEventListener("visibilitychange", onVisibility, true);
      window._dysonVoiceHotkey = hook;
    },

    detachVoiceHotkey: function () {
      var hook = window._dysonVoiceHotkey;
      if (!hook) return;
      window._dysonVoiceHotkey = null;
      if (hook.held) {
        hook.held = false;
        hook.ref.invokeMethodAsync("PttUp").catch(function () {});
      }
      window.removeEventListener("keydown", hook.down, true);
      window.removeEventListener("keyup", hook.up, true);
      window.removeEventListener("blur", hook.blur, true);
      document.removeEventListener("visibilitychange", hook.visibility, true);
    }
  };
})();
