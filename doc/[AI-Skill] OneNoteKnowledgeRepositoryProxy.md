# [AI Skill] OneNoteKnowledgeRepositoryProxy

## 1. Abstract and motivation

`OneNoteKnowledgeRepositoryProxy` projects SharePoint-hosted Microsoft OneNote content into the provider-neutral `IKnowledgeRepository` contract.

The implementation is intentionally not a thin Graph API wrapper. Its purpose is to turn OneNote's notebook/section/page/object model into a deterministic hierarchical knowledge model that can be consumed by aggregators, caches, browser UIs, Joplin/WebDAV adapters, search implementations, and AI-oriented knowledge tooling without exposing OneNote-specific storage conventions.

The implementation differs positively from naive OneNote integrations in several important ways:

- It exposes OneNote through the same logical area/resource contract as unrelated providers.
- It treats OneNote pages as hierarchical documents rather than opaque HTML blobs.
- H1-H6 elements become addressable child areas.
- It separates direct content from aggregated content.
- It preserves binary resources through opaque `knowledge-resource:` identities.
- It translates known OneNote internal links into provider-neutral `knowledge-area:` links.
- External links remain external browser links.
- Mutations use targeted Graph PATCH operations instead of destructive full-page Markdown round-trips.
- Reads use a local tree/page snapshot and avoid unnecessary repository-wide Graph discovery.
- The provider filters known OneNote implementation artifacts such as `*_onefiles`.
- OneNote presentation HTML is normalized before Markdown conversion.
- The implementation explicitly avoids leaking OneNote/Graph details into `IKnowledgeRepository`.

This document is normative for future maintenance of the implementation. Changes should preserve the semantics described here unless the corresponding requirement is explicitly revised.

---

# 2. Scope

This skill applies to:

```text
KnowledgeManagement.SmartStandards.Providers.OneNoteKnowledgeRepositoryProxy
```

Current source baseline documented here:

```text
OneNoteKnowledgeRepositoryProxy_UpdatedV2.cs
```

The provider implements:

```text
IKnowledgeRepository
IDisposable
```

It currently uses:

- Microsoft Graph v1.0
- `HttpClient`
- `HtmlAgilityPack`
- `ReverseMarkdown`
- `Markdig`
- `Newtonsoft.Json`
- project logging via `DevLogger`

No `System.Text.Json` should be introduced.

No Windows-only interop or `DllImport` is required or desired.

---

# 3. Top-down usage examples

## 3.1 Basic read-only provider

Conceptually:

```csharp
IOneNoteGraphAuthenticationProvider authenticationProvider = GetAuthenticationProvider();

OneNoteKnowledgeRepositoryProxy repository = new OneNoteKnowledgeRepositoryProxy(
  oneNoteOrSiteUrl,
  authenticationProvider,
  true,
  ""
);

string[] notebooksOrChildren = repository.GetAreas(
  false,
  "/"
);
```

The default mode should remain read-only unless a caller intentionally opts into write support.

---

## 3.2 Mount one notebook as the provider root

When `notebookName` is configured, the logical provider root `/` represents that notebook directly.

Conceptually:

```text
/
├── Section A
├── Section B
└── Section Group
```

instead of:

```text
/
├── Notebook A
├── Notebook B
└── Notebook C
```

This is important when the provider is mounted below an `AggregatedKnowledgeRepository` mount point. Consumers should not have to see an unnecessary extra notebook level when the integration explicitly targets one notebook.

---

## 3.3 Page projection

OneNote:

```text
Page: Architecture

Preamble paragraph

H1 Runtime
  paragraph
  H2 Caching
    paragraph
H1 Deployment
```

Repository projection:

```text
/Page "Architecture"
├── /Runtime
│   └── /Caching
└── /Deployment
```

The page and headings are content containers.

The page owns only the preamble before the first heading as direct content.

A heading owns only the content after itself and before the next heading boundary that leaves its direct scope.

---

## 3.4 Provider-neutral links

OneNote links that can be resolved from already known repository state:

```text
onenote:...
```

are projected as:

```text
knowledge-area:/logical/path
```

External links remain:

