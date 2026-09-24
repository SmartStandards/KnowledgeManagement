# [AI Skill] Joplin WebDAV Endpoint for `IKnowledgeRepository`

**Status:** Normative implementation guide and synchronization architecture memory  
**Scope:** `JoplinKnowledgeRepositoryWebDavHandler`, WebDAV middleware, projection state, Joplin item model, repository projection, resources, note/area links, mutation mapping, conflict behavior, performance, idempotence, and safety  
**Goal:** Expose any provider-neutral `IKnowledgeRepository` as a normal Joplin synchronization target without leaking Joplin semantics into repository providers.

---

# 1. Abstract and Motivation

The Joplin endpoint is not a normal REST controller.

It is a synchronization adapter that projects the provider-neutral knowledge model into Joplin's sync-item model.

The positive differentiation from naive Joplin integrations is that it does not require:

- a Joplin-specific repository provider,
- a Joplin plugin,
- physical Markdown files arranged like Joplin,
- repository resources to use Joplin IDs,
- destructive mirroring semantics.

Instead:

```text
Joplin client
  -> WebDAV protocol
  -> JoplinKnowledgeRepositoryWebDavHandler
  -> IKnowledgeRepository
  -> arbitrary provider
```

Joplin-specific synchronization artifacts are stored separately from repository truth.

This allows the same Joplin adapter to expose:

- FileBased repositories,
- OneNote repositories,
- aggregated repositories,
- remote repositories,
- future providers.

---

# 2. Architectural Boundary

The Joplin adapter may know:

- Joplin sync item formats,
- Joplin item type IDs,
- `:/<id>` links,
- Joplin resource metadata,
- Joplin folder/note parent IDs,
- Joplin WebDAV file naming,
- Joplin lock/temp/sync artifacts.

It MUST NOT know:

- FileBased directory syntax,
- physical Markdown file paths,
- OneNote page/section IDs,
- provider-specific resource ID encoding,
- provider-specific move mechanics.

The repository provider MUST NOT know Joplin exists.

---

# 3. Why a Separate Sync State Store Exists

Joplin requires persistent protocol identity and state that do not belong to knowledge content.

Examples:

- stable Joplin item IDs,
- resource Joplin IDs,
- projection records,
- suppression state,
- timestamps,
- sync item hashes,
- lock files,
- temporary sync files,
- compatibility metadata.

These belong in:

```text
IJoplinSyncStateStore
```

not in:

```text
IKnowledgeRepository
```

This separation is essential.

Repository state is knowledge truth.

Joplin state is adapter/protocol truth.

---

# 4. Primary Projection

The conceptual mapping is:

```text
IKnowledgeRepository area tree
    |
    +-- structural / aggregation areas
    |      -> Joplin folders
    |
    +-- first ContentContainer below non-container scope
           -> Joplin note
```

Nested `ContentContainer` descendants inside the same content document are normally represented as headings inside the note body rather than duplicated as separate Joplin notes.

This avoids duplicate content and preserves natural Markdown structure.

---

# 5. Joplin Item Types

Canonical sync item type values used by the adapter:

```text
1 = Note
2 = Folder
4 = Resource
```

The same Joplin `:/<32-hex-id>` link syntax may refer to different item types.

Never classify solely from the syntax.

---

# 6. WebDAV Collections and Files

The adapter manages protocol paths such as:

```text
/info.json
/locks
/temp
/.resource
/.sync
/.lock
/<32-char-id>.md
```

Compatibility collections may exist for Joplin expectations.

The exact supported WebDAV projection remains source-authoritative.

---

# 7. Handler vs. Middleware

`JoplinKnowledgeRepositoryWebDavHandler` deliberately is not used as a normal MVC action controller for WebDAV verbs.

A dedicated middleware dispatches based on:

```csharp
HttpRequest.Method
```

This avoids forcing:

- PROPFIND,
- MKCOL,
- MOVE,
- WebDAV-specific behavior

through normal MVC verb discovery, ApiExplorer, Swagger, or formatter negotiation.

---

# 8. Projection State Records

The adapter keeps persistent mapping records.

