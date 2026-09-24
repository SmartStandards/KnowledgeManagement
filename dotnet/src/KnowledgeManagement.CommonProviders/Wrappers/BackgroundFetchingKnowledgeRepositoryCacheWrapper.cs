using KnowledgeManagement.SmartStandards.Providers;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KnowledgeManagement.SmartStandards.Wrappers {

  /// <summary>
  /// Provides a persistent, strictly cache-only read view over an arbitrary knowledge
  /// repository while incrementally filling and refreshing that cache in the background.
  ///
  /// Normal repository reads never access the wrapped source. Missing or expired values are
  /// queued with high priority and the best locally available value is returned immediately.
  /// <see cref="PrefetchNext"/> performs exactly one logical source fetch per successful call.
  /// Explicit consumer demand is always processed first. Autonomous work first completes the
  /// navigable structure breadth-first and only then fills content deepest-first. Aggregated
  /// content is never prefetched autonomously because it represents an explicit subtree read.
  /// </summary>
  public sealed class BackgroundFetchingKnowledgeRepositoryCacheWrapper : IKnowledgeRepository {

    private const int _CacheFormatVersion = 3;
    private const string _CacheDirectoryName = ".knowledge-cache";
    private const string _CacheEntryExtension = ".cache";
    private const string _CacheGenerationFileName = ".generation";
    private const string _KnowledgeResourceReferencePrefix = "knowledge-resource:";


    private readonly object _SyncRoot;
    private readonly object _PrefetchSyncRoot;
    private readonly IKnowledgeRepository _WrappedSource;
    private readonly TimeSpan _Lifetime;
    private readonly string _CacheFileSystemPath;
    private readonly string _CacheDirectory;
    private readonly string _CacheGenerationFile;
    private readonly Dictionary<string, MemoryCacheEntry> _MemoryCache;
    private string _KnownCacheGeneration;
    private readonly Queue<FetchWorkItem> _PriorityQueue;
    private readonly HashSet<string> _QueuedWorkKeys;

    /// <summary>
    /// Creates a background-fetching cache around an authoritative repository.
    /// </summary>
    /// <param name="wrappedSource">The authoritative source repository.</param>
    /// <param name="lifetimeMin">The desired cache lifetime in minutes.</param>
    /// <param name="cacheFileSystemPath">
    /// Optional persistent cache root. The cache format is compatible with the operation-
    /// granular <see cref="KnowledgeRepositoryCacheWrapper"/> cache.
    /// </param>
    public BackgroundFetchingKnowledgeRepositoryCacheWrapper(
      IKnowledgeRepository wrappedSource,
      int lifetimeMin = 10,
      string cacheFileSystemPath = null
    ) {
      if (wrappedSource == null) {
        throw new ArgumentNullException(nameof(wrappedSource));
      }

      if (lifetimeMin < 0) {
        throw new ArgumentOutOfRangeException(nameof(lifetimeMin), "The cache lifetime must not be negative.");
      }

      _SyncRoot = new object();
      _PrefetchSyncRoot = new object();
      _WrappedSource = wrappedSource;
      _Lifetime = TimeSpan.FromMinutes(lifetimeMin);
      _CacheFileSystemPath = this.ResolveCacheFileSystemPath(cacheFileSystemPath);
      _CacheDirectory = Path.Combine(_CacheFileSystemPath, _CacheDirectoryName);
      _CacheGenerationFile = Path.Combine(_CacheDirectory, _CacheGenerationFileName);
      _MemoryCache = new Dictionary<string, MemoryCacheEntry>(StringComparer.Ordinal);
      _PriorityQueue = new Queue<FetchWorkItem>();
      _QueuedWorkKeys = new HashSet<string>(StringComparer.Ordinal);

      Directory.CreateDirectory(_CacheDirectory);
      _KnownCacheGeneration = this.GetOrCreateCacheGeneration();

      // The persistent cache can survive application restarts while the composition of an
      // aggregated repository may have changed, for example because a provider was mounted
      // at a different logical path. Always revalidate the root structure on the first
      // available heartbeat instead of trusting a still-fresh persisted root enumeration.
      this.EnqueuePriorityWork(
        "children",
        "/",
        "startup root structure revalidation",
        true
      );
    }

    /// <summary>
    /// Gets the authoritative repository wrapped by this cache.
    /// </summary>
    public IKnowledgeRepository WrappedSource {
      get {
        return _WrappedSource;
      }
    }

    /// <summary>
    /// Gets the configured cache lifetime.
    /// </summary>
    public TimeSpan Lifetime {
      get {
        return _Lifetime;
      }
    }

    /// <summary>
    /// Gets the physical persistent cache root.
    /// </summary>
    public string CacheFileSystemPath {
      get {
        return _CacheFileSystemPath;
      }
    }

    /// <summary>
    /// Returns logical areas exclusively from the local cache.
    /// Missing child enumerations are queued for background retrieval.
    /// </summary>
    public string[] GetAreas(bool recurse, string startArea = "/") {
      lock (_SyncRoot) {
        if (!recurse) {
          string[] directChildren = this.ReadCachedOnly("children", startArea, Array.Empty<string>());
          DevLogger.LogTrace(0, 99999, "STRUCTURE-DIAG GetAreas(false): parent='" + startArea + "', count=" + directChildren.Length.ToString() + ", children=" + this.FormatDiagnosticAreas(directChildren) + ".");
          return directChildren;
        }

        List<string> result = new List<string>();
        Queue<string> pending = new Queue<string>();
        string[] rootChildren = this.ReadCachedOnly("children", startArea, Array.Empty<string>());

        foreach (string child in rootChildren) {
          pending.Enqueue(child);
        }

        while (pending.Count > 0) {
          string current = pending.Dequeue();
          result.Add(current);

          string[] children = this.ReadCachedOnly("children", current, Array.Empty<string>());
          foreach (string child in children) {
            pending.Enqueue(child);
          }
        }

        string[] recursiveAreas = result.ToArray();
        DevLogger.LogTrace(0, 99999, "STRUCTURE-DIAG GetAreas(true): start='" + startArea + "', count=" + recursiveAreas.Length.ToString() + ", areas=" + this.FormatDiagnosticAreas(recursiveAreas) + ".");
        return recursiveAreas;
      }
    }

    /// <summary>
    /// Returns a cached provider-native keyword search result without source I/O.
    /// </summary>
    public string[] GetAreasByKeyword(string keyword, string startArea = "/") {
      if (string.IsNullOrWhiteSpace(keyword)) {
        return Array.Empty<string>();
      }

      lock (_SyncRoot) {
        string argument = startArea + "\n" + keyword;
        return this.ReadCachedOnly("search", argument, Array.Empty<string>());
      }
    }

    /// <summary>
    /// Returns a cached area display name without source I/O.
    /// </summary>
    public string GetAreaName(string area) {
      lock (_SyncRoot) {
        return this.ReadCachedOnly("name", area, string.Empty);
      }
    }

    /// <summary>
    /// Returns cached area capabilities without source I/O.
    /// </summary>
    public void GetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate,
      out bool supportsResources
    ) {
      lock (_SyncRoot) {
        CachedCapabilities capabilities = this.ReadCachedOnly("capabilities", area, new CachedCapabilities());

        contentLevel = capabilities.ContentLevel;
        supportsSubAreas = capabilities.SupportsSubAreas;
        canBeRenamed = capabilities.CanBeRenamed;
        canBeDeleted = capabilities.CanBeDeleted;
        canAddSubAreas = capabilities.CanAddSubAreas;
        canAppendContent = capabilities.CanAppendContent;
        canTruncate = capabilities.CanTruncate;
        supportsResources = capabilities.SupportsResources;
      }
    }

    /// <summary>
    /// Returns resource metadata from the local cache whenever possible.
    ///
    /// A narrowly scoped synchronous source read is allowed when already cached content
    /// references resources but the metadata required to resolve those references is missing
    /// or incomplete. This preserves cache/source consistency without fabricating placeholder
    /// resources.
    /// </summary>
    public KnowledgeResourceInfo[] GetResources(string area) {
      lock (_SyncRoot) {
        KnowledgeResourceInfo[] resources;
        PersistentCacheEntry entry;
        string[] referencedResourceIds = this.GetReferencedResourceIdsFromCachedContent(area);

        if (this.TryReadCacheValue("resources", area, out entry, out resources)) {
          DateTime utcNow = DateTime.UtcNow;
          TimeSpan age = utcNow - entry.CreatedUtc;
          string[] unresolvedResourceIds = this.GetUnresolvedResourceIds(resources, referencedResourceIds);

          if (unresolvedResourceIds.Length > 0) {
            DevLogger.LogTrace(0, 99999, "Background knowledge cache requires synchronous dependency fetch: operation='resources', argument='" + area + "', reason='cached content references resource identifiers absent from cached resource metadata', referencedResourceCount=" + referencedResourceIds.Length.ToString() + ", unresolvedResourceCount=" + unresolvedResourceIds.Length.ToString() + ".");
            return this.FetchResourceMetadataSynchronously(area, "cached content references resource identifiers absent from cached resource metadata");
          }

          if (!this.IsFresh(entry.CreatedUtc, utcNow)) {
            DevLogger.LogTrace(0, 99999, "Background knowledge cache answered resource metadata from stale cache: area='" + area + "', resourceCount=" + resources.Length.ToString() + ", ageSeconds=" + ((long)age.TotalSeconds).ToString() + ", lifetimeSeconds=" + ((long)_Lifetime.TotalSeconds).ToString() + ". Queuing refresh.");
            this.EnqueuePriorityWork("resources", area, "stale resource metadata was served");
          }
          else {
            DevLogger.LogTrace(0, 99999, "Background knowledge cache answered resource metadata from cache: area='" + area + "', resourceCount=" + resources.Length.ToString() + ", ageSeconds=" + ((long)age.TotalSeconds).ToString() + ".");
          }

          return resources;
        }

        if (referencedResourceIds.Length > 0) {
          DevLogger.LogTrace(0, 99999, "Background knowledge cache requires synchronous dependency fetch: operation='resources', argument='" + area + "', reason='cached content references resources whose metadata is not cached', referencedResourceCount=" + referencedResourceIds.Length.ToString() + ".");
          return this.FetchResourceMetadataSynchronously(area, "cached content references resources whose metadata is not cached");
        }

        this.EnqueuePriorityWork("resources", area, "resource metadata is not cached");
        DevLogger.LogTrace(0, 99999, "Background knowledge cache answered with empty resource fallback: area='" + area + "', reason='resource metadata is not cached and cached content exposes no resource references'.");
        return Array.Empty<KnowledgeResourceInfo>();
      }
    }

    /// <summary>
    /// Returns cached resource bytes. A synchronous source read is used only when the resource
    /// is already confirmed by cached resource metadata and its binary payload is still missing.
    /// </summary>
    public byte[] GetResourceContent(string resourceId) {
      lock (_SyncRoot) {
        byte[] content;
        PersistentCacheEntry entry;

        if (this.TryReadCacheValue("resource-content", resourceId, out entry, out content)) {
          DateTime utcNow = DateTime.UtcNow;
          TimeSpan age = utcNow - entry.CreatedUtc;

          if (!this.IsFresh(entry.CreatedUtc, utcNow)) {
            DevLogger.LogTrace(0, 99999, "Background knowledge cache answered resource content from stale cache: resourceId='" + resourceId + "', byteCount=" + content.Length.ToString() + ", ageSeconds=" + ((long)age.TotalSeconds).ToString() + ", lifetimeSeconds=" + ((long)_Lifetime.TotalSeconds).ToString() + ". Queuing refresh.");
            this.EnqueuePriorityWork("resource-content", resourceId, "stale resource content was served");
          }
          else {
            DevLogger.LogTrace(0, 99999, "Background knowledge cache answered resource content from cache: resourceId='" + resourceId + "', byteCount=" + content.Length.ToString() + ", ageSeconds=" + ((long)age.TotalSeconds).ToString() + ".");
          }

          return content;
        }

        if (this.IsResourceConfirmedByCachedMetadata(resourceId)) {
          DevLogger.LogTrace(0, 99999, "Background knowledge cache requires synchronous dependency fetch: operation='resource-content', argument='" + resourceId + "', reason='resource metadata is cached but binary content is not cached'.");
          return this.FetchResourceContentSynchronously(resourceId, "resource metadata is cached but binary content is not cached");
        }

        this.EnqueuePriorityWork("resource-content", resourceId, "resource content is not cached");
        DevLogger.LogTrace(0, 99999, "Background knowledge cache answered with empty resource-content fallback: resourceId='" + resourceId + "', reason='binary content is not cached and no cached resource metadata confirms the identifier'.");
        return Array.Empty<byte>();
      }
    }

    /// <summary>
    /// Adds a resource to the authoritative source and invalidates the local cache so the
    /// changed state is fetched again in the background.
    /// </summary>
    public bool TryAddResource(string area, string preferredFileName, string contentType, byte[] content, out string resourceId) {
      bool succeeded = _WrappedSource.TryAddResource(area, preferredFileName, contentType, content, out resourceId);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Replaces a resource in the authoritative source and invalidates the local cache.
    /// </summary>
    public bool TryReplaceResource(string resourceId, string contentType, byte[] content) {
      bool succeeded = _WrappedSource.TryReplaceResource(resourceId, contentType, content);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Deletes a resource in the authoritative source and invalidates the local cache.
    /// </summary>
    public bool TryDeleteResource(string resourceId) {
      bool succeeded = _WrappedSource.TryDeleteResource(resourceId);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Returns cached direct-content availability without source I/O.
    /// </summary>
    public bool HasDirectContent(string area) {
      lock (_SyncRoot) {
        return this.ReadCachedOnly("has-direct-content", area, false);
      }
    }

    /// <summary>
    /// Returns cached direct content without source I/O.
    /// </summary>
    public string GetDirectContent(string area) {
      lock (_SyncRoot) {
        string content = this.ReadCachedOnly("direct-content", area, string.Empty);
        return this.GetConsistentCachedContent(area, content);
      }
    }

    /// <summary>
    /// Returns cached aggregated content without source I/O.
    /// </summary>
    public string GetAggregatedContent(string area) {
      lock (_SyncRoot) {
        string content = this.ReadCachedOnly("aggregated-content", area, string.Empty);
        return this.GetConsistentCachedContent(area, content);
      }
    }

    /// <summary>
    /// Deletes an authoritative area and invalidates the local cache after success.
    /// </summary>
    public bool TryDelete(string area) {
      bool succeeded = _WrappedSource.TryDelete(area);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Renames an authoritative area and invalidates the local cache after success.
    /// </summary>
    public bool TryRename(string area, string newName, out KnowledgeResourceIdChange[] resourceIdChanges) {
      bool succeeded = _WrappedSource.TryRename(area, newName, out resourceIdChanges);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Adds an authoritative child area and invalidates the local cache after success.
    /// </summary>
    public bool TryAddSubArea(string area, string name, KnowledgeAreaKind kind) {
      bool succeeded = _WrappedSource.TryAddSubArea(area, name, kind);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Appends authoritative content and invalidates the local cache after success.
    /// </summary>
    public bool TryAppendContent(string area, string content) {
      bool succeeded = _WrappedSource.TryAppendContent(area, content);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Truncates authoritative content and invalidates the local cache after success.
    /// </summary>
    public bool TryTruncate(string area) {
      bool succeeded = _WrappedSource.TryTruncate(area);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Replaces authoritative content and invalidates the local cache after success.
    /// </summary>
    public bool TryReplace(string area, string newContent) {
      bool succeeded = _WrappedSource.TryReplace(area, newContent);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Moves authoritative content and invalidates the local cache after success.
    /// </summary>
    public bool TryMoveContent(string contentAreaToMove, string newParentArea, out KnowledgeResourceIdChange[] resourceIdChanges) {
      bool succeeded = _WrappedSource.TryMoveContent(contentAreaToMove, newParentArea, out resourceIdChanges);
      if (succeeded) {
        this.InvalidateAfterMutation();
      }
      return succeeded;
    }

    /// <summary>
    /// Performs at most one logical background fetch attempt.
    ///
    /// Explicitly requested cache misses and stale reads are processed first. Otherwise the
    /// known repository is scanned breadth-first for one missing value and then oldest-first
    /// for one expired value.
    ///
    /// This method deliberately never loops and never waits for a retry. One invocation is
    /// one heartbeat work unit. A throttled request is queued again for a later heartbeat.
    /// Cancellation is treated as a graceful stop request and therefore returns false rather
    /// than throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <returns>
    /// True when one source value was fetched and cached or one orphaned area was healed;
    /// otherwise false.
    /// </returns>
    public bool PrefetchNext(CancellationToken cancellationToken) {
      DevLogger.LogTrace(
        7421903804101L,
        75200,
        "Background knowledge cache PrefetchNext entered. priorityQueueLength="
        + this.GetPriorityQueueLength().ToString()
        + ", cancellationRequested="
        + cancellationToken.IsCancellationRequested.ToString()
        + "."
      );

      if (cancellationToken.IsCancellationRequested) {
        return false;
      }

      if (!Monitor.TryEnter(_PrefetchSyncRoot)) {
        DevLogger.LogTrace(
          7421903804102L,
          75201,
          "Background knowledge cache skipped heartbeat because another PrefetchNext invocation is still active."
        );

        return false;
      }

      try {
        if (cancellationToken.IsCancellationRequested) {
          return false;
        }

        FetchWorkItem workItem;

        lock (_SyncRoot) {
          if (this.TryDequeuePriorityWork(out workItem)) {
            DevLogger.LogTrace(
              7421903804103L,
              75202,
              "Background knowledge cache selected work from priority queue: operation='"
              + workItem.Operation
              + "', argument='"
              + workItem.Argument
              + "', remainingPriorityQueueLength="
              + _PriorityQueue.Count.ToString()
              + "."
            );
          }
          else {
            DevLogger.LogTrace(
              7421903804104L,
              75203,
              "Background knowledge cache priority queue contains no relevant work. Starting autonomous scheduler."
            );

            workItem = this.FindNextBackgroundWorkItem();
          }
        }

        if (workItem == null) {
          DevLogger.LogTrace(
            7421903804116L,
            75215,
            "Background knowledge cache PrefetchNext found no work item and returns without source access."
          );

          return false;
        }

        DevLogger.LogTrace(
          0,
          99999,
          "Background knowledge cache selected fetch: operation='"
          + workItem.Operation
          + "', argument='"
          + workItem.Argument
          + "', reason='"
          + workItem.Reason
          + "', priorityQueueLength="
          + this.GetPriorityQueueLength().ToString()
          + "."
        );

        if (cancellationToken.IsCancellationRequested) {
          lock (_SyncRoot) {
            this.EnqueuePriorityWork(
              workItem.Operation,
              workItem.Argument,
              "selected background fetch was cancelled before source access",
              workItem.ForceRefresh
            );
          }

          return false;
        }

        try {
          string[] previousChildren = null;

          if (string.Equals(workItem.Operation, "children", StringComparison.Ordinal)) {
            lock (_SyncRoot) {
              string[] cachedChildren;

              if (this.TryReadCachedPayloadOnly(
                    "children",
                    workItem.Argument,
                    out cachedChildren
                  )) {
                previousChildren = cachedChildren;
              }
            }
          }

          if (cancellationToken.IsCancellationRequested) {
            lock (_SyncRoot) {
              this.EnqueuePriorityWork(
                workItem.Operation,
                workItem.Argument,
                "selected background fetch was cancelled before source access",
                workItem.ForceRefresh
              );
            }

            return false;
          }

          DevLogger.LogTrace(
            0,
            99999,
            "Background knowledge cache fetch started: operation='"
            + workItem.Operation
            + "', argument='"
            + workItem.Argument
            + "', reason='"
            + workItem.Reason
            + "'."
          );

          object value = this.FetchFromSource(workItem);

          if (string.Equals(workItem.Operation, "children", StringComparison.Ordinal)) {
            string[] sourceChildren = value as string[];

            if (sourceChildren == null) {
              sourceChildren = Array.Empty<string>();
            }

            DevLogger.LogTrace(
              0,
              99999,
              "STRUCTURE-DIAG source returned children: parent='"
              + workItem.Argument
              + "', count="
              + sourceChildren.Length.ToString()
              + ", children="
              + this.FormatDiagnosticAreas(sourceChildren)
              + "."
            );
          }

          // A synchronous source call cannot be interrupted through the repository contract.
          // If cancellation arrived while it was running, the completed result is still
          // committed so the next heartbeat does not repeat already completed source work.
          lock (_SyncRoot) {
            this.WriteCacheValue(
              workItem.Operation,
              workItem.Argument,
              value
            );

            if (string.Equals(workItem.Operation, "children", StringComparison.Ordinal)) {
              string[] currentChildren = value as string[];

              if (currentChildren == null) {
                currentChildren = Array.Empty<string>();
              }

              this.ProcessChildrenTransition(
                workItem.Argument,
                previousChildren,
                currentChildren
              );
            }
          }

          DevLogger.LogTrace(
            0,
            99999,
            "Background knowledge cache fetch completed: operation='"
            + workItem.Operation
            + "', argument='"
            + workItem.Argument
            + "', reason='"
            + workItem.Reason
            + "'."
          );

          return true;
        }
        catch (InvalidOperationException ex) when (
          this.IsMissingAggregatedKnowledgeAreaException(ex, workItem.Argument)
        ) {
          lock (_SyncRoot) {
            this.HealOrphanedArea(workItem.Argument);
          }

          DevLogger.LogTrace(
            0,
            99999,
            "Background knowledge cache completed orphan cleanup instead of fetch: operation='"
            + workItem.Operation
            + "', argument='"
            + workItem.Argument
            + "', reason='authoritative aggregated repository no longer exposes the cached area'."
          );

          return true;
        }
        catch (HttpRequestException ex) when (
          ex.StatusCode == HttpStatusCode.TooManyRequests
        ) {
          DevLogger.LogError(ex);

          lock (_SyncRoot) {
            this.EnqueuePriorityWork(
              workItem.Operation,
              workItem.Argument,
              "previous background fetch received HTTP 429 Too Many Requests",
              workItem.ForceRefresh
            );
          }

          DevLogger.LogTrace(
            0,
            99999,
            "Background knowledge cache deferred throttled fetch to a later heartbeat: operation='"
            + workItem.Operation
            + "', argument='"
            + workItem.Argument
            + "'."
          );

          return false;
        }
      }
      finally {
        Monitor.Exit(_PrefetchSyncRoot);
      }
    }

    /// <summary>
    /// Keeps resource-bearing cached content immediately available and records any missing
    /// metadata dependency. The dependency itself is resolved synchronously only when a
    /// consumer subsequently asks for the corresponding resource metadata.
    /// </summary>
    private string GetConsistentCachedContent(string area, string content) {
      if (string.IsNullOrEmpty(content)) {
        return content;
      }

      string[] referencedResourceIds = this.ExtractKnowledgeResourceIds(content);
      if (referencedResourceIds.Length == 0) {
        return content;
      }

      CachedCapabilities capabilities;
      bool capabilitiesCached = this.TryReadCachedPayloadOnly("capabilities", area, out capabilities);
      if (!capabilitiesCached) {
        this.EnqueuePriorityWork("capabilities", area, "cached content references resources but capabilities are not cached");
      }

      KnowledgeResourceInfo[] resources;
      bool resourcesCached = this.TryReadCachedPayloadOnly("resources", area, out resources);
      if (!resourcesCached) {
        this.EnqueuePriorityWork("resources", area, "cached content references resources but resource metadata is not cached");
        DevLogger.LogTrace(0, 99999, "Background knowledge cache served resource-bearing content with pending metadata dependency: area='" + area + "', referencedResourceCount=" + referencedResourceIds.Length.ToString() + ", capabilitiesCached=" + capabilitiesCached.ToString() + ", resourceMetadataCached=False. A later GetResources call may resolve this dependency synchronously.");
        return content;
      }

      string[] unresolvedResourceIds = this.GetUnresolvedResourceIds(resources, referencedResourceIds);
      if (unresolvedResourceIds.Length > 0) {
        this.EnqueuePriorityWork("resources", area, "cached content references resource identifiers absent from cached resource metadata");
        DevLogger.LogTrace(0, 99999, "Background knowledge cache detected resource/content mismatch: area='" + area + "', referencedResourceCount=" + referencedResourceIds.Length.ToString() + ", cachedResourceCount=" + resources.Length.ToString() + ", unresolvedReferenceCount=" + unresolvedResourceIds.Length.ToString() + ". Content remains available; a later GetResources call may resolve the dependency synchronously.");
      }
      else {
        DevLogger.LogTrace(0, 99999, "Background knowledge cache validated resource-bearing cached content: area='" + area + "', referencedResourceCount=" + referencedResourceIds.Length.ToString() + ", cachedResourceCount=" + resources.Length.ToString() + ".");
      }

      return content;
    }

    /// <summary>
    /// Returns resource identifiers referenced by content but absent from the supplied metadata.
    /// </summary>
    private string[] GetUnresolvedResourceIds(KnowledgeResourceInfo[] resources, string[] referencedResourceIds) {
      HashSet<string> exposedResourceIds = new HashSet<string>(StringComparer.Ordinal);

      foreach (KnowledgeResourceInfo resource in resources) {
        if (resource != null && !string.IsNullOrWhiteSpace(resource.ResourceId)) {
          exposedResourceIds.Add(resource.ResourceId);
        }
      }

      List<string> unresolvedResourceIds = new List<string>();
      foreach (string resourceId in referencedResourceIds) {
        if (!exposedResourceIds.Contains(resourceId)) {
          unresolvedResourceIds.Add(resourceId);
        }
      }

      return unresolvedResourceIds.ToArray();
    }

    /// <summary>
    /// Performs the exceptional synchronous metadata fetch required to make already served
    /// resource-bearing content resolvable, and stores the authoritative result in the cache.
    /// </summary>
    private KnowledgeResourceInfo[] FetchResourceMetadataSynchronously(string area, string reason) {
      DevLogger.LogTrace(0, 99999, "Background knowledge cache synchronous dependency fetch started: operation='resources', argument='" + area + "', reason='" + reason + "'.");
      KnowledgeResourceInfo[] resources = _WrappedSource.GetResources(area);
      this.WriteCacheValue("resources", area, resources);
      DevLogger.LogTrace(0, 99999, "Background knowledge cache synchronous dependency fetch completed: operation='resources', argument='" + area + "', resourceCount=" + resources.Length.ToString() + ", reason='" + reason + "'.");
      return resources;
    }

    /// <summary>
    /// Performs the exceptional synchronous binary fetch for a resource whose authoritative
    /// metadata is already known, and stores the payload in the normal cache.
    /// </summary>
    private byte[] FetchResourceContentSynchronously(string resourceId, string reason) {
      DevLogger.LogTrace(0, 99999, "Background knowledge cache synchronous dependency fetch started: operation='resource-content', argument='" + resourceId + "', reason='" + reason + "'.");
      byte[] content = _WrappedSource.GetResourceContent(resourceId);
      this.WriteCacheValue("resource-content", resourceId, content);
      DevLogger.LogTrace(0, 99999, "Background knowledge cache synchronous dependency fetch completed: operation='resource-content', argument='" + resourceId + "', byteCount=" + content.Length.ToString() + ", reason='" + reason + "'.");
      return content;
    }

    /// <summary>
    /// Determines whether any cached authoritative resource metadata currently exposes the
    /// supplied opaque resource identifier.
    /// </summary>
    private bool IsResourceConfirmedByCachedMetadata(string resourceId) {
      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();

      foreach (PersistentCacheEntry entry in entries) {
        if (!string.Equals(entry.Operation, "resources", StringComparison.Ordinal)) {
          continue;
        }

        KnowledgeResourceInfo[] resources;
        try {
          resources = JsonConvert.DeserializeObject<KnowledgeResourceInfo[]>(entry.PayloadJson);
        }
        catch (JsonException ex) {
          DevLogger.LogError(ex);
          continue;
        }

        if (resources == null) {
          continue;
        }

        foreach (KnowledgeResourceInfo resource in resources) {
          if (resource != null && string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal)) {
            return true;
          }
        }
      }

      return false;
    }

    /// <summary>
    /// Collects resource identifiers referenced by any currently cached textual projection
    /// of one area without queueing work or accessing the wrapped source.
    /// </summary>
    private string[] GetReferencedResourceIdsFromCachedContent(string area) {
      HashSet<string> resourceIds = new HashSet<string>(StringComparer.Ordinal);
      string directContent;
      string aggregatedContent;

      if (this.TryReadCachedPayloadOnly("direct-content", area, out directContent)) {
        foreach (string resourceId in this.ExtractKnowledgeResourceIds(directContent)) {
          resourceIds.Add(resourceId);
        }
      }

      if (this.TryReadCachedPayloadOnly("aggregated-content", area, out aggregatedContent)) {
        foreach (string resourceId in this.ExtractKnowledgeResourceIds(aggregatedContent)) {
          resourceIds.Add(resourceId);
        }
      }

      return resourceIds.ToArray();
    }

    /// <summary>
    /// Extracts opaque provider resource identifiers from provider-neutral knowledge-resource
    /// references. The parser intentionally stops only at URI/Markdown delimiters and does
    /// not interpret the provider-owned identifier itself.
    /// </summary>
    private string[] ExtractKnowledgeResourceIds(string content) {
      if (string.IsNullOrEmpty(content)) {
        return Array.Empty<string>();
      }

      MatchCollection matches = Regex.Matches(
        content,
        @"knowledge-resource:(?<id>[^\s\)\]>""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
      );

      HashSet<string> resourceIds = new HashSet<string>(StringComparer.Ordinal);
      foreach (Match match in matches) {
        string resourceId = match.Groups["id"].Value;
        if (!string.IsNullOrWhiteSpace(resourceId)) {
          resourceIds.Add(resourceId);
        }
      }

      return resourceIds.ToArray();
    }

    /// <summary>
    /// Reads one local cache value, queues missing or expired data and never calls the source.
    /// </summary>
    private T ReadCachedOnly<T>(string operation, string argument, T fallbackValue) {
      PersistentCacheEntry entry;
      T value;

      DateTime utcNow = DateTime.UtcNow;

      if (this.TryReadCacheValue(operation, argument, out entry, out value)) {
        TimeSpan age = utcNow - entry.CreatedUtc;

        if (string.Equals(operation, "children", StringComparison.Ordinal)) {
          object cachedValue = value;
          string[] cachedChildren = cachedValue as string[];
          if (cachedChildren == null) {
            cachedChildren = Array.Empty<string>();
          }

          DevLogger.LogTrace(0, 99999, "STRUCTURE-DIAG cache read children: parent='" + argument + "', createdUtc='" + entry.CreatedUtc.ToString("O") + "', ageSeconds=" + ((long)age.TotalSeconds).ToString() + ", count=" + cachedChildren.Length.ToString() + ", children=" + this.FormatDiagnosticAreas(cachedChildren) + ".");
        }
        if (!this.IsFresh(entry.CreatedUtc, utcNow)) {
          DevLogger.LogTrace(0, 99999, "Background knowledge cache answered from stale cache: operation='" + operation + "', argument='" + argument + "', ageSeconds=" + ((long)age.TotalSeconds).ToString() + ", lifetimeSeconds=" + ((long)_Lifetime.TotalSeconds).ToString() + ". Queuing refresh.");
          this.EnqueuePriorityWork(operation, argument, "stale cache value was served");
        }
        else {
          //DevLogger.LogTrace(0, 99999, "Background knowledge cache answered from cache: operation='" + operation + "', argument='" + argument + "', ageSeconds=" + ((long)age.TotalSeconds).ToString() + ".");
        }
        return value;
      }

      DevLogger.LogTrace(0, 99999, "Background knowledge cache answered with fallback: operation='" + operation + "', argument='" + argument + "', reason='cache entry does not exist'. Queuing initial fetch.");
      this.EnqueuePriorityWork(operation, argument, "cache entry does not exist");
      return fallbackValue;
    }

    /// <summary>
    /// Adds one source operation to the high-priority queue without creating duplicates.
    ///
    /// Re-requesting an already queued operation is treated as renewed consumer interest and
    /// therefore promotes that work item to the front of the priority queue.
    /// </summary>
    private void EnqueuePriorityWork(string operation, string argument) {
      this.EnqueuePriorityWork(
        operation,
        argument,
        "explicit dependency request",
        false
      );
    }

    /// <summary>
    /// Adds one source operation to the high-priority queue and records why it was queued.
    ///
    /// Re-requesting an already queued operation promotes it to the front so interactive
    /// navigation can overtake unrelated older background demand.
    /// </summary>
    private void EnqueuePriorityWork(
      string operation,
      string argument,
      string reason
    ) {
      this.EnqueuePriorityWork(
        operation,
        argument,
        reason,
        false
      );
    }

    /// <summary>
    /// Adds one source operation to the high-priority queue.
    ///
    /// The queue remains duplicate-free. If the same logical operation is already queued,
    /// the existing entry is removed and the merged work item is inserted at the front.
    /// Force-refresh semantics are preserved when either the existing or the new request
    /// requires them.
    ///
    /// This promotion behavior is intentional: every repeated request indicates current
    /// consumer interest and must be able to move the corresponding work ahead of unrelated
    /// queued work.
    /// </summary>
    private void EnqueuePriorityWork(
      string operation,
      string argument,
      string reason,
      bool forceRefresh
    ) {
      FetchWorkItem requestedItem = new FetchWorkItem(
        operation,
        argument,
        reason,
        forceRefresh
      );

      if (_QueuedWorkKeys.Add(requestedItem.Key)) {
        _PriorityQueue.Enqueue(
          requestedItem
        );

        DevLogger.LogTrace(
          0,
          99999,
          "Background knowledge cache queued priority fetch: operation='"
          + operation
          + "', argument='"
          + argument
          + "', reason='"
          + reason
          + "', forceRefresh="
          + forceRefresh.ToString()
          + ", priorityQueueLength="
          + _PriorityQueue.Count.ToString()
          + "."
        );

        return;
      }

      Queue<FetchWorkItem> retainedItems = new Queue<FetchWorkItem>();
      FetchWorkItem existingItem = null;

      while (_PriorityQueue.Count > 0) {
        FetchWorkItem queuedItem = _PriorityQueue.Dequeue();

        if (existingItem == null &&
            string.Equals(
              queuedItem.Key,
              requestedItem.Key,
              StringComparison.Ordinal
            )) {
          existingItem = queuedItem;
          continue;
        }

        retainedItems.Enqueue(
          queuedItem
        );
      }

      bool effectiveForceRefresh = forceRefresh;

      if (existingItem != null &&
          existingItem.ForceRefresh) {
        effectiveForceRefresh = true;
      }

      FetchWorkItem promotedItem = new FetchWorkItem(
        operation,
        argument,
        reason,
        effectiveForceRefresh
      );

      _PriorityQueue.Enqueue(
        promotedItem
      );

      while (retainedItems.Count > 0) {
        _PriorityQueue.Enqueue(
          retainedItems.Dequeue()
        );
      }

      DevLogger.LogTrace(
        0,
        99999,
        "Background knowledge cache promoted already queued priority fetch to front: operation='"
        + operation
        + "', argument='"
        + argument
        + "', reason='"
        + reason
        + "', forceRefresh="
        + effectiveForceRefresh.ToString()
        + ", priorityQueueLength="
        + _PriorityQueue.Count.ToString()
        + "."
      );
    }

    /// <summary>
    /// Returns the current priority queue length for diagnostics.
    /// </summary>
    private int GetPriorityQueueLength() {
      lock (_SyncRoot) {
        return _PriorityQueue.Count;
      }
    }

    /// <summary>
    /// Removes the oldest still-relevant explicitly requested work item.
    ///
    /// The priority queue represents consumer demand and therefore always wins over
    /// autonomous background discovery. Its insertion order is preserved deliberately:
    /// navigating into a branch queues exactly the values the consumer is waiting for and
    /// those requests must not be reordered behind unrelated autonomous structural work.
    /// </summary>
    private bool TryDequeuePriorityWork(out FetchWorkItem workItem) {
      while (_PriorityQueue.Count > 0) {
        FetchWorkItem candidate = _PriorityQueue.Dequeue();
        _QueuedWorkKeys.Remove(candidate.Key);

        if (!this.IsPriorityWorkStillRelevant(candidate)) {
          continue;
        }

        workItem = candidate;
        return true;
      }

      workItem = null;
      return false;
    }

    /// <summary>
    /// Determines whether a queued cache refresh is still required.
    /// </summary>
    private bool IsPriorityWorkStillRelevant(
      FetchWorkItem workItem
    ) {
      if (workItem.ForceRefresh) {
        return true;
      }

      PersistentCacheEntry entry;

      if (!this.TryReadCacheEntry(
            workItem.Operation,
            workItem.Argument,
            out entry
          )) {
        return true;
      }

      return !this.IsFresh(
        entry.CreatedUtc,
        DateTime.UtcNow
      );
    }

    /// <summary>
    /// Finds exactly one autonomous background work item.
    ///
    /// Autonomous prefetching is intentionally split into two phases:
    ///
    /// 1. Complete the visible repository structure breadth-first using only names,
    ///    capabilities and direct-child enumerations.
    /// 2. After the known structure is complete, fill content deepest-first so leaf content
    ///    becomes available before broader parent projections.
    ///
    /// Aggregated content is deliberately not prefetched autonomously. It is an explicit
    /// subtree operation and is fetched only when a consumer actually requests it, in which
    /// case the normal priority queue moves that request ahead of autonomous work.
    /// </summary>
    private FetchWorkItem FindNextBackgroundWorkItem() {
      DateTime schedulerStartedUtc = DateTime.UtcNow;

      DevLogger.LogTrace(
        7421903804105L,
        75204,
        "Background knowledge cache autonomous scheduler entered."
      );

      FetchWorkItem missingStructure = this.FindNextMissingStructureBreadthFirst();
      if (missingStructure != null) {
        DevLogger.LogTrace(
          7421903804107L,
          75206,
          "Background knowledge cache autonomous scheduler selected missing structure: operation='"
          + missingStructure.Operation
          + "', argument='"
          + missingStructure.Argument
          + "', elapsedMilliseconds="
          + ((long)(DateTime.UtcNow - schedulerStartedUtc).TotalMilliseconds).ToString()
          + "."
        );

        return missingStructure;
      }

      DevLogger.LogTrace(
        7421903804106L,
        75205,
        "Background knowledge cache autonomous scheduler found no missing structural work."
      );

      FetchWorkItem missingContent = this.FindNextMissingContentDeepestFirst();
      if (missingContent != null) {
        DevLogger.LogTrace(
          7421903804109L,
          75208,
          "Background knowledge cache autonomous scheduler selected missing content: operation='"
          + missingContent.Operation
          + "', argument='"
          + missingContent.Argument
          + "', elapsedMilliseconds="
          + ((long)(DateTime.UtcNow - schedulerStartedUtc).TotalMilliseconds).ToString()
          + "."
        );

        return missingContent;
      }

      DevLogger.LogTrace(
        7421903804108L,
        75207,
        "Background knowledge cache autonomous scheduler found no missing content work."
      );

      FetchWorkItem missingResource = this.FindMissingResourceContentWorkItem();
      if (missingResource != null) {
        DevLogger.LogTrace(
          7421903804111L,
          75210,
          "Background knowledge cache autonomous scheduler selected missing resource binary: argument='"
          + missingResource.Argument
          + "', elapsedMilliseconds="
          + ((long)(DateTime.UtcNow - schedulerStartedUtc).TotalMilliseconds).ToString()
          + "."
        );

        return missingResource;
      }

      DevLogger.LogTrace(
        7421903804110L,
        75209,
        "Background knowledge cache autonomous scheduler found no missing resource binary work."
      );

      FetchWorkItem expiredStructure = this.FindOldestExpiredStructuralWorkItem();
      if (expiredStructure != null) {
        DevLogger.LogTrace(
          7421903804113L,
          75212,
          "Background knowledge cache autonomous scheduler selected expired structure: operation='"
          + expiredStructure.Operation
          + "', argument='"
          + expiredStructure.Argument
          + "', elapsedMilliseconds="
          + ((long)(DateTime.UtcNow - schedulerStartedUtc).TotalMilliseconds).ToString()
          + "."
        );

        return expiredStructure;
      }

      DevLogger.LogTrace(
        7421903804112L,
        75211,
        "Background knowledge cache autonomous scheduler found no expired structural work."
      );

      FetchWorkItem expiredContent = this.FindOldestExpiredContentWorkItem();
      if (expiredContent != null) {
        DevLogger.LogTrace(
          7421903804115L,
          75214,
          "Background knowledge cache autonomous scheduler selected expired content: operation='"
          + expiredContent.Operation
          + "', argument='"
          + expiredContent.Argument
          + "', elapsedMilliseconds="
          + ((long)(DateTime.UtcNow - schedulerStartedUtc).TotalMilliseconds).ToString()
          + "."
        );

        return expiredContent;
      }

      DevLogger.LogTrace(
        7421903804114L,
        75213,
        "Background knowledge cache autonomous scheduler found no expired content work. totalElapsedMilliseconds="
        + ((long)(DateTime.UtcNow - schedulerStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      return null;
    }

    /// <summary>
    /// Finds one missing structural operation while traversing the locally known tree
    /// breadth-first.
    ///
    /// This phase never requests textual content or resources. Its only purpose is to make
    /// the complete navigable structure available as quickly as possible.
    /// </summary>
    private FetchWorkItem FindNextMissingStructureBreadthFirst() {
      DateTime scanStartedUtc = DateTime.UtcNow;
      Queue<string> pendingAreas = new Queue<string>();
      HashSet<string> visitedAreas = new HashSet<string>(StringComparer.Ordinal);

      pendingAreas.Enqueue("/");

      while (pendingAreas.Count > 0) {
        string area = pendingAreas.Dequeue();

        if (!visitedAreas.Add(area)) {
          continue;
        }

        if (!this.HasCacheEntry("name", area)) {
          return new FetchWorkItem(
            "name",
            area,
            "missing breadth-first structural name"
          );
        }

        if (!this.HasCacheEntry("capabilities", area)) {
          return new FetchWorkItem(
            "capabilities",
            area,
            "missing breadth-first structural capabilities"
          );
        }

        if (!this.HasCacheEntry("children", area)) {
          return new FetchWorkItem(
            "children",
            area,
            "missing breadth-first structural children"
          );
        }

        string[] children;
        if (this.TryReadCachedPayloadOnly("children", area, out children)) {
          foreach (string child in children) {
            pendingAreas.Enqueue(child);
          }
        }
      }

      DevLogger.LogTrace(
        7421903804117L,
        75216,
        "Background knowledge cache structural missing scan completed without candidate. visitedAreaCount="
        + visitedAreas.Count.ToString()
        + ", elapsedMilliseconds="
        + ((long)(DateTime.UtcNow - scanStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      return null;
    }

    /// <summary>
    /// Finds one missing content operation after structural discovery has completed.
    ///
    /// Areas are processed deepest-first. This makes leaf content available before parent
    /// content and avoids using aggregated-content as an implicit recursive discovery
    /// mechanism.
    /// </summary>
    private FetchWorkItem FindNextMissingContentDeepestFirst() {
      DateTime scanStartedUtc = DateTime.UtcNow;
      string[] knownAreas = this.GetKnownAreasOrderedByDescendingDepth();
      int contentCapableAreaCount = 0;

      foreach (string area in knownAreas) {
        CachedCapabilities capabilities;

        if (!this.TryReadCachedPayloadOnly(
              "capabilities",
              area,
              out capabilities
            )) {
          continue;
        }

        if (capabilities.ContentLevel == ContentLevel.BeyondContent) {
          continue;
        }

        contentCapableAreaCount++;

        if (capabilities.SupportsResources &&
            !this.HasCacheEntry("resources", area)) {
          return new FetchWorkItem(
            "resources",
            area,
            "missing deepest-first resource metadata"
          );
        }

        if (!this.HasCacheEntry("has-direct-content", area)) {
          return new FetchWorkItem(
            "has-direct-content",
            area,
            "missing deepest-first direct-content state"
          );
        }

        if (!this.HasCacheEntry("direct-content", area)) {
          return new FetchWorkItem(
            "direct-content",
            area,
            "missing deepest-first direct content"
          );
        }
      }

      DevLogger.LogTrace(
        7421903804118L,
        75217,
        "Background knowledge cache content missing scan completed without candidate. knownAreaCount="
        + knownAreas.Length.ToString()
        + ", contentCapableAreaCount="
        + contentCapableAreaCount.ToString()
        + ", elapsedMilliseconds="
        + ((long)(DateTime.UtcNow - scanStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      return null;
    }

    /// <summary>
    /// Returns every locally known area ordered from deepest to shallowest.
    ///
    /// Only cached child enumerations are inspected. This method never calls the wrapped
    /// source and therefore cannot accidentally turn scheduling into a recursive fetch.
    /// </summary>
    private string[] GetKnownAreasOrderedByDescendingDepth() {
      Queue<string> pendingAreas = new Queue<string>();
      HashSet<string> visitedAreas = new HashSet<string>(StringComparer.Ordinal);
      List<string> areas = new List<string>();

      pendingAreas.Enqueue("/");

      while (pendingAreas.Count > 0) {
        string area = pendingAreas.Dequeue();

        if (!visitedAreas.Add(area)) {
          continue;
        }

        areas.Add(area);

        string[] children;
        if (this.TryReadCachedPayloadOnly("children", area, out children)) {
          foreach (string child in children) {
            pendingAreas.Enqueue(child);
          }
        }
      }

      return areas
        .OrderByDescending((string area) => this.GetAreaDepth(area))
        .ThenBy((string area) => area, StringComparer.Ordinal)
        .ToArray();
    }

    /// <summary>
    /// Returns the logical path depth of one absolute knowledge area.
    /// </summary>
    private int GetAreaDepth(string area) {
      if (string.IsNullOrWhiteSpace(area) ||
          string.Equals(area, "/", StringComparison.Ordinal)) {
        return 0;
      }

      return area.Count((char character) => character == '/');
    }

    /// <summary>
    /// Finds one known resource whose binary content has not yet been cached.
    /// </summary>
    private FetchWorkItem FindMissingResourceContentWorkItem() {
      DateTime scanStartedUtc = DateTime.UtcNow;
      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();
      int resourceMetadataEntryCount = 0;

      foreach (PersistentCacheEntry entry in entries.OrderBy((PersistentCacheEntry item) => item.CreatedUtc)) {
        if (!string.Equals(entry.Operation, "resources", StringComparison.Ordinal)) {
          continue;
        }

        resourceMetadataEntryCount++;

        KnowledgeResourceInfo[] resources;
        try {
          resources = JsonConvert.DeserializeObject<KnowledgeResourceInfo[]>(entry.PayloadJson);
        }
        catch (JsonException ex) {
          DevLogger.LogError(ex);
          continue;
        }

        if (resources == null) {
          continue;
        }

        foreach (KnowledgeResourceInfo resource in resources) {
          if (!this.HasCacheEntry("resource-content", resource.ResourceId)) {
            return new FetchWorkItem("resource-content", resource.ResourceId, "missing binary content for known resource");
          }
        }
      }

      DevLogger.LogTrace(
        7421903804119L,
        75218,
        "Background knowledge cache resource binary missing scan completed without candidate. persistentEntryCount="
        + entries.Length.ToString()
        + ", resourceMetadataEntryCount="
        + resourceMetadataEntryCount.ToString()
        + ", elapsedMilliseconds="
        + ((long)(DateTime.UtcNow - scanStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      return null;
    }

    /// <summary>
    /// Finds the oldest expired structural cache entry.
    ///
    /// Structural refresh remains ahead of autonomous content refresh so externally changed
    /// trees are rediscovered before old content is refreshed.
    /// </summary>
    private FetchWorkItem FindOldestExpiredStructuralWorkItem() {
      DateTime scanStartedUtc = DateTime.UtcNow;
      DateTime utcNow = DateTime.UtcNow;
      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();
      PersistentCacheEntry oldest = null;
      int structuralEntryCount = 0;
      int expiredStructuralEntryCount = 0;

      foreach (PersistentCacheEntry entry in entries) {
        if (!this.IsStructuralOperation(entry.Operation)) {
          continue;
        }

        structuralEntryCount++;

        if (this.IsFresh(entry.CreatedUtc, utcNow)) {
          continue;
        }

        expiredStructuralEntryCount++;

        if (oldest == null ||
            entry.CreatedUtc < oldest.CreatedUtc) {
          oldest = entry;
        }
      }

      DevLogger.LogTrace(
        7421903804120L,
        75219,
        "Background knowledge cache expired structural scan completed. persistentEntryCount="
        + entries.Length.ToString()
        + ", structuralEntryCount="
        + structuralEntryCount.ToString()
        + ", expiredStructuralEntryCount="
        + expiredStructuralEntryCount.ToString()
        + ", selected="
        + (oldest != null).ToString()
        + ", elapsedMilliseconds="
        + ((long)(DateTime.UtcNow - scanStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      if (oldest == null) {
        return null;
      }

      return new FetchWorkItem(
        oldest.Operation,
        oldest.Argument,
        "oldest expired structural cache entry"
      );
    }

    /// <summary>
    /// Finds one expired autonomous content entry, preferring deeper areas over shallower
    /// areas.
    ///
    /// Aggregated content is excluded deliberately. A stale aggregated-content entry is
    /// refreshed only after a consumer requests it and thereby places it in the priority
    /// queue.
    /// </summary>
    private FetchWorkItem FindOldestExpiredContentWorkItem() {
      DateTime scanStartedUtc = DateTime.UtcNow;
      DateTime utcNow = DateTime.UtcNow;
      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();

      PersistentCacheEntry[] candidates = entries
        .Where(
          (PersistentCacheEntry entry) =>
            this.IsAutonomousContentOperation(entry.Operation) &&
            !this.IsFresh(entry.CreatedUtc, utcNow)
        )
        .OrderByDescending(
          (PersistentCacheEntry entry) => this.GetAreaDepth(entry.Argument)
        )
        .ThenBy(
          (PersistentCacheEntry entry) => entry.CreatedUtc
        )
        .ToArray();

      DevLogger.LogTrace(
        7421903804121L,
        75220,
        "Background knowledge cache expired content scan completed. persistentEntryCount="
        + entries.Length.ToString()
        + ", expiredAutonomousContentEntryCount="
        + candidates.Length.ToString()
        + ", elapsedMilliseconds="
        + ((long)(DateTime.UtcNow - scanStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      if (candidates.Length == 0) {
        return null;
      }

      PersistentCacheEntry selected = candidates[0];

      return new FetchWorkItem(
        selected.Operation,
        selected.Argument,
        "expired deepest-first content cache entry"
      );
    }

    /// <summary>
    /// Determines whether an operation belongs to autonomous structural discovery.
    /// </summary>
    private bool IsStructuralOperation(string operation) {
      return
        string.Equals(operation, "name", StringComparison.Ordinal) ||
        string.Equals(operation, "capabilities", StringComparison.Ordinal) ||
        string.Equals(operation, "children", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether an operation may be refreshed autonomously during the content
    /// phase.
    ///
    /// Aggregated content is intentionally absent because it can represent an arbitrarily
    /// large explicit subtree operation.
    /// </summary>
    private bool IsAutonomousContentOperation(string operation) {
      return
        string.Equals(operation, "has-direct-content", StringComparison.Ordinal) ||
        string.Equals(operation, "direct-content", StringComparison.Ordinal) ||
        string.Equals(operation, "resources", StringComparison.Ordinal) ||
        string.Equals(operation, "resource-content", StringComparison.Ordinal);
    }

    /// <summary>
    /// Executes one logical source operation.
    /// </summary>
    private object FetchFromSource(FetchWorkItem workItem) {
      DevLogger.LogTrace(0, 99999, "Background knowledge cache fetch started: operation='" + workItem.Operation + "', argument='" + workItem.Argument + "', reason='" + workItem.Reason + "'.");

      if (string.Equals(workItem.Operation, "children", StringComparison.Ordinal)) {
        return _WrappedSource.GetAreas(false, workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "name", StringComparison.Ordinal)) {
        return _WrappedSource.GetAreaName(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "capabilities", StringComparison.Ordinal)) {
        return this.ReadCapabilitiesFromSource(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "has-direct-content", StringComparison.Ordinal)) {
        return _WrappedSource.HasDirectContent(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "direct-content", StringComparison.Ordinal)) {
        return _WrappedSource.GetDirectContent(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "aggregated-content", StringComparison.Ordinal)) {
        return _WrappedSource.GetAggregatedContent(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "resources", StringComparison.Ordinal)) {
        return _WrappedSource.GetResources(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "resource-content", StringComparison.Ordinal)) {
        return _WrappedSource.GetResourceContent(workItem.Argument);
      }
      if (string.Equals(workItem.Operation, "search", StringComparison.Ordinal)) {
        int separator = workItem.Argument.IndexOf('\n');
        if (separator < 0) {
          throw new InvalidOperationException("Invalid cached search argument.");
        }
        string startArea = workItem.Argument.Substring(0, separator);
        string keyword = workItem.Argument.Substring(separator + 1);
        return _WrappedSource.GetAreasByKeyword(keyword, startArea);
      }

      throw new InvalidOperationException("Unsupported background cache operation '" + workItem.Operation + "'.");
    }

    /// <summary>
    /// Reads capabilities from the source into a serializable cache object.
    /// </summary>
    private CachedCapabilities ReadCapabilitiesFromSource(string area) {
      CachedCapabilities result = new CachedCapabilities();
      _WrappedSource.GetAreaCapabilities(
        area,
        out ContentLevel contentLevel,
        out bool supportsSubAreas,
        out bool canBeRenamed,
        out bool canBeDeleted,
        out bool canAddSubAreas,
        out bool canAppendContent,
        out bool canTruncate,
        out bool supportsResources
      );

      result.ContentLevel = contentLevel;
      result.SupportsSubAreas = supportsSubAreas;
      result.CanBeRenamed = canBeRenamed;
      result.CanBeDeleted = canBeDeleted;
      result.CanAddSubAreas = canAddSubAreas;
      result.CanAppendContent = canAppendContent;
      result.CanTruncate = canTruncate;
      result.SupportsResources = supportsResources;
      return result;
    }

    /// <summary>
    /// Returns whether one operation/argument cache entry exists.
    /// </summary>
    private bool HasCacheEntry(string operation, string argument) {
      PersistentCacheEntry entry;
      return this.TryReadCacheEntry(operation, argument, out entry);
    }

    /// <summary>
    /// Reads one typed value from memory or persistent storage without queue side effects.
    /// </summary>
    private bool TryReadCacheValue<T>(string operation, string argument, out PersistentCacheEntry entry, out T value) {
      this.EnsureMemoryCacheGenerationIsCurrent();
      string cacheKey = this.CreateCacheKey(operation, argument);
      MemoryCacheEntry memoryEntry;

      if (_MemoryCache.TryGetValue(cacheKey, out memoryEntry)) {
        entry = new PersistentCacheEntry();
        entry.Operation = operation;
        entry.Argument = argument;
        entry.CreatedUtc = memoryEntry.CreatedUtc;
        entry.PayloadJson = string.Empty;
        value = (T)memoryEntry.Value;
        return true;
      }

      if (!this.TryReadPersistentValue(operation, argument, out entry, out value)) {
        value = default(T);
        return false;
      }

      this.StoreMemoryValue(cacheKey, entry.CreatedUtc, value);
      return true;
    }

    /// <summary>
    /// Reads one typed payload without queueing or freshness checks.
    /// </summary>
    private bool TryReadCachedPayloadOnly<T>(string operation, string argument, out T value) {
      PersistentCacheEntry entry;
      return this.TryReadCacheValue(operation, argument, out entry, out value);
    }

    /// <summary>
    /// Reads one cache envelope without deserializing its payload.
    /// </summary>
    private bool TryReadCacheEntry(string operation, string argument, out PersistentCacheEntry entry) {
      this.EnsureMemoryCacheGenerationIsCurrent();
      string cacheKey = this.CreateCacheKey(operation, argument);
      MemoryCacheEntry memoryEntry;
      if (_MemoryCache.TryGetValue(cacheKey, out memoryEntry)) {
        entry = new PersistentCacheEntry();
        entry.Operation = operation;
        entry.Argument = argument;
        entry.CreatedUtc = memoryEntry.CreatedUtc;
        return true;
      }

      string file = this.GetCacheFilePath(cacheKey);
      if (!this.TryReadPersistentEntry(file, out entry)) {
        return false;
      }

      if (!string.Equals(entry.Operation, operation, StringComparison.Ordinal) ||
          !string.Equals(entry.Argument, argument, StringComparison.Ordinal)) {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Writes one cache value atomically to memory and persistent storage.
    /// </summary>
    private void WriteCacheValue(string operation, string argument, object value) {
      PersistentCacheEntry entry = new PersistentCacheEntry();
      entry.FormatVersion = _CacheFormatVersion;
      entry.Operation = operation;
      entry.Argument = argument;
      entry.CreatedUtc = DateTime.UtcNow;
      entry.PayloadJson = JsonConvert.SerializeObject(value);

      string cacheKey = this.CreateCacheKey(operation, argument);
      string file = this.GetCacheFilePath(cacheKey);
      string temporaryFile = file + "." + Guid.NewGuid().ToString("N") + ".tmp";

      bool persisted = false;

      try {
        File.WriteAllText(temporaryFile, JsonConvert.SerializeObject(entry, Formatting.None), Encoding.UTF8);
        File.Move(temporaryFile, file, true);
        persisted = true;
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        this.TryDeleteFile(temporaryFile);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        this.TryDeleteFile(temporaryFile);
      }

      if (persisted) {
        this.AdvanceCacheGeneration();
      }

      this.StoreMemoryValue(cacheKey, entry.CreatedUtc, value);

      if (string.Equals(operation, "children", StringComparison.Ordinal)) {
        string[] writtenChildren = value as string[];
        if (writtenChildren == null) {
          writtenChildren = Array.Empty<string>();
        }

        DevLogger.LogTrace(0, 99999, "STRUCTURE-DIAG cache wrote children: parent='" + argument + "', createdUtc='" + entry.CreatedUtc.ToString("O") + "', count=" + writtenChildren.Length.ToString() + ", children=" + this.FormatDiagnosticAreas(writtenChildren) + ".");
      }
    }

    /// <summary>
    /// Formats logical areas for compact structure diagnostics.
    /// </summary>
    private string FormatDiagnosticAreas(string[] areas) {
      if (areas == null || areas.Length == 0) {
        return "[]";
      }

      return "[" + string.Join(", ", areas.Select((string area) => "'" + area + "'").ToArray()) + "]";
    }

    /// <summary>
    /// Reads and deserializes one persistent cache value.
    /// </summary>
    private bool TryReadPersistentValue<T>(string operation, string argument, out PersistentCacheEntry entry, out T value) {
      value = default(T);
      string cacheKey = this.CreateCacheKey(operation, argument);
      string file = this.GetCacheFilePath(cacheKey);

      if (!this.TryReadPersistentEntry(file, out entry)) {
        return false;
      }

      if (!string.Equals(entry.Operation, operation, StringComparison.Ordinal) ||
          !string.Equals(entry.Argument, argument, StringComparison.Ordinal)) {
        return false;
      }

      try {
        value = JsonConvert.DeserializeObject<T>(entry.PayloadJson);
        return true;
      }
      catch (JsonException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Reads one persistent cache envelope.
    /// </summary>
    private bool TryReadPersistentEntry(string file, out PersistentCacheEntry entry) {
      entry = null;
      if (!File.Exists(file)) {
        return false;
      }

      try {
        string json = File.ReadAllText(file, Encoding.UTF8);
        PersistentCacheEntry loaded = JsonConvert.DeserializeObject<PersistentCacheEntry>(json);
        if (loaded == null || loaded.FormatVersion != _CacheFormatVersion || string.IsNullOrWhiteSpace(loaded.Operation)) {
          return false;
        }
        entry = loaded;
        return true;
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (JsonException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Reads all valid persistent cache envelopes.
    /// </summary>
    private PersistentCacheEntry[] ReadAllPersistentEntries() {
      DateTime scanStartedUtc = DateTime.UtcNow;
      List<PersistentCacheEntry> result = new List<PersistentCacheEntry>();
      string[] files = Directory.GetFiles(
        _CacheDirectory,
        "*" + _CacheEntryExtension,
        SearchOption.TopDirectoryOnly
      );

      foreach (string file in files) {
        PersistentCacheEntry entry;

        if (this.TryReadPersistentEntry(file, out entry)) {
          result.Add(entry);
        }
      }

      DevLogger.LogTrace(
        7421903804122L,
        75221,
        "Background knowledge cache persistent entry scan completed. cacheFileCount="
        + files.Length.ToString()
        + ", validEntryCount="
        + result.Count.ToString()
        + ", elapsedMilliseconds="
        + ((long)(DateTime.UtcNow - scanStartedUtc).TotalMilliseconds).ToString()
        + "."
      );

      return result.ToArray();
    }

    /// <summary>
    /// Processes a freshly fetched direct-child enumeration and reconciles structural
    /// changes with the rest of the local cache.
    ///
    /// Newly discovered children are deliberately not expanded into priority work here.
    /// The autonomous structure phase will discover their name, capabilities and children
    /// breadth-first on subsequent heartbeats. This keeps the priority queue reserved for
    /// real consumer demand and prevents a wide node from generating hundreds of content
    /// work items at once.
    /// </summary>
    private void ProcessChildrenTransition(
      string parentArea,
      string[] previousChildren,
      string[] currentChildren
    ) {
      string[] previous = previousChildren;
      if (previous == null) {
        previous = Array.Empty<string>();
      }

      string[] current = currentChildren;
      if (current == null) {
        current = Array.Empty<string>();
      }

      HashSet<string> previousSet = new HashSet<string>(
        previous,
        StringComparer.Ordinal
      );

      HashSet<string> currentSet = new HashSet<string>(
        current,
        StringComparer.Ordinal
      );

      string[] addedChildren = current
        .Where((string child) => !previousSet.Contains(child))
        .ToArray();

      string[] removedChildren = previous
        .Where((string child) => !currentSet.Contains(child))
        .ToArray();

      foreach (string removedChild in removedChildren) {
        DevLogger.LogTrace(
          0,
          99999,
          "Background knowledge cache detected removed child: parent='"
          + parentArea
          + "', child='"
          + removedChild
          + "', action='remove cached subtree'."
        );

        this.HealOrphanedArea(
          removedChild
        );
      }

      if (addedChildren.Length > 0 || removedChildren.Length > 0) {
        this.EnqueuePriorityWork(
          "capabilities",
          parentArea,
          "parent structure changed and structural capabilities must be refreshed",
          true
        );
      }

      foreach (string addedChild in addedChildren) {
        DevLogger.LogTrace(
          0,
          99999,
          "Background knowledge cache discovered new child: parent='"
          + parentArea
          + "', child='"
          + addedChild
          + "', action='leave discovery to breadth-first structure phase'."
        );
      }

      if (addedChildren.Length > 0 || removedChildren.Length > 0) {
        DevLogger.LogTrace(
          0,
          99999,
          "Background knowledge cache applied children transition: parent='"
          + parentArea
          + "', previousCount="
          + previous.Length.ToString()
          + ", currentCount="
          + current.Length.ToString()
          + ", addedCount="
          + addedChildren.Length.ToString()
          + ", removedCount="
          + removedChildren.Length.ToString()
          + "."
        );
      }
    }

    /// <summary>
    /// Determines whether an exception reports that an area which was previously visible
    /// through an aggregated repository no longer exists.
    ///
    /// The recognition is intentionally narrow so unrelated InvalidOperationException
    /// instances remain visible and cannot silently corrupt the cache.
    /// </summary>
    private bool IsMissingAggregatedKnowledgeAreaException(
      InvalidOperationException exception,
      string area
    ) {
      if (exception == null || string.IsNullOrWhiteSpace(area)) {
        return false;
      }

      string expectedMessage =
        "The aggregated knowledge area does not exist: "
        + area;

      return string.Equals(
        exception.Message,
        expectedMessage,
        StringComparison.Ordinal
      );
    }

    /// <summary>
    /// Removes a knowledge area which is still represented by stale cache entries although
    /// the authoritative aggregated repository no longer exposes it.
    ///
    /// All cached values belonging to the orphaned subtree are removed. Cached child arrays
    /// which still reference the orphan are rewritten immediately so UI navigation stops
    /// exposing the stale item without waiting for a complete repository refresh. Every
    /// affected parent enumeration is then queued for an authoritative background refresh.
    /// </summary>
    private void HealOrphanedArea(string area) {
      if (string.IsNullOrWhiteSpace(area) || string.Equals(area, "/", StringComparison.Ordinal)) {
        return;
      }

      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();
      HashSet<string> parentsToRefresh = new HashSet<string>(StringComparer.Ordinal);
      int removedEntryCount = 0;
      int rewrittenChildrenEntryCount = 0;

      foreach (PersistentCacheEntry entry in entries) {
        if (string.Equals(entry.Operation, "children", StringComparison.Ordinal)) {
          string[] children;

          try {
            children = JsonConvert.DeserializeObject<string[]>(entry.PayloadJson);
          }
          catch (JsonException ex) {
            DevLogger.LogError(ex);
            continue;
          }

          if (children == null) {
            continue;
          }

          string[] filteredChildren = children
            .Where((string child) => !this.IsAreaOrDescendant(child, area))
            .ToArray();

          if (filteredChildren.Length == children.Length) {
            continue;
          }

          this.WriteCacheValue(
            "children",
            entry.Argument,
            filteredChildren
          );

          parentsToRefresh.Add(entry.Argument);
          rewrittenChildrenEntryCount++;

          DevLogger.LogTrace(
            0,
            99999,
            "Background knowledge cache removed orphaned child reference: area='"
            + area
            + "', parent='"
            + entry.Argument
            + "', reason='aggregated repository no longer exposes the cached area'."
          );
        }
      }

      foreach (PersistentCacheEntry entry in entries) {
        if (!this.CacheEntryBelongsToArea(entry, area)) {
          continue;
        }

        if (string.Equals(entry.Operation, "children", StringComparison.Ordinal) &&
            parentsToRefresh.Contains(entry.Argument)) {
          continue;
        }

        this.RemoveCacheEntry(
          entry.Operation,
          entry.Argument
        );

        removedEntryCount++;
      }

      this.RemoveQueuedWorkForArea(area);

      foreach (string parent in parentsToRefresh) {
        this.EnqueuePriorityWork(
          "children",
          parent,
          "orphaned cached child was removed"
        );
      }

      DevLogger.LogTrace(
        0,
        99999,
        "Background knowledge cache removed orphaned area: area='"
        + area
        + "', removedEntryCount="
        + removedEntryCount.ToString()
        + ", rewrittenChildrenEntryCount="
        + rewrittenChildrenEntryCount.ToString()
        + ", parentRefreshCount="
        + parentsToRefresh.Count.ToString()
        + ", reason='aggregated repository no longer exposes the cached area'."
      );
    }

    /// <summary>
    /// Returns whether a logical area equals the orphaned area or belongs to its subtree.
    /// </summary>
    private bool IsAreaOrDescendant(string candidateArea, string area) {
      if (string.IsNullOrWhiteSpace(candidateArea)) {
        return false;
      }

      if (string.Equals(candidateArea, area, StringComparison.Ordinal)) {
        return true;
      }

      string descendantPrefix = area;

      if (!descendantPrefix.EndsWith("/", StringComparison.Ordinal)) {
        descendantPrefix += "/";
      }

      return candidateArea.StartsWith(
        descendantPrefix,
        StringComparison.Ordinal
      );
    }

    /// <summary>
    /// Determines whether a cache entry is scoped to an orphaned area or one of its
    /// descendants. Resource-content entries are intentionally excluded because their opaque
    /// identifiers do not encode area ownership and may still be referenced elsewhere.
    /// </summary>
    private bool CacheEntryBelongsToArea(PersistentCacheEntry entry, string area) {
      if (entry == null) {
        return false;
      }

      if (string.Equals(entry.Operation, "resource-content", StringComparison.Ordinal)) {
        return false;
      }

      if (string.Equals(entry.Operation, "search", StringComparison.Ordinal)) {
        return false;
      }

      return this.IsAreaOrDescendant(
        entry.Argument,
        area
      );
    }

    /// <summary>
    /// Removes one operation/argument pair from both the process-local and persistent cache.
    /// </summary>
    private void RemoveCacheEntry(string operation, string argument) {
      string cacheKey = this.CreateCacheKey(operation, argument);
      _MemoryCache.Remove(cacheKey);

      string file = this.GetCacheFilePath(cacheKey);
      this.TryDeleteFile(file);
      this.AdvanceCacheGeneration();
    }

    /// <summary>
    /// Removes queued background work for an orphaned area and its descendants.
    /// </summary>
    private void RemoveQueuedWorkForArea(string area) {
      if (_PriorityQueue.Count == 0) {
        return;
      }

      Queue<FetchWorkItem> retainedItems = new Queue<FetchWorkItem>();
      _QueuedWorkKeys.Clear();

      while (_PriorityQueue.Count > 0) {
        FetchWorkItem item = _PriorityQueue.Dequeue();

        if (this.IsAreaScopedOperation(item.Operation) &&
            this.IsAreaOrDescendant(item.Argument, area)) {
          continue;
        }

        retainedItems.Enqueue(item);
        _QueuedWorkKeys.Add(item.Key);
      }

      while (retainedItems.Count > 0) {
        _PriorityQueue.Enqueue(
          retainedItems.Dequeue()
        );
      }
    }

    /// <summary>
    /// Returns whether a cached operation uses its argument as a logical knowledge area.
    /// </summary>
    private bool IsAreaScopedOperation(string operation) {
      return
        string.Equals(operation, "children", StringComparison.Ordinal) ||
        string.Equals(operation, "name", StringComparison.Ordinal) ||
        string.Equals(operation, "capabilities", StringComparison.Ordinal) ||
        string.Equals(operation, "has-direct-content", StringComparison.Ordinal) ||
        string.Equals(operation, "direct-content", StringComparison.Ordinal) ||
        string.Equals(operation, "aggregated-content", StringComparison.Ordinal) ||
        string.Equals(operation, "resources", StringComparison.Ordinal);
    }

    /// <summary>
    /// Invalidates the complete local cache after an authoritative mutation.
    /// </summary>
    private void InvalidateAfterMutation() {
      lock (_SyncRoot) {
        _MemoryCache.Clear();
        _PriorityQueue.Clear();
        _QueuedWorkKeys.Clear();

        string[] files = Directory.GetFiles(_CacheDirectory, "*" + _CacheEntryExtension, SearchOption.TopDirectoryOnly);
        foreach (string file in files) {
          this.TryDeleteFile(file);
        }

        this.AdvanceCacheGeneration();
        this.EnqueuePriorityWork("children", "/", "authoritative mutation invalidated the cache");
      }
    }

    /// <summary>
    /// Ensures that the process-local memory cache belongs to the current persistent cache
    /// generation. Another wrapper instance can advance the generation after writing cache
    /// entries, in which case this instance discards only its process-local values and starts
    /// reading the shared persistent cache again.
    /// </summary>
    private void EnsureMemoryCacheGenerationIsCurrent() {
      string currentGeneration = this.ReadCacheGeneration();

      if (string.IsNullOrEmpty(currentGeneration)) {
        currentGeneration = this.GetOrCreateCacheGeneration();
      }

      if (string.Equals(currentGeneration, _KnownCacheGeneration, StringComparison.Ordinal)) {
        return;
      }

      int discardedEntryCount = _MemoryCache.Count;
      _MemoryCache.Clear();
      _KnownCacheGeneration = currentGeneration;

      DevLogger.LogTrace(
        0,
        99999,
        "Background knowledge cache detected a persistent cache generation change. "
        + "Discarded process-local cache entries=" + discardedEntryCount.ToString()
        + ", generation='" + currentGeneration + "'."
      );
    }

    /// <summary>
    /// Reads the current persistent cache generation token.
    /// </summary>
    private string ReadCacheGeneration() {
      try {
        if (!File.Exists(_CacheGenerationFile)) {
          return string.Empty;
        }

        return File.ReadAllText(_CacheGenerationFile, Encoding.UTF8).Trim();
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        return string.Empty;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        return string.Empty;
      }
    }

    /// <summary>
    /// Returns the existing persistent cache generation or creates the initial generation
    /// token when this cache directory has not been used by a generation-aware wrapper yet.
    /// </summary>
    private string GetOrCreateCacheGeneration() {
      string existingGeneration = this.ReadCacheGeneration();
      if (!string.IsNullOrEmpty(existingGeneration)) {
        return existingGeneration;
      }

      string newGeneration = Guid.NewGuid().ToString("N");

      try {
        using (FileStream stream = new FileStream(
          _CacheGenerationFile,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.Read
        )) {
          byte[] bytes = Encoding.UTF8.GetBytes(newGeneration);
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush(true);
        }

        return newGeneration;
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);

        string concurrentGeneration = this.ReadCacheGeneration();
        if (!string.IsNullOrEmpty(concurrentGeneration)) {
          return concurrentGeneration;
        }

        return newGeneration;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        return newGeneration;
      }
    }

    /// <summary>
    /// Advances the shared persistent cache generation after this wrapper has changed the
    /// persistent cache. The generation file is replaced atomically and remains constant in
    /// size, so cache coherence does not create a history or grow the cache.
    /// </summary>
    private void AdvanceCacheGeneration() {
      string newGeneration = Guid.NewGuid().ToString("N");
      string temporaryFile = _CacheGenerationFile + "." + Guid.NewGuid().ToString("N") + ".tmp";

      try {
        File.WriteAllText(temporaryFile, newGeneration, Encoding.UTF8);
        File.Move(temporaryFile, _CacheGenerationFile, true);
        _KnownCacheGeneration = newGeneration;

        DevLogger.LogTrace(
          0,
          99999,
          "Background knowledge cache advanced persistent cache generation to '"
          + newGeneration
          + "'."
        );
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        this.TryDeleteFile(temporaryFile);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        this.TryDeleteFile(temporaryFile);
      }
    }

    /// <summary>
    /// Stores one typed value in the process-local cache.
    /// </summary>
    private void StoreMemoryValue(string cacheKey, DateTime createdUtc, object value) {
      MemoryCacheEntry entry = new MemoryCacheEntry();
      entry.CreatedUtc = createdUtc;
      entry.Value = value;
      _MemoryCache[cacheKey] = entry;
    }

    /// <summary>
    /// Returns whether one cache entry is still inside its configured lifetime.
    /// </summary>
    private bool IsFresh(DateTime createdUtc, DateTime utcNow) {
      if (_Lifetime <= TimeSpan.Zero) {
        return false;
      }
      return utcNow - createdUtc < _Lifetime;
    }

    /// <summary>
    /// Creates a deterministic operation cache key compatible with the normal cache wrapper.
    /// </summary>
    private string CreateCacheKey(string operation, string argument) {
      string input = operation + "\n" + argument;
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
      return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the persistent cache file for one operation key.
    /// </summary>
    private string GetCacheFilePath(string cacheKey) {
      return Path.Combine(_CacheDirectory, cacheKey + _CacheEntryExtension);
    }

    /// <summary>
    /// Resolves the persistent cache root.
    /// </summary>
    private string ResolveCacheFileSystemPath(string cacheFileSystemPath) {
      if (!string.IsNullOrWhiteSpace(cacheFileSystemPath)) {
        string explicitPath = Path.GetFullPath(cacheFileSystemPath);
        Directory.CreateDirectory(explicitPath);
        return explicitPath;
      }

      string temporaryPath = Path.Combine(
        Path.GetTempPath(),
        "knowledge-repository-cache",
        Guid.NewGuid().ToString("N")
      );
      Directory.CreateDirectory(temporaryPath);
      return temporaryPath;
    }

    /// <summary>
    /// Deletes one cache file using best-effort cleanup semantics.
    /// </summary>
    private void TryDeleteFile(string file) {
      try {
        if (File.Exists(file)) {
          File.Delete(file);
        }
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
      }
    }

    /// <summary>
    /// Represents one source fetch operation.
    /// </summary>
    private sealed class FetchWorkItem {

      private readonly string _Operation;
      private readonly string _Argument;
      private readonly string _Reason;
      private readonly bool _ForceRefresh;

      /// <summary>
      /// Creates one immutable work item.
      /// </summary>
      public FetchWorkItem(string operation, string argument) : this(operation, argument, "unspecified", false) {
      }

      /// <summary>
      /// Creates one immutable work item with diagnostic scheduling context.
      /// </summary>
      public FetchWorkItem(string operation, string argument, string reason) : this(operation, argument, reason, false) {
      }

      /// <summary>
      /// Creates one immutable work item with diagnostic scheduling context and an optional
      /// forced-refresh flag.
      /// </summary>
      public FetchWorkItem(string operation, string argument, string reason, bool forceRefresh) {
        _Operation = operation;
        _Argument = argument;
        _Reason = reason;
        _ForceRefresh = forceRefresh;
      }

      /// <summary>
      /// Gets the cache operation.
      /// </summary>
      public string Operation {
        get {
          return _Operation;
        }
      }

      /// <summary>
      /// Gets the operation argument.
      /// </summary>
      public string Argument {
        get {
          return _Argument;
        }
      }


      /// <summary>
      /// Gets the diagnostic reason why this work item was selected or queued.
      /// </summary>
      public string Reason {
        get {
          return _Reason;
        }
      }

      /// <summary>
      /// Gets whether this work item must bypass normal cache-freshness suppression.
      /// </summary>
      public bool ForceRefresh {
        get {
          return _ForceRefresh;
        }
      }

      /// <summary>
      /// Gets the deduplication key.
      /// </summary>
      public string Key {
        get {
          return _Operation + "\n" + _Argument;
        }
      }
    }

    /// <summary>
    /// Represents one process-local typed cache value.
    /// </summary>
    private sealed class MemoryCacheEntry {

      private DateTime _CreatedUtc;
      private object _Value;

      /// <summary>
      /// Gets or sets the source timestamp.
      /// </summary>
      public DateTime CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets the cached value.
      /// </summary>
      public object Value {
        get {
          return _Value;
        }
        set {
          _Value = value;
        }
      }
    }

    /// <summary>
    /// Serializable cache envelope compatible with cache format version 2.
    /// </summary>
    private sealed class PersistentCacheEntry {

      private int _FormatVersion;
      private string _Operation;
      private string _Argument;
      private DateTime _CreatedUtc;
      private string _PayloadJson;

      /// <summary>
      /// Creates an empty cache envelope.
      /// </summary>
      public PersistentCacheEntry() {
        _Operation = string.Empty;
        _Argument = string.Empty;
        _PayloadJson = string.Empty;
      }

      public int FormatVersion {
        get {
          return _FormatVersion;
        }
        set {
          _FormatVersion = value;
        }
      }

      public string Operation {
        get {
          return _Operation;
        }
        set {
          _Operation = value;
        }
      }

      public string Argument {
        get {
          return _Argument;
        }
        set {
          _Argument = value;
        }
      }

      public DateTime CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc = value;
        }
      }

      public string PayloadJson {
        get {
          return _PayloadJson;
        }
        set {
          _PayloadJson = value;
        }
      }
    }

    /// <summary>
    /// Serializable area capability set.
    /// </summary>
    private sealed class CachedCapabilities {

      private ContentLevel _ContentLevel;
      private bool _SupportsSubAreas;
      private bool _CanBeRenamed;
      private bool _CanBeDeleted;
      private bool _CanAddSubAreas;
      private bool _CanAppendContent;
      private bool _CanTruncate;
      private bool _SupportsResources;

      public ContentLevel ContentLevel {
        get {
          return _ContentLevel;
        }
        set {
          _ContentLevel = value;
        }
      }

      public bool SupportsSubAreas {
        get {
          return _SupportsSubAreas;
        }
        set {
          _SupportsSubAreas = value;
        }
      }

      public bool CanBeRenamed {
        get {
          return _CanBeRenamed;
        }
        set {
          _CanBeRenamed = value;
        }
      }

      public bool CanBeDeleted {
        get {
          return _CanBeDeleted;
        }
        set {
          _CanBeDeleted = value;
        }
      }

      public bool CanAddSubAreas {
        get {
          return _CanAddSubAreas;
        }
        set {
          _CanAddSubAreas = value;
        }
      }

      public bool CanAppendContent {
        get {
          return _CanAppendContent;
        }
        set {
          _CanAppendContent = value;
        }
      }

      public bool CanTruncate {
        get {
          return _CanTruncate;
        }
        set {
          _CanTruncate = value;
        }
      }

      public bool SupportsResources {
        get {
          return _SupportsResources;
        }
        set {
          _SupportsResources = value;
        }
      }
    }
  }
}