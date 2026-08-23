using Harness.UI.Components.Models;

namespace Harness.Tests;

/// <summary>
/// ponytail: composer never auto-writes from refresh; settings may clear a missing id.
/// </summary>
public class ModelSlugPickerSelectionTests
{
    [Fact]
    public void Composer_null_selected_does_not_write()
    {
        var wroteUnlisted = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: null,
            allowEmpty: false,
            selectedIsListed: false,
            out var writeUnlisted);
        Assert.False(wroteUnlisted);
        Assert.Null(writeUnlisted);

        var wroteListed = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: null,
            allowEmpty: false,
            selectedIsListed: true,
            out var writeListed);
        Assert.False(wroteListed);
        Assert.Null(writeListed);
    }

    [Fact]
    public void Composer_selected_not_listed_does_not_write()
    {
        var wrote = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: Guid.NewGuid(),
            allowEmpty: false,
            selectedIsListed: false,
            out var write);
        Assert.False(wrote);
        Assert.Null(write);
    }

    [Fact]
    public void Composer_empty_options_does_not_write()
    {
        var wrote = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: Guid.NewGuid(),
            allowEmpty: false,
            selectedIsListed: false,
            out var write);
        Assert.False(wrote);
        Assert.Null(write);
    }

    [Fact]
    public void Settings_selected_not_listed_writes_null()
    {
        var wrote = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: Guid.NewGuid(),
            allowEmpty: true,
            selectedIsListed: false,
            out var write);
        Assert.True(wrote);
        Assert.Null(write);
    }

    [Fact]
    public void Settings_selected_listed_does_not_write()
    {
        var wrote = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: Guid.NewGuid(),
            allowEmpty: true,
            selectedIsListed: true,
            out var write);
        Assert.False(wrote);
        Assert.Null(write);
    }

    [Fact]
    public void Settings_selected_already_null_does_not_write()
    {
        var wrote = ModelSlugPickerSelection.TryGetAutoWrite(
            selectedSlugId: null,
            allowEmpty: true,
            selectedIsListed: false,
            out var write);
        Assert.False(wrote);
        Assert.Null(write);
    }
}
