# [AI Skill] Knowledge Repository HTML and RAW HTTP Endpoints

**Status:** Normative implementation guide and endpoint architecture memory  
**Scope:** `KnowledgeRepositoryHtmlController`, `KnowledgeRepositoryRawController`, routing, Markdown rendering, provider-neutral link translation, cache behavior, search, UI degradation, security, performance, and HTTP-facing pitfalls  
**Goal:** Allow browser and machine consumers to access any `IKnowledgeRepository` implementation without provider-specific knowledge.

---

# 1. Abstract and Motivation

The HTML and RAW endpoints are consumer adapters over `IKnowledgeRepository`.

They must remain completely provider-neutral.

They are not allowed to know whether content originates from:

- FileBased Markdown,
- OneNote,
- Joplin,
- Git,
- a remote HTTP repository,
- a database,
- an aggregate of several repositories.

The endpoints differ in presentation:

- HTML is a human browser experience.
- RAW is a machine-friendly HTTP facade.

They share the same architectural rule:

> Endpoint logic translates HTTP and presentation concerns into repository contract calls; it does not reinterpret provider storage semantics.

---

# 2. Dependency Direction

Correct:

```text
Browser
  -> HTML controller
  -> IKnowledgeRepository
```

```text
HTTP / AI client
  -> RAW controller
  -> IKnowledgeRepository
```

Forbidden:

```text
HTML controller
  -> FileBasedKnowledgeRepository

RAW controller
  -> OneNote page IDs
```

No endpoint should contain:

- OneNote link parsing,
- Markdown physical path parsing,
- Joplin IDs,
- provider-specific folder markers.

---

# 3. Endpoint Responsibilities

## HTML endpoint

Responsibilities:

- navigation,
- breadcrumbs,
- document rendering,
- outline rendering,
- search UI,
- optional mutation UI,
- graceful partial read failure,
- cache-only visual hints,
- provider-neutral link translation,
- safe Markdown rendering.

## RAW endpoint

Responsibilities:

- HTTP-readable navigation,
- direct/aggregated Markdown exposure,
- machine-discoverable child URLs,
- binary resource access,
- provider-neutral link translation,
- mutation mapping to repository contract where enabled,
- low-latency cache preference for reads.

---

# 4. HTTP Route Model

The endpoint should expose named routes rather than constructing URLs by concatenating strings.

Representative names:

```text
HtmlRoot
HtmlArea
HtmlSearch
HtmlSearchPoll
HtmlRefresh
HtmlEdit

RawRoot
RawArea
RawResource
```

The named-route layer is important because:

- application base paths may vary,
- deployment prefixes may vary,
- routes may be renamed,
- URL generation remains centralized.

---

# 5. Root Semantics

Logical root:

```text
/
```

must work in all adapters.

Do not transform root into:

```text
""
"."
"/."
```

HTML:

```text
BuildHtmlAreaRequestPath("/")
```

must resolve to the root HTML route.

RAW:

```text
BuildAbsoluteAreaUrl("/")
```

must resolve to the absolute RAW root URL.

---

# 6. Logical Area vs. HTTP Route Transport

A logical area and an HTTP route value are different layers.

Example logical provider path:

```text
/Organisation/Team%20Software/Coding%20Rules
```

The endpoint must transport that exact identity through ASP.NET/IIS without losing or double-encoding it.

A transport codec was introduced because passing `%` directly through route generation can create double escaping:

```text
%3A
-> %253A
```

IIS may reject this as 404.11.

The preferred approach is **not** enabling `allowDoubleEscaping`.

Instead, use an application-safe route transport representation, conceptually:

```text
% -> ~25
~ -> ~7E
```

The ingress path reverses the transformation before calling the repository.

This gives:

```text
logical:
  /Team%20Software

route transport:
  /Team~2520Software

repository ingress:
  /Team%20Software
```

The exact codec must be centralized and reversible.

---

# 7. Critical Encoding Rule for `knowledge-area:`

Canonical repository Markdown may contain:

```markdown
[Coding Rules](knowledge-area:/Organisation/Team%20Software/Coding%20Rules)
```

The payload after `knowledge-area:` is already the canonical **logical repository area**.

Therefore:

> The endpoint must not blindly call `Uri.UnescapeDataString` on it.

Wrong:

```text
/Team%20Software
-> /Team Software
```

if `%20` is part of the provider's canonical logical path representation.

Correct flow:

```text
knowledge-area:/Team%20Software
-> logical area = /Team%20Software
-> BuildHtmlAreaRequestPath(...)
-> route transport encoding
```

For RAW:

```text
knowledge-area:/Team%20Software
-> BuildAbsoluteAreaUrl(...)
```

No provider-specific decoding.

---

# 8. Canonical Provider-Neutral Link Schemes

Repository Markdown may contain two special schemes:

```text
knowledge-resource:<opaque-resource-id>
knowledge-area:<canonical-logical-area>
```

These are repository semantics.

They are not browser-native URL schemes.

Endpoints translate them at presentation boundaries.

---

# 9. `knowledge-resource:` in HTML

HTML should translate:

```markdown
![Diagram](knowledge-resource:abc)
```

or:

```markdown
[Attachment](knowledge-resource:abc)
```

to the registered RAW resource endpoint.

Conceptually:

```text
knowledge-resource:abc
-> /api/knowledge/raw/_resource/abc
```

The endpoint MUST treat the ID as opaque.

Never:

- decode,
- split,
- derive a path,
- infer MIME type from ID.

---

# 10. `knowledge-area:` in HTML

HTML should translate:

```markdown
[Rules](knowledge-area:/Organisation/Coding%20Rules)
```

using exactly the same route-building logic used by normal navigation:

```csharp
BuildHtmlAreaRequestPath(
  area
);
```

This is important because the helper already owns:

- route names,
- route-safe area encoding,
- root special case,
- `cacheOnly=1` propagation.

Do not duplicate route logic in the Markdown renderer.

---

# 11. Final-Boundary Link Resolution

A critical failure mode was discovered:

```text
knowledge-area:...
-> reaches generic URL security filter
-> custom scheme is classified as unknown
-> filter returns "#"
-> browser appears to link to current page
```

Therefore special repository schemes must be resolved **before** the generic unsafe-scheme fallback.

Robust pattern:

```text
NormalizeUrl(url)
  1. if knowledge-resource: -> resolve repository resource route
  2. if knowledge-area: -> resolve repository area route
  3. reject control characters / backslashes where applicable
  4. allow normal http/https/mailto
  5. reject unknown schemes with "#"
```

This location is safer than relying exclusively on a fragile Markdown pre-render regex.

A pre-render pass may still exist, but the final URL normalization boundary must understand the special schemes.

---

# 12. Normal Link Behavior

The special mapping must not damage normal Markdown links.

These must continue to work:

```text
https://...
http://...
mailto:...
#fragment
/application-relative-path
relative-path
```

Image URL policy may be stricter than normal anchor URL policy.

Unknown executable or unsafe schemes should remain blocked.

---

# 13. Why `href="#"` Is Dangerous for Diagnosis

When a link unexpectedly "goes to the same page", inspect the generated HTML.

If:

```html
<a href="#">
```

is present, routing is not necessarily the problem.

Often the Markdown URL sanitizer intentionally collapsed an unknown scheme to `#`.

This exact symptom occurred with unresolved `knowledge-area:` / provider-specific `onenote:` links.

Always debug link flow layer-by-layer:

```text
provider Markdown
-> wrapper Markdown
-> endpoint Markdown
-> parsed link URL
-> normalized URL
-> generated href
```

---

# 14. Provider-Specific Links Must Not Reach the Endpoint

The HTML and RAW endpoints know only repository-neutral schemes.

For example:

```text
onenote:...
:/JoplinId
file:///...
```

should not normally appear in repository-facing Markdown.

The provider or adapter that owns such syntax must translate it to:

```text
knowledge-area:
knowledge-resource:
```

before returning repository content.

If provider-native syntax reaches the HTML endpoint, the endpoint should not grow provider-specific resolution code.

That would violate the architecture.

---

# 15. HTML Markdown Rendering

The HTML controller owns Markdown-to-HTML presentation.

The renderer should:

- HTML-escape plain text,
- safely render headings,
- render links,
- render images,
- support code spans,
- support bold/italic,
- support lists and tables as implemented,
- sanitize URLs,
- add stable heading anchors.