Conceptually:

```text
JoplinProjectionRecord
  Joplin Id
  repository Area
  item Type
  parent relation
  timestamps
  semantic hash
  suppression state
```

Resource records conceptually map:

```text
Joplin Resource Id
<-> repository ResourceId
```

These mappings are adapter state.

They do not redefine repository identity.

---

# 9. Stable Joplin Identity vs. Repository Identity

A repository resource ID may change after:

- document move,
- rename,
- provider-native path change.

The Joplin resource ID should remain stable.

When the repository reports:

```text
old ResourceId -> new ResourceId
```

the Joplin adapter updates its mapping.

It should not generate a new Joplin identity merely because provider-native resource identity changed.

This is one of the reasons `KnowledgeResourceIdChange[]` exists.

---

# 10. Repository Area Identity

Joplin projection records store the exact logical repository area.

Do not:

- decode area segments,
- derive names from path segments,
- treat provider-specific encoding as URI transport,
- assume physical file semantics.

Use:

```csharp
GetAreaName(area)
```

for display title.

---

# 11. Projection Performance Rule

Normal exact reads and updates must not require a complete repository scan.

The performance goal is:

> Exact GET/HEAD/PROPFIND and normal update paths should use path-specific projection state and direct-child repository navigation.

Use iterative direct-child traversal where discovery is genuinely required.

Do not ask child providers for:

```csharp
GetAreas(
  true,
  "/"
);
```

as a hidden primitive.

---

# 12. Root PROPFIND Is a Special Bulk Boundary

Joplin's sync model may require a flat view of many sync items.

Therefore the root WebDAV `PROPFIND`/projection operation is one of the few legitimate explicit bulk boundaries.

Even there, discovery should be implemented as:

```text
stack
-> GetAreas(false, current)
-> push children
```

rather than delegating recursive full-tree materialization to arbitrary child providers.

This preserves control, cancellation points, and provider isolation.

---

# 13. Projection Stop Rule

When building Joplin note/folder projection:

- structural / aggregation areas continue tree traversal,
- the first `ContentContainer` becomes a note,
- nested content containers below that note remain Markdown structure inside that note.

Do not project every heading as an independent Joplin note.

---

# 14. Joplin Resource Semantics

Joplin resources have:

- metadata sync item,
- binary blob under `/.resource/<id>`.

Repository resources have:

- opaque `ResourceId`,
- metadata via `KnowledgeResourceInfo`,
- binary bytes via `GetResourceContent`.

The adapter maps between them.

---

# 15. `knowledge-resource:` -> Joplin

Repository-facing Markdown:

```markdown
![Diagram](knowledge-resource:<ResourceId>)
```

must become:

```markdown
![Diagram](:/<JoplinResourceId>)
```

for Joplin.

During translation the adapter:

1. locates repository resource metadata,
2. finds or creates stable Joplin resource projection record,
3. reads repository bytes,
4. writes Joplin resource metadata item,
5. writes Joplin resource blob,
6. updates content hash,
7. replaces only the textual reference.

The repository `ResourceId` remains opaque.

---

# 16. Joplin Resource -> `knowledge-resource:`

Incoming Joplin Markdown may contain:

```text
:/0123456789abcdef0123456789abcdef
```

The adapter must first classify the referenced item.

If it is a resource:

1. ensure metadata is available,
2. ensure binary blob is available,
3. locate existing repository mapping or create repository resource,
4. translate to:

```text
knowledge-resource:<ResourceId>
```

Never assume every `:/id` is a resource.

---

# 17. Joplin Note Links and the `knowledge-area:` Contract

A major architectural extension is provider-neutral area references.

Repository-facing Markdown uses:

```text
knowledge-area:<canonical-logical-area>
```

Example:

```markdown
[Rules](knowledge-area:/Organisation/Coding%20Rules)
```

Joplin-facing Markdown uses:

```text
:/<JoplinItemId>
```

Therefore Joplin note/folder links must be translated bidirectionally.

---

# 18. Joplin `:/id` Classification

Incoming `:/id` may refer to:

- note,
- folder,
- resource,
- unknown/not-yet-arrived sync item.

The adapter MUST inspect:

- projection state,
- sync item metadata,
- item type.

Correct behavior:

```text
resource
-> knowledge-resource:

note/folder with repository mapping
-> knowledge-area:

unknown
-> pending dependency
```

Do not leak Joplin-specific `:/id` syntax into provider-neutral repository Markdown.

---

# 19. Joplin Note/Folder -> `knowledge-area:`

If projection state contains:

```text
Joplin ID:
  abcdef...

Area:
  /Organisation/Team%20Software/Coding%20Rules
```

incoming:

```markdown
[Rules](:/abcdef...)
```

becomes:

```markdown
[Rules](knowledge-area:/Organisation/Team%20Software/Coding%20Rules)
```

The area payload is copied exactly.

Do not call:

```csharp
Uri.UnescapeDataString(...)
```

on repository logical paths.

---

# 20. `knowledge-area:` -> Joplin Note/Folder

When exporting repository Markdown:

```markdown
[Rules](knowledge-area:/Organisation/Team%20Software/Coding%20Rules)
```

the adapter searches projection state for an exact non-suppressed record whose:

```text
record.Area == targetArea
```

and whose type is projectable as a Joplin note or folder.

If found:

```text
knowledge-area:/...
-> :/<record.Id>
```

---

# 21. Exact-Match Rule for Area Links

Do not silently map a target heading area to its containing Joplin note if there is no exact Joplin projection record for that heading.

Example repository target:

```text
/Article/Deep Heading
```

If Joplin only projects:

```text
/Article
```

then mapping the link to `/Article` changes semantic destination.

Preferred behavior:

```text
leave knowledge-area: unchanged
```

or use an explicitly designed future heading-anchor mapping.

Do not invent approximate link targets.

---

# 22. Dependency Ordering

Joplin may upload children before parents.

It may upload a note containing a link to an item whose metadata has not arrived yet.

This is not automatically a semantic conflict.

Use:

```text
PendingDependency
```

behavior.

Retry materialization later when required dependencies exist.

---

# 23. Reference Dependency Resolution

For an incoming Joplin body, inspect all `:/id` references before committing repository content.

A reference is resolvable if:

## Existing resource mapping

Resource projection record exists.

## Existing knowledge projection mapping

Note/folder projection record exists.

## New resource

Metadata and blob both exist, allowing repository resource creation.

## Note/folder metadata exists but no repository mapping yet

Not yet safely translatable.

Keep the note pending.

This prevents provider-neutral content from containing unresolved Joplin protocol syntax.

---

# 24. Do Not Turn Link Rendering into Repository-Wide Discovery

Link resolution should use:

- projection state,
- direct mapping,
- already-known logical area identity.

Do not discover the entire repository merely because one note contains a link.

The Joplin endpoint itself controls its projection state and should make link resolution deterministic from that state.

---

# 25. Creating Knowledge Items from Joplin

When Joplin creates a folder/note:

1. resolve `parent_id`,
2. if parent mapping missing -> pending,
3. determine semantic repository operation,
4. use `KnowledgeAreaKind.Structural` for folders,
5. use `KnowledgeAreaKind.Content` for notes,
6. create through `IKnowledgeRepository`,
7. create/update projection record,
8. write note content only after references are safely translatable.

Do not encode FileBased-specific naming conventions.

---

# 26. Updating Existing Knowledge Items

An incoming Joplin item may change:

- title,
- parent,
- body,
- resources.

The adapter should separately detect and apply semantic changes.

Avoid destructive recreation.

For title changes:

```text
TryRename
```

For parent changes:

```text
TryMoveContent
```

For body changes:

```text
TryReplace / provider-neutral content mutation semantics
```

depending on the actual handler design.

---

# 27. Parent Changes

Joplin `parent_id` change represents reparenting.

Correct repository operation:

```csharp
TryMoveContent(
  currentArea,
  newParentArea,
  out resourceIdChanges
);
```

Do not implement as:

```text
create copy under new parent
delete old item
```

This would risk:

- identity changes,
- resources,
- ordering,
- data loss,
- non-atomic intermediate states.

---

# 28. Resource ID Changes After Move/Rename

After a successful repository move or rename:

```text
KnowledgeResourceIdChange[]
```

may be returned.

For each change:

```text
PreviousResourceId
-> CurrentResourceId
```

update Joplin resource mapping while preserving the same Joplin resource ID.

This keeps Joplin note references stable.

---

# 29. Joplin DELETE Must Not Blindly Delete Knowledge

A WebDAV/Joplin DELETE can occur for synchronization reasons that do not necessarily mean:

```text
user intentionally destroy authoritative repository knowledge
```

Therefore the adapter uses conservative suppression semantics.

For projected knowledge items:

```text
DELETE
-> mark projection record suppressed
-> remove relevant sync-state representation
-> keep authoritative knowledge
```

This is a safety decision.

Do not map every WebDAV DELETE directly to:

```csharp
repository.TryDelete(...)
```

without an explicit higher-level product decision.

---

# 30. Resource DELETE Safety

The same caution applies to resources.

A Joplin sync reconciliation delete should not blindly destroy an authoritative repository resource.

Projection suppression/reconciliation is distinct from repository delete.

---

# 31. Conflict Philosophy

Prefer:

- suppression,
- rebind,
- pending dependency,
- retry,
- deterministic reconciliation.

Avoid:

- destructive "fixes",
- duplicate recreation,
- deleting authoritative knowledge because Joplin state disagrees.

The adapter is a facade over authoritative knowledge, not a second source of truth that may erase it casually.

---

# 32. Temporary Write Locks

A repository provider may be temporarily unavailable because of:

- file locks,
- remote throttling,
- transient HTTP failures,
- storage contention.

The Joplin adapter must not convert temporary inability into:

- false success,
- permanent semantic conflict.

Use an appropriate temporary failure result such as HTTP 503 when the protocol path allows it.

---

# 33. Idempotence

Repeated identical Joplin PUTs should be idempotent.

Repeated unchanged GETs should be byte-stable where protocol metadata permits.

Semantic hashes should exclude transport fields that would make the projection self-modifying.

Do not include a field in the semantic hash if the field itself changes solely because the hash changed.

---

# 34. Semantic Hashing

The adapter can compute a stable semantic fingerprint from:

- title,
- parent mapping,
- item type,
- translated body.

Exclude volatile transport timestamps where including them would create a feedback loop.

The semantic hash determines whether a projected item meaningfully changed.

---

# 35. Post-Write Hash Consistency

After applying an incoming Joplin write to the repository, re-read repository state.

For a note, the body used for semantic comparison must be translated back into the **Joplin representation** using the same translation pipeline:

```text
repository knowledge body
-> translate knowledge-area:
-> translate knowledge-resource:
-> Joplin body
-> semantic hash
```

Otherwise repository-level canonical links and Joplin-level `:/id` links would hash differently despite representing the same semantics.

---

# 36. Resource Hashing

Binary resource content may use a byte hash.

If bytes are unchanged, do not rewrite protocol state unnecessarily.

Stable projection reduces sync churn.

---

# 37. Projection Discovery

Build projection using:

```text
direct children
stack
direct children
...
```

Stop descending when a content container has become one Joplin note.

This keeps note boundaries stable.

---

# 38. Exact GET/HEAD/PROPFIND

Exact sync item requests should use known state mappings whenever possible.

Do not rebuild the complete projection tree for every exact file request.

The desired progression is:

```text
request path
-> sync item id
-> projection state record
-> exact repository area/resource
```

---

# 39. Root Discovery

Root listing is the expensive but legitimate bulk operation.

Even then:

- iterate,
- yield periodically if needed,
- avoid hidden recursive child-provider calls,
- preserve deterministic order,
- tolerate transient read failure conservatively.

---

# 40. Thread Yielding

For long synchronous projection loops, periodic:

```csharp
Thread.Yield();
```

can reduce monopolization.

This does not replace cancellation or asynchronous architecture, but it can improve cooperative behavior within the project's explicit no-async coding constraint.

