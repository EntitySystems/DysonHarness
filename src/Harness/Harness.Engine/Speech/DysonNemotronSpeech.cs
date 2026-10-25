using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DysonHarness;

/// <summary>
/// Nemotron 3.5 streaming speech to text (transcribe.cpp v0.4.0, in-process <c>transcribe.dll</c>).
/// Windows only. <see cref="TranscribeLiveAsync"/> is the process-wide live entry; this type is
/// <see cref="IDysonSpeechToText"/> for one WAV clip. The active model implements
/// <see cref="IDysonLiveSpeechToText"/> and calls the static live method.
/// </summary>
public sealed class DysonNemotronSpeech(string installRoot) : IDysonSpeechToText
{
    /// <summary>About 560 ms of 16 kHz mono s16le. Engine does not reference the Windows capture project.</summary>
    internal const int FeedBytes = 17_920;

    private const int SampleRate = 16_000;
    private const string ExpectedVersion = "0.4.0";

    // transcribe.h v0.4.0 transcribe_abi_struct. Append-only.
    private const int AbiModelLoadParams = 0;
    private const int AbiSessionParams = 1;
    private const int AbiRunParams = 2;
    private const int AbiStreamParams = 3;
    private const int AbiStreamText = 10;

    /// <summary>
    /// Win-x64 MSVC layout of <c>transcribe_run_params</c> (v0.4.0): <c>uint64 struct_size</c> at 0,
    /// five 4-byte enums at 8, 4 bytes of pad, <c>const char* language</c> at 32. Init fills the
    /// abi-sized buffer; only this field is patched. <c>transcribe_stream_text</c> is packed as
    /// <c>u64</c>, <c>full_text</c> pointer at 8, <c>full_text_bytes</c> at 16 (no pad; every field is 8 bytes).
    /// </summary>
    internal const int RunParamsLanguageOffset = 32;

    private const int StreamTextFullTextOffset = 8;
    private const int StreamTextFullTextBytesOffset = 16;

