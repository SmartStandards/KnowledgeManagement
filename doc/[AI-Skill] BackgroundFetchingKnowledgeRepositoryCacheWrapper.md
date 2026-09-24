# [AI Skill] BackgroundFetchingKnowledgeRepositoryCacheWrapper

## 1. Abstract and motivation

`BackgroundFetchingKnowledgeRepositoryCacheWrapper` is a persistent, cache-only read facade around an arbitrary `IKnowledgeRepository`.

Its central design goal is responsiveness under slow, remote, throttled, or recursively expensive providers.

Normal reads must return immediately from local cache state. They must not unexpectedly block on the wrapped source. Missing and stale values are scheduled for future retrieval through `PrefetchNext()`.

The implementation differs positively from a conventional TTL cache:

- Normal reads are source-free.
- Cache misses are represented as scheduled work rather than synchronous source reads.
- Stale data is served as last-known-good data.
- Explicit consumer demand can jump ahead of autonomous prefetching.
- One heartbeat performs at most one logical source operation.
- Autonomous discovery is split into structure-first and content-later phases.
- Structure is discovered breadth-first so navigation becomes useful quickly.
- Content is loaded deepest-first so leaf content becomes useful before broad parent projections.
- Aggregated content is not autonomously prefetched because it can represent expensive recursive work.
- Persistent cache generation tokens coordinate multiple wrapper instances.
- Structural root revalidation occurs after startup so changed mount topology is discovered.
- Resource consistency has narrowly scoped synchronous dependency exceptions.
- HTTP 429 can be requeued without sleeping inside the heartbeat.
- Structural changes do not explode into hundreds of queued content jobs.

This document is normative for future work on the wrapper.

---

# 2. Scope

Class:

```text
KnowledgeManagement.SmartStandards.Wrappers.BackgroundFetchingKnowledgeRepositoryCacheWrapper
```

Current source baseline documented here:

```text
BackgroundFetchingKnowledgeRepositoryCacheWrapper_SchedulerReworkedV5.cs
```

Implements:

```text
IKnowledgeRepository
```

The wrapped source remains authoritative.

The cache is a projection, never the source of truth for mutations.

---

# 3. Fundamental invariant

The most important invariant is:

> A normal repository read must not perform ordinary source I/O.

Conceptually:

```text
UI / consumer
    ↓
BackgroundFetchingKnowledgeRepositoryCacheWrapper
    ↓
local memory cache / persistent cache only
```

Source access happens through:

```text
PrefetchNext()
```

or through narrowly defined exact resource-dependency repairs.

This separation must not be weakened casually.

---

# 4. Top-down example

## 4.1 Construction

Conceptually:

```csharp
BackgroundFetchingKnowledgeRepositoryCacheWrapper cachedRepository =
  new BackgroundFetchingKnowledgeRepositoryCacheWrapper(
    authoritativeRepository,
    240,
    cacheDirectory
  );
```

A four-hour background-cache lifetime is a valid deployment choice.

UI-level caching is a separate concern and may have a much shorter lifetime or be disabled entirely.

---

## 4.2 Consumer reads

First UI request:

```text
GetAreas(false, "/")
```

If root children are cached:

```text
return cached children immediately
```

If stale:

```text
return stale children immediately
queue refresh
```

If missing:

```text
return fallback immediately
queue initial fetch
```

The UI stays responsive.

---

## 4.3 Heartbeat

External scheduler:

```text
Go
  ↓
PrefetchNext(cancellationToken)
  ↓
at most one logical source fetch
  ↓
return
```

`PrefetchNext()` must never internally consume the entire queue in one invocation.

---

# 5. Persistent cache layout

The current implementation uses:

```text
<cache-root>/
  .knowledge-cache/
    *.cache
    .generation
```

Important constants:

```text
cache format version: 3
cache entry extension: .cache
generation file: .generation
```

Cache format version 3 intentionally invalidates older projections after semantic provider changes such as OneNote `_onefiles` filtering.

