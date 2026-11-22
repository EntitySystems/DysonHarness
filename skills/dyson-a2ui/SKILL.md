---
name: dyson-a2ui
description: >-
  Dyson A2UI v1.0 for the Meta Agent: when to call RenderA2ui, the JSON
  envelope, the Dyson kit, bindings, question forwarding (questionId),
  QuestionSlot embedding and ActionButton actions. Meta Agent sessions only.
---

# Dyson A2UI (RenderA2ui)

## 1. When to use it

- Use `RenderA2ui` often: whenever a component would clarify or structure what you show (status of several drones, a plan summary with links, a comparison, a checklist, a form).
- Use plain `PostConversationMessage` for a single sentence or small talk.
- Use `PostUserQuestion` for a question. The host already draws its card as A2UI.
- With a surface, put the actions in the surface as `ActionButton`s (section 6c), not in PostConversationMessage `actions`.

## 2. Envelope

`messages` is a JSON array (1..20). Each item is `{"version":"1.0", <one key>}`. The key is one of `createSurface`, `updateComponents`, `updateDataModel`, `deleteSurface`. JSON only: no markdown, HTML or code fences. All messages in one call target the same `surfaceId`; one call is all-or-nothing.

## 3. Surface model

- Stable UAX#31 `surfaceId` (e.g. `deploy_form`; no `dyson_` prefix).
- `createSurface {surfaceId, catalogId?, components, dataModel?}` once; exactly one `"id":"root"`, every child id must exist.
- Then `updateComponents {surfaceId, components}` (same id replaces) / `updateDataModel {surfaceId, path?, value}` (RFC 6901, default `/`; `null` deletes). Re-create is `INTEGRITY_ERROR`; `deleteSurface {surfaceId}` drops it.
- `ActionButton` / `QuestionSlot` need the Dyson catalog: surface `catalogId` or component `"@catalogId"` = `a2ui://dyson/catalogs/v1/catalog.json`. Otherwise omit `catalogId` (basic). In a later `updateComponents`, put `"@catalogId"` on each Dyson component itself (the surface `catalogId` is not carried over).

## 4. Kit

| Component | Props (required **bold**) |
|---|---|
| `Text` | **`text`**; `variant` caption/body |
| `Row`, `Column` | **`children`**; `justify` start/center/end/spaceAround/spaceBetween/spaceEvenly/stretch; `align` start/center/end/stretch |
| `List` | **`children`**; `direction` vertical/horizontal; `align` |
| `Card` | **`child`** |
| `Divider` | `axis` horizontal/vertical |
| `Button` | **`child`** (a Text id), **`action`**; `variant` default/primary/borderless; `@checks` |
| `TextField` | **`label`**, **`value`**; `placeholder`; `variant` shortText/longText/number/obscured; `@checks` |
| `CheckBox` | **`label`**, **`value`** (bool); `@checks` |
| `ChoicePicker` | **`options`** `[{label, value}]`, **`value`** (string list); `label`; `variant` mutuallyExclusive/multipleSelection; `displayStyle` checkbox/chips; `@checks` |
| `ActionButton` (Dyson) | **`label`**, **`func`** (literals) |
| `QuestionSlot` (Dyson) | **`questionId`** (literal Guid) |

`weight` only on direct children of Row/Column; `@accessibility` on any component. Not available: Tabs, Modal, Image, Icon, Video, AudioPlayer, Slider, DateTimeInput, `filterable`, `functionCall`, `sendDataModel`, `metadata`.

## 5. Bindings

- `{"@path":"/abs"}`; templates `{"componentId":"tpl","path":"/items"}` use relative paths (`{"@path":"name"}`).
- Inputs write their `value` path locally; data reaches you only through a Button action.
- Functions: `formatString` (`${/path}` placeholders only), `required`, `length` (`min`/`max`), `email`, `not`, as `{"@call":"required","args":{"value":{"@path":"/sel"}}}`.
- `"@checks":[{"condition":<call>,"message":"Pick one."}]` (max 8). A failing check disables a Button.

## 6. Question forwarding (questionId)

1. `PostUserQuestion` (its `choices` are the canonical values) → `questionId`.
2. `RenderA2ui` with `questionId` and a form. The submit Button `action.event.context` carries `selected` (string or list, equal to choices) and optional `custom` (string). Other keys become JSON custom text when custom answers are allowed.
3. End the turn. The answer arrives as the usual question-answer prompt.

