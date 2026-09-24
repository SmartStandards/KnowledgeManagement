# [AI Skill] Knowledge Repository Core Architecture, Contract, and File-Based Reference Provider

**Status:** Normative implementation guide and architectural memory  
**Scope:** General repository idea, `IKnowledgeRepository` contract, canonical content/resource/link semantics, aggregation/cache extension points, and the exemplary `FileBasedKnowledgeRepository` implementation  
**Language:** English by design, because this file is intended to be durable implementation documentation and AI-skill context  
**Primary goal:** Preserve the provider-neutral architecture while making future implementation work predictable, testable, and resistant to accidental coupling.

---

# 1. Abstract and Motivation

The Knowledge Repository architecture provides a single provider-neutral abstraction for hierarchical knowledge, textual content, embedded resources, mutations, navigation, search, and cross-document references.

The central design goal is deliberately stronger than "wrap a file system" or "expose a wiki through an interface". The system must support fundamentally different providers while preserving one coherent logical model:

- Markdown files and directories,
- OneNote notebooks, sections, pages and headings,
- databases,
- remote HTTP-backed repositories,
- Git-backed repositories,
- virtual or synthesized knowledge views,
- aggregated overlays composed from several repositories.

The positive differentiation from simpler repository implementations is that consumers never need to know how knowledge is physically stored. A browser UI, RAW HTTP endpoint, Joplin adapter, AI client, search component, or future connector can consume only `IKnowledgeRepository`.

This gives several concrete benefits:

- provider implementations remain reusable,
- consumers remain portable,
- physical storage remains replaceable,
- provider-native IDs do not leak into consumers,
- file-system storage can stay human-readable,
- remote providers can remain lazy,
- aggregation can combine heterogeneous providers,
- binary resources remain part of the knowledge domain without becoming fake tree nodes,
- synchronization adapters such as Joplin can maintain their own external identity without contaminating repository semantics.

The architecture intentionally rejects "convenient" shortcuts that weaken abstraction boundaries.

---

# 2. Architectural North Star

The mandatory dependency direction is:

```text
Consumer / Adapter / Endpoint
            |
            v
    IKnowledgeRepository
            |
            v
      Concrete Provider
            |
            v
Physical / Remote Representation
```

A provider may know about its own backing technology.

A consumer must not.

Examples:

```text
HTML endpoint
  -> IKnowledgeRepository
  -> AggregatedKnowledgeRepository
  -> FileBasedKnowledgeRepository
  -> Markdown files
```

```text
Joplin WebDAV
  -> IKnowledgeRepository
  -> OneNoteKnowledgeRepositoryProxy
  -> Microsoft Graph / OneNote
```

```text
RAW endpoint
  -> IKnowledgeRepository
  -> Remote repository wrapper
  -> another server
```

Forbidden dependency direction:

```text
HTML controller
  -> FileBased path syntax

Joplin adapter
  -> Markdown file names

FileBased provider
  -> Joplin IDs

OneNote provider
  -> HTML routes
```

---

# 3. Normative Language

This documentation uses:

- **MUST** / **MUST NOT** for mandatory contract or architecture rules.
- **SHOULD** / **SHOULD NOT** for strong design guidance.
- **MAY** for optional behavior compatible with the architecture.

AI agents and future maintainers should treat these words as requirements, not prose emphasis.

---

# 4. Top-Down Mental Model

A repository exposes a logical ordered tree of **areas**.

Example:

```text
/
├── /Engineering
│   ├── /Engineering/Architecture
│   │   ├── /Engineering/Architecture/Hosting
│   │   └── /Engineering/Architecture/Security
│   └── /Engineering/Coding Rules
└── /Operations
```

The tree is logical.

It is not required to correspond one-to-one to:

- directories,
- files,
- pages,
- notebooks,
- table rows,
- Graph entities.

A provider can synthesize any of these nodes as long as contract semantics remain correct.

---

# 5. Core Area Semantics

## 5.1 Absolute logical paths

All repository area paths are absolute.

Root:

```text
/
```

Examples:

```text
/Organisation
/Organisation/Team%20Software
/Organisation/Team%20Software/Coding%20Rules
```

The path is an **opaque logical address** from the consumer's perspective.