```text
https://example.org/path
```

They must never be rewritten into local browser paths by the provider.

---

## 3.5 Resources

Embedded OneNote objects/images are exposed through opaque provider resource IDs and referenced from canonical Markdown as:

```markdown
![Architecture](knowledge-resource:<opaque-id>)
```

The public `ResourceId` must not expose Graph-native implementation details as a contract.

---

# 4. Logical OneNote-to-repository mapping

| OneNote concept | Repository concept | ContentLevel | Notes |
|---|---|---:|---|
| Provider root | `/` | `ContentAggregation` | Root can represent all notebooks or one configured notebook |
| Notebook | Structural aggregation area | `ContentAggregation` | Only exposed when no exact notebook is configured |
| Section group | Structural aggregation area | `ContentAggregation` | Can contain sections and nested section groups |
| Section | Aggregation area | `ContentAggregation` | Direct children are pages |
| Page | Document/content area | `ContentContainer` | Direct content is page preamble |
| H1-H6 | Hierarchical content area | `ContentContainer` | Child hierarchy follows heading levels |
| Image/object | `KnowledgeResourceInfo` | n/a | Referenced by `knowledge-resource:` |
| Known OneNote internal link | `knowledge-area:` reference | n/a | Resolution is cache/tree based |
| External HTTP(S) link | normal Markdown link | n/a | Must remain external |

`ContentAggregation` is a semantic promise, not merely “has children”. It must be used only where the complete content subtree is a meaningful OneNote unit.

---

# 5. Tree construction and lazy loading

## 5.1 `BuildTree`

`BuildTree()` creates or returns the process-local logical repository tree.

Important behavior:

- It is protected by `_SyncRoot`.
- It uses `_CachedTree`.
- It loads notebook metadata first.
- When a notebook name is configured, `/` becomes the configured notebook root.
- Otherwise `/` receives notebook children.
- Child expansion remains lazy.

The tree is a logical metadata projection. It must not eagerly download every page body.

---

## 5.2 `AreaNode`

`AreaNode` represents a logical knowledge area.

Important internal state includes:

- `NodeKind`
- logical `Path`
- display `Name`
- provider-native `NativeId`
- `PageId`
- heading `Fingerprint`
- `HeadingLevel`
- `ContentLevel`
- children
- `ChildrenLoaded`

The implementation deliberately retains provider-native IDs internally while exposing provider-neutral logical paths externally.

Do not make consumers parse `NativeId`, `PageId`, or Graph URLs.

---

## 5.3 Lazy child loading

Child loading depends on node type:

```text
Root / notebook
  -> sections + section groups

Section group
  -> direct sections + direct child section groups

Section
  -> page metadata only

Page
  -> page HTML, then H1-H6 projection

Heading
  -> hierarchy already established while projecting the page
```

A page enumeration must not implicitly download all page HTML merely to show page titles.

This separation is important for Graph request volume and for background-prefetch operation.

---

# 6. `_onefiles` artifacts

## 6.1 Mandatory filtering

Pages whose title ends with:

```text
_onefiles
```

must be excluded directly inside `LoadSectionChildren()`.

Comparison is case-insensitive.

Examples that must be hidden:

```text
Article_onefiles
ARTICLE_ONEFILES
Article_OneFiles
```

## 6.2 Why filtering belongs in the provider

Do not filter `_onefiles` in:

- the HTML UI,
- `AggregatedKnowledgeRepository`,
- the background cache,
- Joplin/WebDAV,
- search.

`_onefiles` is a OneNote/storage-specific projection artifact. It must never become a logical `AreaNode`.

Filtering at provider level ensures the artifact cannot leak into any downstream consumer.

## 6.3 Cache migration implication

If an older background cache already contains `_onefiles` children, changing only the provider filter is insufficient until the affected cached child enumeration is refreshed.

For the current change, the background cache format was intentionally incremented so previous structural projections are invalidated and rebuilt.

---

# 7. Heading projection

## 7.1 Heading hierarchy

`LoadHeadings()` scans:

```text
h1 ... h6
```

and constructs hierarchical child areas.

The nearest preceding lower heading level becomes the parent.

Example:

