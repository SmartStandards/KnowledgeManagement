using KnowledgeManagement.SmartStandards.Providers;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace KnowledgeManagement.SmartStandards.Wrappers {

  /// <summary>
  /// Wraps an arbitrary <see cref="IKnowledgeRepository"/> with a persistent, demand-driven
  /// file-system cache.
  ///
  /// The wrapped repository always remains authoritative. Unlike the former snapshot-based
  /// implementation, this wrapper never performs a complete repository traversal merely to
  /// satisfy a normal read. Every operation caches only the exact logical scope that was
  /// requested.
  ///
  /// Direct-child enumeration is cached independently per area. Explicit recursive
  /// enumeration remains supported, but is implemented iteratively by repeatedly requesting
  /// direct children with <c>GetAreas(false, ...)</c>. The wrapped provider therefore never
  /// receives a recursive area request from this wrapper.
  ///
  /// Read failures from the backing provider fall back to the most recent cached value,
  /// even when that value exceeded the configured lifetime. This fallback is deliberately
  /// exception-agnostic on reads: a broken provider must not make already cached knowledge
  /// unavailable. Writes never use
  /// this fallback: a mutation is executed against the authoritative provider first and the
  /// directly affected cache scopes are refreshed from the provider immediately afterwards.
  /// Refresh failures after a successful mutation are surfaced to the caller.
  ///
  /// Persistent cache entries are validated individually during construction. Invalid or
  /// no-longer-existing entries are removed without recursively enumerating the repository.
  /// Connectivity failures during startup validation preserve the existing cache so offline
  /// reads remain possible.
  /// </summary>
  public class KnowledgeRepositoryCacheWrapper : FileBasedKnowledgeRepository {

    private const int _CacheFormatVersion = 2;
    private const string _CacheDirectoryName = ".knowledge-cache";
    private const string _CacheEntryExtension = ".cache";
    private const string _LegacyCurrentDirectoryName = "current";
    private const string _LegacyRefreshDirectoryPrefix = ".refresh-";
    private const string _LegacyPreviousDirectoryPrefix = ".previous-";
    private const string _TemporaryCacheRootDirectoryName = ".knowledge-repository-cache";

    private readonly object _CacheSyncRoot;
    private readonly IKnowledgeRepository _WrappedSource;
    private readonly TimeSpan _Lifetime;
    private readonly string _CacheFileSystemPath;
    private readonly string _CacheDirectory;

    private readonly Dictionary<string, MemoryCacheEntry> _MemoryCache;

    /// <summary>
    /// Creates one persistent demand-driven cache around an arbitrary knowledge repository.
    /// </summary>
    /// <param name="wrappedSource">The authoritative provider being wrapped.</param>
    /// <param name="lifetimeMin">
    /// Number of minutes for which one successfully cached read is considered fresh.
    /// A value of zero causes every read to attempt the authoritative provider first.
    /// </param>
    /// <param name="cacheFileSystemPath">
    /// Optional persistent cache root. When omitted, a process-local temporary directory is
    /// created.
    /// </param>
    public KnowledgeRepositoryCacheWrapper(
      IKnowledgeRepository wrappedSource,
      int lifetimeMin = 5,
      string cacheFileSystemPath = null
    ) : base(
      ResolveCacheFileSystemPath(
        cacheFileSystemPath
      ),
      false,
      false
    ) {
      if (wrappedSource == null) {
        throw new ArgumentNullException(
          nameof(wrappedSource)
        );
      }

      if (lifetimeMin < 0) {
        throw new ArgumentOutOfRangeException(
          nameof(lifetimeMin),
          "The cache lifetime must not be negative."
        );
      }

      _CacheSyncRoot =
        new object();

      _WrappedSource =
        wrappedSource;

      _Lifetime =
        TimeSpan.FromMinutes(
          lifetimeMin
        );

      _CacheFileSystemPath =
        this.RootDirectory;

      _CacheDirectory =
        Path.Combine(
          _CacheFileSystemPath,
          _CacheDirectoryName
        );

      _MemoryCache =
        new Dictionary<string, MemoryCacheEntry>(
          StringComparer.Ordinal
        );

      Directory.CreateDirectory(
        _CacheDirectory
      );

      this.DeleteLegacySnapshotCacheArtifacts();

      // Intentionally disabled: validating every persisted cache entry during application
      // startup can itself block startup for large or temporarily unavailable repositories.
      // Cached entries are therefore validated lazily when they are accessed.
      //this.ValidatePersistentCacheOnStartup();
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
    /// Gets the physical root directory containing the persistent cache.
    /// </summary>
    public string CacheFileSystemPath {
      get {
        return _CacheFileSystemPath;
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
    /// Returns logical area paths below one area.
    ///
    /// Normal navigation reads only direct children. Explicit recursive enumeration is
    /// implemented iteratively so the wrapped provider itself is never invoked with
    /// recurse=true.
    /// </summary>
    public override string[] GetAreas(
      bool recurse,
      string startArea = "/"
    ) {
      lock (_CacheSyncRoot) {
        if (!recurse) {
          return this.GetDirectAreasCached(
            startArea
          );
        }

        List<string> result =
          new List<string>();

        Stack<string> pending =
          new Stack<string>();

        string[] rootChildren =
          this.GetDirectAreasCached(
            startArea
          );

        for (int index = rootChildren.Length - 1;
             index >= 0;
             index--) {
          pending.Push(
            rootChildren[index]
          );
        }

        while (pending.Count > 0) {
          string current =
            pending.Pop();

          result.Add(
            current
          );

          string[] children;

          try {
            children =
              this.GetDirectAreasCached(
                current
              );
          }
          catch (Exception ex) {
            DevLogger.LogError(
              ex
            );

            DevLogger.LogTrace(
              0,
              99999,
              "Knowledge repository cache could not enumerate descendants below '"
              + current
              + "'. The already available partial result is returned."
            );

            continue;
          }

          for (int index = children.Length - 1;
               index >= 0;
               index--) {
            pending.Push(
              children[index]
            );
          }
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Executes and caches provider-native keyword search.
    ///
    /// Search is an explicit bulk operation and may be expensive depending on the wrapped
    /// provider. Normal navigation never calls this method implicitly.
    /// </summary>
    public override string[] GetAreasByKeyword(
      string keyword,
      string startArea = "/"
    ) {
      if (string.IsNullOrWhiteSpace(
            keyword
          )) {
        return Array.Empty<string>();
      }

      lock (_CacheSyncRoot) {
        string argument =
          startArea
          + "\n"
          + keyword;

        return this.ReadCached(
          "search",
          argument,
          () => _WrappedSource.GetAreasByKeyword(
            keyword,
            startArea
          )
        );
      }
    }

    /// <summary>
    /// Returns the provider-neutral display name of one logical area.
    /// </summary>
    public override string GetAreaName(
      string area
    ) {
      lock (_CacheSyncRoot) {
        return this.ReadCached(
          "name",
          area,
          () => _WrappedSource.GetAreaName(
            area
          )
        );
      }
    }

    /// <summary>
    /// Returns cached capabilities for one logical area.
    /// </summary>
    public override void GetAreaCapabilities(
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
      lock (_CacheSyncRoot) {
        CachedCapabilities capabilities =
          this.ReadCached(
            "capabilities",
            area,
            () => this.ReadCapabilitiesFromSource(
              area
            )
          );

        contentLevel =
          capabilities.ContentLevel;

        supportsSubAreas =
          capabilities.SupportsSubAreas;

        canBeRenamed =
          capabilities.CanBeRenamed;

        canBeDeleted =
          capabilities.CanBeDeleted;

        canAddSubAreas =
          capabilities.CanAddSubAreas;

        canAppendContent =
          capabilities.CanAppendContent;

        canTruncate =
          capabilities.CanTruncate;

        supportsResources =
          capabilities.SupportsResources;
      }
    }

    /// <summary>
    /// Returns resource metadata for one logical area.
    /// </summary>
    public override KnowledgeResourceInfo[] GetResources(
      string area
    ) {
      lock (_CacheSyncRoot) {
        return this.ReadCached(
          "resources",
          area,
          () => _WrappedSource.GetResources(
            area
          )
        );
      }
    }

    /// <summary>
    /// Returns binary resource content using the opaque provider resource identifier.
    /// </summary>
    public override byte[] GetResourceContent(
      string resourceId
    ) {
      lock (_CacheSyncRoot) {
        return this.ReadCached(
          "resource-content",
          resourceId,
          () => _WrappedSource.GetResourceContent(
            resourceId
          )
        );
      }
    }

    /// <summary>
    /// Adds one resource to the authoritative source and refreshes the affected area and
    /// newly created resource immediately after success.
    /// </summary>
    public override bool TryAddResource(
      string area,
      string preferredFileName,
      string contentType,
      byte[] content,
      out string resourceId
    ) {
      resourceId =
        string.Empty;

      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryAddResource(
            area,
            preferredFileName,
            contentType,
            content,
            out resourceId
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.RefreshAreaAfterWrite(
          area
        );

        this.RefreshResourceContentAfterWrite(
          resourceId
        );

        return true;
      }
    }

    /// <summary>
    /// Replaces one authoritative resource and immediately downloads its new binary value.
    /// </summary>
    public override bool TryReplaceResource(
      string resourceId,
      string contentType,
      byte[] content
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryReplaceResource(
            resourceId,
            contentType,
            content
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateOperationArgument(
          "resource-content",
          resourceId
        );

        this.InvalidateOperation(
          "resources"
        );

        this.RefreshResourceContentAfterWrite(
          resourceId
        );

        return true;
      }
    }

    /// <summary>
    /// Deletes one authoritative resource and invalidates every cached resource listing.
    /// </summary>
    public override bool TryDeleteResource(
      string resourceId
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryDeleteResource(
            resourceId
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateOperationArgument(
          "resource-content",
          resourceId
        );

        this.InvalidateOperation(
          "resources"
        );

        this.InvalidateOperation(
          "search"
        );

        return true;
      }
    }

    /// <summary>
    /// Returns whether one area currently owns non-empty direct textual content.
    /// </summary>
    public override bool HasDirectContent(
      string area
    ) {
      lock (_CacheSyncRoot) {
        return this.ReadCached(
          "has-direct-content",
          area,
          () => _WrappedSource.HasDirectContent(
            area
          )
        );
      }
    }

    /// <summary>
    /// Returns only direct textual content of one area.
    /// </summary>
    public override string GetDirectContent(
      string area
    ) {
      lock (_CacheSyncRoot) {
        return this.ReadCached(
          "direct-content",
          area,
          () => _WrappedSource.GetDirectContent(
            area
          )
        );
      }
    }

    /// <summary>
    /// Returns aggregated textual content of the explicitly addressed area.
    /// </summary>
    public override string GetAggregatedContent(
      string area
    ) {
      lock (_CacheSyncRoot) {
        return this.ReadCached(
          "aggregated-content",
          area,
          () => _WrappedSource.GetAggregatedContent(
            area
          )
        );
      }
    }

    /// <summary>
    /// Deletes one authoritative area and refreshes its direct parent after success.
    /// </summary>
    public override bool TryDelete(
      string area
    ) {
      lock (_CacheSyncRoot) {
        string parentArea =
          this.GetParentArea(
            area
          );

        bool succeeded =
          _WrappedSource.TryDelete(
            area
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.InvalidateAreaScope(
          parentArea
        );

        this.RefreshAreaAfterWrite(
          parentArea
        );

        return true;
      }
    }

    /// <summary>
    /// Renames one authoritative area and refreshes its parent listing after success.
    /// </summary>
    public override bool TryRename(
      string area,
      string newName,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      resourceIdChanges =
        Array.Empty<KnowledgeResourceIdChange>();

      lock (_CacheSyncRoot) {
        string parentArea =
          this.GetParentArea(
            area
          );

        bool succeeded =
          _WrappedSource.TryRename(
            area,
            newName,
            out resourceIdChanges
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.InvalidateAreaScope(
          parentArea
        );

        this.InvalidateChangedResourceIds(
          resourceIdChanges
        );

        this.RefreshAreaAfterWrite(
          parentArea
        );

        string renamedArea =
          this.FindDirectChildByNameFromSource(
            parentArea,
            newName
          );

        if (!string.IsNullOrEmpty(
              renamedArea
            )) {
          this.RefreshAreaAfterWrite(
            renamedArea
          );
        }

        return true;
      }
    }

    /// <summary>
    /// Creates one child area in the authoritative source and refreshes the parent listing.
    /// </summary>
    public override bool TryAddSubArea(
      string area,
      string name,
      KnowledgeAreaKind kind
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryAddSubArea(
            area,
            name,
            kind
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.RefreshAreaAfterWrite(
          area
        );

        string addedArea =
          this.FindDirectChildByNameFromSource(
            area,
            name
          );

        if (!string.IsNullOrEmpty(
              addedArea
            )) {
          this.RefreshAreaAfterWrite(
            addedArea
          );
        }

        return true;
      }
    }

    /// <summary>
    /// Appends content to the authoritative source and immediately refreshes the affected
    /// logical area after success.
    /// </summary>
    public override bool TryAppendContent(
      string area,
      string content
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryAppendContent(
            area,
            content
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.RefreshAreaAfterWrite(
          area
        );

        return true;
      }
    }

    /// <summary>
    /// Truncates the authoritative area and immediately refreshes its new state.
    /// </summary>
    public override bool TryTruncate(
      string area
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryTruncate(
            area
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.RefreshAreaAfterWrite(
          area
        );

        return true;
      }
    }

    /// <summary>
    /// Replaces content in the authoritative area and immediately refreshes its new state.
    /// </summary>
    public override bool TryReplace(
      string area,
      string newContent
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryReplace(
            area,
            newContent
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          area
        );

        this.RefreshAreaAfterWrite(
          area
        );

        return true;
      }
    }

    /// <summary>
    /// Moves one authoritative area and refreshes the old and new parent scopes.
    /// </summary>
    public override bool TryMoveContent(
      string contentAreaToMove,
      string newParentArea,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      resourceIdChanges =
        Array.Empty<KnowledgeResourceIdChange>();

      lock (_CacheSyncRoot) {
        string oldParentArea =
          this.GetParentArea(
            contentAreaToMove
          );

        string movedAreaName =
          _WrappedSource.GetAreaName(
            contentAreaToMove
          );

        bool succeeded =
          _WrappedSource.TryMoveContent(
            contentAreaToMove,
            newParentArea,
            out resourceIdChanges
          );

        if (!succeeded) {
          return false;
        }

        this.InvalidateAreaScope(
          contentAreaToMove
        );

        this.InvalidateAreaScope(
          oldParentArea
        );

        this.InvalidateAreaScope(
          newParentArea
        );

        this.InvalidateChangedResourceIds(
          resourceIdChanges
        );

        this.RefreshAreaAfterWrite(
          oldParentArea
        );

        if (!string.Equals(
              oldParentArea,
              newParentArea,
              StringComparison.Ordinal
            )) {
          this.RefreshAreaAfterWrite(
            newParentArea
          );
        }

        string movedArea =
          this.FindDirectChildByNameFromSource(
            newParentArea,
            movedAreaName
          );

        if (!string.IsNullOrEmpty(
              movedArea
            )) {
          this.RefreshAreaAfterWrite(
            movedArea
          );
        }

        return true;
      }
    }

    /// <summary>
    /// Finds one direct child by its provider-neutral display name without recursive
    /// enumeration.
    /// </summary>
    private string FindDirectChildByNameFromSource(
      string parentArea,
      string displayName
    ) {
      string[] children =
        _WrappedSource.GetAreas(
          false,
          parentArea
        );

      foreach (string child in children) {
        string childName =
          _WrappedSource.GetAreaName(
            child
          );

        if (string.Equals(
              childName,
              displayName,
              StringComparison.Ordinal
            )) {
          return child;
        }
      }

      return string.Empty;
    }

    /// <summary>
    /// Returns cached direct children or loads them from the authoritative provider.
    /// </summary>
    private string[] GetDirectAreasCached(
      string area
    ) {
      return this.ReadCached(
        "children",
        area,
        () => _WrappedSource.GetAreas(
          false,
          area
        )
      );
    }

    /// <summary>
    /// Reads one typed value through the persistent cache.
    ///
    /// A fresh value is preferred. If the authoritative provider cannot be reached, the
    /// most recently persisted value is returned regardless of age.
    /// </summary>
    private T ReadCached<T>(
      string operation,
      string argument,
      Func<T> loader
    ) {
      string cacheKey =
        this.CreateCacheKey(
          operation,
          argument
        );

      DateTime utcNow =
        DateTime.UtcNow;

      MemoryCacheEntry memoryEntry;

      if (_MemoryCache.TryGetValue(
            cacheKey,
            out memoryEntry
          ) &&
          this.IsFresh(
            memoryEntry.CreatedUtc,
            utcNow
          )) {
        return (T)memoryEntry.Value;
      }

      PersistentCacheEntry persistentEntry;
      T persistentValue;
      bool hasPersistentValue =
        this.TryReadPersistentValue(
          operation,
          argument,
          out persistentEntry,
          out persistentValue
        );

      if (hasPersistentValue &&
          this.IsFresh(
            persistentEntry.CreatedUtc,
            utcNow
          )) {
        this.StoreMemoryValue(
          cacheKey,
          persistentEntry.CreatedUtc,
          persistentValue
        );

        return persistentValue;
      }

      try {
        T freshValue =
          loader();

        this.WriteCacheValue(
          operation,
          argument,
          freshValue
        );

        return freshValue;
      }
      catch (Exception ex) {
        DevLogger.LogError(
          ex
        );

        if (hasPersistentValue) {
          DevLogger.LogTrace(
            0,
            99999,
            "Knowledge repository cache source read failed for operation '"
            + operation
            + "'. Serving the last persisted value."
          );

          this.StoreMemoryValue(
            cacheKey,
            persistentEntry.CreatedUtc,
            persistentValue
          );

          return persistentValue;
        }

        throw;
      }
    }

    /// <summary>
    /// Reads area capabilities directly from the authoritative provider.
    /// </summary>
    private CachedCapabilities ReadCapabilitiesFromSource(
      string area
    ) {
      CachedCapabilities result =
        new CachedCapabilities();

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

      result.ContentLevel =
        contentLevel;

      result.SupportsSubAreas =
        supportsSubAreas;

      result.CanBeRenamed =
        canBeRenamed;

      result.CanBeDeleted =
        canBeDeleted;

      result.CanAddSubAreas =
        canAddSubAreas;

      result.CanAppendContent =
        canAppendContent;

      result.CanTruncate =
        canTruncate;

      result.SupportsResources =
        supportsResources;

      return result;
    }

    /// <summary>
    /// Refreshes all inexpensive values belonging directly to one area.
    ///
    /// This method deliberately performs no fallback. It is used after writes, where a
    /// failed re-download must be surfaced directly.
    /// </summary>
    private void RefreshAreaAfterWrite(
      string area
    ) {
      CachedCapabilities capabilities =
        this.ReadCapabilitiesFromSource(
          area
        );

      this.WriteCacheValue(
        "capabilities",
        area,
        capabilities
      );

      string name =
        _WrappedSource.GetAreaName(
          area
        );

      this.WriteCacheValue(
        "name",
        area,
        name
      );

      string[] children =
        _WrappedSource.GetAreas(
          false,
          area
        );

      this.WriteCacheValue(
        "children",
        area,
        children
      );

      bool hasDirectContent =
        _WrappedSource.HasDirectContent(
          area
        );

      this.WriteCacheValue(
        "has-direct-content",
        area,
        hasDirectContent
      );

      if (capabilities.ContentLevel != ContentLevel.BeyondContent) {
        string directContent =
          _WrappedSource.GetDirectContent(
            area
          );

        this.WriteCacheValue(
          "direct-content",
          area,
          directContent
        );

        string aggregatedContent =
          _WrappedSource.GetAggregatedContent(
            area
          );

        this.WriteCacheValue(
          "aggregated-content",
          area,
          aggregatedContent
        );
      }

      if (capabilities.SupportsResources) {
        KnowledgeResourceInfo[] resources =
          _WrappedSource.GetResources(
            area
          );

        this.WriteCacheValue(
          "resources",
          area,
          resources
        );
      }
    }

    /// <summary>
    /// Downloads one resource immediately after a successful resource mutation.
    /// </summary>
    private void RefreshResourceContentAfterWrite(
      string resourceId
    ) {
      byte[] content =
        _WrappedSource.GetResourceContent(
          resourceId
        );

      this.WriteCacheValue(
        "resource-content",
        resourceId,
        content
      );
    }

    /// <summary>
    /// Invalidates cache entries associated with one logical area, its descendants, its
    /// direct parent listing and global search results.
    /// </summary>
    private void InvalidateAreaScope(
      string area
    ) {
      string normalizedArea =
        this.NormalizeArea(
          area
        );

      string parentArea =
        this.GetParentArea(
          normalizedArea
        );

      this.InvalidateMatchingEntries(
        (PersistentCacheEntry entry) => {
          if (string.Equals(
                entry.Operation,
                "search",
                StringComparison.Ordinal
              )) {
            return true;
          }

          if (string.Equals(
                entry.Argument,
                normalizedArea,
                StringComparison.Ordinal
              )) {
            return true;
          }

          if (normalizedArea != "/" &&
              entry.Argument.StartsWith(
                normalizedArea + "/",
                StringComparison.Ordinal
              )) {
            return true;
          }

          if (string.Equals(
                entry.Operation,
                "children",
                StringComparison.Ordinal
              ) &&
              string.Equals(
                entry.Argument,
                parentArea,
                StringComparison.Ordinal
              )) {
            return true;
          }

          return false;
        }
      );
    }

    /// <summary>
    /// Invalidates cached values for one operation and argument.
    /// </summary>
    private void InvalidateOperationArgument(
      string operation,
      string argument
    ) {
      string cacheKey =
        this.CreateCacheKey(
          operation,
          argument
        );

      _MemoryCache.Remove(
        cacheKey
      );

      string file =
        this.GetCacheFilePath(
          cacheKey
        );

      this.TryDeleteCacheFile(
        file
      );
    }

    /// <summary>
    /// Invalidates every cache entry of one operation type.
    /// </summary>
    private void InvalidateOperation(
      string operation
    ) {
      this.InvalidateMatchingEntries(
        (PersistentCacheEntry entry) =>
          string.Equals(
            entry.Operation,
            operation,
            StringComparison.Ordinal
          )
      );
    }

    /// <summary>
    /// Invalidates old and new resource identifiers returned by a provider mutation.
    /// </summary>
    private void InvalidateChangedResourceIds(
      KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      if (resourceIdChanges == null) {
        return;
      }

      foreach (KnowledgeResourceIdChange change in resourceIdChanges) {
        this.InvalidateOperationArgument(
          "resource-content",
          change.PreviousResourceId
        );

        this.InvalidateOperationArgument(
          "resource-content",
          change.CurrentResourceId
        );
      }

      this.InvalidateOperation(
        "resources"
      );
    }

    /// <summary>
    /// Invalidates every persistent entry matching one predicate.
    /// </summary>
    private void InvalidateMatchingEntries(
      Func<PersistentCacheEntry, bool> predicate
    ) {
      string[] files =
        Directory.GetFiles(
          _CacheDirectory,
          "*" + _CacheEntryExtension,
          SearchOption.TopDirectoryOnly
        );

      foreach (string file in files) {
        PersistentCacheEntry entry;

        if (!this.TryReadPersistentEntry(
              file,
              out entry
            )) {
          this.TryDeleteCacheFile(
            file
          );

          continue;
        }

        if (!predicate(
              entry
            )) {
          continue;
        }

        string cacheKey =
          this.CreateCacheKey(
            entry.Operation,
            entry.Argument
          );

        _MemoryCache.Remove(
          cacheKey
        );

        this.TryDeleteCacheFile(
          file
        );
      }
    }

    /// <summary>
    /// Writes one typed cache value atomically.
    /// </summary>
    private void WriteCacheValue<T>(
      string operation,
      string argument,
      T value
    ) {
      PersistentCacheEntry entry =
        new PersistentCacheEntry();

      entry.FormatVersion =
        _CacheFormatVersion;

      entry.Operation =
        operation;

      entry.Argument =
        argument;

      entry.CreatedUtc =
        DateTime.UtcNow;

      entry.PayloadJson =
        JsonConvert.SerializeObject(
          value
        );

      string cacheKey =
        this.CreateCacheKey(
          operation,
          argument
        );

      string file =
        this.GetCacheFilePath(
          cacheKey
        );

      string temporaryFile =
        file
        + "."
        + Guid.NewGuid().ToString(
          "N"
        )
        + ".tmp";

      string serialized =
        JsonConvert.SerializeObject(
          entry,
          Formatting.None
        );

      try {
        File.WriteAllText(
          temporaryFile,
          serialized,
          Encoding.UTF8
        );

        File.Move(
          temporaryFile,
          file,
          true
        );
      }
      catch (IOException ex) {
        DevLogger.LogError(
          ex
        );

        this.TryDeleteCacheFile(
          temporaryFile
        );
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(
          ex
        );

        this.TryDeleteCacheFile(
          temporaryFile
        );
      }

      this.StoreMemoryValue(
        cacheKey,
        entry.CreatedUtc,
        value
      );
    }

    /// <summary>
    /// Reads and deserializes one typed persistent value.
    /// </summary>
    private bool TryReadPersistentValue<T>(
      string operation,
      string argument,
      out PersistentCacheEntry entry,
      out T value
    ) {
      entry =
        null;

      value =
        default(T);

      string cacheKey =
        this.CreateCacheKey(
          operation,
          argument
        );

      string file =
        this.GetCacheFilePath(
          cacheKey
        );

      if (!this.TryReadPersistentEntry(
            file,
            out entry
          )) {
        return false;
      }

      if (!string.Equals(
            entry.Operation,
            operation,
            StringComparison.Ordinal
          ) ||
          !string.Equals(
            entry.Argument,
            argument,
            StringComparison.Ordinal
          )) {
        this.TryDeleteCacheFile(
          file
        );

        entry =
          null;

        return false;
      }

      try {
        T deserialized =
          JsonConvert.DeserializeObject<T>(
            entry.PayloadJson
          );

        value =
          deserialized;

        return true;
      }
      catch (JsonException ex) {
        DevLogger.LogError(
          ex
        );

        this.TryDeleteCacheFile(
          file
        );

        entry =
          null;

        value =
          default(T);

        return false;
      }
    }

    /// <summary>
    /// Reads one persistent cache envelope without interpreting its payload type.
    /// </summary>
    private bool TryReadPersistentEntry(
      string file,
      out PersistentCacheEntry entry
    ) {
      entry =
        null;

      if (!File.Exists(
            file
          )) {
        return false;
      }

      try {
        string json =
          File.ReadAllText(
            file,
            Encoding.UTF8
          );

        PersistentCacheEntry loaded =
          JsonConvert.DeserializeObject<PersistentCacheEntry>(
            json
          );

        if (loaded == null ||
            loaded.FormatVersion != _CacheFormatVersion ||
            string.IsNullOrWhiteSpace(
              loaded.Operation
            )) {
          return false;
        }

        entry =
          loaded;

        return true;
      }
      catch (IOException ex) {
        DevLogger.LogError(
          ex
        );

        return false;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(
          ex
        );

        return false;
      }
      catch (JsonException ex) {
        DevLogger.LogError(
          ex
        );

        return false;
      }
    }

    /// <summary>
    /// Removes cache artifacts produced by the former complete-snapshot implementation.
    ///
    /// The wrapper owns its cache root exclusively. Version-1 snapshots cannot participate
    /// in the demand-driven version-2 cache and retaining them would violate the startup
    /// cleanup guarantee.
    /// </summary>
    private void DeleteLegacySnapshotCacheArtifacts() {
      string legacyCurrentDirectory =
        Path.Combine(
          _CacheFileSystemPath,
          _LegacyCurrentDirectoryName
        );

      this.TryDeleteCacheDirectory(
        legacyCurrentDirectory
      );

      string[] directories =
        Directory.GetDirectories(
          _CacheFileSystemPath,
          "*",
          SearchOption.TopDirectoryOnly
        );

      foreach (string directory in directories) {
        string directoryName =
          Path.GetFileName(
            directory
          );

        if (directoryName.StartsWith(
              _LegacyRefreshDirectoryPrefix,
              StringComparison.Ordinal
            ) ||
            directoryName.StartsWith(
              _LegacyPreviousDirectoryPrefix,
              StringComparison.Ordinal
            )) {
          this.TryDeleteCacheDirectory(
            directory
          );
        }
      }
    }

    /// <summary>
    /// Deletes one cache-owned directory using best-effort cleanup semantics.
    /// </summary>
    private void TryDeleteCacheDirectory(
      string directory
    ) {
      if (!Directory.Exists(
            directory
          )) {
        return;
      }

      try {
        Directory.Delete(
          directory,
          true
        );
      }
      catch (IOException ex) {
        DevLogger.LogError(
          ex
        );
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(
          ex
        );
      }
    }

    /// <summary>
    /// Validates every persistent cache file individually during startup.
    ///
    /// Validation never enumerates the complete repository. Area-backed entries are checked
    /// by resolving only their exact area. Search entries are discarded because their
    /// global result set cannot be proven current without a bulk search. Resource content
    /// entries are checked by resolving only the exact resource identifier.
    /// </summary>
    private void ValidatePersistentCacheOnStartup() {
      string[] files =
        Directory.GetFiles(
          _CacheDirectory,
          "*" + _CacheEntryExtension,
          SearchOption.TopDirectoryOnly
        );

      Dictionary<string, bool> areaExistence =
        new Dictionary<string, bool>(
          StringComparer.Ordinal
        );

      foreach (string file in files) {
        PersistentCacheEntry entry;

        if (!this.TryReadPersistentEntry(
              file,
              out entry
            )) {
          this.TryDeleteCacheFile(
            file
          );

          continue;
        }

        if (string.Equals(
              entry.Operation,
              "search",
              StringComparison.Ordinal
            )) {
          this.TryDeleteCacheFile(
            file
          );

          continue;
        }

        if (string.Equals(
              entry.Operation,
              "resource-content",
              StringComparison.Ordinal
            )) {
          this.ValidateCachedResourceEntry(
            file,
            entry
          );

          continue;
        }

        bool exists;

        if (!areaExistence.TryGetValue(
              entry.Argument,
              out exists
            )) {
          try {
            _WrappedSource.GetAreaCapabilities(
              entry.Argument,
              out ContentLevel contentLevel,
              out bool supportsSubAreas,
              out bool canBeRenamed,
              out bool canBeDeleted,
              out bool canAddSubAreas,
              out bool canAppendContent,
              out bool canTruncate,
              out bool supportsResources
            );

            exists =
              true;

            areaExistence[entry.Argument] =
              true;
          }
          catch (InvalidOperationException) {
            exists =
              false;

            areaExistence[entry.Argument] =
              false;
          }
          catch (Exception ex) when (this.IsConnectivityException(ex)) {
            DevLogger.LogError(
              ex
            );

            // Source connectivity is insufficient to prove the entry obsolete. Preserve
            // the last cache value for offline reads.
            continue;
          }
        }

        if (!exists) {
          this.TryDeleteCacheFile(
            file
          );
        }
      }
    }

    /// <summary>
    /// Validates one persisted resource-content entry without traversing repository areas.
    /// </summary>
    private void ValidateCachedResourceEntry(
      string file,
      PersistentCacheEntry entry
    ) {
      try {
        _WrappedSource.GetResourceContent(
          entry.Argument
        );
      }
      catch (InvalidOperationException) {
        this.TryDeleteCacheFile(
          file
        );
      }
      catch (Exception ex) when (this.IsConnectivityException(ex)) {
        DevLogger.LogError(
          ex
        );
      }
    }

    /// <summary>
    /// Stores one typed value in the process-local cache.
    /// </summary>
    private void StoreMemoryValue<T>(
      string cacheKey,
      DateTime createdUtc,
      T value
    ) {
      MemoryCacheEntry memoryEntry =
        new MemoryCacheEntry();

      memoryEntry.CreatedUtc =
        createdUtc;

      memoryEntry.Value =
        value;

      _MemoryCache[cacheKey] =
        memoryEntry;
    }

    /// <summary>
    /// Returns whether one cache timestamp is still inside the configured lifetime.
    /// </summary>
    private bool IsFresh(
      DateTime createdUtc,
      DateTime utcNow
    ) {
      if (_Lifetime <= TimeSpan.Zero) {
        return false;
      }

      return utcNow - createdUtc < _Lifetime;
    }

    /// <summary>
    /// Determines whether a read failure may safely fall back to a previous cache value.
    /// </summary>
    private bool IsReadFallbackException(
      Exception ex
    ) {
      return ex is IOException ||
        ex is UnauthorizedAccessException ||
        ex is HttpRequestException ||
        ex is TimeoutException ||
        ex is InvalidOperationException;
    }

    /// <summary>
    /// Determines whether an exception clearly represents source connectivity or storage
    /// availability rather than an absent logical area.
    /// </summary>
    private bool IsConnectivityException(
      Exception ex
    ) {
      return ex is IOException ||
        ex is UnauthorizedAccessException ||
        ex is HttpRequestException ||
        ex is TimeoutException;
    }

    /// <summary>
    /// Creates one deterministic cache key for an operation and logical argument.
    /// </summary>
    private string CreateCacheKey(
      string operation,
      string argument
    ) {
      string input =
        operation
        + "\n"
        + argument;

      byte[] hash =
        SHA256.HashData(
          Encoding.UTF8.GetBytes(
            input
          )
        );

      StringBuilder builder =
        new StringBuilder(
          hash.Length * 2
        );

      foreach (byte current in hash) {
        builder.Append(
          current.ToString(
            "x2"
          )
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Returns the physical file for one deterministic cache key.
    /// </summary>
    private string GetCacheFilePath(
      string cacheKey
    ) {
      return Path.Combine(
        _CacheDirectory,
        cacheKey
        + _CacheEntryExtension
      );
    }

    /// <summary>
    /// Deletes one cache file using best-effort cleanup semantics.
    /// </summary>
    private void TryDeleteCacheFile(
      string file
    ) {
      if (!File.Exists(
            file
          )) {
        return;
      }

      try {
        File.Delete(
          file
        );
      }
      catch (IOException ex) {
        DevLogger.LogError(
          ex
        );
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(
          ex
        );
      }
    }

    /// <summary>
    /// Returns the provider-neutral parent path of one logical area.
    /// </summary>
    private string GetParentArea(
      string area
    ) {
      string normalized =
        this.NormalizeArea(
          area
        );

      if (normalized == "/") {
        return "/";
      }

      int separatorIndex =
        normalized.LastIndexOf('/');

      if (separatorIndex <= 0) {
        return "/";
      }

      return normalized.Substring(
        0,
        separatorIndex
      );
    }

    /// <summary>
    /// Normalizes one logical area path without interpreting provider-specific segments.
    /// </summary>
    private string NormalizeArea(
      string area
    ) {
      if (string.IsNullOrWhiteSpace(
            area
          )) {
        return "/";
      }

      string normalized =
        area.Trim();

      if (!normalized.StartsWith(
            "/",
            StringComparison.Ordinal
          )) {
        normalized =
          "/"
          + normalized;
      }

      if (normalized.Length > 1) {
        normalized =
          normalized.TrimEnd('/');
      }

      return normalized;
    }

    /// <summary>
    /// Resolves the local cache root before the base provider is constructed.
    /// </summary>
    private static string ResolveCacheFileSystemPath(
      string cacheFileSystemPath
    ) {
      if (!string.IsNullOrWhiteSpace(
            cacheFileSystemPath
          )) {
        return Path.GetFullPath(
          cacheFileSystemPath
        );
      }

      return Path.Combine(
        Path.GetTempPath(),
        _TemporaryCacheRootDirectoryName,
        Guid.NewGuid().ToString(
          "N"
        )
      );
    }

    /// <summary>
    /// Represents one persisted operation-granular cache value.
    /// </summary>
    private sealed class PersistentCacheEntry {

      private int _FormatVersion;
      private string _Operation;
      private string _Argument;
      private DateTime _CreatedUtc;
      private string _PayloadJson;

      /// <summary>
      /// Creates one empty persistent cache entry.
      /// </summary>
      public PersistentCacheEntry() {
        _Operation =
          string.Empty;

        _Argument =
          string.Empty;

        _PayloadJson =
          string.Empty;
      }

      /// <summary>
      /// Gets or sets the serialization format version.
      /// </summary>
      public int FormatVersion {
        get {
          return _FormatVersion;
        }
        set {
          _FormatVersion =
            value;
        }
      }

      /// <summary>
      /// Gets or sets the cached repository operation.
      /// </summary>
      public string Operation {
        get {
          return _Operation;
        }
        set {
          if (value == null) {
            _Operation =
              string.Empty;
          }
          else {
            _Operation =
              value;
          }
        }
      }

      /// <summary>
      /// Gets or sets the provider-neutral operation argument.
      /// </summary>
      public string Argument {
        get {
          return _Argument;
        }
        set {
          if (value == null) {
            _Argument =
              string.Empty;
          }
          else {
            _Argument =
              value;
          }
        }
      }

      /// <summary>
      /// Gets or sets the time at which the value was obtained from the source.
      /// </summary>
      public DateTime CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc =
            value;
        }
      }

      /// <summary>
      /// Gets or sets the JSON representation of the typed cache value.
      /// </summary>
      public string PayloadJson {
        get {
          return _PayloadJson;
        }
        set {
          if (value == null) {
            _PayloadJson =
              string.Empty;
          }
          else {
            _PayloadJson =
              value;
          }
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
      /// Gets or sets the source timestamp of the value.
      /// </summary>
      public DateTime CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc =
            value;
        }
      }

      /// <summary>
      /// Gets or sets the typed value.
      /// </summary>
      public object Value {
        get {
          return _Value;
        }
        set {
          _Value =
            value;
        }
      }
    }

    /// <summary>
    /// Serializable representation of one area capability set.
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

      /// <summary>
      /// Gets or sets the content level.
      /// </summary>
      public ContentLevel ContentLevel {
        get {
          return _ContentLevel;
        }
        set {
          _ContentLevel =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether the area exposes direct children.
      /// </summary>
      public bool SupportsSubAreas {
        get {
          return _SupportsSubAreas;
        }
        set {
          _SupportsSubAreas =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether rename is supported.
      /// </summary>
      public bool CanBeRenamed {
        get {
          return _CanBeRenamed;
        }
        set {
          _CanBeRenamed =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether deletion is supported.
      /// </summary>
      public bool CanBeDeleted {
        get {
          return _CanBeDeleted;
        }
        set {
          _CanBeDeleted =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether child creation is supported.
      /// </summary>
      public bool CanAddSubAreas {
        get {
          return _CanAddSubAreas;
        }
        set {
          _CanAddSubAreas =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether append is supported.
      /// </summary>
      public bool CanAppendContent {
        get {
          return _CanAppendContent;
        }
        set {
          _CanAppendContent =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether truncation is supported.
      /// </summary>
      public bool CanTruncate {
        get {
          return _CanTruncate;
        }
        set {
          _CanTruncate =
            value;
        }
      }

      /// <summary>
      /// Gets or sets whether binary resources are supported.
      /// </summary>
      public bool SupportsResources {
        get {
          return _SupportsResources;
        }
        set {
          _SupportsResources =
            value;
        }
      }
    }
  }
}