Consumers MUST NOT assume that:

- a segment is a file name,
- `%20` must be decoded to produce a display name,
- square brackets represent directories,
- the final segment is always the display name,
- a slash-separated string is a physical path.

For display names, always call:

```csharp
string GetAreaName(string area);
```

### Critical percent-encoding rule

Some providers use URI-safe logical path segments internally, for example:

```text
/Team%20Software/Coding%20Rules
```

That value may already be the provider's canonical logical area identity.

Therefore:

> Consumers MUST NOT arbitrarily call `Uri.UnescapeDataString` on logical repository area paths.

Transport layers may encode the logical path for HTTP routing, but they must restore the exact original logical area before calling the repository.

---

# 6. `ContentLevel`

The canonical enum is:

```csharp
public enum ContentLevel {
  BeyondContent = 0,
  ContentAggregation = 1,
  ContentContainer = 2
}
```

## 6.1 `BeyondContent`

Pure navigation / structure.

Typical examples:

- repository root,
- structural folder,
- virtual grouping,
- a physical directory that contains only subdirectories.

Text-content operations are not semantically applicable.

## 6.2 `ContentAggregation`

A content-accessible scope that **never owns direct textual content**.

Mandatory invariant:

```text
HasDirectContent(area) == false
GetDirectContent(area) == string.Empty
```

It may expose `GetAggregatedContent`.

Examples:

- notebook,
- directory containing multiple Markdown documents,
- documentation collection,
- virtual "all conclusions" view.

## 6.3 `ContentContainer`

A concrete content-bearing scope.

It may:

- own direct content,
- contain child areas,
- expose aggregated content that includes descendants.

Examples:

- Markdown document,
- Markdown heading subtree,
- OneNote page,
- database article.

---

# 7. Area Ordering

Ordering is semantically significant.

`GetAreas(false, area)` returns direct children in stable natural order.

`GetAreas(true, area)` returns all descendants in **depth-first pre-order**.

Example:

```text
A
├── B
│   ├── C
│   └── D
├── E
└── F
```

Recursive enumeration:

```text
B
C
D
E
F
```

Providers MUST NOT introduce arbitrary alphabetic ordering unless alphabetic ordering is explicitly the provider's natural order.

For Markdown heading trees, order MUST correspond to the document.

---

# 8. Performance Rule: No Accidental Full-Tree Materialization

A central architectural performance rule is:

> No consumer-facing normal read or navigation operation should require materializing the complete repository tree.

This is especially important for:

- Microsoft Graph providers,
- HTTP-backed providers,
- large file trees,
- aggregate repositories,
- remote databases.

Use:

```csharp
GetAreas(false, currentArea)
```

for ordinary navigation.

Treat:

```csharp
GetAreas(true, "/")
```

as an explicit bulk operation, not a primitive used internally by every consumer.

A provider may implement recursive enumeration iteratively using repeated direct-child reads.

---

# 9. `IKnowledgeRepository` Contract

The central interface owns all provider-neutral semantics.

Core method families:

```csharp
string[] GetAreas(
  bool recurse,
  string startArea = "/"
);

string[] GetAreasByKeyword(
  string keyword,
  string startArea = "/"
);

string GetAreaName(
  string area
);

void GetAreaCapabilities(
  string area,
  out ContentLevel contentLevel,
  out bool supportsSubAreas,
  out bool canBeRenamed,
  out bool canBeDeleted,
  out bool canAddSubAreas,
  out bool canAppendContent,
  out bool canTruncate,
  out bool supportsResources
);

bool HasDirectContent(
  string area
);

string GetDirectContent(
  string area
);

string GetAggregatedContent(
  string area
);

bool TryDelete(
  string area
);

bool TryRename(
  string area,
  string newName,
  out KnowledgeResourceIdChange[] resourceIdChanges
);

bool TryAddSubArea(
  string area,
  string name,
  KnowledgeAreaKind kind
);

bool TryAppendContent(
  string area,
  string content
);

bool TryTruncate(
  string area
);

bool TryReplace(
  string area,
  string content
);

bool TryMoveContent(
  string contentAreaToMove,
  string newParentArea,
  out KnowledgeResourceIdChange[] resourceIdChanges
);

KnowledgeResourceInfo[] GetResources(
  string area
);

byte[] GetResourceContent(
  string resourceId
);

bool TryAddResource(
  string area,
  string preferredFileName,
  string contentType,
  byte[] content,
  out string resourceId
);

bool TryReplaceResource(
  string resourceId,
  string contentType,
  byte[] content
);

bool TryDeleteResource(
  string resourceId
);
```

