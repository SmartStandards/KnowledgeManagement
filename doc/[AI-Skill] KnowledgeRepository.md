# [AI Skill] Provider-Neutral Knowledge Repository

## 1. Purpose and Status

This document defines the desired architecture, semantics, implementation rules, and AI coding guidance for the `KnowledgeManagement.SmartStandards` knowledge repository ecosystem.

It is a **normative desired-state document**. It is not a migration log and must not preserve obsolete implementation details merely because they existed in an earlier revision. When existing code contradicts this document, the desired semantics described here take precedence unless a later explicit project decision supersedes them.

The primary goal is to provide one small, provider-neutral knowledge abstraction that can represent hierarchical wiki/documentation content across very different physical storage technologies while keeping provider-specific knowledge inside the provider implementation.

Typical providers and adapters include:

- file-system-backed Markdown repositories,
- Git-backed Markdown repositories,
- aggregated/overlay repositories,
- database or remote repositories,
- Joplin WebDAV projection,
- REST or MCP access layers,
- future providers that are currently unknown.

The architecture intentionally does **not** model files, folders, Joplin notes, notebooks, Markdown headings, database records, or Git artifacts directly in the public abstraction. These are provider concerns.

The desired result is that an arbitrary future implementation of `IKnowledgeRepository` can be introduced without requiring Joplin, REST, MCP, or other consumers to learn that provider's storage conventions.

---

## 2. Core Architectural Principles

### 2.1 Provider neutrality is the highest-level rule

`IKnowledgeRepository` describes logical knowledge semantics.

Consumers must not depend on:

- physical file paths,
- directory markers,
- Markdown file names,
- heading syntax,
- Joplin item IDs,
- Git repository layout,
- database keys,
- provider-specific naming conventions,
- provider-specific resource layouts.

A provider may internally use any of these concepts, but it must expose only the logical contract.

The following dependency direction is mandatory:

```text
Consumer / Adapter
        |
        v
IKnowledgeRepository
        |
        v
Concrete provider
        |
        v
Physical representation
```

A concrete provider must never contain knowledge about a specific adapter such as Joplin.

A Joplin adapter must never contain knowledge about a specific provider such as `FileBasedKnowledgeRepository`.

---

### 2.2 One repository interface

Knowledge hierarchy, textual content, and wiki resources belong to one problem domain and are exposed through one `IKnowledgeRepository` interface.

Resources are **not** modeled as areas, but they remain part of the same repository abstraction.

Do not introduce a parallel `IKnowledgeResourceRepository` merely to separate binary resources from textual content.

Do not introduce optional provider-specific interfaces such as:

```csharp
IKnowledgeRepositoryAreaMoveSupport
```

when the operation has a coherent provider-neutral meaning.

The repository contract should remain semantically strong enough that consumers can rely on one interface.

---

### 2.3 Do not proliferate overlapping operations

One operation should have one universally coherent semantic meaning.

A previous design considered separate move concepts for files, headings, or provider-native nodes. This is explicitly rejected.

The canonical move operation is a logical reparenting operation:

```csharp
bool TryMoveContent(
  string contentAreaToMove,
  string newParentArea,
  out KnowledgeResourceIdChange[] resourceIdChanges
);
```

The concrete provider decides whether that means:

- moving a Markdown heading subtree,
- moving a Markdown file,
- moving a database node,
- changing a remote parent reference,
- moving another provider-native structure.

Consumers must not know which physical action occurred.

---

### 2.4 Human-editable native storage remains valuable

A file-based provider is not merely a serialization format for `IKnowledgeRepository`.

Its on-disk representation must remain useful independently from the repository API.

In particular:

- Markdown files should be normal standalone Markdown files.
- Embedded images should use normal relative Markdown file references.
- A user should be able to open and edit the Markdown files with ordinary Markdown tools.
- A user should be able to add a local image manually without having to register it in hidden metadata.
- No provider-private sidecar database or hidden mapping file should be required to understand the repository.

The file system itself is the authoritative representation of a file-based repository.

---

## 3. Normative Language

The terms **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** are used normatively.

AI agents generating code for this architecture must treat these words as design requirements rather than stylistic suggestions.

---

# 4. Logical Area Model

## 4.1 Absolute logical paths

Areas are addressed by absolute logical paths.

The repository root is:

```text
/
```

All area paths begin with `/`.

Area paths are logical addresses. They are not physical file-system paths.

A consumer must never derive provider-specific information from the representation of an area path.

When a human-readable area name is required, use:

```csharp
string GetAreaName(string area);
```

Do not decode or split a logical path and assume that the final path segment is a provider-neutral display name.

---

## 4.2 `ContentLevel`

The repository distinguishes three logical content levels.

### `BeyondContent`

A structural/navigation area outside textual content access.

Typical examples may include:

- a physical directory containing only subdirectories,
- a navigation root,
- a provider-defined structural node.

It may contain sub-areas but has no textual content semantics of its own.

---

### `ContentAggregation`

A content-accessible scope that never owns direct content.

Required semantics:

```text
HasDirectContent(area) == false
GetDirectContent(area) == string.Empty
```

Aggregated content may still be meaningful.

A `ContentAggregation` area may be:

- a physical notebook/folder-like scope,
- a documentation collection,
- a virtual read-only aggregation,
- a cross-cutting provider-generated projection.

It may expose mutation capabilities, but these are independent from its content level.

---

### `ContentContainer`

A concrete content-bearing logical scope.

It may:

- own direct textual content,
- contain sub-areas,
- contribute to aggregated textual content.

For a Markdown provider this may correspond to:

- one Markdown document,
- one heading inside a Markdown document.

That mapping is provider-specific and must not leak into consumers.

---

# 5. Area Enumeration and Ordering

## 5.1 `GetAreas`

Canonical semantics:

```csharp
string[] GetAreas(
  bool recurse,
  string startArea = "/"
);
```

When `recurse == false`:

- return only direct children.

When `recurse == true`:

- return all descendants,
- use depth-first pre-order traversal,
- each parent appears before its descendants,
- sibling order remains stable and meaningful.

Providers must not arbitrarily sort areas unless that sorting is explicitly the provider's natural logical order.

For Markdown headings, sibling order must match document order.

Reads must never mutate, normalize, rewrite, or otherwise modify the underlying repository.

---

## 5.2 `GetAreasByKeyword`

The contract includes:

```csharp
string[] GetAreasByKeyword(
  string keyword,
  string startArea = "/"
);
```