A format version bump is appropriate when persisted values are structurally or semantically incompatible with the new projection.

Do not bump the format version for ordinary TTL changes.

---

# 6. Operation-granular cache model

The wrapper caches operations independently.

Typical operations:

```text
children
name
capabilities
search
resources
resource-content
has-direct-content
direct-content
aggregated-content
```

A cache key combines operation and argument.

This allows one logical area to have fresh structure but stale content, or cached content but missing resource metadata.

That granularity is intentional.

---

# 7. Read behavior

## 7.1 Missing entry

Normal behavior:

```text
read local cache
entry absent
    ↓
enqueue priority work
    ↓
return fallback
```

No source access.

## 7.2 Stale entry

Normal behavior:

```text
entry exists but TTL expired
    ↓
serve stale value
    ↓
enqueue refresh
```

Stale is better than blocking or returning empty data when a valid last-known-good value exists.

## 7.3 Fresh entry

Return directly.

---

# 8. Priority queue semantics

## 8.1 Purpose

The priority queue represents consumer demand.

If the user navigates deep into the repository, the requested missing/stale values must overtake unrelated autonomous background work.

This is the mechanism that makes the following UX possible:

```text
navigate into deep branch
    ↓
branch reads get queued
    ↓
next heartbeat fetches demanded data
    ↓
navigate back / reopen
    ↓
content is now available
```

## 8.2 Repeated demand promotes an existing queue item

A duplicate logical work item is not duplicated.

Instead:

```text
A B C D E
consumer requests D again
    ↓
D A B C E
```

The existing item is promoted to the front.

If either the old or new item has `ForceRefresh = true`, the promoted item retains force-refresh semantics.

This is essential for interactive navigation.

## 8.3 Queue must remain duplicate-free

`_QueuedWorkKeys` tracks logical work identity.

Do not allow multiple copies of the same operation/argument pair.

---

# 9. Startup topology revalidation

A persistent cache can outlive repository composition.

Example:

```text
old mount:
/SmartStandards

new mount:
/SmartStandards2
```

The persisted root children may still look fresh by TTL even though the topology changed.

Therefore construction queues:

```text
children("/")
ForceRefresh = true
```

This does not synchronously call the source.

The first available heartbeat revalidates the root structure.

This mechanism is intentionally cheap and protects against renamed/moved mounts.

---

# 10. Autonomous scheduler phases

When no relevant priority work is pending, the scheduler proceeds in this order:

```text
1. missing structure
2. missing content
3. missing resource binaries
4. expired structure
5. expired content
```

This ordering is a hard design decision.

Missing knowledge is more valuable than refreshing already usable stale knowledge.

---

# 11. Phase 1: missing structure breadth-first

Structural operations:

```text
name
capabilities
children
```

Autonomous structural discovery must not request content.

Traversal begins at `/` and proceeds breadth-first.

Why breadth-first:

- top navigation becomes complete quickly,
- users can discover repository layout before content finishes,
- broad structure does not get blocked behind one deep branch,
- new provider mounts become visible early.

Newly discovered children must not automatically enqueue six different operations.

They become visible to the next structural pass naturally.

This prevents queue explosions.

---

# 12. Phase 2: missing content deepest-first

After the known structure is complete, content is selected deepest-first.

Typical autonomous content operations:

```text
resources metadata
has-direct-content
direct-content
```

Why deepest-first:

```text
structure:
A
└── B
    └── C
        └── D
```

Load:

```text
D
C
B
A
```

This makes leaf content available first and reduces dependence on broad recursive aggregate operations.

It aligns with interactive navigation: users usually want the content at the leaf they opened.

---

# 13. Aggregated content is not autonomously prefetched

This is a critical safety rule.

Do not autonomously schedule:

```text
aggregated-content
```

as part of repository crawling.

Reason:

`GetAggregatedContent()` may represent a whole subtree and may recurse through many child areas.

