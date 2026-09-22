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
using System.Threading;

namespace KnowledgeManagement.SmartStandards.Wrappers {

  /// <summary>
  /// Provides a persistent, strictly cache-only read view over an arbitrary knowledge
  /// repository while incrementally filling and refreshing that cache in the background.
  ///
  /// Normal repository reads never access the wrapped source. Missing or expired values are
  /// queued with high priority and the best locally available value is returned immediately.
  /// <see cref="PrefetchNext"/> performs exactly one logical source fetch per successful call.
  /// When no explicitly requested work is pending, missing values are discovered breadth-first
  /// from the repository root before expired values are refreshed oldest-first.
  /// </summary>
  public sealed class BackgroundFetchingKnowledgeRepositoryCacheWrapper : IKnowledgeRepository {

    private const int _CacheFormatVersion = 2;
    private const string _CacheDirectoryName = ".knowledge-cache";
    private const string _CacheEntryExtension = ".cache";
    private const int _InitialTooManyRequestsDelayMilliseconds = 60000;
    private const int _MaximumTooManyRequestsDelayMilliseconds = 900000;

    private readonly object _SyncRoot;
    private readonly IKnowledgeRepository _WrappedSource;
    private readonly TimeSpan _Lifetime;
    private readonly string _CacheFileSystemPath;
    private readonly string _CacheDirectory;
    private readonly Dictionary<string, MemoryCacheEntry> _MemoryCache;
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
      int lifetimeMin = 240,
      string cacheFileSystemPath = null
    ) {
      if (wrappedSource == null) {
        throw new ArgumentNullException(nameof(wrappedSource));
      }

      if (lifetimeMin < 0) {
        throw new ArgumentOutOfRangeException(nameof(lifetimeMin), "The cache lifetime must not be negative.");
      }

      _SyncRoot = new object();
      _WrappedSource = wrappedSource;
      _Lifetime = TimeSpan.FromMinutes(lifetimeMin);
      _CacheFileSystemPath = this.ResolveCacheFileSystemPath(cacheFileSystemPath);
      _CacheDirectory = Path.Combine(_CacheFileSystemPath, _CacheDirectoryName);
      _MemoryCache = new Dictionary<string, MemoryCacheEntry>(StringComparer.Ordinal);
      _PriorityQueue = new Queue<FetchWorkItem>();
      _QueuedWorkKeys = new HashSet<string>(StringComparer.Ordinal);

      Directory.CreateDirectory(_CacheDirectory);
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
          return this.ReadCachedOnly("children", startArea, Array.Empty<string>());
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

        return result.ToArray();
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
    /// Returns cached resource metadata without source I/O.
    /// </summary>
    public KnowledgeResourceInfo[] GetResources(string area) {
      lock (_SyncRoot) {
        return this.ReadCachedOnly("resources", area, Array.Empty<KnowledgeResourceInfo>());
      }
    }

    /// <summary>
    /// Returns cached resource bytes without source I/O.
    /// </summary>
    public byte[] GetResourceContent(string resourceId) {
      lock (_SyncRoot) {
        return this.ReadCachedOnly("resource-content", resourceId, Array.Empty<byte>());
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
        return this.ReadCachedOnly("direct-content", area, string.Empty);
      }
    }

    /// <summary>
    /// Returns cached aggregated content without source I/O.
    /// </summary>
    public string GetAggregatedContent(string area) {
      lock (_SyncRoot) {
        return this.ReadCachedOnly("aggregated-content", area, string.Empty);
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
    /// Performs exactly one successful logical background fetch.
    ///
    /// Explicitly requested cache misses and stale reads are processed first. Otherwise the
    /// known repository is scanned breadth-first for missing values and then oldest-first for
    /// expired values. HTTP 429 keeps this method blocked on the same work item using bounded
    /// exponential backoff. The supplied cancellation token interrupts that wait immediately.
    /// </summary>
    /// <returns>
    /// True when one source value was fetched and cached; false when no work is currently due.
    /// </returns>
    public bool PrefetchNext(CancellationToken cancellationToken) {
      FetchWorkItem workItem;

      lock (_SyncRoot) {
        if (!this.TryDequeuePriorityWork(out workItem)) {
          workItem = this.FindNextBackgroundWorkItem();
        }
      }

      if (workItem == null) {
        return false;
      }

      int tooManyRequestsDelayMilliseconds = _InitialTooManyRequestsDelayMilliseconds;

      while (true) {
        cancellationToken.ThrowIfCancellationRequested();

        try {
          object value = this.FetchFromSource(workItem);

          lock (_SyncRoot) {
            this.WriteCacheValue(workItem.Operation, workItem.Argument, value);
          }

          return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) {
          DevLogger.LogTrace(
            0,
            99999,
            "Background knowledge cache received HTTP 429 Too Many Requests for operation '"
            + workItem.Operation
            + "' and argument '"
            + workItem.Argument
            + "'. Waiting "
            + tooManyRequestsDelayMilliseconds
            + " ms before retrying the same fetch."
          );

          if (cancellationToken.WaitHandle.WaitOne(tooManyRequestsDelayMilliseconds)) {
            cancellationToken.ThrowIfCancellationRequested();
          }

          long nextDelay = (long)tooManyRequestsDelayMilliseconds * 2L;
          tooManyRequestsDelayMilliseconds = (int)Math.Min(nextDelay, _MaximumTooManyRequestsDelayMilliseconds);
        }
      }
    }

    /// <summary>
    /// Reads one local cache value, queues missing or expired data and never calls the source.
    /// </summary>
    private T ReadCachedOnly<T>(string operation, string argument, T fallbackValue) {
      PersistentCacheEntry entry;
      T value;

      if (this.TryReadCacheValue(operation, argument, out entry, out value)) {
        if (!this.IsFresh(entry.CreatedUtc, DateTime.UtcNow)) {
          this.EnqueuePriorityWork(operation, argument);
        }
        return value;
      }

      this.EnqueuePriorityWork(operation, argument);
      return fallbackValue;
    }

    /// <summary>
    /// Adds one source operation to the high-priority queue without creating duplicates.
    /// </summary>
    private void EnqueuePriorityWork(string operation, string argument) {
      FetchWorkItem workItem = new FetchWorkItem(operation, argument);
      if (_QueuedWorkKeys.Add(workItem.Key)) {
        _PriorityQueue.Enqueue(workItem);
      }
    }

    /// <summary>
    /// Removes the next still-relevant high-priority work item.
    /// </summary>
    private bool TryDequeuePriorityWork(out FetchWorkItem workItem) {
      while (_PriorityQueue.Count > 0) {
        FetchWorkItem candidate = _PriorityQueue.Dequeue();
        _QueuedWorkKeys.Remove(candidate.Key);

        PersistentCacheEntry entry;
        if (!this.TryReadCacheEntry(candidate.Operation, candidate.Argument, out entry) ||
            !this.IsFresh(entry.CreatedUtc, DateTime.UtcNow)) {
          workItem = candidate;
          return true;
        }
      }

      workItem = null;
      return false;
    }

    /// <summary>
    /// Finds the next background operation by first completing missing values breadth-first
    /// and then selecting the oldest expired cache entry.
    /// </summary>
    private FetchWorkItem FindNextBackgroundWorkItem() {
      FetchWorkItem missing = this.FindNextMissingBreadthFirst();
      if (missing != null) {
        return missing;
      }

      return this.FindOldestExpiredWorkItem();
    }

    /// <summary>
    /// Finds the first missing operation while traversing known areas breadth-first.
    /// </summary>
    private FetchWorkItem FindNextMissingBreadthFirst() {
      Queue<string> pendingAreas = new Queue<string>();
      HashSet<string> visitedAreas = new HashSet<string>(StringComparer.Ordinal);
      pendingAreas.Enqueue("/");

      while (pendingAreas.Count > 0) {
        string area = pendingAreas.Dequeue();
        if (!visitedAreas.Add(area)) {
          continue;
        }

        string[] basicOperations = new string[] {
          "children",
          "capabilities",
          "name",
          "has-direct-content"
        };

        foreach (string operation in basicOperations) {
          if (!this.HasCacheEntry(operation, area)) {
            return new FetchWorkItem(operation, area);
          }
        }

        CachedCapabilities capabilities;
        if (this.TryReadCachedPayloadOnly("capabilities", area, out capabilities)) {
          if (capabilities.ContentLevel != ContentLevel.BeyondContent) {
            if (!this.HasCacheEntry("direct-content", area)) {
              return new FetchWorkItem("direct-content", area);
            }
            if (!this.HasCacheEntry("aggregated-content", area)) {
              return new FetchWorkItem("aggregated-content", area);
            }
          }

          if (capabilities.SupportsResources && !this.HasCacheEntry("resources", area)) {
            return new FetchWorkItem("resources", area);
          }
        }

        string[] children;
        if (this.TryReadCachedPayloadOnly("children", area, out children)) {
          foreach (string child in children) {
            pendingAreas.Enqueue(child);
          }
        }
      }

      FetchWorkItem missingResource = this.FindMissingResourceContentWorkItem();
      if (missingResource != null) {
        return missingResource;
      }

      return null;
    }

    /// <summary>
    /// Finds one known resource whose binary content has not yet been cached.
    /// </summary>
    private FetchWorkItem FindMissingResourceContentWorkItem() {
      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();
      foreach (PersistentCacheEntry entry in entries.OrderBy((PersistentCacheEntry item) => item.CreatedUtc)) {
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
          if (!this.HasCacheEntry("resource-content", resource.ResourceId)) {
            return new FetchWorkItem("resource-content", resource.ResourceId);
          }
        }
      }

      return null;
    }

    /// <summary>
    /// Finds the oldest expired persisted operation.
    /// </summary>
    private FetchWorkItem FindOldestExpiredWorkItem() {
      DateTime utcNow = DateTime.UtcNow;
      PersistentCacheEntry[] entries = this.ReadAllPersistentEntries();
      PersistentCacheEntry oldest = null;

      foreach (PersistentCacheEntry entry in entries) {
        if (string.Equals(entry.Operation, "search", StringComparison.Ordinal)) {
          continue;
        }

        if (this.IsFresh(entry.CreatedUtc, utcNow)) {
          continue;
        }

        if (oldest == null || entry.CreatedUtc < oldest.CreatedUtc) {
          oldest = entry;
        }
      }

      if (oldest == null) {
        return null;
      }

      return new FetchWorkItem(oldest.Operation, oldest.Argument);
    }

    /// <summary>
    /// Executes one logical source operation.
    /// </summary>
    private object FetchFromSource(FetchWorkItem workItem) {
      DevLogger.LogTrace(0, 99999, "Background knowledge cache fetch: " + workItem.Operation + " | " + workItem.Argument);

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

      try {
        File.WriteAllText(temporaryFile, JsonConvert.SerializeObject(entry, Formatting.None), Encoding.UTF8);
        File.Move(temporaryFile, file, true);
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        this.TryDeleteFile(temporaryFile);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        this.TryDeleteFile(temporaryFile);
      }

      this.StoreMemoryValue(cacheKey, entry.CreatedUtc, value);
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
      List<PersistentCacheEntry> result = new List<PersistentCacheEntry>();
      string[] files = Directory.GetFiles(_CacheDirectory, "*" + _CacheEntryExtension, SearchOption.TopDirectoryOnly);

      foreach (string file in files) {
        PersistentCacheEntry entry;
        if (this.TryReadPersistentEntry(file, out entry)) {
          result.Add(entry);
        }
      }

      return result.ToArray();
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

        this.EnqueuePriorityWork("children", "/");
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

      /// <summary>
      /// Creates one immutable work item.
      /// </summary>
      public FetchWorkItem(string operation, string argument) {
        _Operation = operation;
        _Argument = argument;
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