The renderer must never trust raw Markdown as pre-sanitized HTML.

---

# 16. Heading Anchors and Logical Areas

When a `ContentContainer` includes descendant areas represented as Markdown headings, the HTML controller may map rendered headings to logical descendant areas.

Outline anchors should be deterministic.

A typical strategy:

```text
logical area
-> stable hash
-> section-<short-hash>
```

If heading matching is based on display label, duplicate labels require occurrence tracking.

Do not assume heading titles are globally unique.

---

# 17. Document Root Resolution

An addressed logical heading may need to redirect to its containing content document plus anchor.

Example:

```text
requested:
/Docs/Article/Details

document root:
/Docs/Article

HTTP:
.../Docs/Article#section-...
```

The controller can determine the first ancestor whose content level is `ContentContainer`.

Avoid full recursive materialization to find this.

---

# 18. HTML Navigation Performance

Normal HTML page navigation should use only direct children.

Do not call:

```csharp
GetAreas(
  true,
  "/"
);
```

to render every sidebar.

Use:

```csharp
GetAreas(
  false,
  area
);
```

or a narrowly scoped iterative traversal only where a document outline specifically requires descendant content.

---

# 19. Partial Failure, Not Total Failure

The HTML UI follows a resilience rule:

> A read failure in one provider branch should not destroy the entire page when useful partial content can still be rendered.

Examples:

## `GetAreaName` failure

Fallback to a conservative path segment display if necessary.

## `GetAreas` failure

Render an empty branch and a subtle warning.

## capability failure

Degrade to:

```text
ContentLevel.BeyondContent
```

instead of assuming mutation/content capability.

## content failure

Keep page chrome and navigation alive when possible.

Warnings should be subtle and informative.

Mutations do **not** follow the same tolerant policy. Writes must remain strict.

---

# 20. Search Architecture

A previous design used detached background work.

That caused:

```text
ObjectDisposedException
IFeatureCollection has been disposed
```

because controller/request-scoped state such as `Url`, `HttpContext`, and request services was accessed after the HTTP request ended.

The final architecture is cooperative polling.

---

# 21. Search Session Flow

1. Browser sends:

```text
GET /_search?q=...
```

2. Controller creates an in-memory `SearchSession`.
3. Controller immediately returns opaque `searchId`.
4. Browser polls:

```text
GET /_search/{searchId}
```

5. Each poll performs one bounded slice of repository work in the live request.
6. Browser appends newly found results.
7. Closing dialog, navigation, tab close, or browser close stops polling.
8. No polling means no additional repository work.

This gives natural cancellation without detached controller tasks.

---

# 22. Search Slice Limits

Target behavior used in the implementation:

```text
maximum ~25 areas per poll
or
maximum ~350 ms per poll
```

Browser poll cadence approximately:

```text
750 ms
```

Search result cap:

```text
30 results
```

These are operational choices and may be tuned, but the architecture should remain bounded and cooperative.

---

# 23. Search Traversal

Search should traverse using:

```csharp
GetAreas(
  false,
  currentArea
);
```

with an explicit stack/queue.

Never require child providers to implement a full recursive tree read just for UI search.

Per-area failures should be isolated.

Search status may show:

```text
7 results so far · 184 areas checked
```

When the 30-result cap is reached, clearly state that search stopped at the cap.

---

# 24. Application Shutdown

Search polling should observe `IHostApplicationLifetime.ApplicationStopping`.

Do not start new expensive repository work while shutdown is in progress.

Search sessions should be opportunistically cleaned up after heartbeat expiration.

---

# 25. HTML UI Cache vs. Repository Cache

There can be two separate caching concepts:

1. HTML controller's own rendered/read cache.
2. A repository-local `KnowledgeRepositoryCacheWrapper`.

These are not the same thing.

The optional repository cache-control interface is intended to control layer 2.

When `cacheOnly=1` is active and the repository exposes `IKnowledgeRepositoryCacheControl`, HTML should avoid serving a separate controller cache in a way that prevents the repository wrapper from becoming populated.

Otherwise:

```text
gray uncached navigation link
-> click
-> HTML cache serves old result
-> repository cache wrapper never sees request
-> link stays gray forever
```