```text
H1 A
H3 B
H2 C
```

produces a hierarchy based on the nearest available parent level, with the page used as the fallback parent when no appropriate heading exists.

## 7.2 Empty headings

Empty heading text is normalized to:

```text
Untitled
```

A logical area must always have a usable name.

## 7.3 Heading identity

The provider uses:

- HTML object/element IDs where available,
- page identity,
- a heading fingerprint

to resolve heading nodes back into fresh OneNote page HTML for targeted mutations.

Do not use heading display text alone as identity because headings can be duplicated or renamed.

---

# 8. Direct content semantics

## 8.1 Page direct content

`GetPageDirectMarkdown()` returns only the page preamble:

```text
<body>
  content before first H1-H6
  H1 ...
</body>
```

Everything starting at the first projected heading belongs to child areas.

This prevents duplication when page content is aggregated recursively.

## 8.2 Heading direct content

`GetHeadingDirectMarkdown()` returns nodes following the heading until the next heading boundary.

It must not indiscriminately serialize the entire remaining page.

## 8.3 `HasDirectContent`

`HasDirectContent()` must correspond to the direct-content semantics above, not to “some descendant contains content”.

---

# 9. HTML to canonical Markdown conversion

## 9.1 Conversion pipeline

The read projection uses this conceptual order:

```text
OneNote HTML
    ↓
HtmlAgilityPack DOM
    ↓
OneNote-specific normalization
    ↓
resource placeholder extraction
    ↓
ReverseMarkdown conversion
    ↓
resource placeholder replacement
    ↓
residual BR normalization
    ↓
U+FFFC removal
    ↓
canonical knowledge Markdown
```

The order is significant.

---

## 9.2 Normalize presentation-only markup

`NormalizeOneNoteHtmlForMarkdown()` performs provider-specific cleanup before generic conversion.

Current normalization includes:

- projection-only attribute removal
- one-cell layout table unwrapping
- OneNote link normalization
- HTML list normalization
- HTML break normalization
- removal of empty presentation nodes

The authoritative OneNote DOM is not modified. The normalization operates on a temporary read projection.

---

## 9.3 Single-cell layout tables

OneNote frequently uses tables as layout containers.

A one-cell table should not automatically become a Markdown table because that produces artificial pipe syntax and can break contained lists.

Therefore one-cell presentation tables are unwrapped.

Real multi-cell tables remain tables.

---

## 9.4 Presentation-only attributes

Attributes such as editor/layout IDs, style, class, and selected `data-*` metadata are not part of canonical knowledge content.

They should not create noisy Markdown.

Do not remove semantically meaningful attributes required for links/resources.

---

# 10. The U+FFFC object replacement character

OneNote HTML can contain:

```text
U+FFFC OBJECT REPLACEMENT CHARACTER
```

visually rendered as:

```text
￼
```

This is often adjacent to embedded resources.

The actual resource is already projected independently as `knowledge-resource:`. Therefore U+FFFC is removed from the generated Markdown after the HTML-to-Markdown conversion.

This is not a UTF-8/Windows-1252 encoding repair.

Do not “fix” the problem by changing HTTP character encodings or by stripping arbitrary non-ASCII characters.

Only the known replacement placeholder is removed.

---

# 11. Link handling

## 11.1 Critical rule: exactly one HTML-to-Markdown conversion

An `<a>` element must not be replaced by Markdown syntax before `ReverseMarkdown` runs.

Bad pipeline:

```text
<a href="https://example">text</a>
    ↓ manually replace with text node
[text](https://example)
    ↓ ReverseMarkdown again
escaped/nested/broken Markdown
```

This was a real failure mode.

Correct pipeline:

```text
<a href="...">text</a>
    ↓ normalize href/label while keeping <a>
ReverseMarkdown
    ↓
[text](...)
```

---

## 11.2 External HTTP(S) links

External links must remain external.

Example:

```html
<a href="https://en.wikipedia.org/wiki/AOP">Wikipedia</a>
```

must become ordinary Markdown:

```markdown
[Wikipedia](https://en.wikipedia.org/wiki/AOP)
```

The provider must not convert this into:

```text
knowledge-area:
```

or a local application URL.

---

## 11.3 Internal OneNote links

Links beginning with:

```text
onenote:
```

are candidates for conversion to provider-neutral area references.

Resolution uses already materialized repository state.

If a page can be resolved:

```text
knowledge-area:/path/to/page
```

If an object/heading can also be resolved from already loaded page headings:

```text
knowledge-area:/path/to/page/heading
```

The resolver must not initiate repository-wide Graph discovery merely because content contains a hyperlink.

If the target cannot safely be resolved, retain the original link rather than inventing a logical area.

---

## 11.4 Nested textual Markdown inside an HTML anchor

Imported OneNote content may contain a visible label such as:

```text
[https://example.org](https://example.org)
```

inside an already existing HTML `<a>` element.

If left untouched, a converter can produce nested Markdown.

The implementation normalizes such a label only when the embedded target and the actual anchor target are equivalent.

Then:

```text
[https://example.org](https://example.org)
```

as visible label becomes:

```text
https://example.org
```

before the outer anchor is converted once.

Do not flatten arbitrary bracketed text that is not demonstrably a duplicate link label.

---

## 11.5 Angle-bracket source notation

Source content such as:

```text
<[http://example](http://example)>
```

must not be allowed to create double-escaped constructs such as:

```text
&lt;[*[...]
```

The provider's job is to produce syntactically valid canonical Markdown. The browser UI should then render that Markdown exactly once.

When debugging this class of issue, always inspect these three stages independently:

```text
Graph HTML
canonical Markdown returned by provider
HTML emitted by UI
```

Never assume the UI is at fault until the provider's canonical Markdown is verified.

---

# 12. Resource model

## 12.1 Opaque resource identity

Resources use provider-owned opaque IDs.

The public ID is derived from page/resource identity but consumers must treat it as opaque.

Use:

```text
knowledge-resource:<ResourceId>
```

inside repository-facing Markdown.

## 12.2 Resource enumeration

`GetResources(area)` exposes resource metadata relevant to the addressed content scope.

`KnowledgeResourceInfo` includes:

- `ResourceId`
- `FileName`
- `ContentType`
- `Length`

## 12.3 Binary retrieval

`GetResourceContent(resourceId)` decodes provider resource identity, loads the page/object, and returns bytes.

## 12.4 Mutations

Resource mutations must target the exact OneNote object/page.

Do not rewrite an entire page merely to replace one binary object.

Multipart Graph PATCH is used where required.

---

# 13. Aggregated content

`GetAggregatedContent(area)` produces the complete logical document scope rooted at an area.

For pages/headings, rendering uses hierarchical heading output.

The provider-level aggregate is appropriate because OneNote page heading trees are bounded, meaningful content units.

This is exactly the type of scope for which `ContentAggregation` semantics are intended.

However, callers wrapping this provider in `AggregatedKnowledgeRepository` or background caching must still avoid turning arbitrary repository roots into uncontrolled recursive fetches.

---

# 14. Mutation strategy

## 14.1 General rule

Never perform a lossy full HTML → Markdown → HTML round-trip for an unrelated page region.

Mutations should modify only the addressed logical scope.

## 14.2 Rename

Page rename:

```text
target page title only
```

Heading rename:

```text
target heading element only
```

## 14.3 Append

Append operations insert a targeted fragment at the appropriate page/heading position.

## 14.4 Replace

Heading/body replacement must preserve unrelated siblings and parent content.

## 14.5 Truncate

Truncation removes direct addressed content without deleting the addressed area itself.

## 14.6 Move

Same-page heading subtree moves can be supported through targeted HTML fragments.

Cross-page moves are intentionally conservative because resources and object IDs make atomic reconstruction non-trivial.

Do not claim atomic cross-page move support until resource remapping and rollback semantics are genuinely implemented.

---

# 15. Graph access and throttling

The implementation has a local Graph request governor.

Important settings in the current baseline include:

```text
minimum request spacing: approximately 550 ms
maximum retry count: 6
initial retry backoff: 2000 ms
maximum retry backoff: 60000 ms
```

HTTP 429 is intentionally important to upstream background scheduling.