This method must remain part of `IKnowledgeRepository`.

Matching strategy may be provider-defined, but returned areas must be valid logical paths in the same repository.

The operation is read-only.

---

# 6. Semantic Area Creation

Provider-specific naming conventions must not be used to request different kinds of logical children.

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

The caller states logical intent only.

The provider decides how to physically represent it.

Examples:

```text
Structural
```

may become a directory, notebook, database grouping node, or virtual structure.

```text
Content
```

may become a Markdown file, note, database record, or another content-bearing unit.

A Joplin adapter must never request a FileBased-specific syntax such as `[Folder]`.

---

# 7. Area Capabilities

Capabilities are queried per concrete area:

```csharp
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
```

Important distinctions:

- `supportsSubAreas` means children may logically exist.
- `canAddSubAreas` means the caller may create new children.
- These are not equivalent.

`supportsResources` means content addressed through this area may use canonical repository resource references and the provider can resolve them.

Resources still do not become areas.

A virtual aggregation may legitimately be read-only while still exposing aggregated content.

---

# 8. Textual Content Semantics

## 8.1 Direct content

```csharp
bool HasDirectContent(string area);
string GetDirectContent(string area);
```

Direct content belongs only to the addressed area.

Descendant content must not be included.

For `ContentAggregation`:

```text
HasDirectContent == false
GetDirectContent == string.Empty
```

For a heading-backed Markdown area, the heading itself is structural representation. Direct content is the text after that heading and before the first subordinate heading.

---

## 8.2 Aggregated content

```csharp
string GetAggregatedContent(string area);
```

For `ContentContainer`:

- includes own direct content,
- includes descendant content in logical order.

For `ContentAggregation`:

- consists entirely of subordinate content.

Aggregation may be physical or virtual.

The provider must render a deterministic valid textual representation.

---

# 9. Mutation Semantics

All mutations are atomic from the consumer's perspective.

A mutation either:

- completes fully,

or:

- leaves the repository in its previous externally observable state.

This rule applies across text, hierarchy, and affected resources.

---

## 9.1 Delete

```csharp
bool TryDelete(string area);
```

Deletes the addressed area itself and its complete descendant tree.

This differs fundamentally from truncation.

Provider-specific physical deletion behavior is hidden.

---

## 9.2 Rename

Renaming changes the logical name of an area while preserving:

- content,
- descendants,
- sibling position.

Because a provider-native resource identity may depend on location or name, rename may also change resource identifiers.

The desired signature is therefore:

```csharp
bool TryRename(
  string area,
  string newName,
  out KnowledgeResourceIdChange[] resourceIdChanges
);
```

When no resource identifier changes:

```csharp
resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();
```

Consumers that persist mappings to repository resource IDs must process this array.

---

## 9.3 Append

`TryAppendContent` is a sparse hierarchical merge, not a physical append-to-file operation.

It may merge incoming structured content into several existing descendant branches.

Existing content must not be deleted merely because it is absent from the incoming payload.

---

## 9.4 Truncate

```csharp
bool TryTruncate(string area);
```

Preserves the addressed area itself while clearing its content scope according to provider semantics.

For a content aggregation this may remove direct subordinate content while preserving structural aspects that are outside the truncation semantics.

---

## 9.5 Replace

`TryReplace` logically replaces the content represented through the addressed area.

It is not a rename and not a move.

Providers must preserve unrelated content and structures.

---

# 10. Canonical Move Semantics

## 10.1 Meaning

Canonical desired signature:

```csharp
bool TryMoveContent(
  string contentAreaToMove,
  string newParentArea,
  out KnowledgeResourceIdChange[] resourceIdChanges
);
```

Parameter names are intentionally asymmetric.

`contentAreaToMove`:

- the concrete logical scope whose parent changes.

`newParentArea`:

- the destination parent under which the moved scope is inserted.

`newParentArea` is never:

- replaced,
- truncated,
- interpreted as the moved content itself.

---

## 10.2 Basic invariants

The moved scope preserves:

- logical name,
- direct content,
- descendants,
- relative descendant ordering.

The previous parent remains and loses only the moved child.

Example:

```text
/[FolderA]/Document
/[FolderB]
```

Operation:

```csharp
TryMoveContent(
  "/[FolderA]/Document",
  "/[FolderB]",
  out resourceIdChanges
);
```

Logical result:

```text
/[FolderA]
/[FolderB]/Document
```

The old parent is not deleted.

---

## 10.3 Heading example

Before:

```text
/Doc/A/B
/Doc/C
```

Operation:

```csharp
TryMoveContent(
  "/Doc/A/B",
  "/Doc/C",
  out resourceIdChanges
);
```

After:

```text
/Doc/A
/Doc/C/B
```

`A` remains.

`C` remains.

Only `B` and its subtree move.

---

## 10.4 Move is not append-plus-truncate

The old interpretation:

```text
copy target content
+
truncate source
```

is obsolete.

Do not implement move by composing `TryAppendContent` and `TryTruncate`.

A move changes parent relationship and may require provider-native operations that cannot be represented as text copy/delete.

For FileBased this may be a physical file move or a Markdown heading subtree relocation.

---

# 11. Resource Model

## 11.1 Resources belong to the wiki problem domain

Images, PDFs, diagrams, and other embedded files are an essential part of wiki/documentation content.

They belong in `IKnowledgeRepository`.

However, resources are **not areas**.

They do not participate in:

- area traversal,
- parent/child area relationships,
- `ContentLevel`,
- textual aggregation.

Text references resources using a provider-neutral canonical reference.

---

## 11.2 Canonical textual resource reference

The canonical repository-level form is:

```text
knowledge-resource:<ResourceId>
```

Example:

```markdown
![Architecture](knowledge-resource:1.RG9jcy9BcnRpY2xlLlJlczEyMy5wbmc)
```

The value following `knowledge-resource:` is an opaque repository resource identifier.

Consumers must not parse it.

---

# 12. `ResourceId` Is Opaque and Provider-Owned

## 12.1 Public type

The desired public resource identifier type is:

```csharp
string ResourceId
```

The previous globally stable `long ResourceUid` model is superseded.

A resource identifier is:

- unique within the repository at the time it is exposed,
- generated or derived by the concrete provider,
- opaque to consumers,
- not guaranteed to remain stable across provider-native rename/move operations.

---

## 12.2 No semantic interpretation by consumers

Consumers MUST NOT:

- split a resource ID,
- decode it,
- treat parts as paths,
- derive parent areas from it,
- generate new IDs by composition,
- infer a file extension from it,
- infer provider type from it.

Even when a concrete provider uses a reversible encoding internally, that encoding is private implementation detail.

---

## 12.3 Best practice for public IDs

Providers SHOULD avoid exposing identifiers whose semantic structure invites external interpretation.

Bad example for a public ID:

```text
Docs/Architecture/diagram.png
```

This strongly invites callers to treat the ID as a path.

Preferred public form:

```text
1.RG9jcy9BcmNoaXRlY3R1cmUvZGlhZ3JhbS5wbmc
```

The second value may internally encode the first, but consumers must treat it as opaque.

This is not intended as encryption or secrecy.

The purpose is abstraction discipline.

---

## 12.4 Identifier changes are legitimate

Some providers have native identities that change when a resource moves or is renamed.

This is allowed.

The contract therefore does not claim that `ResourceId` is lifetime-stable.

Operations that knowingly change provider-native resource identity must report old-to-new identifier mappings.

---

# 13. Resource ID Change Reporting

Use a provider-neutral change descriptor:

```csharp
public sealed class KnowledgeResourceIdChange {

  private string _PreviousResourceId;
  private string _CurrentResourceId;

  /// <summary>
  /// Gets or sets the resource identifier that was valid before the mutation.
  /// </summary>
  public string PreviousResourceId {
    get {
      return _PreviousResourceId;
    }
    set {
      _PreviousResourceId = value;
    }
  }

  /// <summary>
  /// Gets or sets the resource identifier that is valid after the mutation.
  /// </summary>
  public string CurrentResourceId {
    get {
      return _CurrentResourceId;
    }
    set {
      _CurrentResourceId = value;
    }
  }
}
```

This is primarily required for operations such as:

- moving a content scope,
- renaming a content scope,
- provider-native collision resolution.

Adapters that keep their own stable identities, such as Joplin, use these changes to update their mapping without recreating the external resource identity.

---

# 14. Desired Resource Contract

The exact final API may evolve while preserving these semantics, but the target shape is:

```csharp
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

`preferredFileName` is a hint.

A provider may preserve it if meaningful.

A provider may choose another physical name when:

- no usable name exists,
- the name is unsafe,
- the name collides,
- the provider has no file-name concept.

The returned `resourceId` is authoritative.

---

## 14.1 `KnowledgeResourceInfo`

A resource information object should expose semantic metadata such as:

```csharp
public sealed class KnowledgeResourceInfo {

  private string _ResourceId;
  private string _FileName;
  private string _ContentType;
  private long _Length;

  public string ResourceId {
    get {
      return _ResourceId;
    }
    set {
      _ResourceId = value;
    }
  }

  public string FileName {
    get {
      return _FileName;
    }
    set {
      _FileName = value;
    }
  }

  public string ContentType {
    get {
      return _ContentType;
    }
    set {
      _ContentType = value;
    }
  }

  public long Length {
    get {
      return _Length;
    }
    set {
      _Length = value;
    }
  }
}
```

`FileName` is descriptive metadata.

It is not the resource identity.

---

# 15. FileBased Provider: General Model

## 15.1 Physical Markdown must remain normal Markdown

This requirement is mandatory.

The file-based provider must never persist:

```markdown
![Image](knowledge-resource:...)
```

as its native Markdown representation.

Instead, physical Markdown contains ordinary relative file links:

```markdown
![Image](Article.Res123.png)
```

or:

```markdown
![Logo](../Shared/CompanyLogo.png)
```

Only the repository-facing representation uses:

```markdown
![Image](knowledge-resource:<opaque-resource-id>)
```

---

## 15.2 Mapping occurs at the storage boundary

FileBased uses two conceptual transformations:

```text
Physical Markdown
    |
    | read mapping
    v
Canonical Knowledge Markdown
```

and:

```text
Canonical Knowledge Markdown
    |
    | write mapping
    v
Physical Markdown
```

The internal logical content model should operate on canonical `knowledge-resource:` references.

Physical paths should appear only at the FileBased storage boundary.

---

## 15.3 Read mapping

Example physical file:

```text
Docs/Article.md
Docs/diagram.png
```

Physical Markdown:

```markdown
![Architecture](diagram.png)
```

The FileBased provider resolves the relative link to the normalized repository-relative resource path:

```text
Docs/diagram.png
```

It then creates its opaque public `ResourceId`.

The repository-facing Markdown becomes conceptually:

```markdown
![Architecture](knowledge-resource:<encoded Docs/diagram.png>)
```

The physical file is not modified merely because it was read.

---

## 15.4 Write mapping

When canonical repository content contains:

```markdown
![Architecture](knowledge-resource:<ResourceId>)
```

FileBased resolves its own opaque ID back to the provider-native resource path and writes the correct relative Markdown path.

For example:

```markdown
![Architecture](diagram.png)
```

or:

```markdown
![Architecture](../Shared/diagram.png)
```

depending on physical location.

This makes the stored Markdown independently usable.

---

# 16. FileBased Native Resource Identity

## 16.1 Native identity

For FileBased, the provider-native resource identity is the normalized repository-relative path.

Example:

```text
Docs/Article.Res123.png
```

Do not use the absolute operating-system path:

```text
C:\Repositories\Wiki\Docs\Article.Res123.png
```

because moving or cloning the repository root must not change every resource identity.

Normalize provider-native paths consistently.

Prefer `/` as the canonical internal separator independent of operating system.

---

## 16.2 Public FileBased `ResourceId`

FileBased should not expose the native path directly.

Preferred implementation:

```text
<encoding-version>.<Base64Url(normalized repository-relative path)>
```

Example:

```text
1.RG9jcy9BcnRpY2xlLlJlczEyMy5wbmc
```

The version prefix is optional implementation detail but recommended because persisted adapter mappings may survive future encoding changes.

Base64Url is used instead of normal Base64 to avoid URI-hostile characters.

This encoding is a FileBased implementation detail.

No consumer may decode it.

---

# 17. No FileBased Sidecar Mapping Files

Do not introduce files such as:

```text
.resources.json
.knowledge/resources.json
resource-map.db
```

solely to remember mappings between logical resources and physical resource paths.

For FileBased, the file system itself is the source of truth.

A manually created file already has a native identity through its repository-relative path.

A manually edited Markdown reference already expresses the relationship between document and resource.

This is a deliberate architectural decision.

---

# 18. FileBased Resource Naming

## 18.1 Arbitrary manually managed resource names are valid

A user may create:

```text
Article.md
architecture.png
logo-final.svg
photo 2026-09-17.jpg
```

and write:

```markdown
![Architecture](architecture.png)
```

The provider must accept this.

It must not rename such a file merely to force an internal UID naming scheme.

---

## 18.2 Provider-generated fallback names

When a resource enters through the API and no useful physical filename is available, FileBased needs a safe generated filename.

Canonical fallback convention:

```text
<DocumentBaseName>.Res<Snowflake44>.<extension>
```

Example:

```text
Article.Res111039863826467328.png
```

Snowflake44 remains useful here, but its purpose is now:

- collision-resistant physical fallback naming,

not:

- the public resource identity.

The public identity is still the provider-defined opaque string derived from native identity.

---

## 18.3 Preferred filename

When an upstream source supplies a meaningful filename, FileBased should preserve it when safe and collision-free.

If no meaningful filename exists, such as a pasted screenshot from Joplin, use the generated fallback.

If a supplied name collides, FileBased may choose a different safe name.

The returned `ResourceId` always identifies the actual resulting resource.

---

# 19. FileBased Resource Ownership Convention

A crucial distinction exists between **document-owned generated resources** and **free/shared resources**.

No sidecar metadata is used to record ownership.

Ownership is inferred only from a deliberately narrow generated naming convention.

---

## 19.1 Document-owned resource

A file matching:

```text
<DocumentBaseName>.Res<ResourceToken>.<extension>
```

is considered document-owned by that Markdown document.

Example:

```text
Article.md
Article.Res111039863826467328.png
```

This convention is intentionally narrow.

Do not treat every file beginning with `Article` as owned.

For example:

```text
ArticleLogo.png
Article.Architecture.png
```

must not automatically be considered owned merely because their names begin with the document name.

---

## 19.2 Free/shared resource

Any resource that does not match the strict owned-resource pattern is treated as independent.

Example:

```text
CompanyLogo.png
SharedArchitecture.svg
```

A Markdown document may reference it, but the provider does not infer exclusive ownership.

---

# 20. Moving a FileBased Document with Resources

This is one of the most important resource semantics.

Consider:

```text
FolderA/
  Article.md
  Article.Res123.png
  CompanyLogo.png