Button `action` needs a question-bound surface. Example (choices `["staging","prod"]`, custom allowed):

```json
{"questionId":"3f2b8c1e-0d4a-4c5e-9a7b-1e2f3a4b5c6d","messages":[{"version":"1.0","createSurface":{"surfaceId":"deploy_form","components":[
{"id":"root","component":"Column","children":["target","note","submit"]},
{"id":"target","component":"ChoicePicker","label":"Target","variant":"mutuallyExclusive","options":[{"label":"Staging","value":"staging"},{"label":"Production","value":"prod"}],"value":{"@path":"/sel"}},
{"id":"note","component":"TextField","label":"Notes","variant":"longText","value":{"@path":"/note"}},
{"id":"submit","component":"Button","variant":"primary","child":"submit_label","action":{"event":{"name":"answer","context":{"selected":{"@path":"/sel"},"custom":{"@path":"/note"}}}},"@checks":[{"condition":{"@call":"required","args":{"value":{"@path":"/sel"}}},"message":"Pick a target."}]},
{"id":"submit_label","component":"Text","text":"Deploy"}],
"dataModel":{"sel":[],"note":""}}}]}
```

## 6b. Embedding a question (QuestionSlot)

The host draws every question card; rebuild it only for a different form (section 6). To place it in your layout, `PostUserQuestion`, then `RenderA2ui` without `questionId`:

```json
{"messages":[{"version":"1.0","createSurface":{"surfaceId":"review","components":[
{"id":"root","component":"Column","children":["intro","q"]},
{"id":"intro","component":"Text","text":"Review done. One decision left:"},
{"id":"q","component":"QuestionSlot","@catalogId":"a2ui://dyson/catalogs/v1/catalog.json","questionId":"3f2b8c1e-0d4a-4c5e-9a7b-1e2f3a4b5c6d"}]}}]}
```

`questionId` is the literal id (no `@path`), one embed per question. Unknown, closed or SecureData ids and duplicates (same surface or another) are `VALIDATION_FAILED`. The standalone card hides while embedded.

## 6c. Actions (ActionButton)

- `func`: `open_plan:{planId}`, `open_scratch:{id}`, `open_file:{path}` (work-relative), `open_url:{url}` (http/https); same keys as PostConversationMessage actions. Max 8 per surface; fine on unbound surfaces.
- PostConversationMessage `actions` only for a plain message with no surface. Never both: a rendered surface hides the turn's inline pills.

Status card example:

```json
{"message":"Build status","messages":[{"version":"1.0","createSurface":{"surfaceId":"build_status","catalogId":"a2ui://dyson/catalogs/v1/catalog.json","components":[
{"id":"root","component":"Card","child":"body"},
{"id":"body","component":"Column","children":["title","drones","links"]},
{"id":"title","component":"Text","text":"Plan 362: build status"},
{"id":"drones","component":"List","children":{"componentId":"drone_row","path":"/drones"}},
{"id":"drone_row","component":"Text","variant":"caption","text":{"@call":"formatString","args":{"value":"${name}: ${state}"}}},
{"id":"links","component":"Row","children":["open_plan","open_spec"]},
{"id":"open_plan","component":"ActionButton","label":"Plan 362","func":"open_plan:362"},
{"id":"open_spec","component":"ActionButton","label":"A2UI spec","func":"open_url:https://a2ui.org/"}],
"dataModel":{"drones":[{"name":"Engine","state":"done"},{"name":"UI","state":"building"}]}}}]}
```

Update it later with `updateDataModel {"surfaceId":"build_status","path":"/drones/1/state","value":"done"}`.

## 7. Errors

An invalid call returns `{"version":"1.0","error":{"code":"VALIDATION_FAILED","surfaceId","path","message"}}`; `path` points into `messages` (e.g. `/messages/0/createSurface/components/3/text`). Fix it and resend with the same `surfaceId`; the last good render stays.

## 8. Security

No HTML, script, styles or URLs: `Text` is plain text, unknown props (`html`, `url`, `style`, …) are rejected, no `openUrl`. Only `ActionButton` `func` opens things.