The wrapper must be authoritative for cache-state UI.

---

# 26. `cacheOnly=1` Semantics

Despite the historical parameter name, this is not strict offline mode.

Correct semantics:

```text
existing repository cache entry
-> use it, even if normal lifetime says expired

missing cache entry
-> call source
-> cache result
-> use result
```

This is better described as:

```text
prefer-existing / do-not-refresh-existing
```

It is a performance optimization.

---

# 27. Optional `IKnowledgeRepositoryCacheControl`

Expected shape:

```csharp
public interface IKnowledgeRepositoryCacheControl {
  bool IsAreaCached(
    string area
  );

  IDisposable BeginPreferExistingScope();
}
```

HTML must treat it as optional.

If the repository does not implement it:

- operate normally,
- do not gray links,
- do not assume cache miss.

The capability is intentionally local and should not be serialized across remote repository protocols.

---

# 28. Cache Scope Propagation Through Wrappers

If object graph is:

```text
HTML
-> AggregatedKnowledgeRepository
-> KnowledgeRepositoryCacheWrapper
-> remote provider
```

then HTML can activate prefer-existing semantics only if every local decorator layer propagates the optional capability.

`AggregatedKnowledgeRepository` should implement `IKnowledgeRepositoryCacheControl` and open child scopes for mounted repositories that support it.

A wrapper that hides the capability causes the outer endpoint to behave as if no cache exists.

---

# 29. Cache-State Navigation Coloring

In `cacheOnly=1` mode:

```text
cached target
-> normal link appearance

not cached
-> subtle light gray
```

Important UI decisions:

- no italics,
- do not make the state visually alarming,
- only gray when the outer repository actually exposes reliable cache inspection,
- unknown state should remain normal rather than falsely gray.

CSS specificity matters because links often have normal theme color rules.

Use a sufficiently specific selector, for example:

```css
.area-nav a.cache-miss,
.document-list a.cache-miss {
  color: #9aa0a6 !important;
}
```

also cover:

```text
:visited
:hover
:focus
```

if the theme overrides them.

---

# 30. RAW Endpoint Philosophy

The RAW endpoint is intentionally machine-friendly.

A client should be able to start from one URL and discover child URLs without separate API documentation.

For structural/aggregation areas, responses may include:

```text
Knowledge area: `/...`

The following directly accessible sub-area URLs are available:

- `/A` -> <https://host/.../A>
- `/B` -> <https://host/.../B>
```

Only direct children should be listed.

Clients can recursively follow links themselves.

---

# 31. RAW `ContentLevel` Behavior

## `BeyondContent`

Return navigation-oriented response.

## `ContentAggregation`

Return:

- direct-child navigation,
- optionally aggregated Markdown according to current endpoint design.

Avoid recursively exploding unrelated structural descendants.

## `ContentContainer`

Return Markdown content.

The exact current controller behavior remains source-authoritative, but the endpoint must respect repository content levels.

---

# 32. RAW Absolute URLs

Machine clients benefit from fully qualified URLs.

`BuildAbsoluteAreaUrl` should generate:

```text
scheme://host/path
```

using named routes and current `PathBase`.

Avoid accidental dependence on ambient route values.

Generate the concrete route path first, then construct the absolute URL if necessary.

---

# 33. RAW `knowledge-area:` Translation

Before returning Markdown to a client:

```text
knowledge-area:/A/B
```

should become the absolute RAW URL for `/A/B`.

Example:

```markdown
[Rules](knowledge-area:/A/B)
```

becomes conceptually:

```markdown
[Rules](https://host/api/knowledge/raw/A/B)
```

The logical area payload is preserved exactly.

No OneNote/Joplin/provider parsing is allowed.

---

# 34. RAW `knowledge-resource:` Translation

Similarly:

```text
knowledge-resource:<opaque-id>
```

becomes the absolute RAW resource URL.

Resource IDs remain opaque.

---

# 35. RAW Cache Policy

For read performance, the RAW endpoint should prefer an existing repository cache whenever available.

Desired default:

```text
existing cached value
-> use it

missing cached value
-> fetch source and populate cache
```