    // ponytail: one model + one stream per process. The C API allows one active stream per model;
    // a second install root frees the first model. transcribe_init_backends stays on the first runtime dir.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lock NativeGate = new();
    private static NativeApi? _api;
    private static IntPtr _model;
    private static IntPtr _session;
    private static string? _loadedRoot;
    private static bool _backendsReady;

    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));

    public string RuntimeId => DysonGgufSpeechModels.NemotronId;

    /// <summary>
    /// Frees the process-wide session and model. Waits until the active transcribe call leaves
    /// <see cref="Gate"/> (cancel that call first). The DLL stays loaded.
    /// </summary>
    public static void Stop()
    {
        Gate.Wait();
        try
        {
            FreeNative();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Loads <c>transcribe.dll</c> and checks <c>transcribe_version</c>. Does not open the GGUF.</summary>
    internal static Task<VoidResult<string>> CheckRuntimeAsync(string installRoot, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
                return new VoidResult<string>("Nemotron 3.5 is Windows only.");
            try
            {
                var api = Bind(installRoot);
                var version = PtrToString(api.Version());
                if (!string.Equals(version, ExpectedVersion, StringComparison.Ordinal))
                    return new VoidResult<string>($"Nemotron 3.5 runtime version is '{version}', expected {ExpectedVersion}.");
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return new VoidResult<string>(NativeLoadMessage(ex));
            }
        }, CancellationToken.None);

    public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
        Stream audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
        if (!Installed(_installRoot))
        {
            yield return NotInstalled();
            yield break;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield return WindowsOnly();
            yield break;
        }

        Result<byte[], string>? pcm = null;
        string? readError = null;
        var readCancelled = false;
        try
        {
            pcm = await ReadWavAsync(audio, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            readCancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readError = $"Nemotron 3.5 could not read the audio: {ex.Message}";
        }

        if (readCancelled)
            yield break;
        if (readError is not null)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(readError);
            yield break;
        }

        if (pcm!.IsError)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(pcm.Error);
            yield break;
        }

        await foreach (var chunk in RunStreamAsync(_installRoot, Blocks(pcm.Value, cancellationToken), cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// Live 16 kHz mono s16le. Not installed is an error before the DLL is touched. Off Windows is
    /// "Nemotron 3.5 is Windows only." One stream at a time.
    /// </summary>
    public static async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeLiveAsync(
        string installRoot,
        IAsyncEnumerable<Result<byte[], string>> pcm,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(pcm);
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
        if (!Installed(installRoot))
        {
            yield return NotInstalled();
            yield break;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield return WindowsOnly();
            yield break;
        }

        await foreach (var chunk in RunStreamAsync(installRoot, pcm, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Null when a partial did not change. A final is always returned.</summary>
    internal static DysonSpeechChunk? NextChunk(string? previousText, string currentText, bool isFinal, int endMs)
    {
        if (!isFinal && string.Equals(previousText, currentText, StringComparison.Ordinal))
            return null;
        return new DysonSpeechChunk(currentText, 0, endMs, isFinal);
    }

    /// <summary>Little-endian s16le to float32 in [-1, 1]. A trailing odd byte is ignored.</summary>
    internal static float[] PcmS16ToFloat(ReadOnlySpan<byte> pcm)
    {
        var n = pcm.Length / 2;
        var floats = new float[n];
        for (var i = 0; i < n; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
            floats[i] = sample / 32768f;
        }

        return floats;
    }

    private static async IAsyncEnumerable<Result<DysonSpeechChunk, string>> RunStreamAsync(
        string installRoot,
        IAsyncEnumerable<Result<byte[], string>> pcmBlocks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entered = false;
        var began = false;
        try
        {
            try
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
            }
            catch (OperationCanceledException)
            {
            }

            if (!entered)
                yield break;

            string? fatal = null;
            try
            {
                var ensured = await Task.Run(() => EnsureLoaded(installRoot), CancellationToken.None).ConfigureAwait(false);
                if (ensured.IsError)
                    fatal = ensured.Error;
                else
                {
                    var begun = await Task.Run(BeginStream, CancellationToken.None).ConfigureAwait(false);
                    if (begun.IsError)
                        fatal = begun.Error;
                    else
                        began = true;
                }

                long samples = 0;
                string? previous = null;
                if (fatal is null)
                {
                    var blocks = pcmBlocks.GetAsyncEnumerator(cancellationToken);
                    try
                    {
                        while (true)
                        {
                            bool more;
                            var cancelled = false;
                            try
                            {
                                more = await blocks.MoveNextAsync().ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                cancelled = true;
                                more = false;
                            }

                            if (cancelled || !more)
                                break;

                            var block = blocks.Current;
                            if (block.IsError)
                            {
                                fatal = block.Error;
                                break;
                            }

                            if (block.Value is not { Length: > 0 })
                                continue;
                            var floats = PcmS16ToFloat(block.Value);
                            if (floats.Length == 0)
                                continue;
                            var fed = await Task.Run(() => Feed(floats), CancellationToken.None).ConfigureAwait(false);
                            if (fed.IsError)
                            {
                                fatal = fed.Error;
                                break;
                            }

                            if (fed.Value != 0)
                            {
                                fatal = StatusText(fed.Value);
                                break;
                            }

                            samples += floats.Length;
                            var text = await Task.Run(ReadFullText, CancellationToken.None).ConfigureAwait(false);
                            if (text.IsError)
                            {
                                fatal = text.Error;
                                break;
                            }

                            var partial = NextChunk(previous, text.Value, false, EndMs(samples));
                            if (partial is null)
                                continue;
                            previous = text.Value;
                            yield return Result<DysonSpeechChunk, string>.AsValue(partial);
                        }
                    }
                    finally
                    {
                        await blocks.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (fatal is not null)
                {
                    yield return Result<DysonSpeechChunk, string>.AsError(fatal);
                    yield break;
                }

                if (!began)
                    yield break;

                var final = await Task.Run(FinalizeAndRead, CancellationToken.None).ConfigureAwait(false);
                if (final.IsError)
                {
                    yield return Result<DysonSpeechChunk, string>.AsError(final.Error);
                    yield break;
                }

                var endMs = EndMs(samples);
                yield return Result<DysonSpeechChunk, string>.AsValue(NextChunk(previous, final.Value, true, endMs)!);
            }
            finally
            {
                if (began)
                    await Task.Run(ResetStream, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (entered)
                Gate.Release();
        }
    }

    private static async IAsyncEnumerable<Result<byte[], string>> Blocks(
        byte[] pcm,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < pcm.Length; offset += FeedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(FeedBytes, pcm.Length - offset);
            var slice = new byte[n];
            pcm.AsSpan(offset, n).CopyTo(slice);
            yield return Result<byte[], string>.AsValue(slice);
            await Task.CompletedTask;
        }
    }

    private static async Task<Result<byte[], string>> ReadWavAsync(Stream audio, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        using var copy = new MemoryStream();
        long total = 0;
        int read;
        while ((read = await audio.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > DysonPhononSpeech.MaxAudioBytes)
                return Result<byte[], string>.AsError(
                    $"Audio clip is over {DysonPhononSpeech.MaxAudioBytes / (1024 * 1024)} MB.");
            copy.Write(buffer, 0, read);
        }

        if (total == 0)
            return Result<byte[], string>.AsError("Audio clip is empty.");
        return ParseWav(copy.GetBuffer().AsSpan(0, (int)copy.Length));
    }

    private static Result<byte[], string> ParseWav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12
            || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F'
            || wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            return Result<byte[], string>.AsError("Nemotron 3.5 needs a 16 kHz mono 16-bit PCM WAV.");

        short format = -1, channels = -1, bits = -1;
        var rate = -1;
        var dataAt = -1;
        var dataLen = -1;
        var i = 12;
        while (i + 8 <= wav.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(i + 4, 4));
            if (size < 0 || i + 8 + size > wav.Length)
                return Result<byte[], string>.AsError("Nemotron 3.5 could not read the WAV.");
            var id = wav.Slice(i, 4);
            var body = wav.Slice(i + 8, size);
            if (id.SequenceEqual("fmt "u8) && format < 0)
            {
                if (body.Length < 16)
                    return Result<byte[], string>.AsError("Nemotron 3.5 could not read the WAV.");
                format = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body.Slice(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(body.Slice(14));
            }
            else if (id.SequenceEqual("data"u8) && dataAt < 0)
            {
                dataAt = i + 8;
                dataLen = size;
            }

            i += 8 + size;
            if ((size & 1) != 0)
                i++;
        }

        if (format != 1 || channels != 1 || rate != SampleRate || bits != 16 || dataAt < 0)
            return Result<byte[], string>.AsError("Nemotron 3.5 needs a 16 kHz mono 16-bit PCM WAV.");
        if (dataLen == 0)
            return Result<byte[], string>.AsError("Audio clip is empty.");
        return Result<byte[], string>.AsValue(wav.Slice(dataAt, dataLen).ToArray());
    }

    private static bool Installed(string installRoot) =>
        DysonSpeechInstaller.Probe(installRoot).IsInstalled(DysonGgufSpeechModels.NemotronId);

    private static Result<DysonSpeechChunk, string> NotInstalled() =>
        Result<DysonSpeechChunk, string>.AsError("Nemotron 3.5 is not installed. Open Settings → Voice and install it.");

    private static Result<DysonSpeechChunk, string> WindowsOnly() =>
        Result<DysonSpeechChunk, string>.AsError("Nemotron 3.5 is Windows only.");

    private static int EndMs(long samples) => (int)Math.Min(int.MaxValue, samples * 1000 / SampleRate);

    private static VoidResult<string> EnsureLoaded(string installRoot)
    {
        if (_session != IntPtr.Zero && string.Equals(_loadedRoot, installRoot, StringComparison.OrdinalIgnoreCase))
            return VoidResult<string>.Success;
        FreeNative();
        try
        {
            var api = Bind(installRoot);
            if (!_backendsReady)
            {
                var dir = Utf8(DysonGgufSpeechModels.GetNemotronRuntimeDir(installRoot));
                try
                {
                    var status = api.InitBackends(dir);
                    if (status != 0)
                        return new VoidResult<string>(StatusText(status));
                    _backendsReady = true;
                }
                finally
                {
                    Free(dir);
                }
            }

            var modelPath = Utf8(DysonGgufSpeechModels.GetMainModelFile(installRoot, DysonGgufSpeechModels.NemotronId));
            var loadParams = AllocStruct(api, AbiModelLoadParams);
            var sessionParams = AllocStruct(api, AbiSessionParams);
            var modelSlot = Marshal.AllocHGlobal(IntPtr.Size);
            var sessionSlot = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (loadParams.IsError)
                    return new VoidResult<string>(loadParams.Error);
                if (sessionParams.IsError)
                    return new VoidResult<string>(sessionParams.Error);
                api.ModelLoadParamsInit(loadParams.Value);
                Marshal.WriteIntPtr(modelSlot, IntPtr.Zero);
                var loaded = api.ModelLoadFile(modelPath, loadParams.Value, modelSlot);
                if (loaded != 0)
                {
                    FreeHandle(api.ModelFree, modelSlot);
                    return new VoidResult<string>(StatusText(loaded));
                }

                _model = Marshal.ReadIntPtr(modelSlot);
                if (_model == IntPtr.Zero)
                    return new VoidResult<string>("Nemotron 3.5 failed to load: no model handle.");

                api.SessionParamsInit(sessionParams.Value);
                Marshal.WriteIntPtr(sessionSlot, IntPtr.Zero);
                var opened = api.SessionInit(_model, sessionParams.Value, sessionSlot);
                if (opened != 0)
                {
                    FreeHandle(api.SessionFree, sessionSlot);
                    FreeNative();
                    return new VoidResult<string>(StatusText(opened));
                }

                _session = Marshal.ReadIntPtr(sessionSlot);
                if (_session == IntPtr.Zero)
                {
                    FreeNative();
                    return new VoidResult<string>("Nemotron 3.5 failed to load: no session handle.");
                }

                _loadedRoot = installRoot;
                return VoidResult<string>.Success;
            }
            finally
            {
                Free(modelPath);
                if (loadParams.IsSuccess)
                    Free(loadParams.Value);
                if (sessionParams.IsSuccess)
                    Free(sessionParams.Value);
                Free(modelSlot);
                Free(sessionSlot);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            FreeNative();
            return new VoidResult<string>(NativeLoadMessage(ex));
        }
    }

    private static VoidResult<string> BeginStream()
    {
        var api = _api;
        if (api is null || _session == IntPtr.Zero)
            return new VoidResult<string>("Nemotron 3.5 is not loaded.");
        var run = AllocStruct(api, AbiRunParams);
        var stream = AllocStruct(api, AbiStreamParams);
        var lang = Utf8("en-US");
        try
        {
            if (run.IsError)
                return new VoidResult<string>(run.Error);
            if (stream.IsError)
                return new VoidResult<string>(stream.Error);
            api.RunParamsInit(run.Value);
            var size = api.StructSize(AbiRunParams);
            if (size < (nuint)(RunParamsLanguageOffset + IntPtr.Size))
                return new VoidResult<string>("transcribe_run_params is smaller than the v0.4.0 language field.");
            Marshal.WriteIntPtr(run.Value, RunParamsLanguageOffset, lang);
            api.StreamParamsInit(stream.Value);
            var status = api.StreamBegin(_session, run.Value, stream.Value);
            return status == 0 ? VoidResult<string>.Success : new VoidResult<string>(StatusText(status));
        }
        finally
        {
            Free(lang);
            if (run.IsSuccess)
                Free(run.Value);
            if (stream.IsSuccess)
                Free(stream.Value);
        }
    }

    private static Result<int, string> Feed(float[] samples)
    {
        var api = _api;
        if (api is null || _session == IntPtr.Zero)
            return Result<int, string>.AsError("Nemotron 3.5 is not loaded.");
        var pin = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            return Result<int, string>.AsValue(api.StreamFeed(_session, pin.AddrOfPinnedObject(), samples.Length, IntPtr.Zero));
        }
        finally
        {
            pin.Free();
        }
    }

    private static Result<string, string> FinalizeAndRead()
    {
        var api = _api;
        if (api is null || _session == IntPtr.Zero)
            return Result<string, string>.AsError("Nemotron 3.5 is not loaded.");
        var status = api.StreamFinalize(_session, IntPtr.Zero);
        if (status != 0)
            return Result<string, string>.AsError(StatusText(status));
        return ReadFullText();
    }

    private static Result<string, string> ReadFullText()
    {
        var api = _api;
        if (api is null || _session == IntPtr.Zero)
            return Result<string, string>.AsError("Nemotron 3.5 is not loaded.");
        var buf = AllocStruct(api, AbiStreamText);
        if (buf.IsError)
            return Result<string, string>.AsError(buf.Error);
        try
        {
            var size = api.StructSize(AbiStreamText);
            if (size < (nuint)(StreamTextFullTextBytesOffset + sizeof(long)))
                return Result<string, string>.AsError("transcribe_stream_text is smaller than the v0.4.0 full_text field.");
            api.StreamTextInit(buf.Value);
            var status = api.StreamGetText(_session, buf.Value);
            if (status != 0)
                return Result<string, string>.AsError(StatusText(status));
            var bytes = Marshal.ReadInt64(buf.Value, StreamTextFullTextBytesOffset);
            if (bytes < 0 || bytes > 1_000_000)
                return Result<string, string>.AsError("Nemotron 3.5 returned an unreadable transcript.");
            if (bytes == 0)
                return Result<string, string>.AsValue("");
            var ptr = Marshal.ReadIntPtr(buf.Value, StreamTextFullTextOffset);
            if (ptr == IntPtr.Zero)
                return Result<string, string>.AsValue("");
            var raw = new byte[(int)bytes];
            Marshal.Copy(ptr, raw, 0, raw.Length);
            return Result<string, string>.AsValue(Encoding.UTF8.GetString(raw));
        }
        finally
        {
            Free(buf.Value);
        }
    }

    private static void ResetStream()
    {
        if (_api is not null && _session != IntPtr.Zero)
            _api.StreamReset(_session);
    }

    private static void FreeHandle(NativeApi.FreeFn free, IntPtr slot)
    {
        var handle = Marshal.ReadIntPtr(slot);
        if (handle != IntPtr.Zero)
            free(handle);
    }

    private static void FreeNative()
    {
        var api = _api;
        if (api is not null && _session != IntPtr.Zero)
        {
            api.SessionFree(_session);
            _session = IntPtr.Zero;
        }

        if (api is not null && _model != IntPtr.Zero)
        {
            api.ModelFree(_model);
            _model = IntPtr.Zero;
        }

        _loadedRoot = null;
    }

    private static string StatusText(int status)
    {
        var api = _api;
        if (api is null)
            return $"Nemotron 3.5 failed ({status}).";
        var ptr = api.StatusString(status);
        var detail = PtrToString(ptr);
        if (detail.Length == 0)
            detail = "unknown status";
        return $"Nemotron 3.5 failed ({status}): {detail}";
    }

    private static string PtrToString(IntPtr ptr) =>
        ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";

    private static string NativeLoadMessage(Exception ex) =>
        $"Nemotron 3.5 could not load transcribe.dll. The Visual C++ runtime may be missing. {ex.Message}";

    private static NativeApi Bind(string installRoot)
    {
        lock (NativeGate)
        {
            if (_api is not null)
                return _api;
            var dll = DysonGgufSpeechModels.GetTranscribeDll(installRoot);
            if (!NativeLibrary.TryLoad(dll, out var handle)
                && !NativeLibrary.TryLoad(
                    dll,
                    typeof(DysonNemotronSpeech).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies,
                    out handle))
                throw new DllNotFoundException(dll);
            try
            {
                _api = new NativeApi(handle);
                return _api;
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }
        }
    }

    private static Result<IntPtr, string> AllocStruct(NativeApi api, int which)
    {
        var size = api.StructSize(which);
        if (size == 0 || size > 1_048_576)
            return Result<IntPtr, string>.AsError($"transcribe struct {which} has size {size}.");
        var ptr = Marshal.AllocHGlobal((nint)size);
        var zeros = new byte[(int)size];
        Marshal.Copy(zeros, 0, ptr, zeros.Length);
        return Result<IntPtr, string>.AsValue(ptr);
    }

    private static IntPtr Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    private static void Free(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
            Marshal.FreeHGlobal(ptr);
    }

    private sealed class NativeApi
    {
        public NativeApi(IntPtr library)
        {
            Version = BindFn<VersionFn>(library, "transcribe_version");
            StatusString = BindFn<StatusStringFn>(library, "transcribe_status_string");
            StructSize = BindFn<StructSizeFn>(library, "transcribe_abi_struct_size");
            InitBackends = BindFn<InitBackendsFn>(library, "transcribe_init_backends");
            ModelLoadParamsInit = BindFn<InitFn>(library, "transcribe_model_load_params_init");
            ModelLoadFile = BindFn<ModelLoadFileFn>(library, "transcribe_model_load_file");
            ModelFree = BindFn<FreeFn>(library, "transcribe_model_free");
            SessionParamsInit = BindFn<InitFn>(library, "transcribe_session_params_init");
            SessionInit = BindFn<SessionInitFn>(library, "transcribe_session_init");
            SessionFree = BindFn<FreeFn>(library, "transcribe_session_free");
            RunParamsInit = BindFn<InitFn>(library, "transcribe_run_params_init");
            StreamParamsInit = BindFn<InitFn>(library, "transcribe_stream_params_init");
            StreamBegin = BindFn<StreamBeginFn>(library, "transcribe_stream_begin");
            StreamFeed = BindFn<StreamFeedFn>(library, "transcribe_stream_feed");
            StreamFinalize = BindFn<StreamEndFn>(library, "transcribe_stream_finalize");
            StreamTextInit = BindFn<InitFn>(library, "transcribe_stream_text_init");
            StreamGetText = BindFn<StreamGetTextFn>(library, "transcribe_stream_get_text");
            StreamReset = BindFn<FreeFn>(library, "transcribe_stream_reset");
        }

        public VersionFn Version { get; }
        public StatusStringFn StatusString { get; }
        public StructSizeFn StructSize { get; }
        public InitBackendsFn InitBackends { get; }
        public InitFn ModelLoadParamsInit { get; }
        public ModelLoadFileFn ModelLoadFile { get; }
        public FreeFn ModelFree { get; }
        public InitFn SessionParamsInit { get; }
        public SessionInitFn SessionInit { get; }
        public FreeFn SessionFree { get; }
        public InitFn RunParamsInit { get; }
        public InitFn StreamParamsInit { get; }
        public StreamBeginFn StreamBegin { get; }
        public StreamFeedFn StreamFeed { get; }
        public StreamEndFn StreamFinalize { get; }
        public InitFn StreamTextInit { get; }
        public StreamGetTextFn StreamGetText { get; }
        public FreeFn StreamReset { get; }

        private static T BindFn<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr VersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr StatusStringFn(int status);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nuint StructSizeFn(int which);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int InitBackendsFn(IntPtr artifactDir);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InitFn(IntPtr param);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ModelLoadFileFn(IntPtr path, IntPtr param, IntPtr outModel);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SessionInitFn(IntPtr model, IntPtr param, IntPtr outSession);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FreeFn(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StreamBeginFn(IntPtr session, IntPtr runParams, IntPtr streamParams);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StreamFeedFn(IntPtr session, IntPtr pcm, int nSamples, IntPtr update);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StreamEndFn(IntPtr session, IntPtr update);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StreamGetTextFn(IntPtr session, IntPtr text);
    }
}
