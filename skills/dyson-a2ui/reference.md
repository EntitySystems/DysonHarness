# Dyson A2UI component reference

Full props per component (source of truth: `Harness.Engine/A2ui/DysonA2uiCatalog.cs`). Required props are **bold**. Every prop except literals (`href`, `url`, `xKey`, `key`, ids, bounded ints) may also be a `{"@path"}` binding. Cosmetic upstream props (`background`, `border`, `borderColor`, `radius`, `shadow`, `width`, `height`, `min*`/`max*`, `aspectRatio`, `color`, `fontWeight`, `textAlign`, `size` where unmapped, …) are accepted as scalars and ignored. `gap`/`padding` are ints 0..12 (steps of 4px).

## Layout

| Component | Props |
|---|---|
| Row, Column | **children**; justify, align, gap, padding |
| List | **children**; direction vertical/horizontal, align, gap, padding |
| Card | child or children (exactly one); gap, padding |
| Divider | axis horizontal/vertical |
| Box | **children**; direction col/row, gap, padding, align, justify, wrap |
| Grid | **children**; columns 1..12, minChildWidth 40..600 (px), gap, padding |
| GridItem, FlowItem | child or children; span 1..12, rowSpan 1..12 (only under Grid / Flow) |
| Flow | **children**; minChildWidth, gap, padding |
| Carousel | **children** (CarouselItem / CarouselMediaItem); visibleItems 1..6, activeIndex, gap |
| CarouselItem | child or children |
| Spacer | size 1..12 |
| Tabs | **tabs** [{**title**, **child**}] 1..12; selectedIndex |
| Section | **title**, **children**; eyebrow, description, gap |
| Accordion | **items** [{**title**, text?, child?}] 1..50; multiple |

## Text and content

| Component | Props |
|---|---|
| Text | **text**; variant caption/body/h1..h5 |
| Title | **text**; size xs/sm/md/lg/xl/2xl/3xl |
| Heading | **text**; level "1".."4", eyebrow, subtitle |
| Caption | **text** |
| Label | **text**; for |
| Bold, Italic, Underline, Strikethrough, ShimmerText | **text** |
| Badge | **text**; variant soft/solid/outline, color (tone), size sm/md/lg |
| Blockquote, Quote | **text**; author, source, attribution, cite |
| Code, CodeBlock | code or text; language, title (no syntax colours) |
| Kbd | **keys** (≤8); label |
| Markdown | text or content (raw HTML shown as text, links inert with the URL shown, images as alt text) |
| Math | text or tex; display (TeX source shown, not typeset) |
| Link | **text**, **href** (link target) |
| Tooltip | **child**; text or tip |
| VideoEmbed | **url** (link target); title, caption, thumbnail (media) |
| ImageGallery, ImageGrid | images or items [{url?, caption?, alt?, title?, description?}] ≤50; columns 1..6 |

## Inputs

| Component | Props |
|---|---|
| Button | **child**, **action**; variant default/primary/borderless (question-bound surface only) |
| Pressable | **child**, **action** (question-bound surface only) |
| Form | **children**; title, gap (no `<form>`; submit is a Button inside) |
| TextField | **label**, **value**; placeholder, variant shortText/longText/number/obscured |
| TextArea | **label**, **value**; placeholder, rows 2..12 |
| CheckBox | **label**, **value** (bool) |
| ChoicePicker | **options** [{label, value}], **value** (string list); label, variant, displayStyle |
| Select, RadioGroup, SegmentedControl | **options** (strings or {**value**, label?, description?}, 1..50), **value** (string); label; Select also placeholder |
| Radio | **label**, **value** (group binding), **option** (literal) |
| Slider | **value** (number); label, min, max, step, unit, marks [{**value**, label?}] ≤12 |
| DateTimeInput | **value** (ISO string); label, enableDate, enableTime, min, max (ISO literals) |

Inputs accept `checks` as an alias of `@checks`.

## Data display