```

`Article.md` references both images.

The document is moved to:

```text
FolderB/
```

---

## 20.1 Owned resource

`Article.Res123.png` matches the owned-resource convention.

Therefore it moves with the document:

```text
FolderB/
  Article.md
  Article.Res123.png
```

The physical Markdown reference may remain byte-identical:

```markdown
![Image](Article.Res123.png)
```

However, the provider-native resource identity changed:

```text
FolderA/Article.Res123.png
```

to:

```text
FolderB/Article.Res123.png
```

Therefore the opaque public `ResourceId` also changes.

`TryMoveContent` must report:

```text
old ResourceId -> new ResourceId
```

through `KnowledgeResourceIdChange[]`.

---

## 20.2 Free/shared resource

`CompanyLogo.png` does not match the owned-resource pattern.

It remains in `FolderA`.

The moved Markdown document must be repaired so that its physical relative link remains valid.

Before:

```markdown
![Logo](CompanyLogo.png)
```

After moving `Article.md` to `FolderB`:

```markdown
![Logo](../FolderA/CompanyLogo.png)
```

The resource itself did not move.

Therefore its provider-native identity and its public `ResourceId` remain unchanged.

No `KnowledgeResourceIdChange` is produced for that resource.

---

## 20.3 Important symmetry

The two cases are intentionally complementary:

```text
OWNED RESOURCE

Resource moves
Physical relative Markdown reference may stay unchanged
Repository ResourceId changes
```

```text
FREE / SHARED RESOURCE