A previous failure mode involved one “logical” aggregated-content work item spending effectively unbounded time recursively rendering a very large tree.

Therefore:

```text
aggregated-content
```

is fetched only when a consumer explicitly requests it and therefore places it into priority work.

This preserves functionality while preventing autonomous recursive content storms.

---

# 14. Resource dependency exception

Normal reads are cache-only, but resources have a narrow consistency exception.

If cached Markdown contains:

```text
knowledge-resource:<id>
```

and the exact resource metadata or binary needed to resolve that already-served cached content is missing, the wrapper may synchronously fetch the exact dependency.

Why:

Returning cached content with unresolved resources would produce internally inconsistent output.

This exception must remain narrowly scoped.

Do not generalize it into “missing data can fetch synchronously”.

---

# 15. Structural transitions

When refreshed `children(parent)` differs from the previous cached value:

```text
added children
removed children
```

the wrapper reconciles structure.

Removed child:

```text
remove cached subtree
remove queued work below subtree
```

Added child:

```text
do not flood priority queue
leave normal breadth-first structural discovery to find it
```

The parent's capabilities can be force-refreshed because composite capability semantics may depend on child composition.

Do not immediately enqueue content, resources, and aggregation for every newly discovered child.

---

# 16. Orphan healing

If a source operation indicates that a previously known area no longer exists, the wrapper can heal the stale projection by removing that area's cache subtree.

This is important after:

- provider-side deletion,
- mount changes,
- structural rename,
- stale aggregate projections.

Healing must remove both cache data and queued area-scoped work.

---

# 17. Persistent cache generation

Multiple wrapper instances may share one persistent cache directory.

`.generation` contains a generation token.

When one instance writes/invalidate persistent cache state:

```text
advance generation
```

Another instance detects the changed token:

```text
discard process-local memory cache
re-read shared persistent entries
```

This solves cross-instance coherence without storing generation history.

Do not delete or recreate the entire persistent cache merely because another process changed one entry.

---

# 18. Memory cache vs persistent cache

There are two cache layers:

```text
process-local memory cache
persistent operation cache
```

Memory cache is disposable acceleration.

Persistent cache is the reusable last-known-good state.

Generation changes invalidate only process-local snapshots first.

---

# 19. Cache format migration

Current format:

```text
_CacheFormatVersion = 3
```

Version 3 was introduced to force rebuild of old provider projections after semantics changed.

A persisted cache entry with the wrong format version must not be treated as valid.

Potential reasons for future format bumps:

- payload schema change,
- changed resource identity semantics,
- changed logical path projection,
- provider artifact filtering that changes persisted `children` semantics.

Non-reasons:

- changed TTL,
- additional logging,
- scheduler ordering changes that do not alter payload meaning.

---

# 20. `PrefetchNext()` contract

`PrefetchNext(CancellationToken)` is one heartbeat unit.

Mandatory behavior:

```text
at most one logical source fetch
then return
```

Do not implement:

```text
while(queue not empty)
  fetch
```

Do not sleep waiting for retry windows.

Do not turn one heartbeat into an entire repository synchronization.

---

# 21. Overlapping heartbeats

`Monitor.TryEnter(_PrefetchSyncRoot)` prevents two heartbeat executions from running concurrently.

If another `PrefetchNext()` is active:

```text
return false
```

without blocking.

This avoids parallel source access and overlapping tree/cache mutation from repeated scheduler triggers.

---

# 22. Cancellation

Cancellation is a graceful stop signal.

Required behavior:

```text
if cancellation requested
  return false
```

Do not use `ThrowIfCancellationRequested()` as the normal heartbeat contract.

If a work item has already been selected but source access has not begun, requeue it before returning when appropriate.

---

# 23. HTTP 429 / throttling

A throttled source request should not create an internal retry loop.

Correct behavior:

```text
429
  ↓
log
  ↓
requeue same work item
preserve ForceRefresh
  ↓
return false
```