Do not hide long 429 retry loops inside a provider when an outer scheduler is expected to requeue work.

Retry policy must distinguish:

- transient server errors,
- throttling,
- permanent request failures.

---

# 16. Authentication

Authentication is delegated through:

```text
IOneNoteGraphAuthenticationProvider
```

The proxy asks the authentication provider for an `HttpClient`.

Read-only mode must influence requested permissions/scopes wherever the authentication strategy supports this distinction.

Do not embed credentials or token acquisition logic directly into repository traversal code.

---

# 17. Cache model inside the provider

The provider maintains:

- `_CachedTree`
- `_CachedNotebooks`
- `_PageCache`

Reads should reuse stable snapshots where appropriate.

Successful mutations call `InvalidateRepositoryCache()`.

Reads must not invalidate caches merely because they occur.

The provider cache and the outer `BackgroundFetchingKnowledgeRepositoryCacheWrapper` serve different purposes:

```text
provider-local cache
  = Graph request reduction / stable read snapshot

background wrapper cache
  = persistent cache-only repository facade / asynchronous refresh
```

Do not conflate the two.

---

# 18. Error handling

Expected Graph and projection failures are handled deliberately.

For targeted expected exceptions, log:

```csharp
DevLogger.LogError(ex);
```

Do not add broad catch-all blocks merely to suppress failures.

Provider methods that implement `Try...` semantics should return `false` when the operation cannot be safely completed.

Read methods should not silently fabricate content when Graph data is invalid.

---

# 19. Logging conventions for KnowledgeManagement

For new logging in this project:

```text
DevLogger.LogTrace(<SourceLineUid>, <EventKindId>, <message>)
```

Rules:

1. Parameter 1 is a statically assigned Snowflake44 `long`.
2. It is written inline at the exact log call.
3. Every logging call gets a unique SourceLineUid.
4. Do not move SourceLineUids into constants.
5. The purpose is to find the exact emitting code location.
6. Parameter 2 is the semantic EventKindId and is also written inline.
7. Multiple source locations may share an EventKindId when they describe the same semantic event.
8. KnowledgeManagement uses the project-wide range:

```text
75200-75299
```

9. Ask before allocating a new EventKind range.
10. Prefer one physical source line for a log statement when reasonably readable.
11. Prefer interpolated strings:

```csharp
$"...{value}..."
```

over long `"" + value + ""` concatenation.

---

# 20. Requirements matrix

| ID | Requirement | Mandatory |
|---|---|---|
| ON-001 | Implement the frozen `IKnowledgeRepository` signatures unchanged | Yes |
| ON-002 | Use `/` as logical root | Yes |
| ON-003 | Notebook/section-group/section hierarchy is navigable | Yes |
| ON-004 | Pages are `ContentContainer` | Yes |
| ON-005 | H1-H6 are hierarchical `ContentContainer` areas | Yes |
| ON-006 | Page title is not duplicated as a heading | Yes |
| ON-007 | Page direct content is only the preamble before first heading | Yes |
| ON-008 | Heading direct content is scope-local | Yes |
| ON-009 | `_onefiles` pages are excluded at provider level | Yes |
| ON-010 | `_onefiles` suffix comparison is case-insensitive | Yes |
| ON-011 | Resources use opaque IDs | Yes |
| ON-012 | Canonical Markdown uses `knowledge-resource:` | Yes |
| ON-013 | Known internal OneNote links may become `knowledge-area:` | Yes |
| ON-014 | External HTTP(S) links remain external | Yes |
| ON-015 | Link conversion occurs exactly once | Yes |
| ON-016 | U+FFFC placeholder is removed from canonical Markdown | Yes |
| ON-017 | Reads avoid repository-wide link-resolution discovery | Yes |
| ON-018 | Writes use targeted mutations where possible | Yes |
| ON-019 | Avoid lossy whole-page round-trips for local edits | Yes |
| ON-020 | Use Newtonsoft.Json | Yes |
| ON-021 | No async/await pattern in project implementation | Yes |
| ON-022 | No Windows-only interop | Yes |
| ON-023 | Graph throttling is respected | Yes |
| ON-024 | Read-only mode remains enforceable | Yes |
| ON-025 | Mutations invalidate provider-local snapshots | Yes |

