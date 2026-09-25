# Blazor circuit must not run blocking work

Circuit code is anything that runs or resumes on the Blazor `SynchronizationContext`: Razor lifecycle and event handlers, and `Harness.UI` methods they call (`DysonUiHost`, `DysonUiAgentSessionRuntimeConfigBuilder`, component code-behind).

That code must not call sync I/O or process APIs (`File.*` / `Directory.*` sync, `Process.Start`, sync git) and must not block (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep`, `lock` around I/O).

It must not await a method that does that work on the caller before the first real yield. `ConfigureAwait(false)` is not a yield. An already-completed await continues inline on the circuit.

Engine methods the UI awaits, when they can start a process, read config from disk, or do an MCP handshake, must put that work in `Task.Run` (or an existing fire-and-forget like `DysonCustomMcpPromptUpdater.EnqueueRefresh`) before doing it. UI session create/resume must not wait for that work. Cancel it from dispose, not from the request token the handler is about to drop.

`Task.Run` once, at that boundary. Do not wrap every engine call.