No automatic refresh of an existing value during the prefer-existing scope.

Writes are intentionally excluded from this scope.

They must operate against authoritative repository state.

A historical constructor flag may be named:

```text
disableCacheRefresh
```

with default `true`.

The semantics matter more than the awkward name.

A future breaking API revision may rename it to something clearer, but do not silently alter public compatibility.

---

# 36. RAW Mutation Behavior

Mutations map directly to provider-neutral repository methods.

Examples:

```text
PUT / area
-> append/replace according to endpoint contract

DELETE / area
-> truncate according to endpoint contract

resource PUT/DELETE
-> repository resource contract
```

Before mutation:

- resolve area,
- inspect capabilities,
- reject unsupported operations with meaningful HTTP status.

Do not guess provider behavior.

---

# 37. HTTP Status Philosophy

Typical mapping:

```text
invalid request
-> 400

missing area
-> 404

operation unsupported
-> 405

conflict / atomic mutation rejected
-> 409

temporary provider unavailability
-> 503

successful delete/truncate
-> 204 where appropriate
```

The endpoint must not turn an ambiguous provider failure into false success.

---

# 38. HTML Security

HTML rendering should use a restrictive CSP.

Typical concerns:

- prevent arbitrary script execution,
- restrict image origins,
- restrict connection targets,
- prevent base tag abuse,
- restrict forms,
- prevent framing if appropriate.

Per-response nonces can authorize the controller's own style/script blocks.

Never render provider Markdown as raw trusted HTML unless explicitly sanitized by design.

---

# 39. URL Security

Generic URL normalization should reject:

- control characters,
- unsafe custom schemes,
- malformed dangerous values.

But repository schemes must be handled before the generic rejection.

Allowed anchor schemes typically include:

```text
http:
https:
mailto:
```

Images often only need:

```text
http:
https:
```

plus explicitly generated local RAW resource routes.

---

# 40. Do Not Make the Endpoint Provider-Specific to "Fix" Broken Links

If OneNote returns:

```text
onenote:...
```

do not add OneNote link parsing to HTML.

Fix the OneNote provider.

If Joplin-specific `:/id` leaks into repository Markdown, fix the Joplin adapter.

Endpoint responsibility ends at:

```text
knowledge-area:
knowledge-resource:
normal web URLs
```

---

# 41. Diagnostic Pipeline for Broken Links

When links behave incorrectly, inspect these exact stages:

```text
1. provider GetDirectContent/GetAggregatedContent result
2. aggregate/wrapper transformed Markdown
3. endpoint pre-render Markdown
4. parsed URL token
5. normalized URL
6. generated HTML href or RAW Markdown target
7. actual HTTP request path
8. endpoint ToRepositoryArea result
9. repository area received
```

This prevents fixing the wrong layer.

---

# 42. Known Link Failure Patterns

## Same-page navigation

Likely generated:

```html
href="#"
```

Often caused by unknown custom scheme reaching sanitizer.

## IIS 404.11

Likely double escape:

```text
%25...
```

Use route transport codec, not `allowDoubleEscaping=true`.

## Target not found after valid route

Check whether the logical area was incorrectly URI-decoded or re-encoded before repository call.

## Wrong aggregate mount target

Check `AggregatedKnowledgeRepository` provider-local `knowledge-area:` rebasing.

---

# 43. Requirements Matrix

| Requirement | HTML | RAW |
|---|---:|---:|
| Provider-neutral | MUST | MUST |
| Root `/` | MUST | MUST |
| Direct-child navigation | MUST | MUST |
| Full-tree navigation on normal request | MUST NOT | MUST NOT |
| `knowledge-resource:` mapping | RAW resource route | absolute RAW resource URL |
| `knowledge-area:` mapping | HTML route | absolute RAW area URL |
| URI-decode logical area | MUST NOT blindly | MUST NOT blindly |
| HTTP route transport codec | MUST | MUST where catch-all route requires it |
| Partial read degradation | SHOULD | endpoint-specific |
| Mutations strict | MUST | MUST |
| Search heartbeat | MUST | n/a |
| Detached controller task | MUST NOT | n/a |
| Cache-only visual state | optional | n/a |
| Prefer-existing cache | `cacheOnly=1` | default read optimization |
| Missing cache entry | source load + cache | source load + cache |
| Existing cache refresh | suppressed in prefer-existing scope | suppressed by default policy |
| Cache capability optional | MUST | MUST |
| Writes use stale-only cache scope | MUST NOT | MUST NOT |
| Normal http/https/mailto links | preserved | preserved in Markdown |
| Unknown schemes | sanitized | normally left only if RAW policy explicitly allows; provider-native schemes should never arrive |

