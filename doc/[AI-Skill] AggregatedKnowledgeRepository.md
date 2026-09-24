# [AI Skill] AggregatedKnowledgeRepository

## 1. Abstract and motivation

`AggregatedKnowledgeRepository` combines an arbitrary number of `IKnowledgeRepository` providers into one deterministic logical repository.

It is not a simple concatenation layer.

It provides:

- absolute logical mount points,
- synthetic mount ancestors,
- deterministic overlay semantics,
- provider-neutral paths,
- provider-neutral resource IDs,
- provider-neutral `knowledge-area:` references,
- content-boundary-aware aggregation,
- conservative mutation routing,
- controlled tree materialization,
- failure isolation between providers,
- cache-control propagation,
- stable ordering.

The design intentionally avoids common composite-repository failures:

- leaking provider-native paths into the public model,
- recursively materializing every mounted provider,
- treating “has descendants” as equivalent to “is aggregatable content”,
- allowing `BeyondContent` descendants to leak into parent aggregation,
- guessing mutation ownership when multiple providers overlap,
- exposing child resource IDs directly,
- attempting non-atomic cross-provider moves,
- eagerly traversing complete subtrees for ordinary navigation.

This document is normative for maintenance of the aggregator.

---

# 2. Scope

Class:

```text
KnowledgeManagement.SmartStandards.Wrappers.AggregatedKnowledgeRepository
```

Current source baseline:

```text
AggregatedKnowledgeRepository.cs
```

Implements:

```text
IKnowledgeRepository
IKnowledgeRepositoryCacheControl
```

The aggregator does not own the lifetime of mounted repositories.

Calling `Add()` does not transfer disposal ownership.

---

# 3. Top-down usage

## 3.1 Side-by-side mounts

Conceptually:

```csharp
AggregatedKnowledgeRepository aggregate = new AggregatedKnowledgeRepository();

aggregate.Add(
  gitRepository,
  "/Git"
);

aggregate.Add(
  oneNoteRepository,
  "/OneNote"
);
```

Visible tree:

```text
/
├── Git
└── OneNote
```

---

## 3.2 Deep mount with synthetic ancestors

Mount:

```text
/Company/Engineering
```

The aggregate creates:

```text
/
└── Company
    └── Engineering
```

even if no provider directly exposes `/Company`.

Synthetic nodes are logical aggregator structure.

---

## 3.3 Overlay

Two providers can be mounted so that they contribute to the same global path.

That path appears exactly once.

Reads combine contributions in deterministic registration order.

Later providers overlay the existing logical node rather than creating duplicates.

---

# 4. Frozen path model

Repository paths are absolute.

Root:

```text
/
```

Examples:

```text
/OneNote
/OneNote/Architecture
/Git/Docs/API
```

Consumers must not parse provider conventions from paths.

The aggregator translates between:

```text
global aggregated path
```

and:

```text
mounted provider-local path
```

internally.

---

# 5. Registration and ordering

## 5.1 `Add`

`Add(repository, mountPoint)`:

1. validates repository,
2. normalizes mount point,
3. assigns deterministic registration metadata,
4. stores the mount,
5. invalidates the aggregate tree cache.

Registration order matters.

The first logical appearance of a child determines its position.

Later overlays merge into that position.

This creates stable ordering without requiring providers to know about one another.

---

# 6. Internal tree model

Important internal artifacts:

```text
MountedRepository
AreaContribution
AggregatedNode
AggregatedTree
```

## 6.1 `MountedRepository`

Tracks:

- provider instance
- mount point
- registration index
- ordinal among providers using the same mount point
- resource namespace identity

## 6.2 `AreaContribution`

Represents one concrete provider's contribution to one global node.

Contains at least:

```text
mounted repository
provider-local area
```

## 6.3 `AggregatedNode`

Represents one unique global logical area.

It can have:

- zero contributions: synthetic mount ancestor,
- one contribution: ordinary mounted node,
- multiple contributions: overlay node.

## 6.4 `AggregatedTree`