Exact signatures in source remain authoritative if they evolve, but the semantics in this document are normative.

---

# 10. Capability Semantics

`GetAreaCapabilities` is intentionally detailed.

Do not infer mutations from `ContentLevel`.

Important distinctions:

| Capability | Meaning |
|---|---|
| `supportsSubAreas` | Children may logically exist. |
| `canAddSubAreas` | Caller may create a new direct child. |
| `canBeRenamed` | Addressed area may be renamed. |
| `canBeDeleted` | Addressed area and descendants may be deleted. |
| `canAppendContent` | Hierarchical append semantics are supported. |
| `canTruncate` | Content scope may be cleared while keeping the addressed area. |
| `supportsResources` | Content in this scope may reference repository resources. |

A read-only provider may return:

```text
supportsSubAreas = true
canAddSubAreas = false
```

That is normal.

A virtual aggregation may support reads while all mutation flags are false.

---

# 11. Semantic Area Creation

Use:

```csharp
public enum KnowledgeAreaKind {
  Structural = 0,
  Content = 1
}
```

and:

```csharp
bool TryAddSubArea(
  string area,
  string name,
  KnowledgeAreaKind kind
);
```

Consumers specify intent, not provider syntax.

Never encode provider-specific structure into `name`.

Wrong:

```text
"[New Folder]"
```

when brackets only mean "directory" to one provider.

Correct:

```csharp
TryAddSubArea(
  "/Parent",
  "New Folder",
  KnowledgeAreaKind.Structural
);
```

The provider decides physical representation.

---

# 12. Direct and Aggregated Content

## 12.1 Direct content

Direct content belongs only to the addressed area.

For a Markdown heading:

```markdown
## Child

Direct text.

### Grandchild

Grandchild text.
```

The direct content of `Child` is:

```markdown
Direct text.
```

The `### Grandchild` subtree is excluded.

The identifying heading itself is structural and is not direct content.

## 12.2 Aggregated content

Aggregated content includes the addressed content scope and all relevant descendant content in logical order.

For `ContentAggregation`, all content comes from descendants.

For `ContentContainer`, aggregated content includes own direct content and descendant structure.

Reads MUST be side-effect free.

---

# 13. Mutation Semantics

Every mutation MUST be atomic from the consumer's perspective.

Success:

```text
complete logical change visible
```

Failure:

```text
previous externally observable state preserved
```

No half-moved subtree, partial rewrite, orphaned resource, or partially renamed hierarchy may remain.

---

# 14. Delete vs. Truncate

These operations are intentionally different.

## Delete

```csharp
TryDelete(area)
```

removes the area itself and its descendants.

## Truncate

```csharp
TryTruncate(area)
```

preserves the addressed area but clears its content scope according to provider semantics.

Do not implement delete by truncate or vice versa.

---

# 15. Append Semantics

`TryAppendContent` is a logical structured merge.

It is not equivalent to:

```text
File.AppendAllText(...)
```

Incoming structured content may create or extend child areas.

Existing unrelated content must not disappear merely because it is absent from the appended payload.

---

# 16. Replace Semantics

`TryReplace` replaces the content represented through the addressed area.

It should behave as an atomic logical replace.

Do not confuse replace with:

- rename,
- delete/recreate without preserving semantics,
- move,
- append.

---

# 17. Canonical Move Semantics

The canonical operation is:

```csharp
bool TryMoveContent(
  string contentAreaToMove,
  string newParentArea,
  out KnowledgeResourceIdChange[] resourceIdChanges
);
```

The operation means:

> Reparent one logical content scope below a new parent.

It does **not** mean:

```text
copy source content to target
then truncate source
```

It does **not** replace the target parent.

Example:

```text
Before:
/A/Document
/B

Move:
/A/Document -> /B

After:
/A
/B/Document
```