---

# 44. Regression Test Matrix

Use MSTest.

## Link tests

- HTML `knowledge-area:/` -> HTML root.
- HTML encoded logical area remains logically identical.
- `%20` is not incorrectly converted to literal space.
- `%25` does not become double-encoded.
- `knowledge-resource:` maps to RAW resource route.
- `http:` unchanged.
- `https:` unchanged.
- `mailto:` unchanged.
- unknown custom scheme sanitized.
- unresolved `knowledge-area:` never becomes `#`.
- aggregate-mounted area link reaches the correct mount.

## Routing tests

- catch-all route roundtrips `%`.
- catch-all route roundtrips `~`.
- IIS-safe transport value contains no forbidden double escape.
- `PathBase` preserved.

## Cache tests

- no cache capability -> normal link color.
- cached -> normal color.
- uncached -> gray.
- click uncached -> source called once and cache populated.
- next render -> normal color.
- expired existing cache in prefer-existing scope -> no source call.
- missing entry in prefer-existing scope -> source call.
- aggregator propagates cache scope.

## Search tests

- start call performs no repository traversal.
- poll performs bounded work.
- no `GetAreas(true)` on child providers.
- 30-result cap.
- stopping polling stops further work.
- no controller state used after request disposal.
- per-area read exception does not kill healthy search.

## Failure tests

- one area-name failure -> page still renders.
- one branch enumeration failure -> page still renders.
- capability failure -> conservative navigation-only behavior.
- mutation rejection -> correct non-success HTTP code.

---

# 45. Bottom-Up Artifact Guide

## `KnowledgeRepositoryHtmlController`

Human-facing adapter.

Owns:

- HTML rendering,
- search sessions,
- route generation,
- CSP,
- navigation,
- cache state presentation,
- partial read degradation.

Does not own provider semantics.

## `KnowledgeRepositoryHtmlOptions`

UI/endpoint behavior configuration.

May include:

- title/branding,
- edit authorization,
- cache directory,
- cache lifetime,
- refresh hooks.

## `KnowledgeRepositoryRawController`

Machine-facing HTTP adapter.

Owns:

- direct navigation responses,
- content responses,
- resource download,
- absolute URLs,
- HTTP mutation mapping,
- prefer-existing read policy.

## `IKnowledgeRepositoryCacheControl`

Optional local optimization contract.

## `KnowledgeRepositoryCacheWrapper`

Actual repository read cache implementation.

## `AggregatedKnowledgeRepository`

Must propagate cache capability if mounted child repositories expose it.

---

# 46. Implementation Rules

For generated C# in this project:

- German prompt communication,
- English code/comments,
- no `var`,
- explicit code,
- XML summaries on methods,
- arrays in public signatures,
- explicit `this.` for instance members except fields,
- no async/await unless explicitly approved,
- no ternary operators,
- braces always,
- Newtonsoft.Json,
- targeted `DevLogger.LogError(ex)`,
- `DevLogger.LogTrace(0, 99999, "...")`,
- MSTest only.

---

# 47. Decision Summary

The HTTP adapter architecture intentionally chooses:

1. named-route generation instead of string concatenation,
2. logical area identity separate from HTTP route transport,
3. application-safe route transport encoding instead of IIS double-escape configuration,
4. special repository schemes resolved at final URL-normalization boundaries,
5. no unconditional URI decoding of logical areas,
6. direct-child navigation for performance,
7. cooperative polling search rather than detached tasks,
8. partial read degradation in human UI,
9. strict writes,
10. optional local cache-control capability,
11. repository cache as authoritative cache-state source,
12. default prefer-existing RAW reads for low latency,
13. zero provider-specific logic in endpoints.

Future endpoint changes should preserve these decisions unless the architecture is explicitly revised.