---

# 21. Common traps and non-solutions

## 21.1 Filtering `_onefiles` in the UI

Wrong.

The artifact has already polluted the logical repository tree by then.

Filter in `LoadSectionChildren()`.

## 21.2 Treating U+FFFC as an encoding mismatch

Wrong.

It is a Unicode object placeholder, not evidence of a wrong charset.

## 21.3 Generating Markdown inside the DOM before ReverseMarkdown

Wrong.

That creates double conversion and broken links.

## 21.4 Resolving every OneNote link by crawling Graph

Wrong.

A page containing many links could trigger uncontrolled repository discovery.

Use already-known tree state only.

## 21.5 Using display titles as stable identity

Wrong.

Titles can be duplicated or renamed.

## 21.6 Replacing an entire OneNote page for a small edit

Wrong.

Markdown/HTML conversion is not lossless and can destroy unrelated OneNote-specific content.

## 21.7 Exposing Graph object IDs as a consumer contract

Wrong.

Consumer-visible resource and area identity must stay provider-neutral.

---

# 22. Recommended MSTest coverage

At minimum:

```text
LoadSectionChildren_HidesOneFilesPages
LoadSectionChildren_HidesOneFilesCaseInsensitive
LoadSectionChildren_KeepsNormalPages
HtmlToKnowledgeMarkdown_RemovesObjectReplacementCharacter
HtmlToKnowledgeMarkdown_PreservesExternalHttpLink
HtmlToKnowledgeMarkdown_PreservesExternalHttpsLink
NormalizeOneNoteLinks_ConvertsResolvableInternalLink
NormalizeOneNoteLinks_DoesNotGraphCrawlForUnknownLink
NormalizeOneNoteLinks_DoesNotDoubleEncodeMarkdownStyleLabel
PageDirectContent_StopsAtFirstHeading
HeadingDirectContent_StopsAtNextHeadingBoundary
HeadingProjection_BuildsNestedHierarchy
ResourceReference_UsesKnowledgeResourceScheme
Mutation_RenameHeading_DoesNotReplaceWholePage
Mutation_MoveCrossPage_IsRejectedWhenAtomicityCannotBeGuaranteed
```

Tests must use MSTest.

---

# 23. Bottom-up artifact guide

## `NodeKind`

Defines the physical/logical OneNote node types:

```text
Root
Notebook
SectionGroup
Section
Page
Heading
```

## `AreaNode`

Internal logical tree node.

Do not expose this type as provider contract.

## `NotebookInfo`

Small metadata representation used during notebook discovery.

## `ResourceIdentity`

Decoded provider-native resource identity.

## `Patch`

Represents a targeted OneNote Graph patch operation.

## `Target`

Represents a resolved insertion/replacement target in OneNote HTML.

## `_PageCache`

Caches parsed page HTML snapshots.

## `_CachedTree`

Caches the logical metadata tree.

## `_HtmlToMarkdown`

ReverseMarkdown converter configured for provider projection.

---

# 24. Maintenance checklist

Before changing this provider, verify:

- Does the change preserve `IKnowledgeRepository` semantics?
- Does it accidentally force page-body loading during navigation?
- Does it expose Graph implementation details?
- Does it cross a content boundary?
- Does it duplicate page/heading content?
- Does it turn an external URL into `knowledge-area:`?
- Does it generate Markdown before the actual HTML-to-Markdown converter?
- Does it reintroduce `_onefiles`?
- Does it leave U+FFFC in output?
- Does it modify unrelated page regions?
- Does it create hidden synchronous Graph recursion?
- Does it handle 429 without unbounded internal waiting?
- Does it follow KnowledgeManagement logging UID/EventKind rules?

---

# 25. Design invariant summary

The implementation should always preserve the following mental model:

```text
OneNote is the storage model.
IKnowledgeRepository is the public semantic model.
Canonical Markdown is the content interchange model.
Graph IDs/HTML are implementation details.
```

Any new feature should be evaluated against this boundary first.
