# UI CSS lives in isolation files

New component CSS goes in `<Component>.razor.css` next to the component, not in [`wwwroot/app.css`](../src/Harness/Harness.UI/wwwroot/app.css). Scoped CSS is already wired — `Harness.UI.styles.css` is linked from [`App.razor`](../src/Harness/Harness.UI/Components/App.razor), so adding the file is the whole setup.

Reason: `app.css` is a ~7k-line monolith with no component boundaries. Every new global rule makes the next selector collision more likely and the next delete riskier.

## Must stay global in `app.css`

- `:root` / `[data-theme="…"]` / `[data-accent="…"]` design tokens (radii, motion/easing, colors, shadows).
- `@keyframes`. Blazor does **not** rewrite keyframe names, so a scoped file leaks them globally anyway and two components can collide. Shared keyframes live in `app.css` with a `dyson-` prefix.
- Motion classes applied by a *consumer* component to its own markup (`.dyson-fade`, `.dyson-fade-scale`, `.dyson-pop`). The scope attribute belongs to the component that writes the markup, so the primitive's scoped file cannot reach it.
- Styles targeting `MarkupString` output (Markdig / ColorCode HTML under `.turn-block__body`). Raw markup never receives the `b-xxxxx` scope attribute.
- The blanket `@media (prefers-reduced-motion: reduce)` rule and its carve-outs.

## Writing scoped CSS

- `::deep` reaches into child-component markup rendered inside your element (`.panel ::deep .list-item { … }`). Without it the selector only matches elements your own `.razor` file declares.
- A scoped selector still needs the element to exist in *your* markup — `ChildContent` passed in from a parent carries the parent's scope.
- Keep using the shared tokens (`var(--motion-fast)`, `var(--ease)`, `var(--bg-2)`); isolation is about selector scope, not re-inventing theme values.

## Do not migrate as drive-by work

Per [`rules_ponytail.md`](rules_ponytail.md), do **not** bulk-move existing `app.css` blocks into `.razor.css` files. Move a block only when you are already editing that component's styling, and move it whole rather than splitting rules across both files.