---

# 41. Resource Metadata Projection

Joplin resource metadata includes information such as:

- ID,
- MIME type,
- file extension,
- file name/title,
- size,
- timestamps.

Repository `KnowledgeResourceInfo` provides provider-neutral metadata.

Do not derive repository identity from Joplin metadata.

---

# 42. Resource File Names

Repository `preferredFileName` is only a hint.

When Joplin uploads a resource, pass a meaningful file name if available.

The repository provider remains free to:

- preserve it,
- sanitize it,
- generate another physical name.

The returned `ResourceId` is authoritative.

---

# 43. State Store Separation

`IJoplinSyncStateStore` may hold:

- `projection.json`,
- root sync items,
- resource metadata items,
- resource blobs,
- lock/temp files.

It must not become hidden authoritative knowledge storage.

If state is lost, the repository should still contain the knowledge.

The adapter may need to rebuild mapping state, but knowledge should not disappear.

---

# 44. Profile-Scoped Endpoint Paths

The handler may be constructed with a profile-specific WebDAV base path, for example:

```text
/api/knowledge/joplin/123456789
```

WebDAV href generation must consistently use the configured base path.

Do not hardcode one global path if multiple profiles are supported.

---

# 45. Authentication Boundary

Authentication belongs outside repository semantics.

The Joplin endpoint may be protected by middleware/application auth.

Do not pass Joplin credentials into provider APIs unless the provider architecture explicitly requires independent authentication.

---

# 46. WebDAV Href Generation

`PROPFIND` responses require correctly encoded WebDAV hrefs.

Be careful with:

- base path,
- leading slash,
- collection slash,
- item file name,
- URI escaping.

Do not reuse repository logical area encoding for WebDAV transport paths.

They are separate identity spaces.

---

# 47. No HTTP Knowledge Routes Inside Joplin Markdown

The Joplin adapter should never translate `knowledge-area:` to the HTML or RAW endpoint URL.

Joplin has its own native link identity:

```text
:/<id>
```

The mapping is semantic:

```text
knowledge-area
<-> Joplin item ID
```

not:

```text
knowledge-area
<-> HTTP URL
```

This preserves offline Joplin behavior and Joplin-native navigation.

---

# 48. No Provider-Specific Link Logic

The Joplin adapter may translate:

```text
knowledge-area:
knowledge-resource:
:/id
```

It must not parse:

```text
onenote:
file:
provider-x:
```

If a provider returns provider-specific links, that provider must normalize them before the Joplin adapter sees them.

---

# 49. Area-Link Mapping Through Aggregation

When Joplin consumes an `AggregatedKnowledgeRepository`, the adapter sees aggregate-global paths.

Example:

```text
/Teams/A/Common/Rules
```

It should store exactly that in projection state.

The aggregator is responsible for rebasing mounted provider-local:

```text
knowledge-area:/Common/Rules
```

to:

```text
knowledge-area:/Teams/A/Common/Rules
```

Joplin must not attempt to reconstruct mount information.

---

# 50. Cache Behavior

Joplin is a synchronization endpoint, not a low-latency browse endpoint.

Do **not** automatically force "prefer stale existing cache forever" behavior merely because HTML/RAW use it for performance.

Sync correctness requires observing authoritative changes.

A repository may internally cache according to its own policy, but the Joplin adapter should not globally activate the HTML/RAW prefer-existing optimization unless a specific synchronization design explicitly requires it.

---

# 51. Requirements Matrix