The old parent remains.

The destination parent remains.

Only the selected subtree changes parent.

This matters strongly for:

- Markdown headings,
- files,
- remote page trees,
- Joplin parent changes.

---

# 18. Resources Are Part of the Repository but Not Areas

Resources include:

- images,
- PDFs,
- diagrams,
- attachments,
- binary files.

They belong to the same domain but do not participate in the area hierarchy.

Resources MUST NOT be returned from `GetAreas`.

Canonical textual reference:

```text
knowledge-resource:<ResourceId>
```

Example:

```markdown
![Architecture](knowledge-resource:1.ABCDEF...)
```

`ResourceId` is:

- a string,
- provider-owned,
- opaque to consumers,
- not guaranteed to survive every provider-native rename/move.

Consumers MUST NOT:

- split it,
- Base64-decode it,
- infer paths,
- derive extensions,
- derive parent areas,
- construct new IDs.

---

# 19. Resource ID Changes

A provider-native move or rename may change a resource's opaque repository ID.

This is allowed.

Mutations that may affect resource identity return:

```csharp
KnowledgeResourceIdChange[]
```

with mappings:

```text
PreviousResourceId -> CurrentResourceId
```

This is crucial for stateful adapters such as Joplin that keep their own stable external resource identity.

---

# 20. Canonical Provider-Neutral Area Links

Repository-facing Markdown may use:

```text
knowledge-area:<logical-area-path>
```

Example:

```markdown
[Coding Rules](knowledge-area:/Organisation/Team%20Software/Coding%20Rules)
```

This scheme is analogous to `knowledge-resource:`.

Requirements:

- the target is a logical repository area,
- no provider name appears,
- no HTML route appears,
- no OneNote ID appears,
- no Joplin ID appears,
- no physical file path appears.

### Important encoding decision

The payload is the repository's canonical logical area representation.

Therefore, if the provider's logical area contains `%20`, consumers and wrappers MUST preserve `%20` exactly.

Do not perform an unconditional URI-decode/re-encode roundtrip at repository boundaries.

HTTP adapters translate this logical identity into transport-safe URLs.

Aggregation wrappers rebase provider-local logical area links into aggregate-global logical area links.

---

# 21. Aggregation Wrapper Semantics

`AggregatedKnowledgeRepository` combines repositories under logical mount points.

Example:

```text
Provider A mounted at /Knowledge/A
Provider B mounted at /Knowledge/B
```

Each provider may internally expose:

```text
/
/Common
/Common/Coding%20Rules
```

Aggregate paths become:

```text
/Knowledge/A/Common/Coding%20Rules
/Knowledge/B/Common/Coding%20Rules
```

## 21.1 Overlay semantics

When multiple providers contribute to the same global area:

- visible area appears once,
- read contributions may be combined,
- registration order remains significant,
- mutation routing is conservative,
- ambiguous mutations are rejected.

## 21.2 Mount ancestors

Mounting:

```text
/Company/Engineering
```

may synthesize:

```text
/Company
```

even when no provider owns `/Company`.

Synthetic ancestors are local aggregate structure.

## 21.3 Link rebasing

If a mounted provider returns:

```markdown
[Rules](knowledge-area:/Common/Coding%20Rules)
```

and is mounted under:

```text
/Knowledge/A
```

the aggregator must expose:

```markdown
[Rules](knowledge-area:/Knowledge/A/Common/Coding%20Rules)
```

Otherwise identical provider-local links from several mounted repositories collide globally.

This rebasing belongs in the aggregation wrapper, not in HTML, RAW, FileBased, OneNote, or Joplin.

---

# 22. Optional Local Cache Control

Caching is an optimization layer, not part of the transport-neutral repository contract.

Optional local capability:

```csharp
public interface IKnowledgeRepositoryCacheControl {
  bool IsAreaCached(string area);
  IDisposable BeginPreferExistingScope();
}
```

Semantics:

## `IsAreaCached`

Passive only.

It MUST NOT trigger source access, recursive materialization, or remote calls.

## `BeginPreferExistingScope`

Inside this local scope:

```text
existing cache entry
-> return it regardless of normal expiration

missing cache entry
-> load from source
-> cache it
-> return it
```