Indexes nodes by normalized global path and stores ordered child relations.

---

# 7. Tree cache and read burst

The aggregator caches tree materialization briefly to avoid rebuilding structure repeatedly during one UI/read burst.

The current implementation uses a short tree-read burst window.

This tree cache is not a persistent content cache.

Do not confuse it with:

```text
BackgroundFetchingKnowledgeRepositoryCacheWrapper
```

The aggregator's tree cache is an in-process structural optimization.

---

# 8. Lazy materialization

The aggregator must not eagerly crawl every mounted repository.

Important methods include:

```text
EnsureAreaPathMaterialized
EnsureDirectChildrenMaterialized
EnsureAggregationChildrenMaterialized
EnsureSubtreeMaterialized
MaterializeContributionChildren
```

Each has a distinct purpose.

---

# 9. Direct child enumeration

`GetAreas(false, startArea)`:

```text
materialize only enough path to find startArea
materialize direct children
return ordered children
```

It should not recursively enumerate all descendants.

---

# 10. Recursive enumeration

`GetAreas(true, startArea)` is an explicit bulk operation.

Even then, the aggregator should expand iteratively one level at a time.

It deliberately does not call:

```text
childProvider.GetAreas(true, ...)
```

because a single recursive provider call could:

- materialize an arbitrarily large tree,
- hide provider-specific recursion,
- overflow a provider's call stack,
- prevent controlled failure isolation.

Recursive enumeration is therefore aggregator-controlled.

---

# 11. ContentLevel semantics

The three levels are semantically important:

```text
BeyondContent
ContentAggregation
ContentContainer
```

They must not be treated merely as navigation flags.

## 11.1 `ContentContainer`

A concrete addressed content unit.

Examples:

```text
Markdown document
OneNote page
OneNote heading
```

## 11.2 `ContentAggregation`

An explicit provider promise:

> The complete content subtree below this area forms one meaningful logical content unit and can reasonably be retrieved as such.

This should be used sparingly.

Typical good example:

```text
OneNote page heading hierarchy
```

Poor examples:

```text
whole Git repository root
large arbitrary folder tree
all company knowledge
```

unless the provider genuinely guarantees bounded meaningful aggregation.

## 11.3 `BeyondContent`

A hard content-aggregation boundary.

It does not merely mean “no direct content”.

Aggregation must not traverse through it looking for hidden content.

---

# 12. Combined content-level resolution

`ResolveCombinedContentLevel(node)` uses read-oriented overlay semantics.

Conceptually:

```text
if any concrete contribution is ContentContainer
    => ContentContainer

else if any concrete contribution is ContentAggregation
    => ContentAggregation

else if synthetic node has a direct aggregatable child
    => ContentAggregation

else
    => BeyondContent
```

A synthetic root may therefore be aggregatable even when some direct child mounts are `BeyondContent`.

This is intentional.

Example:

```text
/
├── README             ContentContainer
├── ReleaseNotes       ContentAggregation
├── SmartStandards     BeyondContent
└── OneNote            BeyondContent
```

The root aggregate may include:

```text
README
ReleaseNotes
```

while skipping:

```text
SmartStandards
OneNote
```

The root itself must not become `BeyondContent` merely because some children are boundaries.

---

# 13. Aggregation boundary visibility

`IsChildVisibleToAggregation(parent, child)` prevents content leaking through a `BeyondContent` parent contribution.

A child is eligible when:

- it is a direct mount root that is independently content-capable, or
- the same mounted repository contributes the parent with a non-`BeyondContent` content level.

This distinction is essential for overlays.

Without it, an independently content-capable descendant could be incorrectly pulled through an opaque navigation-only branch.

---

# 14. `GetAggregatedContent`

The aggregator renders a content-aware projection rooted at one global area.

Critical invariant:

> It must not first materialize the complete structural subtree.

Instead:

```text
render direct content
render provider-native opaque aggregation leaf content when necessary
materialize only aggregation-eligible direct children
skip BeyondContent branches
recurse only into eligible content children
```