Resource stays
Physical Markdown reference changes
Repository ResourceId stays unchanged
```

This distinction must be preserved in implementations and tests.

---

# 21. Renaming a FileBased Document

If:

```text
Article.md
Article.Res123.png
```

is renamed to:

```text
Architecture.md
```

the owned resource should become:

```text
Architecture.Res123.png
```

The physical Markdown link must be updated accordingly.

The provider-native resource path changes, therefore the public `ResourceId` changes.

The rename operation must report the resource ID change.

Free/shared resources are not renamed merely because the referencing document is renamed.

---

# 22. Manual File-System Changes

The FileBased provider must remain friendly to direct manual editing.

## 22.1 Manually added image

A user may add:

```text
new-diagram.png
```

and reference it from Markdown.

No registration step is required.

On the next read, the provider derives a resource identity from the existing file path and exposes an opaque `ResourceId`.

No Snowflake is generated merely because the file was manually added.

---

## 22.2 Manually edited image content

If the same physical path remains and the bytes change, it remains the same provider-native resource identity.

The repository exposes the new content under the same FileBased resource ID.

---

## 22.3 Manual rename outside the repository API

If a user manually renames:

```text
foo.png
```

to:

```text
bar.png
```

and adjusts Markdown accordingly, then without hidden historical metadata the provider cannot prove that these are the same historical logical resource.

That is acceptable.

From the repository's perspective this may appear as:

```text
old resource disappeared
new resource appeared
```

Adapters may optionally reconcile by content hash, but such heuristics are not part of the core contract.

Do not add sidecar metadata merely to preserve identity across arbitrary out-of-band filesystem changes.

---

# 23. FileBased Markdown Document Semantics

The established logical FileBased area model remains:

```text
[Folder]
Document
Document/Heading
Document/Heading/SubHeading
```

This syntax is a FileBased logical-path concern only.

Other adapters must never depend on it.

---

## 23.1 Directory content level

A physical directory is `ContentAggregation` only when it directly contains at least one active Markdown document.

A directory containing only subdirectories is `BeyondContent`.

---

## 23.2 Directory aggregation depth

Directory aggregation includes only direct Markdown documents.

Do not recursively fold subdirectories into the parent aggregation.

For each direct document, aggregation renders an appropriate document-level heading and rebases internal headings.

---

## 23.3 Non-Markdown files

Normal resource files do not become areas.

Other unrelated files are ignored by textual area enumeration.

Provider-generated resource companions must never accidentally become knowledge documents merely because of naming or extension edge cases.

---

# 24. FileBased Soft Delete

Soft delete is an optional safety feature.

When enabled, deleting:

```text
Foo.md
```

renames it to a form such as:

```text
Foo.DELETED.md
Foo.DELETED.2.md
```

Soft-deleted artifacts are ignored by logical repository loading.

Soft delete is a safety net, not a substitute for correct synchronization semantics.

Moves must not trigger soft delete.

A move must use provider-native move semantics.

---

# 25. FileBased Atomicity and Rollback Safety

A previous destructive rollback design deleted the complete repository root and then copied a snapshot back.

This is forbidden.

A transient lock on one file could otherwise destroy unrelated files.

Rollback must be non-destructive:

1. restore/copy all original snapshot artifacts first,
2. only after originals have been restored, remove artifacts that should not exist,
3. use atomic replacement where possible,
4. retry transient sharing/IO failures,
5. never delete the whole repository before successful restoration.

Expected transient IO failures may be logged with:

```csharp
DevLogger.LogError(ex);
```

Do not introduce broad pro-forma catch blocks.

Temporary file locks should fail safely without data loss.

---

# 26. Git-Based Provider

The Git provider builds on FileBased semantics.

Typical design:

- clone into a temporary working directory,
- knowledge root under a configured repository path such as `/doc`,
- use LibGit2Sharp,
- use explicit authentication callback,
- do not depend on global Git credentials,
- refresh/fetch before mutation,
- commit one successful logical mutation,
- push,
- on non-fast-forward, fetch/reset and replay the logical mutation,
- never force-push.

Resource behavior is inherited from FileBased storage semantics.

The Git layer must not reinterpret resources, areas, or move semantics.

---

# 27. Aggregated Repository

`AggregatedKnowledgeRepository` may mount arbitrary repositories at arbitrary logical mount points.

It may support:

- deep mounts,
- synthetic parent areas,
- multiple repositories mounted at the same logical path,
- overlay reads.

Read behavior may merge compatible results.

Mutation routing must remain conservative.

A mutation is allowed only when target ownership can be determined unambiguously.

Cross-provider moves must be rejected unless one implementation can guarantee the repository contract's atomicity and semantics.

Do not implement cross-provider move as copy-plus-delete merely to make the call succeed.

Resource IDs remain opaque provider-defined values.

An aggregator must not decode or reinterpret resource identifiers.

If an aggregator needs to multiplex resource identities from multiple child repositories, it must generate its own opaque aggregate-level resource IDs or otherwise preserve unambiguous ownership without exposing child-provider semantics.

---

# 28. Joplin WebDAV Adapter

## 28.1 Role

The Joplin layer is an adapter/projection over one `IKnowledgeRepository`.

It is not a knowledge provider implementation.

Its job is to translate between:

```text
Joplin synchronization model
        and
provider-neutral knowledge model
```

It must not know FileBased storage syntax.

---

## 28.2 WebDAV handling

Use ASP.NET Core middleware that dispatches raw HTTP methods.

Typical methods include:

- `OPTIONS`,
- `PROPFIND`,
- `GET`,
- `HEAD`,
- `PUT`,
- `DELETE`,
- `MKCOL`,
- `MOVE`.

Do not depend on MVC HTTP verb attributes for WebDAV verbs.

Middleware registration conceptually looks like:

```csharp
app.UseJoplinKnowledgeRepositoryWebDav(
  "/api/knowledge/joplin"
);
```

The exact integration must respect the application's established startup conventions.

---

## 28.3 Joplin-specific state is separate

Joplin requires protocol artifacts that are not knowledge:

- `info.json`,
- locks,
- temp files,
- `.resource` blobs,
- Joplin item metadata,
- unsupported item types,
- projection state.

These belong in:

```csharp
IJoplinSyncStateStore
```

They must not be injected into the knowledge area hierarchy.

This state store is intentionally Joplin-specific.

---

# 29. Joplin Item Types

Relevant Joplin sync item types include:

```text
1 = note
2 = folder/notebook
4 = resource
```

A note's `parent_id` represents notebook placement.

Changing `parent_id` is mapped to the provider-neutral repository move operation.

A Joplin resource is a distinct sync item plus binary blob.

---

# 30. Joplin `:/<id>` References Must Be Classified

Joplin uses the `:/<32-hex-id>` syntax for more than binary attachments.

It may also be used for links to notes.

Therefore:

```text
:/abc...
```

must never automatically be treated as a resource.

The adapter must classify the referenced Joplin item.

Only actual resource items (`type_: 4`) or already-established resource mappings are translated to:

```text
knowledge-resource:<ResourceId>
```

Note links remain note links.

This rule is critical.

---

# 31. Joplin Resource Mapping Has Its Own Stable Identity

Joplin resource identity and repository resource identity are deliberately decoupled.

The Joplin sync/projection state stores:

```text
JoplinResourceId <-> Knowledge ResourceId
```

Example:

```text
Joplin:
f5f34707ac3d80ab59368d5101d933fe

Knowledge:
1.RG9jcy9BcnRpY2xlLlJlczEyMy5wbmc
```

The Joplin ID is Joplin's stable external identity.

The Knowledge `ResourceId` is the current opaque repository identity.

---

## 31.1 Repository ID change does not imply new Joplin resource

If FileBased moves an owned resource and reports:

```text
old Knowledge ResourceId
    ->
new Knowledge ResourceId
```

the adapter updates the existing mapping:

```text
same Joplin resource ID
    ->
new Knowledge ResourceId
```

It must not create a new Joplin resource merely because the repository identifier changed.

This is a major reason why Joplin mapping state is retained independently.

---

# 32. Joplin Import of Resources

When Joplin uploads a resource:

1. Joplin metadata and blob may arrive in either order.
2. The adapter stores raw synchronization artifacts in the sync-state store.
3. Once enough information exists, the adapter calls `TryAddResource`.
4. It supplies a meaningful preferred filename if Joplin provides one.
5. If no usable name exists, the provider chooses a safe physical fallback.
6. The returned opaque `ResourceId` is stored in the Joplin projection mapping.
7. Joplin Markdown references are translated to canonical `knowledge-resource:<ResourceId>` references before repository content is written.

FileBased may use:

```text
<Document>.Res<Snowflake44>.<ext>
```

when a pasted screenshot has no meaningful file name.

Joplin must not know that convention.

---

# 33. Joplin Export of Resources

When repository Markdown contains:

```text
knowledge-resource:<ResourceId>
```

the Joplin adapter:

1. looks up an existing Joplin resource mapping,
2. creates one if necessary,
3. retrieves binary bytes and metadata through `IKnowledgeRepository`,
4. persists/refreshes Joplin metadata and blob artifacts in the sync-state store,
5. renders:

```text
:/<JoplinResourceId>
```

into the Joplin-facing Markdown.

The original Knowledge `ResourceId` is never exposed to Joplin as semantic content.

---

# 34. Joplin Pending Parent Handling

Joplin may upload a child note before its parent notebook.

This is not a semantic repository error.

Required behavior:

1. persist the raw Joplin sync item,
2. accept the upload as pending,
3. return successful sync semantics when appropriate,
4. materialize the note once the parent arrives,
5. retry pending items after relevant parent changes.

Do not return a semantic conflict merely because upload order is temporarily incomplete.

---

# 35. Joplin Raw Serialization

Joplin raw item serialization must preserve its expected item structure.

A robust pattern is:

```text
title