This is "prefer existing", not "offline only".

It is suitable for performance-sensitive local consumer layers such as HTML and RAW endpoints.

Decorators and aggregators between the consumer and actual cache wrapper must propagate the capability if they want the outer consumer to use it.

---

# 23. File-Based Provider: Physical Model

`FileBasedKnowledgeRepository` is the exemplary provider because it demonstrates how a concrete physical model can implement the provider-neutral contract without leaking its details.

Physical root:

```text
KnowledgeRoot/
```

maps to logical:

```text
/
```

Physical example:

```text
KnowledgeRoot/
  Organisation/
    Coding Rules.md
    Architecture.md
    SharedDiagram.png
```

A concrete FileBased implementation may expose directory and document logical segments using its own encoding conventions.

The consumer never depends on those conventions.

---

# 24. File-Based Content-Level Mapping

Reference behavior:

- directories with no directly contained Markdown files can act as `BeyondContent`,
- directories with directly contained Markdown files can act as `ContentAggregation`,
- Markdown documents are `ContentContainer`,
- headings inside Markdown documents are nested `ContentContainer` areas.

Directory aggregation is intentionally one physical directory level.

Subdirectories remain separate structural areas instead of being recursively swallowed into a parent aggregated content response.

This avoids surprising huge reads and keeps navigation meaningful.

---

# 25. Human-Readable Storage Is an Architectural Requirement

Physical Markdown must remain ordinary usable Markdown.

Wrong physical persistence:

```markdown
![Image](knowledge-resource:1.XYZ)
```

Correct physical persistence:

```markdown
![Image](diagram.png)
```

or:

```markdown
![Logo](../Shared/logo.png)
```

Repository-facing reads translate physical relative references into:

```markdown
![Image](knowledge-resource:<opaque-id>)
```

Repository-facing writes translate canonical resource references back into normal physical Markdown links.

The file system itself remains authoritative.

No hidden resource database should be required to understand manually maintained Markdown.

---

# 26. File-Based Area Segment Encoding

The FileBased provider has historically used a reversible logical segment encoding similar to:

```csharp
value
  .Replace("%", "%25", StringComparison.Ordinal)
  .Replace("/", "%2F", StringComparison.Ordinal)
  .Replace("\\", "%5C", StringComparison.Ordinal)
  .Replace("[", "%5B", StringComparison.Ordinal)
  .Replace("]", "%5D", StringComparison.Ordinal);
```

Decoding occurs only inside the provider.

This is a major abstraction lesson:

> A consumer must never decode a FileBased area segment.

For a FileBased provider, `%20` may be literal logical data or part of provider encoding rules. Only the provider knows.

---

# 27. Markdown Parsing Model

A file-based Markdown implementation typically builds an internal document tree:

```text
Document
├── direct content
├── H1/H2 child
│   ├── direct content
│   └── nested heading
└── sibling heading
```

The parser must preserve:

- heading order,
- heading depth,
- content blocks,
- relative resource references,
- sufficient source structure to perform stable mutations.

Do not flatten the document if later mutations need to preserve hierarchy.

---

# 28. File-Based Resource Discovery

A normal physical Markdown resource reference such as:

```markdown
![Diagram](diagram.png)
```

is resolved relative to the document location.

The provider maps that physical file to an opaque public `ResourceId`.

No sidecar registration is required merely because the user manually dropped an image into the directory and referenced it.

The provider must validate that resolved resource paths remain inside the configured repository root.

---

# 29. Owned vs. Free Resources

The FileBased implementation distinguishes conceptually between:

## Owned resource

A generated resource using the strict naming convention:

```text
<Document>.Res<Token>.<ext>
```

This is treated as belonging to the document.

When the document moves, the owned resource may move with it.

That may change its provider-native path and therefore its `ResourceId`.

The move must report the ID change.

## Free/shared resource

Any resource that does not match the strict ownership convention.

Examples:

```text
logo.png
Shared/architecture.svg
company.pdf
```

A free resource does not move merely because one referencing document moves.

Instead, physical Markdown links are repaired so the moved document continues to reference the same resource.

This distinction protects manually managed shared assets.

---

# 30. Resource Naming