This is content recursion, not repository-wide structural recursion.

Even so, callers such as background prefetchers must treat `GetAggregatedContent()` as potentially expensive and should not autonomously invoke it for arbitrary roots.

---

# 15. Rendering hierarchy

`RenderAggregatedNodeContent()` conceptually emits:

```text
direct content

# Child
child content

## Grandchild
grandchild content
```

Heading level is capped at 6.

The renderer:

1. appends direct content,
2. preserves opaque provider aggregate leaf content when needed,
3. expands only aggregation-visible children,
4. skips `BeyondContent`,
5. writes child heading,
6. recursively renders child content.

Do not flatten all descendants into the same heading level.

---

# 16. Opaque aggregation leaves

Some providers can expose meaningful aggregate content that cannot be reconstructed solely from visible child areas.

`RenderOpaqueAggregationLeafContent()` preserves that provider-native aggregate projection when appropriate.

This allows virtual/cross-cutting providers to participate without forcing the aggregator to understand their internal model.

Use this carefully.

It must not duplicate content that is already reconstructable from child nodes.

---

# 17. Direct content overlay

When multiple providers contribute direct content to the same global area, the aggregator reads each contribution in registration order and joins non-empty blocks deterministically.

One provider failure should not automatically eliminate content from the others.

Provider read failures are logged and isolated where read semantics allow partial results.

---

# 18. Provider failure isolation

Read operations deliberately isolate provider failures.

Typical policy:

```text
provider A succeeds
provider B throws
provider C succeeds
```

Aggregate read can still use A and C where safe.

Failure logging includes mount/provider context.

Do not silently swallow failures without traceability.

Do not let one broken provider unnecessarily destroy unrelated mounted knowledge.

Mutation behavior is intentionally stricter than read behavior.

---

# 19. Resources

## 19.1 Provider resource IDs must not leak

Child providers own their own opaque resource IDs.

The aggregator wraps them into an aggregator-owned opaque resource ID.

Consumers see only:

```text
aggregate ResourceId
```

not:

```text
provider identity + child id
```

as a contract.

## 19.2 Resource namespace

Each mount gets a deterministic namespace token used to route wrapped resource IDs back to the correct provider.

## 19.3 Resource enumeration

`GetResources(area)`:

1. resolves the global node,
2. checks each contribution's `supportsResources`,
3. obtains provider resources,
4. clones metadata,
5. wraps each child `ResourceId`,
6. returns deterministic ordering.

## 19.4 Binary retrieval

`GetResourceContent(aggregateId)` decodes the aggregate ID, resolves the mounted provider, and delegates to that provider's child resource ID.

Invalid/unresolvable aggregate IDs must fail rather than guess.

---

# 20. Resource ID versions

The implementation supports current and legacy aggregated resource ID decoding.

This enables migration compatibility.

New code should emit only the current form.

Old forms may remain readable for compatibility.

Do not create new legacy IDs.

---

# 21. Content reference translation

Provider content can contain:

```text
knowledge-resource:<provider-id>
knowledge-area:<provider-local-area>
```

The aggregator translates these references into aggregate-global equivalents when reading content.

Likewise, content passed to a uniquely owned child provider must translate aggregate-global references back into that provider's local namespace.

This boundary is fundamental.

A mounted provider must not receive another provider's resource identity accidentally.

---

# 22. `knowledge-area:` translation

For content read from a mounted provider:

```text
knowledge-area:/local/path
```

is translated into the corresponding global mounted path.

For writes routed back to one provider, aggregate-global area references must be translated back when they belong to that provider.

Do not apply path-string concatenation blindly.

Always use the mount translation helpers.

---

# 23. Conservative mutation routing

Reads can combine overlays.

Writes cannot safely guess.

General rule:

> A mutation is forwarded only when exactly one concrete provider unambiguously owns the addressed target and reports the necessary capability.

If multiple providers overlap the same global area:

```text
mutation => reject
```

unless ownership is otherwise uniquely defined by the specific operation.

This avoids accidental modification of the wrong source.

---