optional body

property: value
property: value
type_: N
```

Do not append a trailing newline after the final `type_:` line if that causes Joplin's parser to interpret an empty line as the end of the property block.

Serialization stability matters.

---

# 36. Joplin Projection Stability

Repeated GET/PROPFIND operations over unchanged knowledge must not manufacture new remote versions.

Important rules:

- root timestamps must be stable,
- projection hashes must exclude volatile transport metadata,
- semantic hash and transport representation must be separated,
- a successful PUT must synchronize state to the actual resulting repository content,
- do not intentionally clear hashes merely to force a subsequent remote change.

A stable unchanged projection should be byte-stable where practical.

This behavior is critical for avoiding conflict loops.

---

# 37. Joplin DELETE Is Not Permission to Destroy Knowledge

A WebDAV DELETE is a synchronization-protocol event.

It must not blindly call:

```csharp
IKnowledgeRepository.TryDelete(...)
```

or:

```csharp
TryDeleteResource(...)
```

for projected knowledge.

Joplin may delete/reconcile sync artifacts for reasons that do not mean:

```text
permanently destroy the authoritative knowledge source
```

The safe default is:

- suppress the Joplin projection record,
- preserve provider-neutral knowledge.

This applies to notes and resources.

---

# 38. Joplin Conflict Rebind

A Joplin conflict copy may later appear with a new Joplin item ID but correspond to an existing logical knowledge area.

The adapter may rebind the new Joplin ID to the existing knowledge area when logical identity is unambiguous.

This avoids duplicate physical documents.

The previous Joplin identity can remain suppressed.

Knowledge must never be deleted merely to reconcile Joplin conflict state.

---

# 39. Temporary Storage Failures

If repository mutation fails because storage is temporarily unavailable, such as a transient file lock:

- retry provider-local transient IO where appropriate,
- do not misrepresent the failure as a semantic conflict,
- do not acknowledge a Joplin resource update as committed if repository replacement failed,
- return `503 Service Unavailable` with an appropriate retry hint when the adapter can classify the failure as temporary.

A false `204` after an unsuccessful resource write is forbidden.

---

# 40. Joplin Authentication

Authentication is optional and externally pluggable.

Use an abstraction such as:

```csharp
public interface IJoplinWebDavAuthenticationValidator {

  bool ValidateCredentials(
    string userName,
    string password,
    HttpContext context
  );
}
```

When no validator is registered, existing open-endpoint behavior may remain.

When a validator is registered:

- parse HTTP Basic Authentication,
- invoke the validator,
- return `401`,
- send an appropriate `WWW-Authenticate` header on failure.

Do not embed a user database into the Joplin adapter.

---

# 41. Generic REST/MCP Facades

A generic facade should remain intentionally thin.

It should expose repository semantics rather than reimplement provider logic.

Do not introduce FileBased or Joplin assumptions into generic HTTP or MCP layers.

Resource delivery may use facade-specific URLs, but the underlying identity remains the opaque `ResourceId`.

The facade must not require the consumer to understand how a provider encodes the identifier.

---

# 42. Top-Down Example: Basic Knowledge Traversal

```csharp
IKnowledgeRepository repository = CreateRepository();

string[] areas = repository.GetAreas(
  true,
  "/"
);

foreach (string area in areas) {
  string name = repository.GetAreaName(
    area
  );

  ContentLevel contentLevel;
  bool supportsSubAreas;
  bool canBeRenamed;
  bool canBeDeleted;
  bool canAddSubAreas;
  bool canAppendContent;
  bool canTruncate;
  bool supportsResources;

  repository.GetAreaCapabilities(
    area,
    out contentLevel,
    out supportsSubAreas,
    out canBeRenamed,
    out canBeDeleted,
    out canAddSubAreas,
    out canAppendContent,
    out canTruncate,
    out supportsResources
  );

  // Consumer logic operates exclusively on provider-neutral semantics.
}
```

The caller never asks whether `area` is a file, directory, heading, notebook, or database node.

---

# 43. Top-Down Example: Moving Content

```csharp
KnowledgeResourceIdChange[] resourceIdChanges;

bool moved = repository.TryMoveContent(
  contentAreaToMove,
  newParentArea,
  out resourceIdChanges
);

if (moved) {
  foreach (KnowledgeResourceIdChange change in resourceIdChanges) {
    UpdateExternalResourceMapping(
      change.PreviousResourceId,
      change.CurrentResourceId
    );
  }
}
```

This pattern is intentionally adapter-friendly.

A consumer that does not persist resource mappings may ignore the returned array.

A Joplin adapter must use it.

---

# 44. Advanced Example: Owned FileBased Resource Move

Initial physical state:

```text
FolderA/
  Article.md
  Article.Res987654321.png
```

Physical Markdown:

```markdown
![Screenshot](Article.Res987654321.png)
```

Repository-facing Markdown:

```markdown
![Screenshot](knowledge-resource:<OpaqueResourceIdA>)
```

Move:

```csharp
TryMoveContent(
  articleArea,
  folderBArea,
  out resourceIdChanges
);
```

Result:

```text
FolderB/
  Article.md
  Article.Res987654321.png
```

Physical Markdown may remain:

```markdown
![Screenshot](Article.Res987654321.png)
```

Repository-facing Markdown now contains:

```markdown
![Screenshot](knowledge-resource:<OpaqueResourceIdB>)
```

The operation reports:

```text
OpaqueResourceIdA -> OpaqueResourceIdB
```

The Joplin adapter updates its mapping while preserving the existing Joplin resource ID.

---

# 45. Advanced Example: Free Shared Resource Move

Initial state:

```text
FolderA/
  Article.md
  CompanyLogo.png