When a new resource has no usable safe file name or collides with existing storage, a generated fallback may use:

```text
<Document>.Res<Snowflake44>.<ext>
```

The exact token implementation is provider detail.

The public repository `ResourceId` remains opaque.

Do not expose the Snowflake token as a cross-provider semantic requirement.

---

# 31. Resource Deletion

`TryDeleteResource` must reject deletion while repository-exposed textual content still references the resource.

A resource endpoint or adapter must not silently destroy an attachment still referenced by a document.

The provider must verify reference safety according to its model.

---

# 32. File-Based Mutation Atomicity

The FileBased reference implementation uses a mutation scope.

Conceptual flow:

```text
lock
  -> create snapshot
  -> perform logical mutation
  -> persist all changed documents/files
  -> if success: remove snapshot
  -> if failure: restore snapshot
```

Expected targeted exceptions include:

- `IOException`,
- `UnauthorizedAccessException`.

These should be logged with:

```csharp
DevLogger.LogError(ex);
```

A mutation must return failure after rollback rather than leaving partial state.

---

# 33. Non-Destructive Rollback

Rollback must be conservative.

A dangerous anti-pattern is:

```text
delete repository root
then restore backup
```

If restoration fails, this destroys the previously recoverable source.

Prefer restoration strategies that preserve recoverability until the restored state is known to be usable.

---

# 34. Temporary File Locks and Retries

File-system providers must expect short-lived locks caused by:

- editors,
- antivirus,
- synchronization tools,
- indexing,
- backup software.

Targeted retry behavior is appropriate for expected file I/O contention.

Do not wrap every exception in a generic retry loop.

Do not hide permanent permission errors indefinitely.

---

# 35. Physical Moves

## 35.1 Moving a document

A document move may require:

- moving the `.md` file,
- moving owned resources,
- repairing relative references to free/shared resources,
- updating references from other documents if required by the provider's semantics,
- reporting resource ID changes.

## 35.2 Moving a heading subtree

A heading move must:

- remove exactly the selected heading subtree from the old parent,
- preserve the old parent,
- preserve the destination parent,
- insert the subtree in a stable logical position,
- adjust heading levels if necessary,
- preserve nested heading relationships.

Never implement heading move by replacing entire source and target documents using naive string concatenation.

---

# 36. Soft Delete

Soft delete is an optional safety feature for explicit delete operations.

It MUST NOT be used as an implementation mechanism for move.

A move should be a move.

If a move produces `.DELETED` artifacts, the implementation is conceptually wrong.

---

# 37. Git-Based Derivation

A Git-backed provider may derive from or wrap FileBased behavior.

Git responsibilities may include:

- synchronize,
- commit,
- push,
- conflict handling.

Git must not redefine logical repository semantics.

The logical operation still means:

```text
TryMoveContent
TryRename
TryReplace
...
```

not:

```text
git mv
```

at the contract level.

---

# 38. Read Robustness

Consumers should not assume every provider is always available.

Wrappers such as `AggregatedKnowledgeRepository` should isolate read failures where possible:

- one failing provider should not erase healthy siblings,
- missing metadata from one contribution should not necessarily fail an entire aggregate page,
- mutations remain strict because partial writes are unsafe.

This distinction is important:

```text
read degradation
!=
write ambiguity
```

---

# 39. Error Model

The interface largely communicates mutation rejection through `bool`.

Read failures may throw provider-appropriate exceptions.

Consumers must not use provider-specific exception types as semantic dependencies.

Expected provider-level failures should be translated at endpoint boundaries.

---

# 40. Security Boundaries

FileBased must prevent path traversal.

Never trust:

- logical area strings,
- resource paths,
- user-supplied file names,
- `..`,
- rooted paths,
- encoded separators.

Resolve physical paths and verify that they remain inside the configured root.

Do not rely solely on string prefix comparison without canonical full-path normalization.

---

# 41. Requirements Matrix

