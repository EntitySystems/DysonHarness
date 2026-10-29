using DysonHarness;

namespace Harness.Tests;

/// <summary>Owner gate for the shared work-directory focus. No UI and no voice.</summary>
public class DysonWorkDirectoryFocusTests
{
    [Fact]
    public async Task Non_owner_has_no_side_effect()
    {
        var work = new FakeWorkDirectories();
        var focus = new DysonWorkDirectoryFocus(work);
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        var opened = 0;
        var switched = 0;
        focus.SetActive(active);
        focus.OpenPanel = () =>
        {
            opened++;
            return Task.CompletedTask;
        };
        focus.SwitchTo = _ =>
        {
            switched++;
            return Task.CompletedTask;
        };

        var list = await focus.ListJsonAsync(other);
        var open = await focus.OpenPanelAsync(other);
        var switchedResult = await focus.SwitchAsync(other, Guid.NewGuid());

        Assert.Equal(DysonWorkDirectoryFocus.NotOwnerMessage, list.Error);
        Assert.Equal(DysonWorkDirectoryFocus.NotOwnerMessage, open.Error);
        Assert.Equal(DysonWorkDirectoryFocus.NotOwnerMessage, switchedResult.Error);
        Assert.Equal(0, opened);
        Assert.Equal(0, switched);
        Assert.Equal(0, work.Gets);
        Assert.Equal(0, work.Lists);
        Assert.Equal(active, focus.ActiveId);

        focus.SetActive(null);
        var deniedList = await focus.ListJsonAsync(active);
        var deniedOpen = await focus.OpenPanelAsync(active);
        var deniedSwitch = await focus.SwitchAsync(active, other);
        Assert.Equal(DysonWorkDirectoryFocus.NotOwnerMessage, deniedList.Error);
        Assert.Equal(DysonWorkDirectoryFocus.NotOwnerMessage, deniedOpen.Error);
        Assert.Equal(DysonWorkDirectoryFocus.NotOwnerMessage, deniedSwitch.Error);
        Assert.Equal(0, opened);
        Assert.Equal(0, switched);
        Assert.Equal(0, work.Gets);
        Assert.Equal(0, work.Lists);
        Assert.Null(focus.ActiveId);
    }

    [Fact]
    public async Task Owner_switch_sets_active_and_calls_hook()
    {
        var work = new FakeWorkDirectories();
        var active = Guid.NewGuid();
        var target = Guid.NewGuid();
        work.Add(new DysonWorkDirectoryEntity
        {
            Id = target,
            Name = "Harness",
            AbsolutePath = "E:\\Harness",
        });

        var focus = new DysonWorkDirectoryFocus(work);
        focus.SetActive(active);
        Guid? hooked = null;
        focus.SwitchTo = id =>
        {
            hooked = id;
            focus.SetActive(id); // what the page's switcher callback does once the screen moves
            return Task.CompletedTask;
        };

        var switched = await focus.SwitchAsync(active, target);
        Assert.True(switched.IsSuccess);
        Assert.Equal(target, hooked);
        Assert.Equal(target, focus.ActiveId);

        var quiet = new DysonWorkDirectoryFocus(work);
        quiet.SetActive(active);
        var blocked = await quiet.SwitchAsync(active, target);
        Assert.Equal(DysonWorkDirectoryFocus.PanelNotShownMessage, blocked.Error);
        Assert.Equal(active, quiet.ActiveId);
    }

    [Fact]
    public async Task Switch_whose_hook_did_not_move_the_screen_fails_and_keeps_active()
    {
        var work = new FakeWorkDirectories();
        var active = Guid.NewGuid();
        var target = Guid.NewGuid();
        work.Add(new DysonWorkDirectoryEntity { Id = target, Name = "RealStrategy", AbsolutePath = "E:\\RS" });

        var focus = new DysonWorkDirectoryFocus(work);
        focus.SetActive(active);
        var hooked = 0;
        focus.SwitchTo = _ =>
        {
            hooked++; // switcher locked or navigation failed: ActiveId is never moved
            return Task.CompletedTask;
        };

        var switched = await focus.SwitchAsync(active, target);
        Assert.Equal(DysonWorkDirectoryFocus.SwitchNotAppliedMessage, switched.Error);
        Assert.Equal(1, hooked);
        Assert.Equal(active, focus.ActiveId);
    }

    internal sealed class FakeWorkDirectories : IDysonWorkDirectoryRepository
    {
        public Dictionary<Guid, DysonWorkDirectoryEntity> Items { get; } = [];
        public int Gets;
        public int Lists;

        public void Add(DysonWorkDirectoryEntity entity) => Items[entity.Id] = entity;

        public Task<Result<Guid, string>> CreateAsync(
            string absolutePath, string? name = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Guid, string>.AsError("not used"));

        public Task<Result<DysonWorkDirectoryEntity, string>> GetAsync(
            Guid id, CancellationToken cancellationToken = default)
        {
            Gets++;
            return Task.FromResult(
                Items.TryGetValue(id, out var entity)
                    ? Result<DysonWorkDirectoryEntity, string>.AsValue(entity)
                    : Result<DysonWorkDirectoryEntity, string>.AsError($"Work directory '{id}' not found."));
        }

        public Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            Lists++;
            return Task.FromResult(
                Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsValue(Items.Values.ToList()));
        }

        public Task<VoidResult<string>> TouchOpenedAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));

        public Task<VoidResult<string>> UpdateGitMetadataAsync(
            Guid id, string? gitOrigin, string? gitProvider, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));

        public Task<VoidResult<string>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));
    }
}
