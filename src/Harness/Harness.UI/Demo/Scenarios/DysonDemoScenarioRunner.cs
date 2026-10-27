#if DYSON_PREVIEW
using DysonHarness;
using Microsoft.JSInterop;

namespace Harness.UI.Demo;

public sealed class DysonDemoScenarioRunner : IDisposable
{
    private readonly IDysonDemoHooks _hooks;
    private readonly IJSRuntime _js;
    private CancellationTokenSource? _run;
    private DotNetObjectReference<DysonDemoScenarioRunner>? _dotNetRef;
    private bool _cleaning;

    public DysonDemoScenarioRunner(IDysonDemoHooks hooks, IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(js);
        _hooks = hooks;
        _js = js;
    }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public event Action? Changed;

    public void Start(IDysonDemoScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (IsRunning)
            return;

        var cts = new CancellationTokenSource();
        _run = cts;
        _cleaning = false;
        LastError = null;
        IsRunning = true;
        _dotNetRef?.Dispose();
        _dotNetRef = DotNetObjectReference.Create(this);
        Changed?.Invoke();
        _ = ExecuteAsync(scenario, cts);
    }

    public void Cancel()
    {
        if (_cleaning)
            return;
        _cleaning = true;
        _run?.Cancel();
    }

    [JSInvokable]
    public void CancelFromHotkey() => Cancel();

    public void Dispose()
    {
        _dotNetRef?.Dispose();
        _dotNetRef = null;
        _run?.Dispose();
        _run = null;
    }

    private async Task ExecuteAsync(IDysonDemoScenario scenario, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await Task.Delay(1, token).ConfigureAwait(true);
            if (_dotNetRef is not null)
                await DysonCircuitJs.InvokeVoidAsync(_js, "dysonDemo.attach", _dotNetRef).ConfigureAwait(true);

            var ran = await scenario.RunAsync(_hooks, token).ConfigureAwait(true);
            if (ran.IsError)
                LastError = ran.Error;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            _cleaning = true;
            try
            {
                var cleaned = await _hooks.CleanupAsync(CancellationToken.None).ConfigureAwait(true);
                if (cleaned.IsError && LastError is null)
                    LastError = cleaned.Error;
            }
            catch (Exception ex)
            {
                LastError ??= ex.Message;
            }

            await DysonCircuitJs.InvokeVoidAsync(_js, "dysonDemo.detach").ConfigureAwait(true);
            _dotNetRef?.Dispose();
            _dotNetRef = null;
            IsRunning = false;
            if (ReferenceEquals(_run, cts))
                _run = null;
            cts.Dispose();
            _cleaning = false;
            Changed?.Invoke();
        }
    }
}
#endif