| Requirement | Normative rule |
|---|---|
| Provider neutrality | Consumers depend only on `IKnowledgeRepository`. |
| Absolute areas | Every area starts with `/`; root is `/`. |
| Area opacity | Consumers must not infer provider physical semantics from path segments. |
| Display name | Use `GetAreaName`, not path decoding. |
| Stable order | Direct and recursive enumeration preserve natural sibling order. |
| Recursive order | Recursive enumeration is depth-first pre-order. |
| Performance | Normal navigation must not require full-tree materialization. |
| Reads | Reads do not mutate source. |
| ContentAggregation | No direct content. |
| ContentContainer | May own direct content and descendants. |
| Creation | `KnowledgeAreaKind` expresses semantic intent. |
| Capabilities | Queried per area; do not infer from provider type. |
| Delete | Removes addressed area itself. |
| Truncate | Keeps addressed area, clears content scope. |
| Move | Logical reparenting, not copy+truncate. |
| Mutation atomicity | All-or-nothing externally observable result. |
| Resources | Same repository domain, but not tree areas. |
| Resource reference | `knowledge-resource:<opaque-id>`. |
| Area reference | `knowledge-area:<canonical-logical-area>`. |
| Resource IDs | Opaque, provider-owned, may change. |
| ID changes | Rename/move report mappings when needed. |
| File storage | Human-readable ordinary Markdown remains authoritative. |
| File resource links | Physical Markdown uses normal relative links. |
| No sidecar dependency | File system itself must remain understandable. |
| Owned resources | Strict generated naming convention. |
| Free resources | Remain in place on document move; references repaired. |
| Soft delete | Optional safety feature; never a move mechanism. |
| Cache control | Optional local optimization, not repository transport contract. |
| Aggregation | Rebase provider-local area links into global mount namespace. |

---

# 42. Common Traps and How to Avoid Them

## Trap: decode logical paths in consumers

Wrong:

```csharp
string area =
  Uri.UnescapeDataString(
    repositoryArea
  );
```

Why wrong:

The provider's logical identity may already use percent-like encoding.

Correct:

Pass the logical area back to the provider unchanged.

Only HTTP transport encoding belongs in an endpoint layer.

## Trap: parse `ResourceId`

Wrong:

```csharp
string path =
  DecodeResourceId(resourceId);
```

outside the owning provider.

Correct:

```csharp
byte[] content =
  repository.GetResourceContent(
    resourceId
  );
```

## Trap: recursive enumeration for every page

Wrong:

```csharp
repository.GetAreas(
  true,
  "/"
);
```

during ordinary navigation.

Correct:

```csharp
repository.GetAreas(
  false,
  currentArea
);
```

## Trap: provider-specific area creation syntax

Wrong:

```csharp
TryAddSubArea(
  area,
  "[Folder]",
  ...
);
```

Correct:

Use `KnowledgeAreaKind.Structural`.

## Trap: move by append + truncate

This breaks:

- identity,
- ordering,
- references,
- resources,
- atomicity.

Use provider-native reparenting semantics.

## Trap: persist `knowledge-resource:` into physical Markdown

This makes native storage dependent on the API.

Translate only at the FileBased boundary.

## Trap: treat every non-Markdown file as an area

Binary resources are not areas.

## Trap: force lifetime-stable repository resource IDs

Provider-native identities may change.

Use `KnowledgeResourceIdChange`.

---

# 43. Advanced Example: File Move with Shared and Owned Resources

Physical state:

```text
Docs/
  A/
    Article.md
    Article.Res123.png
  Shared/
    logo.png
```

`Article.md`:

```markdown
![Owned](Article.Res123.png)

![Shared](../Shared/logo.png)
```

Move:

```text
/A/Article
-> parent /B
```

Correct behavior:

```text
B/
  Article.md
  Article.Res123.png

Shared/
  logo.png
```

The owned resource moves.

The shared resource stays.

The moved Markdown's relative link to `Shared/logo.png` is repaired.

If moving the owned resource changes its provider-native resource identity, the mutation returns:

```text
old ResourceId -> new ResourceId
```

---

# 44. Advanced Example: Aggregated Mounts with Identical Provider Trees

Four providers each expose:

```text
/
/Common
/Common/Rules
```

Mounted as:

```text
/Teams/A
/Teams/B
/Teams/C
/Teams/D
```

Correct aggregate tree:

```text
/Teams/A/Common/Rules
/Teams/B/Common/Rules
/Teams/C/Common/Rules
/Teams/D/Common/Rules
```