```

Physical Markdown:

```markdown
![Logo](CompanyLogo.png)
```

Move `Article.md` to `FolderB`.

Result:

```text
FolderA/
  CompanyLogo.png

FolderB/
  Article.md
```

Updated physical Markdown:

```markdown
![Logo](../FolderA/CompanyLogo.png)
```

Repository resource ID remains unchanged because the resource itself did not move.

No resource ID change is reported.

---

# 46. Advanced Example: Joplin Screenshot Paste

A user pastes a screenshot into Joplin.

Joplin provides:

```text
Joplin Resource ID = f5f34707ac3d80ab59368d5101d933fe
```

There may be no meaningful original file name.

The adapter calls:

```csharp
TryAddResource(
  noteArea,
  string.Empty,
  "image/png",
  content,
  out resourceId
);
```

FileBased chooses:

```text
Article.Res<Snowflake44>.png
```

and returns an opaque resource ID representing its native path.

The Joplin sync state stores:

```text
f5f34707ac3d80ab59368d5101d933fe
    <->
<opaque Knowledge ResourceId>
```

Joplin continues to use:

```text
:/f5f34707ac3d80ab59368d5101d933fe
```

The physical Markdown uses:

```text
Article.Res<Snowflake44>.png
```

The repository-facing Markdown uses:

```text
knowledge-resource:<opaque ResourceId>
```

Each layer uses its own natural identity.

---

# 47. Requirements Matrix

| Requirement | Normative rule |
|---|---|
| Provider neutrality | Consumers must not depend on provider-native storage representation. |
| One interface | Areas, content, and resources are exposed through `IKnowledgeRepository`. |
| Resources are not areas | Binary assets do not participate in area hierarchy or `ContentLevel`. |
| Area names | Use `GetAreaName`; do not decode path segments. |
| Area creation | Use `KnowledgeAreaKind`, not provider-specific name syntax. |
| Recursive enumeration | Depth-first pre-order; preserve natural sibling order. |
| Reads | Must not mutate underlying storage. |
| Mutations | Atomic from consumer perspective. |
| Move | Reparent logical scope; never append-plus-truncate. |
| Move target | `newParentArea` is a parent, never replacement content. |
| Resource capability | Exposed through `supportsResources`. |
| Resource reference | Canonical form is `knowledge-resource:<ResourceId>`. |
| Resource ID type | `string`. |
| Resource ID ownership | Provider-defined and opaque. |
| Resource ID interpretation | Consumers must not parse, decode, compose, or infer semantics. |
| Resource ID stability | May change on provider-native rename/move. |
| Resource ID changes | Mutations report old-to-new mappings. |
| FileBased native identity | Normalized repository-relative resource path. |
| FileBased public ID | Prefer opaque versioned Base64Url encoding of native identity. |
| FileBased sidecar metadata | Not used for resource identity mapping. |
| Physical Markdown | Must contain ordinary relative file links. |
| FileBased read mapping | Relative path -> opaque resource ID -> canonical `knowledge-resource:` reference. |
| FileBased write mapping | Canonical reference -> physical relative Markdown path. |
| Manual resources | Arbitrary safe filenames are valid and remain unchanged. |
| Generated fallback | `<Document>.Res<Snowflake44>.<ext>`. |
| Owned resource | Strict generated `<Document>.Res<Token>.<ext>` convention. |
| Free/shared resource | Any resource not matching the strict ownership pattern. |
| Owned resource on document move | Moves with document; resource ID changes and is reported. |
| Free resource on document move | Remains in place; Markdown link is repaired; resource ID remains stable. |
| Soft delete | Optional safety feature; never used to implement moves. |
| Rollback | Non-destructive restore; never delete repository root before successful restoration. |
| Joplin state | Separate `IJoplinSyncStateStore`. |
| Joplin IDs | Stable Joplin identity is independent from Knowledge ResourceId. |
| Joplin mapping | Joplin Resource ID <-> current Knowledge ResourceId. |
| Joplin ID after Knowledge move | Keep same Joplin ID; update mapping only. |
| Joplin `:/id` | Classify item type; do not assume every ID is a resource. |
| Joplin parent ordering | Child-before-parent uploads are pending, not semantic conflicts. |
| Joplin DELETE | Must not blindly destroy knowledge. |
| Joplin conflicts | Prefer safe suppression/rebind over destructive reconciliation. |
| Temporary write lock | Retry safely; use 503 rather than false conflict/success. |
| Unit-test framework | MSTest only for .NET tests. |

---

# 48. Bottom-Up Artifact Guide

## 48.1 `IKnowledgeRepository`

The central provider-neutral contract.

It defines:

- area traversal,
- names,
- capabilities,
- content reads,
- content mutations,
- resource access,
- resource ID change reporting.

It must contain no Joplin- or FileBased-specific semantics.

---

## 48.2 `ContentLevel`

Defines whether an area is:

- structural,
- aggregating,
- directly content-bearing.

It is not a physical storage type.

---

## 48.3 `KnowledgeAreaKind`

Communicates semantic creation intent.

It prevents consumers from encoding provider-specific structure into names.

---

## 48.4 `KnowledgeResourceInfo`

Describes one provider-neutral resource.

Its `ResourceId` is opaque.

Its filename is metadata, not identity.

---

## 48.5 `KnowledgeResourceIdChange`

Describes a resource identifier transition caused by a successful repository mutation.

It allows external adapters to preserve their own stable identities.

---

## 48.6 `FileBasedKnowledgeRepository`

Owns all FileBased-specific behavior:

- logical file/directory/heading mapping,
- physical Markdown parsing/rendering,
- physical-to-canonical resource-link mapping,
- Base64Url resource ID encoding,
- owned/free resource handling,
- fallback resource naming,
- physical move/rename behavior,
- soft delete,
- atomic IO,
- non-destructive rollback,
- transient lock retry.

No Joplin behavior belongs here.

---

## 48.7 `GitBasedKnowledgeRepository`

Adds Git synchronization, commit, and push semantics around FileBased logical mutations.

It should not redefine the logical knowledge model.

---

## 48.8 `AggregatedKnowledgeRepository`

Combines repositories under logical mounts.

It owns overlay and routing behavior.

It must remain conservative for ambiguous mutations and resource ownership.

---

## 48.9 `IJoplinSyncStateStore`

Stores Joplin-specific protocol and projection state.

It is allowed to remember Joplin mappings because those mappings are Joplin adapter state, not provider-neutral knowledge.

---

## 48.10 `JoplinKnowledgeRepositoryWebDavHandler`

Owns translation between:

- Joplin folders,
- Joplin notes,
- Joplin resources,
- Joplin WebDAV sync items,

and:

- provider-neutral areas,
- textual content,
- opaque repository resources.

It must use only `IKnowledgeRepository` semantics.

---

## 48.11 `JoplinKnowledgeRepositoryWebDavMiddleware`

Owns raw WebDAV HTTP method dispatch.

It exists to avoid forcing WebDAV verbs through normal MVC verb discovery.

---

# 49. Testing Strategy

All .NET unit tests for this project MUST use MSTest.

Do not generate xUnit or NUnit tests.

Canonical attributes:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ExampleTests {

  /// <summary>
  /// Verifies the expected behavior.
  /// </summary>
  [TestMethod]
  public void Operation_Condition_ExpectedResult() {
  }
}
```

