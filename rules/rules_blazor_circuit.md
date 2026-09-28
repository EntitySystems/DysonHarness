# Blazor circuit must not run blocking work

Circuit code is anything that runs or resumes on the Blazor `SynchronizationContext`: Razor lifecycle and event handlers, and `Harness.UI` methods they call (`DysonUiHost`, `DysonUiAgentSessionRuntimeConfigBuilder`, component code-behind).

That code must not call sync I/O or process APIs (`File.*` / `Directory.*` sync, `Process.Start`, sync git) and must not block (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep`, `lock` around I/O).

It must not await a method that does that work on the caller before the first real yield. `ConfigureAwait(false)` is not a yield. An already-completed await continues inline on the circuit.

Engine methods the UI awaits, when they can start a process, read config from disk, or do an MCP handshake, must put that work in `Task.Run` (or an existing fire-and-forget like `DysonCustomMcpPromptUpdater.EnqueueRefresh`) before doing it. UI session create/resume must not wait for that work. Cancel it from dispose, not from the request token the handler is about to drop.

`Task.Run` once, at that boundary. Do not wrap every engine call.

## JS interop

Circuit JS calls go through `DysonCircuitJs`. `Timeout` is 2 seconds, and it still applies when the caller passes `CancellationToken.None` — a token passed straight to `IJSRuntime.InvokeAsync` turns off the framework default and can wait forever. Swallow only `OperationCanceledException`, `JSDisconnectedException`, `JSException`, `InvalidOperationException`, and `ObjectDisposedException`.

Do not call `NavigationManager.NavigateTo`. Its JS invoke has no token, and a cancel that is not already a permanent disconnect terminates the circuit (yellow “unhandled error” bar). Use `DysonCircuitJs.NavigateAsync` (`dysonNav.go` → `Blazor.navigateTo`). `CircuitOptions.JSInteropDefaultCallTimeout` is the same 2 seconds for framework calls the helper does not own.

The boot splash may pass a longer cap into the helper (`ClientReadyMaxWaitMs`). That wait stays under the splash.