A provider-local link:

```text
knowledge-area:/Common/Rules
```

must be rebased per contribution.

Otherwise every mounted branch may link to the same global destination.

---

# 45. Testing Strategy

All .NET unit tests MUST use MSTest.

High-value tests:

- `GetAreas(false)` returns only direct children.
- `GetAreas(true)` preserves pre-order.
- sibling order remains stable.
- read operations do not modify timestamps/content.
- `ContentAggregation.GetDirectContent` is empty.
- `ContentAggregation.HasDirectContent` is false.
- rename preserves descendants and sibling position.
- delete removes exactly the selected subtree.
- truncate preserves addressed area.
- append is sparse merge.
- move preserves both old and new parents.
- heading move preserves subtree.
- document move does not create soft-delete artifacts.
- owned resource moves with document.
- free resource remains physically shared.
- resource ID change is reported.
- physical Markdown contains ordinary relative resource links.
- repository-facing Markdown contains canonical `knowledge-resource:` links.
- manual resource requires no sidecar registration.
- path traversal is rejected.
- transient file lock retry works.
- failed mutation restores previous state.
- aggregator does not call child `GetAreas(true)` for normal navigation.
- aggregator isolates one failing provider on reads.
- `knowledge-area:` is rebased through aggregate mounts.
- consumer code does not decode logical area path segments.

---

# 46. Bottom-Up Artifact Guide

## `IKnowledgeRepository`

The central provider-neutral contract.

Owns:

- hierarchy,
- content,
- mutations,
- search,
- resources.

## `ContentLevel`

Defines content participation, not storage type.

## `KnowledgeAreaKind`

Defines semantic creation intent.

## `KnowledgeResourceInfo`

Resource metadata with opaque `ResourceId`.

## `KnowledgeResourceIdChange`

Old-to-new resource identity mapping after provider-native mutation.

## `FileBasedKnowledgeRepository`

Reference physical provider.

Owns:

- directory/document/heading projection,
- physical Markdown parsing,
- physical resource links,
- opaque resource IDs,
- human-readable persistence,
- atomic file mutations,
- resource ownership semantics.

## `AggregatedKnowledgeRepository`

Logical mount and overlay wrapper.

Owns:

- mount rebasing,
- synthetic ancestors,
- read overlays,
- conservative mutation routing,
- provider-local `knowledge-area:` rebasing,
- optional cache-control propagation.

## `KnowledgeRepositoryCacheWrapper`

Local read optimization wrapper.

Must remain semantically invisible to the contract.

## `IKnowledgeRepositoryCacheControl`

Optional local capability for consumers that explicitly want prefer-existing cache semantics.

---

# 47. Implementation Style Rules for This Project

For generated C#:

- communicate with the user in German,
- code and comments in English,
- do not use `var`,
- avoid nullable/coalescing syntax unless genuinely needed,
- class fields/constants use `_` + PascalCase,
- methods have XML `<summary>` documentation,
- arrays are preferred for collections transported through signatures,
- use explicit `this.` for instance members except fields,
- no async/await pattern unless explicitly approved,
- no ternary inline-if,
- always use braces,
- opening brace remains on the declaration/control line,
- `else`, `catch`, `finally` remain on the same line as the preceding closing brace,
- lambdas use parenthesized parameters,
- properties are fully implemented,
- targeted expected exceptions use `DevLogger.LogError(ex)`,
- trace uses `DevLogger.LogTrace(0, 99999, "...")`,
- avoid Windows-only interop,
- use Newtonsoft.Json instead of System.Text.Json,
- MSTest only for unit tests.

---

# 48. Decision Summary

The architecture intentionally chooses:

1. one strong provider-neutral repository interface,
2. logical areas rather than physical storage objects,
3. semantic capability discovery instead of provider type checks,
4. human-readable file storage,
5. canonical repository-level resource and area link schemes,
6. opaque provider-owned resource IDs,
7. explicit resource-ID-change reporting,
8. native move semantics,
9. lazy navigation,
10. optional local cache control below consumers,
11. aggregation that rebases provider-local identities into a global mount namespace,
12. strict separation between repository truth and adapter-specific state.

Any future change should be evaluated against those decisions before code is modified.