High-value regression tests include:

- document move does not create soft-delete artifacts,
- heading move preserves both parents,
- document rename preserves resource behavior,
- owned resource moves with document,
- owned resource move reports ResourceId change,
- free resource remains in place while physical Markdown link is repaired,
- physical Markdown never persists `knowledge-resource:` references,
- reading physical Markdown returns canonical `knowledge-resource:` references,
- manually added image becomes a resource without sidecar registration,
- Joplin child-before-parent materializes later,
- Joplin `parent_id` change invokes repository move semantics,
- repeated unchanged GET is byte-stable,
- identical repeated PUT is idempotent,
- Joplin note link is not misclassified as a resource,
- Joplin resource replacement preserves Joplin identity,
- Joplin DELETE does not delete authoritative knowledge,
- conflict rebind does not duplicate knowledge,
- temporary resource-write failure does not return false success,
- Joplin resource mapping updates after repository ResourceId changes.

Tests should prefer public APIs over private implementation details.

---

# 50. C# Implementation Rules for This Skill

Apply the project's global C# development rules.

In particular:

- prompt communication may be German, but code and code comments are English,
- do not use `var`,
- avoid nullable/coalescing constructs when unnecessary,
- fields and constants use PascalCase with `_` prefix,
- methods have `<summary>` XML documentation,
- prefer arrays over `List<>` in public signatures,
- use explicit `this.` for instance members except fields,
- do not use the async/await pattern unless explicitly approved,
- do not use inline conditional operators,
- always use braces,
- opening braces stay on the same line,
- lambda parameters are parenthesized,
- properties are explicitly implemented,
- use `DevLogger.LogError(ex)` only for targeted expected exceptions,
- trace with `DevLogger.LogTrace(0, 99999, "...")`,
- avoid Windows-only interop/DllImport unless explicitly approved,
- use Newtonsoft.Json rather than `System.Text.Json`,
- MSTest only for .NET unit tests.

All generated C# artifacts in this knowledge-access project use:

```csharp
namespace KnowledgeManagement.SmartStandards {
```

Tests use the corresponding test namespace.

---

# 51. Anti-Patterns

AI agents must actively avoid the following.

## 51.1 FileBased syntax in adapters

Wrong:

```text
Joplin checks whether an area segment is [Folder].
```

Correct:

```text
Joplin asks the repository for semantic type/capabilities.
```

---

## 51.2 Physical path as public semantic contract

Wrong:

```text
Consumer splits ResourceId on "/".
```

Correct:

```text
Consumer stores ResourceId as opaque string.
```

---

## 51.3 Persisting canonical resource links directly into FileBased Markdown

Wrong:

```markdown
![Image](knowledge-resource:...)
```

on disk.

Correct:

```markdown
![Image](relative-image.png)
```

on disk.

---

## 51.4 Hidden FileBased resource brain

Wrong:

```text
.knowledge/resources.json is required to know what image belongs to what.
```

Correct:

```text
The physical filesystem and Markdown references are authoritative.
```

---

## 51.5 Treating every Joplin `:/id` as a resource

Wrong.

The target may be a note.

Classify the Joplin item first.

---

## 51.6 Move implemented as copy-plus-delete

Wrong.

Use provider-native logical reparenting semantics.

---

## 51.7 Joplin DELETE mapped to authoritative delete

Wrong.

Sync reconciliation is not equivalent to user-authorized source destruction.

---

## 51.8 Stable-ID assumptions

Wrong:

```text
ResourceId never changes.
```

Correct:

```text
Provider-native identity may change; mutations report ResourceId changes.
```

---

# 52. Design Rationale

This architecture deliberately separates three identity domains:

```text
External adapter identity
        |
        | adapter-owned mapping
        v
Opaque repository ResourceId
        |
        | provider-owned mapping/encoding
        v
Provider-native identity
```

For Joplin + FileBased this becomes:

```text
Joplin Resource ID
        |
        | IJoplinSyncStateStore
        v
Knowledge ResourceId
        |
        | FileBased private Base64Url resolution
        v
Repository-relative physical resource path
```

This is a strength rather than duplication.

Each layer owns only the identity semantics it can correctly maintain.

The repository abstraction is protected from Joplin-specific IDs.

Joplin is protected from FileBased path semantics.

The physical file repository remains human-readable and independently useful.

No hidden FileBased metadata database is required.

Moves and renames remain correctly observable by stateful adapters through explicit resource-ID change reporting.

---

# 53. Final Architectural North Star

When evaluating any future change, ask:

1. **Can an arbitrary new `IKnowledgeRepository` implementation work with the existing adapters without knowing those adapters?**
2. **Can an adapter such as Joplin operate without knowing the concrete repository implementation?**
3. **Does the contract describe logical semantics rather than physical implementation?**
4. **Does FileBased remain a normal, human-editable Markdown repository on disk?**
5. **Are resource identifiers treated as opaque provider-owned values?**
6. **Are identity changes reported explicitly rather than guessed later?**
7. **Does a mutation preserve unrelated knowledge and avoid destructive fallbacks?**
8. **Would the behavior remain correct under sync retries, conflicts, file locks, and manual filesystem edits?**

If the answer to any of these questions is no, the design is probably crossing an abstraction boundary.

The architectural target is not merely to make today's FileBased/Joplin combination work.

The target is a stable knowledge abstraction in which physical providers and external adapters can evolve independently while preserving predictable wiki semantics.
