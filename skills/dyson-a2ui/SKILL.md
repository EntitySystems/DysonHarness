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
- `ActionButton` / `QuestionSlot` / `LocalVideo` / `Model3D` / `PipelineRun` / `SubagentTasks` / `TreeDiagram` are Dyson catalog components. They work with `catalogId` omitted (or set to `a2ui://dyson/catalogs/v1/catalog.json`). Only an explicit basic surface `catalogId` or component `"@catalogId"` rejects them. No component needs `@catalogId`.
- The CopilotKit playground id `https://copilotkit.ai/a2ui/catalogs/playground/v1` is accepted as an alias of the Dyson catalog.

## 4. Kit

Required props in **bold**. Full props per component: [reference.md](reference.md) (`LoadSkill("skills/dyson-a2ui", loadIndexOnly:false)`). An unknown-prop error lists the accepted props.

| Group | Components (required props) |
|---|---|
| Layout | `Row`/`Column`/`List` (**children**; justify, align, gap/padding 0..12), `Card` (child or children), `Divider`, `Box` (**children**; direction), `Grid` (**children**; columns 1..12 or minChildWidth), `GridItem`/`FlowItem` (span), `Flow`, `Carousel` (**children**), `CarouselItem`, `Spacer`, `Tabs` (**tabs** [{title, child}]), `Section` (**title**, **children**), `Accordion` (**items** [{title, text?, child?}]) |
| Text | `Text` (**text**; variant caption/body/h1..h5), `Title`, `Heading` (**text**; level, eyebrow, subtitle), `Caption`, `Label`, `Bold`, `Italic`, `Underline`, `Strikethrough`, `Badge`, `Blockquote`/`Quote`, `Code`/`CodeBlock` (code), `Kbd` (**keys**), `Markdown` (text; links inert, images as alt text), `Math` (TeX source shown), `ShimmerText`, `Link` (**text**, **href**), `Tooltip` (**child**, text) |
| Inputs | `Button` (**child**, **action**), `TextField`, `TextArea`, `CheckBox`, `ChoicePicker`, `Select`/`RadioGroup`/`SegmentedControl` (**options**, **value**), `Radio` (**label**, **value**, **option**), `Slider` (**value**; min/max/step), `DateTimeInput` (**value**), `Pressable` (**child**, **action**), `Form` (**children**); `checks` = `@checks` |
| Data | `ListItem` (**label**), `Table` (columns + rows, or TableSection/TableRow/TableCell children), `DataTable` (**columns** [{key, label, format?}], **rows**), `ComparisonTable` (**options**, **rows** [{feature, values}]), `Stat` (**label**, **value**; delta, trend, tone), `StatGroup` (**items**), `KeyValueList`/`Timeline` (**items**), `Progress`/`RadialProgress`/`Rating`/`Gauge` (**value**), `TagList` (**tags**), `Avatar` (**name**), `AvatarGroup` (**people**), `Agenda` (**days**), `Map` (**markers** [{label, lat, lng}]) |
| Charts | `Chart` (**data**; chartType, xKey, series), `BarChart`, `LineChart`, `AreaChart`, `PieChart`, `Sparkline` (**data**) — section 4b |
| Media | `Image` (exactly one of **url** / **path**; height 120..800), `Icon` (**name**), `Favicon`, `Video`/`AudioPlayer`/`VideoEmbed` (**url**), `ImageGallery`/`ImageGrid` (images), `CarouselMediaItem`, `LinkCard` (**url**, **title**), `Citation`, `EntityCard` (**title**) — section 4c |
| Overlay | `Popover` (children = [PopoverTrigger, PopoverContent]), `Modal` (**trigger**, **content**), `FlashcardFlip` (**front**, **back**), `PulseIndicator` |
| Dyson | `ActionButton` (**label**, **func**), `QuestionSlot` (**questionId**), `LocalVideo` (**path**; title), `Model3D` (**path**; title, height 160..800), `PipelineRun` (exactly one of **runId** integer = the whole run with its jobs, or **jobIds** 1..30 integers = only those jobs; get ids from `ListPipelineRuns`, the host fills in the rest), `SubagentTasks` (**agentId** integer = a drone/explore/review's agentId; the host fills the title, the X/N todo pill and the running state, live), `TreeDiagram` (**nodes** nested `{id, title, text?, tags?[{label, color red|green|accent}], children?}`; flow vertical|horizontal; title) |

`weight` on direct children of Row/Column/Box/GridItem; `@accessibility` on any component. Parent rules: GridItem under Grid, FlowItem under Flow, CarouselItem under Carousel, TableRow under Table/TableSection, TableCell under TableRow. `Pressable`, like `Button`, needs a question-bound surface.

Rejected: `Svg`, `HtmlView`, Icon `svgPath` (no model markup), `Treemap`, `SankeyChart`, Button/Pressable `href` (use Link), `onVisible`/`onClick`/`onAction`, `functionCall`. Not yet: `FollowUp` (use PostUserQuestion choices), `ScatterChart`, `Heatmap`, `RadarChart`, `CandlestickChart`, `ListItem.action`.

## 4b. Charts

- Inline SVG drawn by Dyson. `chartType` bar/line/area/pie/donut; `BarChart`, `LineChart`, `AreaChart`, `PieChart`, `Sparkline` are the same with a fixed kind; `Gauge {value, min, max}`.
- Standard shape: `data` = flat objects, `xKey` = category field, `series` = field names or `{key, label}` (default: every numeric field).
- Playground shape: `data` = `[{x, y, series?}]`; pie: `[{label, value}]`.
- Options: `stacked`, `layout` horizontal, `showDots`, `format` number/currency/percent/compact, `unit`, `title`, `subtitle`, `height` 120..480.
- Caps: 8 series, 500 points per series, 2000 in total, 24 slices. Summarise: keep it to about 300 points. Bound data over a cap is truncated with a note.

## 4c. Links and media

- `Link.href`, `LinkCard.url`, `Citation.url`, `EntityCard.href`/`actions[].href`, `Video`/`AudioPlayer`/`VideoEmbed.url`: literal `http(s)` URL (becomes `open_url:`) or `open_plan:`/`open_scratch:`/`open_file:`/`open_folder:`. They count toward the 8 actions per surface with ActionButtons (a PipelineRun counts 1).
- Remote media URLs (`Image.url`, `Avatar.image`, galleries) are validated but never loaded: a placeholder with the alt/caption text, initials for Avatar. Video and audio render as link cards. `Image.path` shows a work-directory png/jpg/gif/webp/bmp ≤ 20 MB with an expand-to-modal button (section 6b2c). Nothing in a surface fetches from the network.

Dashboard example:

```json
{"messages":[{"version":"1.0","createSurface":{"surfaceId":"revenue","components":[
{"id":"root","component":"Column","gap":4,"children":["head","kpis","trend","channels"]},
{"id":"head","component":"Heading","text":"Revenue","eyebrow":"Q4","subtitle":"Billing data"},
{"id":"kpis","component":"Grid","columns":3,"gap":4,"children":["mrr","churn","arpu"]},
{"id":"mrr","component":"Stat","label":"MRR","value":"$12k","delta":"+4%","trend":"up","tone":"positive"},
{"id":"churn","component":"Stat","label":"Churn","value":"1.2%","trend":"down","tone":"warning"},
{"id":"arpu","component":"Stat","label":"ARPU","value":"$48"},
{"id":"trend","component":"Chart","chartType":"area","xKey":"month","series":["web","ads"],"stacked":true,"format":"currency","data":[{"month":"Oct","web":7,"ads":3},{"month":"Nov","web":8,"ads":4}]},
{"id":"channels","component":"Table","columns":["Channel",{"label":"Share","align":"end"}],"rows":[["Web","62%"],["Ads","38%"]]}]}}]}
```

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

## 6b2. Local video (LocalVideo)

A card for a video file in the work directory: title, first-frame thumbnail, duration pill. A click opens the file viewer's video player. Formats are the file viewer's (`mp4`, `m4v`, `webm`, `mov`, `mkv`). Not for URLs, YouTube or Vimeo. A rooted or `..` path is `VALIDATION_FAILED`; a missing or unsupported file renders as card text.

```json
{"id":"demo","component":"LocalVideo","@catalogId":"a2ui://dyson/catalogs/v1/catalog.json","path":"media/demo.mp4","title":"Login flow demo"}
```

## 6b2b. 3D model (Model3D)

An inline three.js viewer for a `.glb` or `.fbx` file in the work directory: drag to rotate, full width of its parent, `height` 160..800 px (default 320). The expand icon opens a large view with zoom, pan and temperature / intensity light sliders (the inline view keeps its default light). Soft light from every direction, transparent background, no environment. Same path rules as LocalVideo: a rooted or `..` path is `VALIDATION_FAILED`; a missing, unsupported (`.gltf`, `.obj`), over 64 MB or over 2,000,000-triangle model renders as card text. Draco / Meshopt / KTX2-compressed GLBs and FBX external textures are not supported; animations are not played. At most 8 inline viewers per page.

```json
{"id":"chair","component":"Model3D","@catalogId":"a2ui://dyson/catalogs/v1/catalog.json","path":"models/chair.glb","title":"Chair","height":360}
```

## 6b2c. Local image (Image.path)

`Image` takes exactly one of `url` (remote, placeholder only, never loaded) or `path` (a file in the work directory, shown). Formats: png, jpg/jpeg, gif, webp, bmp, at most 20 MB (no svg, tiff, ico). Width is 100% of the parent; `height` 120..800 px sets the box (default by `variant`: small 120, medium 240, header 200, large 400); the image is fitted, not cropped. An expand icon on the image opens it large in a modal (Esc, × or a click outside closes; the modal can open the file viewer). Alt text is the first of `alt`, `description`, `fallback`, else the file name; `caption` shows under it. Same path rules as LocalVideo: a rooted or `..` path is `VALIDATION_FAILED`; a missing, unsupported or oversized file renders as card text. To show a web image, download it into the work directory first.

```json
{"id":"shot","component":"Image","path":"docs/img/login.png","alt":"Login screen","caption":"After the fix","height":360}
```

## 6b3. Subagent tasks (SubagentTasks)

One live row per agent: title, `#id`, and an `X/N` pill (todos complete / all todos of that agent). Pass the `agentId` from `StartAsyncMetaAgentDrone` / `ListMetaAgentDrones` (your direct children only). `0/0` means no todos yet. A running agent shows the roster spinner and status color. An unknown id or a stopped agent renders as card text, not an error.

```json
{"id":"t1","component":"SubagentTasks","agentId":3}
```

## 6b4. Tree diagram (TreeDiagram)

Cards linked by lines, drawn by the host. `nodes` is the root array; each node nests its `children`, so a cycle or a missing parent cannot be written. A card shows `title`, up to 6 `tags` as pills (`color` is `red`, `green` or `accent`; omitted = `accent`; no other colors) and optional `text`. `flow` is `vertical` (default, top-down) or `horizontal` (left to right). Several roots lay out as siblings. Caps: 100 nodes, depth 12, 6 tags per card, unique node `id`s (`VALIDATION_FAILED` at the duplicate). Node strings (`id`, `title`, `text`, tag `label`) and the `nodes`/`children`/`tags` arrays are literal only (no `@path`); the component `title` may be bound. Cards are a fixed size: long titles ellipsize and text clamps to 2 lines. `"nodes":[]` renders as the card text "Empty tree.". The whole tree enters once; a wide tree scrolls inside its card (use `horizontal` for wide, shallow trees).

```json
{"id":"arch","component":"TreeDiagram","flow":"vertical","title":"Service map","nodes":[
 {"id":"api","title":"API gateway","text":"Routes all traffic","tags":[{"label":"live","color":"green"}],"children":[
  {"id":"auth","title":"Auth","tags":[{"label":"degraded","color":"red"}]},
  {"id":"orders","title":"Orders","text":"Postgres + queue","tags":[{"label":"new","color":"accent"}],"children":[
   {"id":"billing","title":"Billing"}]}]}]}
```

## 6c. Actions (ActionButton)

- `func`: `open_plan:{planId}`, `open_scratch:{id}`, `open_file:{path}` (work-relative), `open_folder:{path}` (work-relative folder; opens it in the OS file manager; a missing path or a file shows an error), `open_url:{url}` (http/https); same keys as PostConversationMessage actions. Max 8 per surface; fine on unbound surfaces.
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

No HTML, script, styles or fetched URLs: text is plain text, unknown props (`html`, `style`, …) are rejected, cosmetic props (`background`, `radius`, `width`, `color`, …) are accepted and ignored, no `openUrl`. Only `ActionButton` `func` and the link props in section 4c open things.
