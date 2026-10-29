using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// The work directory on screen for one subject (in-memory, one instance per retained subject scope).
/// Owns the owner gate for the root Meta Agent work-directory tools: only the meta session of
/// <see cref="ActiveId"/> may list, open the panel, or switch. UI pages bind <see cref="OpenPanel"/> and
/// <see cref="SwitchTo"/> while mounted.
/// </summary>
public sealed class DysonWorkDirectoryFocus(IDysonWorkDirectoryRepository workDirectories)
{
    public const string NotOwnerMessage =
        "Other work directory owns the application, you can ask the user in voice mode to change directories via MessageVoiceModeSidecar.";

    public const string PanelNotShownMessage = "The work directories panel is not on screen.";

    private readonly IDysonWorkDirectoryRepository _workDirectories =
        workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, string> _names = [];
    private Guid? _activeId;

    /// <summary>Work directory on screen; <c>null</c> means nobody owns the application.</summary>
    public Guid? ActiveId
    {
        get { lock (_gate) return _activeId; }
    }

    /// <summary>UI hook: open the existing work-directory modal. Bound by the mounted page.</summary>
    public Func<Task>? OpenPanel { get; set; }

    /// <summary>UI hook: select a work directory through the existing switcher. Bound by the mounted page.</summary>
    public Func<Guid, Task>? SwitchTo { get; set; }

    /// <summary>UI only. No ownership check. Does not touch voice.</summary>
    public void SetActive(Guid? id)
    {
        lock (_gate)
            _activeId = id is { } g && g != Guid.Empty ? g : null;
    }

    /// <summary>
    /// Current display name for <paramref name="id"/>; falls back to the last cached name, else <c>""</c>.
    /// </summary>
    public async Task<string> GetDisplayNameAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var get = await _workDirectories.GetAsync(id, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (get.IsSuccess)
            {
                _names[id] = get.Value.Name;
                return get.Value.Name;
            }

            return _names.TryGetValue(id, out var cached) ? cached : "";
        }
    }

    /// <summary>
    /// Live stored name for <paramref name="id"/>, or <c>null</c> when the row is gone or unreadable.
    /// Voice playback uses this at play time; the enqueue snapshot covers the <c>null</c> case.
    /// </summary>
    public async Task<string?> TryGetLiveNameAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var get = await _workDirectories.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (get.IsError)
            return null;
        lock (_gate)
            _names[id] = get.Value.Name;
        return get.Value.Name;
    }

    /// <summary>Owner gate: caller and active id are both non-empty and equal.</summary>
    public bool IsOwner(Guid callerWorkDirectoryId)
    {
        var active = ActiveId;
        return callerWorkDirectoryId != Guid.Empty && active is { } a && a == callerWorkDirectoryId;
    }

    /// <summary>JSON array of <c>{ id, name, path, active }</c> for the subject's work directories.</summary>
    public async Task<Result<string, string>> ListJsonAsync(
        Guid callerWorkDirectoryId,
        CancellationToken cancellationToken = default)
    {
        if (!IsOwner(callerWorkDirectoryId))
            return Result<string, string>.AsError(NotOwnerMessage);

        var list = await _workDirectories.ListAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsError)
            return Result<string, string>.AsError(list.Error);

        var active = ActiveId;
        lock (_gate)
        {
            foreach (var w in list.Value)
                _names[w.Id] = w.Name;
        }

        var rows = list.Value
            .Select(w => new { id = w.Id, name = w.Name, path = w.AbsolutePath, active = w.Id == active })
            .ToList();
        return Result<string, string>.AsValue(JsonSerializer.Serialize(rows));
    }

    /// <summary>Gate, then the bound <see cref="OpenPanel"/> hook.</summary>
    public async Task<VoidResult<string>> OpenPanelAsync(Guid callerWorkDirectoryId)
    {
        if (!IsOwner(callerWorkDirectoryId))
            return VoidResult<string>.AsError(NotOwnerMessage);

        var hook = OpenPanel;
        if (hook is null)
            return VoidResult<string>.AsError(PanelNotShownMessage);

        try
        {
            await hook().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // UI boundary: a disposed circuit or render failure becomes a tool error.
            return VoidResult<string>.AsError(PanelNotShownMessage, ex);
        }

        return VoidResult<string>.Success;
    }

    /// <summary>Gate, target must exist, hook must be bound, then <see cref="SetActive"/> and <see cref="SwitchTo"/>.</summary>
    public async Task<VoidResult<string>> SwitchAsync(
        Guid callerWorkDirectoryId,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        if (!IsOwner(callerWorkDirectoryId))
            return VoidResult<string>.AsError(NotOwnerMessage);

        var get = await _workDirectories.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (get.IsError)
            return VoidResult<string>.AsError(get.Error);

        var hook = SwitchTo;
        if (hook is null)
            return VoidResult<string>.AsError(PanelNotShownMessage);

        lock (_gate)
            _names[targetId] = get.Value.Name;
        SetActive(targetId);
        try
        {
            await hook(targetId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // UI boundary: a disposed circuit or render failure becomes a tool error.
            return VoidResult<string>.AsError(PanelNotShownMessage, ex);
        }

        return VoidResult<string>.Success;
    }
}