| Requirement | Normative rule |
|---|---|
| Provider neutrality | Joplin depends only on `IKnowledgeRepository`. |
| Joplin state | Stored separately in `IJoplinSyncStateStore`. |
| Structural areas | Project as folders. |
| First content container | Project as note. |
| Nested content containers | Remain note Markdown structure. |
| Resource ID | Repository-owned opaque string. |
| Joplin resource ID | Adapter-owned stable identity. |
| `:/id` classification | Must inspect item type/state. |
| Joplin resource link | Convert to/from `knowledge-resource:`. |
| Joplin note/folder link | Convert to/from `knowledge-area:`. |
| Logical area payload | Preserve exactly; no arbitrary URI decoding. |
| Unknown note target | Pending dependency, not leaked protocol syntax. |
| Exact target mapping | Required for `knowledge-area:` -> Joplin. |
| Heading approximation | Must not silently link to containing note. |
| Parent change | `TryMoveContent`, not copy/delete. |
| Rename | Provider-neutral rename. |
| Resource ID change | Update mapping, preserve Joplin ID. |
| Joplin DELETE | Suppress/reconcile; do not blindly destroy knowledge. |
| Temporary source failure | Do not return false success. |
| Exact sync read | Use projection state; avoid full scan. |
| Root projection | Explicit bulk boundary. |
| Child enumeration | Direct-child iterative traversal. |
| Hashing | Semantic, stable, transport-noise excluded. |
| Repeated PUT | Idempotent. |
| Provider-native links | Must not leak into Joplin adapter. |
| HTTP knowledge URLs | Do not substitute for Joplin `:/id`. |
| Prefer-existing endpoint cache | Not enabled globally for sync. |

---

# 52. Common Traps

## Trap: assume every `:/id` is a resource

Wrong.

Joplin uses the same syntax for note links.

Classify first.

## Trap: leave Joplin note links in repository Markdown

Wrong:

```text
:/abcdef...
```

is Joplin protocol syntax.

Translate to:

```text
knowledge-area:/...
```

## Trap: decode repository areas

Wrong:

```csharp
Uri.UnescapeDataString(
  record.Area
);
```

Repository paths are already canonical logical identities.

## Trap: use HTML URL as Joplin note link

Wrong:

```text
https://host/api/knowledge/...
```

Correct:

```text
:/<JoplinItemId>
```

## Trap: map heading link to containing note silently

Changes semantics.

Leave unresolved or introduce a future explicit anchor mapping.

## Trap: rebuild whole repository on every item GET

Performance disaster for remote providers.

Use projection state.

## Trap: delete repository content on WebDAV DELETE

Unsafe synchronization semantics.

Suppress/reconcile instead.

## Trap: copy+delete for parent move

Breaks identity and atomicity.

Use `TryMoveContent`.

## Trap: recreate Joplin resource ID after repository move

Breaks Joplin references.

Update repository-ID mapping only.

---

# 53. Advanced Example: Note Link Roundtrip

Repository projection state:

```text
Record A:
  Id   = 11111111111111111111111111111111
  Area = /Docs/A

Record B:
  Id   = 22222222222222222222222222222222
  Area = /Docs/B
```

Joplin note A body:

```markdown
See [B](:/22222222222222222222222222222222).
```

Incoming translation:

```markdown
See [B](knowledge-area:/Docs/B).
```

Repository stores canonical provider-neutral Markdown.

Outgoing translation:

```markdown
See [B](:/22222222222222222222222222222222).
```

No HTTP URL and no provider-specific path is involved.

---

# 54. Advanced Example: Resource Roundtrip

Joplin:

```markdown
![Image](:/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa)
```

Classification determines ID is a resource.

Adapter:

```text
Joplin resource ID
-> repository ResourceId
```

Repository Markdown:

```markdown
![Image](knowledge-resource:1.XYZ)
```

Later the repository document is moved.

Repository reports:

```text
1.XYZ -> 1.NEW
```

Adapter updates:

```text
aaaaaaaa... -> 1.NEW
```

Joplin body remains:

```markdown
![Image](:/aaaaaaaa...)
```

Joplin identity stays stable.

---

# 55. Advanced Example: Child Before Parent

Joplin uploads child note:

```text
parent_id = BBB...
```

but parent record does not yet exist.

Correct:

```text
PendingDependency
```

Later parent arrives and is materialized.

Retry child.

Incorrect:

- create child at root,
- guess parent,
- reject permanently,
- duplicate item later.

---

# 56. Advanced Example: Link Target Not Yet Materialized

Incoming body contains:

```text
:/CCCC...
```

metadata reveals CCCC is a note, but no projection record yet maps it to a repository area.

Correct:

```text
containing note remains pending
```

Reason:

The adapter cannot generate correct:

```text
knowledge-area:<area>
```

without the area mapping.

Do not persist the original Joplin syntax into repository content.

---

# 57. Regression Tests

Use MSTest.

## Projection

- direct children only during ordinary discovery.
- child provider never receives unexpected `GetAreas(true)`.
- first content container becomes one note.
- nested headings do not become duplicate notes.
- root bulk projection is deterministic.

## Links

- Joplin resource `:/id` -> `knowledge-resource:`.
- Joplin note `:/id` -> `knowledge-area:`.
- Joplin folder `:/id` -> `knowledge-area:` where applicable.
- unknown `:/id` -> pending.
- `knowledge-area:` exact target -> correct Joplin ID.
- `knowledge-area:` nonexistent exact target remains unchanged.
- heading target is not silently mapped to containing note.
- logical `%20` remains `%20`.
- no arbitrary URI decode.

## Resources

- same repository resource -> same Joplin ID across reads.
- repository ResourceId change updates mapping.
- binary unchanged -> stable hash/no unnecessary rewrite.
- resource replacement preserves Joplin identity.
- missing blob -> pending, not false success.

## Mutations

- parent change uses `TryMoveContent`.
- rename handles resource ID changes.
- child-before-parent retries.
- repeated identical PUT is idempotent.
- unchanged GET is byte-stable.
- temporary provider failure becomes temporary sync failure.

## Safety

- Joplin DELETE does not delete authoritative knowledge.
- resource DELETE does not blindly destroy repository resource.
- conflict rebind does not duplicate knowledge.
- protocol suppression is stored only in sync state.

---

# 58. Bottom-Up Artifact Guide

## `JoplinKnowledgeRepositoryWebDavHandler`

Core protocol adapter.

Owns:

- WebDAV sync item representation,
- projection discovery,
- item serialization/parsing,
- resource mapping,
- link translation,
- mutation application,
- reconciliation.

## `JoplinKnowledgeRepositoryWebDavMiddleware`

Dispatches WebDAV verbs directly.

Avoids MVC/WebDAV mismatch.

## `IJoplinSyncStateStore`

Stores adapter/protocol state.

Must remain separate from repository truth.

## `JoplinProjectionState`

Persistent mapping/state model.

## `JoplinProjectionRecord`

Stable Joplin item identity to logical repository area mapping.

## `JoplinResourceProjectionRecord`

Stable Joplin resource identity to repository `ResourceId` mapping.

## `IKnowledgeRepository`

Only knowledge API the Joplin adapter may depend on.

---

# 59. Implementation Style Rules

For C# changes:

- German user communication,
- English code/comments,
- no `var`,
- avoid unnecessary nullable/coalescing syntax,
- `_` + PascalCase fields/constants,
- XML summaries,
- arrays in signatures,
- explicit `this.` for instance members except fields,
- no async/await unless explicitly approved,
- no ternary operators,
- braces always,
- same-line `else/catch/finally`,
- parenthesized lambda parameters,
- fully implemented properties,
- targeted `DevLogger.LogError(ex)`,
- trace via `DevLogger.LogTrace(0, 99999, "...")`,
- Newtonsoft.Json,
- MSTest only.

---

# 60. Decision Summary

The Joplin architecture intentionally chooses:

1. Joplin as an adapter, never as repository truth,
2. separate persistent sync state,
3. stable Joplin IDs independent from repository IDs,
4. exact semantic translation between repository links and Joplin links,
5. `knowledge-area:` for note/folder references,
6. `knowledge-resource:` for binary resources,
7. pending dependencies instead of guessed mappings,
8. native repository move semantics for Joplin parent changes,
9. suppression instead of destructive WebDAV DELETE mapping,
10. iterative lazy repository traversal,
11. explicit root bulk projection,
12. semantic idempotent hashing,
13. no provider-specific knowledge,
14. no HTML/RAW URL substitution inside Joplin bodies,
15. synchronization correctness over aggressive stale-cache preference.

Future changes should be checked against these decisions before modifying the protocol adapter.