| Component | Props |
|---|---|
| ListItem | **label**; description, value, icon, children |
| Table | columns (strings or {label?, align?, componentId?}) ≤20 + rows (cells: text, number or {componentId}) ≤200, or children (TableSection/TableRow); cellComponents, caption, emptyLabel |
| TableSection | **children**; header |
| TableRow | **children** (TableCell); label, header |
| TableCell | text or child; colSpan, rowSpan 1..20 |
| DataTable | **columns** [{**key**, **label**, format number/currency/percent/compact/date, align}], **rows** (objects) ≤200; title, searchable, pageSize 1..100 |
| ComparisonTable | **options** (≤8 names), **rows** [{**feature**, **values**}] ≤50; title, highlight 0..7 |
| Stat | **label**, **value**; delta, trend up/down/flat, caption, tone, icon |
| StatGroup | **items** [{**label**, **value**, delta?, trend?, tone?, caption?, icon?}] 1..12; title |
| Avatar | **name**; image (media, not loaded), subtitle, size, status online/away/offline |
| AvatarGroup | **people** [{**name**, image?}] ≤50; max 1..20, label |
| KeyValueList | **items** [{**label**, **value**, hint?}] ≤100; title, columns 1..3 |
| Timeline | **items** [{**title**, time?, description?, status?, icon?}] ≤100; title |
| Progress | **value** 0..100; label, caption, tone |
| RadialProgress | **value** 0..100; label, display, tone |
| Rating | **value** 0..5; count, label |
| TagList | **tags** (≤50); tone |
| Agenda | **days** [{**date**, label?, events [{time?, **title**, description?, location?}] ≤50}] ≤31; title |
| Map | **markers** [{**label**, **lat**, **lng**, category?, description?}] ≤100; route, selected, title (place list + relative plot, no tiles) |

Tones: default, positive, negative, warning, info, accent, secondary, success, danger, discovery.

## Charts

| Component | Props |
|---|---|
| Chart | **data**; chartType bar/line/area/pie/donut, xKey, series (names or {**key**, label?}), stacked, layout vertical/horizontal, showDots |
| BarChart | **data**; stacked, layout |
| LineChart | **data**; showDots |
| AreaChart | **data**; stacked |
| PieChart | **data** [{label, value}] |
| Sparkline | **data** |
| Gauge | **value**; min, max, label, unit, tone, format |

All charts also take title, subtitle, height 120..480, format number/currency/percent/compact, unit, showLegend. Caps: 8 series, 500 points per series, 2000 total, 24 slices.

## Media and composites

| Component | Props |
|---|---|
| Image | exactly one of **url** (media, placeholder, never loaded) or **path** (work-relative png/jpg/gif/webp/bmp, shown); height 120..800; description, fallback, alt, caption, variant icon/avatar/small/medium/large/header |
| Icon | **name** (built-in set: check, x, plus, minus, arrow-up/down/left/right, chevron-right, info, warning, error, star, star-half, clock, calendar, user, file, folder, link, external-link, search, settings, git-branch, play, pause); label |
| Favicon | url or domain (first letter shown) |
| Video | **url** (link target); title, caption (link card) |
| AudioPlayer | exactly one of **url** (link target; http(s) plays on click, otherwise a link card) or **path** (work-relative audio, played); title, caption |
| CarouselMediaItem | url (media); caption, alt, title, description |
| LinkCard | **url** (link target), **title**; description, image, favicon, siteName |
| Citation | label, index, title, snippet, source, url (link target) |
| EntityCard | **title**; subtitle, description, meta, badge, rating, image, href (link target), actions [{**label**, **href**}] ≤3 |

## Overlay and feedback

| Component | Props |
|---|---|
| PulseIndicator | tone, label |
| FlashcardFlip | **front**, **back**; hint |
| Popover | **children** = [PopoverTrigger, PopoverContent]; open |
| PopoverTrigger, PopoverContent | child or children (only under Popover) |
| Modal | **trigger**, **content** (component ids); title, maxWidth 240..960, open |

## Dyson catalog

| Component | Props |
|---|---|
| ActionButton | **label**, **func** (open_plan/open_scratch/open_file/open_folder/open_url) |
| QuestionSlot | **questionId** |
| LocalVideo | **path** (work-relative); title |
| Model3D | **path** (work-relative `.glb` / `.fbx`); title; height 160..800 (default 320) |
| PipelineRun | exactly one of runId (integer ≥1) or jobIds (1..30 distinct integers); literals only |
| TreeDiagram | **nodes** (nested `{id, title, text?, tags?[{label, color red\|green\|accent}] ≤6, children?}`; ≤100 nodes, depth ≤12, unique ids, literals only); flow vertical\|horizontal; title |
| SubagentTasks | **agentId** (integer ≥1, literal; your direct child). Row opens the agent's session; a "Show N agents" button expands its own sub-agents (host-filled, no props) |

Link targets: literal `http(s)` URL or `open_plan:`/`open_scratch:`/`open_file:`/`open_folder:`; `mailto:`, `javascript:`, `data:` and relative URLs are rejected. Media URLs: literal `http(s)`, at most 2048 chars, no `data:`.
