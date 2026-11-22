namespace DysonHarness;

/// <summary>
/// Optional host hook fired after a slug default max target context is saved.
/// Arguments are the slug id and the normalized value (null = cleared).
/// LocalDb does not own the session bus; the UI registers the publisher.
/// </summary>
public sealed class DysonSlugDefaultMaxTargetContextChanged(Action<Guid, int?> publish)
{
    private readonly Action<Guid, int?> _publish = publish ?? throw new ArgumentNullException(nameof(publish));

    public void Invoke(Guid slugId, int? maxTargetContextTokens) => _publish(slugId, maxTargetContextTokens);
}