# 24. Move semantics

`TryMoveContent(contentAreaToMove, newParentArea, ...)` is allowed only when:

- source resolves uniquely,
- destination resolves uniquely,
- both belong to the same concrete repository instance,
- provider capability allows the operation.

Cross-provider move is rejected.

Reason:

`IKnowledgeRepository` requires atomic mutation semantics.

The aggregator cannot provide a distributed transaction across unrelated repositories.

Do not implement cross-provider move as:

```text
read source
write destination
delete source
```

without an explicit transactional contract. That can lose content.

---

# 25. Mutation capability resolution

Capability checks are contributor-specific.

A merged read node may have broad visible capabilities, but a write still requires a unique concrete contribution.

Do not interpret merged read capability as proof that an arbitrary provider can be modified.

---

# 26. Cache-control propagation

The aggregator implements optional cache control.

`BeginPreferExistingScope()`:

- enumerates unique mounted repository instances,
- finds those implementing `IKnowledgeRepositoryCacheControl`,
- opens their local prefer-existing scopes,
- composes disposal.

Nested aggregators therefore propagate the cache preference recursively.

The aggregator itself does not become a content cache.

---

# 27. `IsAreaCached`

The cache-inspection method is intentionally passive.

It must not:

- build the full tree,
- enumerate provider children,
- perform normal repository reads.

Synthetic mount ancestors can be considered locally known because their structure follows from registration.

When no provider exposes cache inspection, “unknown” should not be misrepresented as a definite cache miss.

---

# 28. Synthetic mount ancestors

A synthetic node:

- may have no provider contributions,
- can still be a valid navigation area,
- can become `ContentAggregation` if it has direct content-capable children,
- must not invent direct content.

This enables clean top-level organization independent of provider storage layout.

---

# 29. Path translation

Important conceptual helpers:

```text
ToGlobalPath
TryTranslateGlobalAreaToMountedLocalArea
TryTranslateGlobalScopeToLocal
NormalizeAreaPath
CombineAreaPath
IsSameOrDescendant
GetParentAreaPath
```

These functions define the mount boundary.

Do not duplicate path translation logic ad hoc in feature methods.

---

# 30. Tree invalidation

Adding a mount invalidates the aggregate tree cache.

Structural changes in child repositories are discovered through subsequent provider reads/cache wrappers rather than by mutating the aggregator's cached tree permanently.

The tree is a projection, not authoritative state.

---

# 31. Requirements matrix

| ID | Requirement | Mandatory |
|---|---|---|
| AG-001 | Mount points are absolute logical paths | Yes |
| AG-002 | `/` is root | Yes |
| AG-003 | Missing mount ancestors are synthesized | Yes |
| AG-004 | Multiple providers may overlap one global path | Yes |
| AG-005 | One global path appears only once | Yes |
| AG-006 | Registration order gives deterministic ordering | Yes |
| AG-007 | Direct child enumeration is lazy | Yes |
| AG-008 | Recursive enumeration is aggregator-controlled | Yes |
| AG-009 | Do not call child `GetAreas(true)` for recursive expansion | Yes |
| AG-010 | `BeyondContent` is a hard aggregation boundary | Yes |
| AG-011 | Synthetic parent may aggregate direct content-capable children | Yes |
| AG-012 | Content aggregation materializes only eligible children | Yes |
| AG-013 | Aggregated resources use aggregator-owned opaque IDs | Yes |
| AG-014 | Provider resource references are translated | Yes |
| AG-015 | Provider area references are translated | Yes |
| AG-016 | Reads isolate provider failures where safe | Yes |
| AG-017 | Mutations require unambiguous ownership | Yes |
| AG-018 | Cross-provider moves are rejected | Yes |
| AG-019 | Mounted repository lifetime ownership is not transferred | Yes |
| AG-020 | Optional cache-control scopes propagate | Yes |
| AG-021 | Cache inspection remains passive | Yes |
| AG-022 | Content-level semantics remain provider-driven | Yes |

---

# 32. Common traps

