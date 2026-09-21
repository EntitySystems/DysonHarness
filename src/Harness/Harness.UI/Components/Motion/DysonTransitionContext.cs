namespace Harness.UI.Components.Motion;

/// <summary>
/// Render context handed to <c>DysonTransition&lt;TItem&gt;</c> child content.
/// </summary>
/// <param name="Item">
/// The last non-null <c>Item</c>. Stays populated through the exit window, so content can keep
/// rendering after the owning service state is already null.
/// </param>
/// <param name="StateClass">
/// <c>is-entering</c> / <c>is-entered</c> / <c>is-exiting</c> — append next to a shared motion
/// class (<c>dyson-fade</c>, <c>dyson-fade-scale</c>, <c>dyson-pop</c>) in <c>app.css</c>.
/// </param>
/// <param name="IsExiting">
/// True while the exit window runs. The item is already gone logically, so suppress commands
/// (clicks, submits) rather than acting on stale state.
/// </param>
public sealed record DysonTransitionContext<TItem>(TItem Item, string StateClass, bool IsExiting);