The external heartbeat determines when the next attempt occurs.

This prevents one heartbeat from blocking for long provider retry intervals.

---

# 24. Missing-before-stale invariant

After explicit demand, autonomous work must prioritize absent data over refreshes.

Correct:

```text
priority demand
missing structure
missing content
missing resource binaries
expired structure
expired content
```

Wrong:

```text
refresh thousands of stale entries
before first-time content is populated
```

This invariant is especially important after startup.

---

# 25. Scheduler diagnostics

The implementation includes detailed scheduler tracing to distinguish:

```text
no work exists
```

from:

```text
scheduler scan is expensive
```

and from:

```text
stale entries are not recognized
```

Useful diagnostics include:

- Prefetch entry
- priority selected / empty
- autonomous scheduler entry
- missing structure selected / none
- missing content selected / none
- missing resource selected / none
- expired structure selected / none
- expired content selected / none
- persistent entry scan counts
- elapsed milliseconds
- visited area counts
- structural entry counts
- expired entry counts

Do not remove diagnostic visibility until scheduler behavior is proven stable.

---

# 26. KnowledgeManagement logging convention

All new `DevLogger.LogTrace` calls must follow:

```csharp
DevLogger.LogTrace(<inlineSnowflake44>, <inlineEventKindId>, $"...");
```

Rules:

- SourceLineUid is a unique Snowflake44 `long`.
- It is inline, never a constant.
- Every source logging line has a different UID.
- EventKindId is inline.
- Semantically equal messages may reuse the same EventKindId.
- KnowledgeManagement range:

```text
75200-75299
```

- Ask before allocating another number range.
- Prefer single-line log calls unless genuinely extreme.
- Prefer `$""` interpolation over string concatenation.

The SourceLineUid exists specifically to locate the exact source statement.

---

# 27. Mutation behavior

All mutation calls go to the authoritative `_WrappedSource`.

On successful mutation:

```text
invalidate local cache
clear memory cache
remove persistent entries
advance generation
queue root rediscovery
```

The wrapper must not implement mutations against cached values only.

---

# 28. Requirements matrix

| ID | Requirement | Mandatory |
|---|---|---|
| BF-001 | Normal reads do not perform ordinary source I/O | Yes |
| BF-002 | Missing reads queue work and return fallback | Yes |
| BF-003 | Stale reads serve stale value and queue refresh | Yes |
| BF-004 | Priority demand always precedes autonomous work | Yes |
| BF-005 | Repeated demand promotes existing queued item | Yes |
| BF-006 | Queue remains duplicate-free | Yes |
| BF-007 | One `PrefetchNext` does at most one logical fetch | Yes |
| BF-008 | Overlapping heartbeats do not block | Yes |
| BF-009 | Cancellation returns gracefully | Yes |
| BF-010 | Startup queues forced root child revalidation | Yes |
| BF-011 | Missing structure is breadth-first | Yes |
| BF-012 | Missing content is deepest-first | Yes |
| BF-013 | Missing work precedes expired refresh work | Yes |
| BF-014 | Aggregated content is not autonomously prefetched | Yes |
| BF-015 | New child discovery does not flood the priority queue | Yes |
| BF-016 | Removed subtrees are healed | Yes |
| BF-017 | Persistent cache generation coordinates instances | Yes |
| BF-018 | 429 requeues and returns; no internal wait loop | Yes |
| BF-019 | Exact resource dependencies may be repaired synchronously | Yes |
| BF-020 | Cache format version is validated | Yes |
| BF-021 | Mutations operate on authoritative source | Yes |
| BF-022 | Successful mutations invalidate cache projections | Yes |
| BF-023 | Logging follows inline Snowflake44 rules | Yes |

---

# 29. Common traps

## 29.1 Priority queue filled by autonomous discovery

Wrong.

Priority means consumer demand, not “anything discovered”.

## 29.2 Enqueueing every operation for every new child

Wrong.