## 32.1 Marking a whole aggregate root `BeyondContent`

Wrong merely because one child is a navigation boundary.

A synthetic root can aggregate some direct children and skip others.

## 32.2 Traversing through `BeyondContent`

Wrong.

It is a hard boundary.

## 32.3 Eager full-subtree materialization in `GetAggregatedContent`

Wrong.

This caused severe recursion/performance problems.

## 32.4 Calling provider `GetAreas(true)`

Wrong for controlled aggregation.

Use one-level expansion.

## 32.5 Exposing child resource IDs directly

Wrong.

IDs may collide across providers and leak implementation details.

## 32.6 Picking “the first” provider for writes

Wrong when overlays exist.

Reject ambiguity.

## 32.7 Cross-provider move via copy/delete

Wrong without transaction semantics.

## 32.8 Treating `ContentAggregation` as “folder”

Wrong.

It is an explicit bounded content promise.

---

# 33. Recommended MSTest coverage

High-value tests:

```text
Add_DeepMount_CreatesSyntheticAncestors
GetAreas_OverlappingProviders_DeduplicatesGlobalPath
GetAreas_PreservesFirstAppearanceOrdering
GetAreasRecursive_DoesNotCallProviderRecursiveEnumeration
ResolveCombinedContentLevel_ContentContainerWins
ResolveCombinedContentLevel_ContentAggregationWinsOverBeyond
SyntheticRoot_WithContentChild_IsContentAggregation
Aggregation_SkipsBeyondContentChild
Aggregation_DoesNotTraverseBeyondContentDescendants
Aggregation_DirectMountContentRoot_CanParticipate
GetDirectContent_OverlaysProviderContentInRegistrationOrder
GetResources_WrapsProviderResourceIds
GetResourceContent_RoutesWrappedIdToCorrectProvider
ProviderContent_ResourceReferencesBecomeAggregateReferences
ProviderContent_AreaReferencesBecomeGlobalReferences
Mutation_AmbiguousOwner_IsRejected
Mutation_UniqueOwner_IsDelegated
Move_SameProvider_CanDelegate
Move_CrossProvider_IsRejected
ProviderReadFailure_DoesNotHideOtherProviderResults
BeginPreferExistingScope_PropagatesOncePerUniqueRepository
IsAreaCached_DoesNotMaterializeTree
```

Use MSTest only.

---

# 34. Bottom-up artifact guide

## `_Repositories`

Registration-ordered mount list.

## `MountedRepository`

One mounted provider and its mount metadata.

## `AreaContribution`

One provider-local contribution to a global logical node.

## `AggregatedNode`

Merged global area.

## `AggregatedTree`

Path-indexed logical projection.

## `_KnowledgeResourceReferenceRegex`

Finds canonical resource references in content.

## `_KnowledgeAreaReferenceRegex`

Finds canonical logical area references in content.

## `_TreeReadBurstWindow`

Short-lived structural reuse window; not a persistent cache.

## `CompositeCacheReadScope`

Combines optional child cache-control scopes.

---

# 35. Extension rules

When adding a new aggregate feature:

1. Decide whether semantics are read-combinable or write-exclusive.
2. Preserve deterministic provider registration order.
3. Use global/local path translators.
4. Respect `BeyondContent`.
5. Do not materialize more tree than required.
6. Translate provider-neutral references at mount boundaries.
7. Wrap provider resource IDs.
8. Reject ambiguous mutations.
9. Isolate read failures where partial output remains safe.
10. Add MSTests for overlays and boundary behavior.

---

# 36. Architectural mental model

Think of the aggregator as:

```text
               global logical namespace
                         |
          +--------------+--------------+
          |              |              |
       mount A         mount B        mount C
          |              |              |
     provider A      provider B      provider C
```

The global namespace owns:

- paths,
- overlay semantics,
- resource namespacing,
- reference translation,
- mutation routing decisions.

Each provider still owns:

- its storage,
- its local identity,
- its content semantics,
- its actual mutations.

The aggregator must bridge those worlds without flattening away the important boundaries.