A node with 37 children can create hundreds of items instantly.

## 29.3 Automatically fetching `GetAggregatedContent("/")`

Wrong.

This can become one enormous recursive source operation.

## 29.4 Stale-before-missing scheduling

Wrong for startup usability.

Missing content must be populated first.

## 29.5 `PrefetchNext()` retry loop

Wrong.

One heartbeat is one work unit.

## 29.6 Throwing cancellation exceptions from heartbeat

Wrong for the intended scheduler contract.

## 29.7 Treating the UI cache lifetime as the background repository lifetime

Wrong.

They are independent cache layers with independent goals.

## 29.8 Assuming TTL validates repository topology

Wrong.

Mount points can change while root children still look fresh.

Root topology is explicitly revalidated on startup.

---

# 30. Recommended MSTest coverage

Minimum high-value tests:

```text
ReadMissingValue_ReturnsFallbackAndQueuesFetch
ReadStaleValue_ReturnsStaleAndQueuesRefresh
ReadFreshValue_DoesNotQueueRefresh
RepeatedDemand_PromotesExistingWorkToFront
RepeatedDemand_PreservesForceRefresh
PriorityDemand_PrecedesAutonomousWork
PrefetchNext_PerformsOnlyOneSourceCall
PrefetchNext_WhenConcurrent_ReturnsFalse
PrefetchNext_WhenCancelled_ReturnsFalse
Startup_QueuesForcedRootChildrenRefresh
AutonomousScheduler_CompletesMissingStructureBeforeContent
AutonomousScheduler_UsesBreadthFirstStructureOrder
AutonomousScheduler_UsesDeepestFirstContentOrder
AutonomousScheduler_DoesNotPrefetchAggregatedContent
AutonomousScheduler_MissingContentPrecedesExpiredStructure
ChildrenTransition_DoesNotQueueContentForEveryNewChild
ChildrenTransition_RemovesDeletedSubtree
GenerationChange_DropsOnlyProcessLocalMemorySnapshot
ThrottledFetch_RequeuesWithoutLooping
ResourceDependency_MaySynchronouslyRepairExactMissingMetadata
MutationSuccess_InvalidatesPersistentProjection
FormatVersionMismatch_DoesNotServeOldEntry
```

Use MSTest only.

---

# 31. Bottom-up artifact guide

## `FetchWorkItem`

Represents:

- operation
- argument
- reason
- force-refresh flag
- duplicate key

It is scheduler state, not repository content.

## `_PriorityQueue`

Demand queue.

Do not reuse it as the general autonomous work queue.

## `_QueuedWorkKeys`

Duplicate suppression.

Must remain coherent whenever queue items are removed/promoted/requeued.

## `MemoryCacheEntry`

Process-local deserialized cache state.

## `PersistentCacheEntry`

Operation-granular persistent envelope.

Contains creation timestamp, operation, argument, format information, and payload.

## `CachedCapabilities`

Serializable representation of the multi-output `GetAreaCapabilities()` call.

## `.generation`

Cross-instance invalidation token.

---

# 32. Extension rules

When adding a new cached operation:

1. Decide whether it is area-scoped.
2. Decide whether it is structural or content.
3. Decide whether it can be autonomously prefetched.
4. Decide whether it can recurse internally.
5. Define fallback semantics.
6. Define stale-serving semantics.
7. Define mutation invalidation semantics.
8. Define orphan-subtree cleanup behavior.
9. Add scheduler diagnostics.
10. Add MSTests for queue order and one-fetch heartbeat semantics.

Do not simply add the operation to every autonomous phase.

---

# 33. Operational mental model

The intended runtime behavior is:

```text
Consumer reads quickly from cache
        +
Consumer demand moves to queue front
        +
Background slowly learns the whole tree
        +
Background fills leaf content
        +
Only afterwards refreshes old usable data
```

The wrapper is not a synchronization engine.

It is a responsive cache-first knowledge facade with cooperative incremental convergence.
