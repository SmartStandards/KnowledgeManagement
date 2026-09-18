using KnowledgeManagement.SmartStandards.Providers;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using Newtonsoft.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KnowledgeManagement.SmartStandards.Wrappers {

  /// <summary>
  /// Wraps an arbitrary <see cref="IKnowledgeRepository"/> with a persistent file-system
  /// read cache.
  ///
  /// The wrapped repository remains authoritative. Reads are served from a local snapshot
  /// that is refreshed on demand according to the configured lifetime. If a refresh fails,
  /// the most recent complete local snapshot continues to be served.
  ///
  /// Mutations are never applied optimistically to the cache. They are executed against
  /// the wrapped repository first. After a successful remote mutation, the complete cache
  /// snapshot is downloaded again immediately. A mutation-side refresh failure is surfaced
  /// directly to the caller because the cache must not silently claim a state that can no
  /// longer be proven to match the authoritative source.
  ///
  /// The cache directory is owned exclusively by this wrapper. A successful refresh
  /// rebuilds the snapshot from scratch and removes every stale cache artifact that is no
  /// longer represented by the wrapped repository. This also happens during construction,
  /// so a persistent cache is validated and pruned after every process restart.
  ///
  /// The class derives from <see cref="FileBasedKnowledgeRepository"/> so it participates
  /// naturally in the existing provider hierarchy and uses one ordinary local repository
  /// directory as its persistent cache root. The public repository contract is overridden
  /// completely because the wrapper must preserve the logical paths, capabilities,
  /// resource identifiers and provider-neutral semantics of the wrapped source rather than
  /// expose the cache's physical layout.
  /// </summary>
  public class KnowledgeRepositoryCacheWrapper : FileBasedKnowledgeRepository {

    private const int _CacheFormatVersion = 1;
    private const string _CurrentDirectoryName = "current";
    private const string _ManifestFileName = "cache-manifest.json";
    private const string _ContentDirectoryName = "content";
    private const string _ResourceDirectoryName = "resources";
    private const string _TemporaryCacheRootDirectoryName = ".knowledge-repository-cache";

    private static readonly Encoding _Utf8WithoutBom =
      new UTF8Encoding(
        false
      );

    private readonly object _CacheSyncRoot;
    private readonly IKnowledgeRepository _WrappedSource;
    private readonly TimeSpan _Lifetime;
    private readonly string _CacheFileSystemPath;

    private CacheManifest _Manifest;
    private bool _HasSnapshot;
    private DateTime _LastRefreshAttemptUtc;

    /// <summary>
    /// Creates one persistent caching wrapper around an arbitrary knowledge repository.
    /// </summary>
    /// <param name="wrappedSource">
    /// The authoritative provider whose complete readable state is mirrored into the local
    /// file-system cache.
    /// </param>
    /// <param name="lifetimeMin">
    /// Minimum number of minutes between automatic read-side refresh attempts. A value of
    /// zero causes every read operation to attempt a refresh.
    /// </param>
    /// <param name="cacheFileSystemPath">
    /// Optional persistent cache directory. When omitted, a new process-local temporary
    /// directory is allocated. Supply an explicit path when the cache should survive
    /// process restarts.
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

      _Manifest =
        new CacheManifest();

      _HasSnapshot =
        false;

      _LastRefreshAttemptUtc =
        DateTime.MinValue;

      this.TryLoadSnapshotFromDisk();

      // Construction always validates the persistent cache against the authoritative
      // provider. A successful refresh also removes every stale file from older snapshots.
      try {
        this.RefreshCacheFromSource();
      }
      catch (Exception ex) {
        DevLogger.LogError(
          ex
        );

        if (!_HasSnapshot) {
          throw;
        }

        DevLogger.LogTrace(
          0,
          99999,
          "Knowledge repository cache startup refresh failed. "
          + "The last complete local snapshot remains active."
        );
      }
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
    /// Returns logical area paths from the current complete cache snapshot.
    /// </summary>
    public override string[] GetAreas(
      bool recurse,
      string startArea = "/"
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        CacheArea area =
          this.GetCachedArea(
            startArea
          );

        List<string> result =
          new List<string>();

        this.CollectCachedAreas(
          area,
          recurse,
          result
        );

        return result.ToArray();
      }
    }

    /// <summary>
    /// Searches the cached snapshot using the same deterministic path, name and direct
    /// content matching semantics used by the file-based provider.
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
        this.EnsureCacheForRead();

        CacheArea startAreaEntry =
          this.GetCachedArea(
            startArea
          );

        List<string> candidates =
          new List<string>();

        this.CollectCachedAreas(
          startAreaEntry,
          true,
          candidates
        );

        List<string> result =
          new List<string>();

        foreach (string areaPath in candidates) {
          CacheArea area =
            this.GetCachedArea(
              areaPath
            );

          if (area.Path.IndexOf(
                keyword,
                StringComparison.OrdinalIgnoreCase
              ) >= 0) {
            result.Add(
              area.Path
            );

            continue;
          }

          if (area.Name.IndexOf(
                keyword,
                StringComparison.OrdinalIgnoreCase
              ) >= 0) {
            result.Add(
              area.Path
            );

            continue;
          }

          if (area.ContentLevel ==
              ContentLevel.ContentContainer) {
            string directContent =
              this.ReadCachedText(
                area.DirectContentFile
              );

            if (directContent.IndexOf(
                  keyword,
                  StringComparison.OrdinalIgnoreCase
                ) >= 0) {
              result.Add(
                area.Path
              );
            }
          }
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the cached provider-neutral display name of one logical area.
    /// </summary>
    public override string GetAreaName(
      string area
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        return this.GetCachedArea(
          area
        ).Name;
      }
    }

    /// <summary>
    /// Returns the capabilities captured from the authoritative source during the most
    /// recent successful refresh.
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
        this.EnsureCacheForRead();

        CacheArea cachedArea =
          this.GetCachedArea(
            area
          );

        contentLevel =
          cachedArea.ContentLevel;

        supportsSubAreas =
          cachedArea.SupportsSubAreas;

        canBeRenamed =
          cachedArea.CanBeRenamed;

        canBeDeleted =
          cachedArea.CanBeDeleted;

        canAddSubAreas =
          cachedArea.CanAddSubAreas;

        canAppendContent =
          cachedArea.CanAppendContent;

        canTruncate =
          cachedArea.CanTruncate;

        supportsResources =
          cachedArea.SupportsResources;
      }
    }

    /// <summary>
    /// Returns resource metadata captured for the requested area.
    /// </summary>
    public override KnowledgeResourceInfo[] GetResources(
      string area
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        CacheArea cachedArea =
          this.GetCachedArea(
            area
          );

        KnowledgeResourceInfo[] result =
          new KnowledgeResourceInfo[
            cachedArea.ResourceIds.Length
          ];

        for (int index = 0;
             index < cachedArea.ResourceIds.Length;
             index++) {
          CacheResource resource =
            this.GetCachedResource(
              cachedArea.ResourceIds[index]
            );

          result[index] =
            this.CreateResourceInfo(
              resource
            );
        }

        return result;
      }
    }

    /// <summary>
    /// Returns cached binary content for one opaque resource identifier.
    /// </summary>
    public override byte[] GetResourceContent(
      string resourceId
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        CacheResource resource =
          this.GetCachedResource(
            resourceId
          );

        string physicalPath =
          this.ResolveCurrentSnapshotPath(
            resource.ContentFile
          );

        if (!File.Exists(
              physicalPath
            )) {
          throw new InvalidOperationException(
            "The cached knowledge resource content is missing."
          );
        }

        return File.ReadAllBytes(
          physicalPath
        );
      }
    }

    /// <summary>
    /// Adds one resource to the authoritative provider and downloads a fresh complete
    /// snapshot immediately after success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Replaces one authoritative resource and refreshes the complete cache after success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Deletes one authoritative resource and refreshes the complete cache after success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Returns whether cached direct textual content exists for the addressed area.
    /// </summary>
    public override bool HasDirectContent(
      string area
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        return this.GetCachedArea(
          area
        ).HasDirectContent;
      }
    }

    /// <summary>
    /// Returns cached direct textual content.
    /// </summary>
    public override string GetDirectContent(
      string area
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        CacheArea cachedArea =
          this.GetCachedArea(
            area
          );

        if (cachedArea.ContentLevel ==
            ContentLevel.ContentAggregation) {
          return string.Empty;
        }

        return this.ReadCachedText(
          cachedArea.DirectContentFile
        );
      }
    }

    /// <summary>
    /// Returns cached aggregated textual content.
    /// </summary>
    public override string GetAggregatedContent(
      string area
    ) {
      lock (_CacheSyncRoot) {
        this.EnsureCacheForRead();

        CacheArea cachedArea =
          this.GetCachedArea(
            area
          );

        return this.ReadCachedText(
          cachedArea.AggregatedContentFile
        );
      }
    }

    /// <summary>
    /// Deletes one area in the authoritative source and refreshes the complete cache after
    /// success.
    /// </summary>
    public override bool TryDelete(
      string area
    ) {
      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryDelete(
            area
          );

        if (!succeeded) {
          return false;
        }

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Renames one authoritative area and refreshes the complete cache after success.
    /// </summary>
    public override bool TryRename(
      string area,
      string newName,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      resourceIdChanges =
        Array.Empty<KnowledgeResourceIdChange>();

      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryRename(
            area,
            newName,
            out resourceIdChanges
          );

        if (!succeeded) {
          return false;
        }

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Creates one child area in the authoritative source and refreshes the complete cache
    /// after success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Appends content to the authoritative source and refreshes the complete cache after
    /// success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Truncates the authoritative source and refreshes the complete cache after success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Replaces content in the authoritative source and refreshes the complete cache after
    /// success.
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

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Moves one authoritative content scope and refreshes the complete cache after success.
    /// </summary>
    public override bool TryMoveContent(
      string contentAreaToMove,
      string newParentArea,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      resourceIdChanges =
        Array.Empty<KnowledgeResourceIdChange>();

      lock (_CacheSyncRoot) {
        bool succeeded =
          _WrappedSource.TryMoveContent(
            contentAreaToMove,
            newParentArea,
            out resourceIdChanges
          );

        if (!succeeded) {
          return false;
        }

        this.RefreshCacheAfterWrite();
        return true;
      }
    }

    /// <summary>
    /// Ensures a sufficiently fresh complete snapshot for one read operation.
    ///
    /// Refresh failures are intentionally swallowed only when a previous complete snapshot
    /// exists. This is the defining offline-read behavior of the wrapper.
    /// </summary>
    private void EnsureCacheForRead() {
      DateTime now =
        DateTime.UtcNow;

      if (_HasSnapshot &&
          _Lifetime > TimeSpan.Zero &&
          now - _LastRefreshAttemptUtc < _Lifetime) {
        return;
      }

      try {
        this.RefreshCacheFromSource();
      }
      catch (Exception ex) {
        DevLogger.LogError(
          ex
        );

        if (!_HasSnapshot) {
          throw new InvalidOperationException(
            "The knowledge repository cache could not be refreshed and no complete local snapshot is available.",
            ex
          );
        }

        DevLogger.LogTrace(
          0,
          99999,
          "Knowledge repository cache refresh failed during read access. "
          + "The last complete local snapshot is served."
        );
      }
    }

    /// <summary>
    /// Refreshes the complete snapshot after one successful authoritative mutation.
    ///
    /// Unlike read-side refreshes, failures are never hidden. The caller must observe that
    /// the cache could not be brought back into a proven synchronized state.
    /// </summary>
    private void RefreshCacheAfterWrite() {
      this.RefreshCacheFromSource();
    }

    /// <summary>
    /// Downloads one complete provider-neutral snapshot into a staging directory and
    /// atomically publishes it as the current local cache.
    /// </summary>
    private void RefreshCacheFromSource() {
      _LastRefreshAttemptUtc =
        DateTime.UtcNow;

      string stagingDirectory =
        Path.Combine(
          _CacheFileSystemPath,
          ".refresh-"
          + Guid.NewGuid().ToString(
            "N"
          )
        );

      Directory.CreateDirectory(
        stagingDirectory
      );

      try {
        CacheManifest manifest =
          this.BuildSnapshot(
            stagingDirectory
          );

        manifest.FormatVersion =
          _CacheFormatVersion;

        manifest.LastSuccessfulRefreshUtc =
          DateTime.UtcNow;

        string manifestPath =
          Path.Combine(
            stagingDirectory,
            _ManifestFileName
          );

        string manifestJson =
          JsonConvert.SerializeObject(
            manifest,
            Formatting.Indented
          );

        File.WriteAllText(
          manifestPath,
          manifestJson,
          _Utf8WithoutBom
        );

        this.PublishSnapshot(
          stagingDirectory
        );

        _Manifest =
          manifest;

        _HasSnapshot =
          true;

        _LastRefreshAttemptUtc =
          manifest.LastSuccessfulRefreshUtc;
      }
      catch {
        this.TryDeleteDirectory(
          stagingDirectory
        );

        throw;
      }
    }

    /// <summary>
    /// Builds one complete cache snapshot without modifying the currently published cache.
    /// </summary>
    private CacheManifest BuildSnapshot(
      string stagingDirectory
    ) {
      string contentDirectory =
        Path.Combine(
          stagingDirectory,
          _ContentDirectoryName
        );

      string resourceDirectory =
        Path.Combine(
          stagingDirectory,
          _ResourceDirectoryName
        );

      Directory.CreateDirectory(
        contentDirectory
      );

      Directory.CreateDirectory(
        resourceDirectory
      );

      List<string> areaPaths =
        new List<string>();

      areaPaths.Add(
        "/"
      );

      string[] descendantAreas =
        _WrappedSource.GetAreas(
          true,
          "/"
        );

      foreach (string descendantArea in descendantAreas) {
        areaPaths.Add(
          descendantArea
        );
      }

      List<CacheArea> areas =
        new List<CacheArea>();

      Dictionary<string, CacheResource> resources =
        new Dictionary<string, CacheResource>(
          StringComparer.Ordinal
        );

      foreach (string areaPath in areaPaths) {
        CacheArea cachedArea =
          this.DownloadArea(
            areaPath,
            contentDirectory,
            resourceDirectory,
            resources
          );

        areas.Add(
          cachedArea
        );
      }

      CacheManifest manifest =
        new CacheManifest();

      manifest.Areas =
        areas.ToArray();

      CacheResource[] resourceArray =
        new CacheResource[
          resources.Count
        ];

      int resourceIndex =
        0;

      foreach (KeyValuePair<string, CacheResource> resourcePair in resources) {
        resourceArray[resourceIndex] =
          resourcePair.Value;

        resourceIndex++;
      }

      manifest.Resources =
        resourceArray;

      return manifest;
    }

    /// <summary>
    /// Downloads one logical area's metadata, content, direct children and resources.
    /// </summary>
    private CacheArea DownloadArea(
      string areaPath,
      string contentDirectory,
      string resourceDirectory,
      Dictionary<string, CacheResource> resources
    ) {
      CacheArea cachedArea =
        new CacheArea();

      cachedArea.Path =
        areaPath;

      cachedArea.Name =
        _WrappedSource.GetAreaName(
          areaPath
        );

      ContentLevel contentLevel;
      bool supportsSubAreas;
      bool canBeRenamed;
      bool canBeDeleted;
      bool canAddSubAreas;
      bool canAppendContent;
      bool canTruncate;
      bool supportsResources;

      _WrappedSource.GetAreaCapabilities(
        areaPath,
        out contentLevel,
        out supportsSubAreas,
        out canBeRenamed,
        out canBeDeleted,
        out canAddSubAreas,
        out canAppendContent,
        out canTruncate,
        out supportsResources
      );

      cachedArea.ContentLevel =
        contentLevel;

      cachedArea.SupportsSubAreas =
        supportsSubAreas;

      cachedArea.CanBeRenamed =
        canBeRenamed;

      cachedArea.CanBeDeleted =
        canBeDeleted;

      cachedArea.CanAddSubAreas =
        canAddSubAreas;

      cachedArea.CanAppendContent =
        canAppendContent;

      cachedArea.CanTruncate =
        canTruncate;

      cachedArea.SupportsResources =
        supportsResources;

      cachedArea.DirectChildren =
        _WrappedSource.GetAreas(
          false,
          areaPath
        );

      string areaToken =
        this.CreateStableToken(
          areaPath
        );

      if (contentLevel ==
          ContentLevel.ContentContainer) {
        cachedArea.HasDirectContent =
          _WrappedSource.HasDirectContent(
            areaPath
          );

        string directContent =
          _WrappedSource.GetDirectContent(
            areaPath
          );

        cachedArea.DirectContentFile =
          _ContentDirectoryName
          + "/"
          + areaToken
          + ".direct.md";

        File.WriteAllText(
          Path.Combine(
            contentDirectory,
            areaToken
            + ".direct.md"
          ),
          directContent,
          _Utf8WithoutBom
        );
      }
      else {
        cachedArea.HasDirectContent =
          false;

        cachedArea.DirectContentFile =
          string.Empty;
      }

      if (contentLevel !=
          ContentLevel.BeyondContent) {
        string aggregatedContent =
          _WrappedSource.GetAggregatedContent(
            areaPath
          );

        cachedArea.AggregatedContentFile =
          _ContentDirectoryName
          + "/"
          + areaToken
          + ".aggregated.md";

        File.WriteAllText(
          Path.Combine(
            contentDirectory,
            areaToken
            + ".aggregated.md"
          ),
          aggregatedContent,
          _Utf8WithoutBom
        );
      }
      else {
        cachedArea.AggregatedContentFile =
          string.Empty;
      }

      if (supportsResources) {
        KnowledgeResourceInfo[] sourceResources =
          _WrappedSource.GetResources(
            areaPath
          );

        string[] resourceIds =
          new string[
            sourceResources.Length
          ];

        for (int index = 0;
             index < sourceResources.Length;
             index++) {
          KnowledgeResourceInfo sourceResource =
            sourceResources[index];

          resourceIds[index] =
            sourceResource.ResourceId;

          if (!resources.ContainsKey(
                sourceResource.ResourceId
              )) {
            CacheResource cacheResource =
              this.DownloadResource(
                sourceResource,
                resourceDirectory
              );

            resources.Add(
              sourceResource.ResourceId,
              cacheResource
            );
          }
        }

        cachedArea.ResourceIds =
          resourceIds;
      }
      else {
        cachedArea.ResourceIds =
          Array.Empty<string>();
      }

      return cachedArea;
    }

    /// <summary>
    /// Downloads one unique opaque resource into the staging snapshot.
    /// </summary>
    private CacheResource DownloadResource(
      KnowledgeResourceInfo sourceResource,
      string resourceDirectory
    ) {
      byte[] content =
        _WrappedSource.GetResourceContent(
          sourceResource.ResourceId
        );

      string resourceToken =
        this.CreateStableToken(
          sourceResource.ResourceId
        );

      string fileName =
        resourceToken
        + ".bin";

      File.WriteAllBytes(
        Path.Combine(
          resourceDirectory,
          fileName
        ),
        content
      );

      CacheResource resource =
        new CacheResource();

      resource.ResourceId =
        sourceResource.ResourceId;

      resource.FileName =
        sourceResource.FileName;

      if (resource.FileName == null) {
        resource.FileName =
          string.Empty;
      }

      resource.ContentType =
        sourceResource.ContentType;

      if (resource.ContentType == null) {
        resource.ContentType =
          string.Empty;
      }

      resource.Length =
        content.LongLength;

      resource.ContentFile =
        _ResourceDirectoryName
        + "/"
        + fileName;

      return resource;
    }

    /// <summary>
    /// Publishes one complete staging snapshot while preserving the previous snapshot until
    /// the new one is ready.
    /// </summary>
    private void PublishSnapshot(
      string stagingDirectory
    ) {
      string currentDirectory =
        this.GetCurrentSnapshotDirectory();

      string backupDirectory =
        Path.Combine(
          _CacheFileSystemPath,
          ".previous-"
          + Guid.NewGuid().ToString(
            "N"
          )
        );

      bool hadPreviousSnapshot =
        Directory.Exists(
          currentDirectory
        );

      if (hadPreviousSnapshot) {
        Directory.Move(
          currentDirectory,
          backupDirectory
        );
      }

      try {
        Directory.Move(
          stagingDirectory,
          currentDirectory
        );
      }
      catch {
        if (hadPreviousSnapshot &&
            Directory.Exists(
              backupDirectory
            ) &&
            !Directory.Exists(
              currentDirectory
            )) {
          Directory.Move(
            backupDirectory,
            currentDirectory
          );
        }

        throw;
      }

      if (Directory.Exists(
            backupDirectory
          )) {
        this.TryDeleteDirectory(
          backupDirectory
        );
      }

      this.DeleteObsoleteRootEntries();
    }

    /// <summary>
    /// Removes stale files and abandoned refresh directories from the cache root after a
    /// successful authoritative refresh.
    /// </summary>
    private void DeleteObsoleteRootEntries() {
      string currentDirectory =
        this.GetCurrentSnapshotDirectory();

      string[] directories =
        Directory.GetDirectories(
          _CacheFileSystemPath
        );

      foreach (string directory in directories) {
        if (string.Equals(
              Path.GetFullPath(
                directory
              ),
              Path.GetFullPath(
                currentDirectory
              ),
              StringComparison.OrdinalIgnoreCase
            )) {
          continue;
        }

        this.TryDeleteDirectory(
          directory
        );
      }

      string[] files =
        Directory.GetFiles(
          _CacheFileSystemPath
        );

      foreach (string file in files) {
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
    }

    /// <summary>
    /// Attempts to load the last complete persistent snapshot before contacting the source.
    /// </summary>
    private void TryLoadSnapshotFromDisk() {
      string manifestPath =
        Path.Combine(
          this.GetCurrentSnapshotDirectory(),
          _ManifestFileName
        );

      if (!File.Exists(
            manifestPath
          )) {
        return;
      }

      try {
        string json =
          File.ReadAllText(
            manifestPath,
            _Utf8WithoutBom
          );

        CacheManifest manifest =
          JsonConvert.DeserializeObject<CacheManifest>(
            json
          );

        if (manifest == null ||
            manifest.FormatVersion !=
            _CacheFormatVersion) {
          return;
        }

        this.ValidateManifest(
          manifest
        );

        _Manifest =
          manifest;

        _HasSnapshot =
          true;

        _LastRefreshAttemptUtc =
          DateTime.MinValue;
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
      catch (JsonException ex) {
        DevLogger.LogError(
          ex
        );
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(
          ex
        );
      }
    }

    /// <summary>
    /// Validates that every file referenced by a persisted manifest still exists locally.
    /// </summary>
    private void ValidateManifest(
      CacheManifest manifest
    ) {
      foreach (CacheArea area in manifest.Areas) {
        if (!string.IsNullOrEmpty(
              area.DirectContentFile
            )) {
          this.ValidateSnapshotFileExists(
            area.DirectContentFile
          );
        }

        if (!string.IsNullOrEmpty(
              area.AggregatedContentFile
            )) {
          this.ValidateSnapshotFileExists(
            area.AggregatedContentFile
          );
        }
      }

      foreach (CacheResource resource in manifest.Resources) {
        this.ValidateSnapshotFileExists(
          resource.ContentFile
        );
      }
    }

    /// <summary>
    /// Validates one manifest-relative snapshot file.
    /// </summary>
    private void ValidateSnapshotFileExists(
      string relativePath
    ) {
      string physicalPath =
        this.ResolveCurrentSnapshotPath(
          relativePath
        );

      if (!File.Exists(
            physicalPath
          )) {
        throw new InvalidOperationException(
          "The persisted knowledge repository cache snapshot is incomplete."
        );
      }
    }

    /// <summary>
    /// Returns one cached area or throws when the requested logical path does not exist in
    /// the current snapshot.
    /// </summary>
    private CacheArea GetCachedArea(
      string area
    ) {
      foreach (CacheArea cachedArea in _Manifest.Areas) {
        if (string.Equals(
              cachedArea.Path,
              area,
              StringComparison.Ordinal
            )) {
          return cachedArea;
        }
      }

      throw new InvalidOperationException(
        "The requested knowledge area does not exist in the cached snapshot."
      );
    }

    /// <summary>
    /// Returns one cached resource by its opaque identifier.
    /// </summary>
    private CacheResource GetCachedResource(
      string resourceId
    ) {
      foreach (CacheResource resource in _Manifest.Resources) {
        if (string.Equals(
              resource.ResourceId,
              resourceId,
              StringComparison.Ordinal
            )) {
          return resource;
        }
      }

      throw new InvalidOperationException(
        "The requested knowledge resource does not exist in the cached snapshot."
      );
    }

    /// <summary>
    /// Recursively enumerates cached child areas in stable source order.
    /// </summary>
    private void CollectCachedAreas(
      CacheArea startArea,
      bool recurse,
      List<string> result
    ) {
      foreach (string childPath in startArea.DirectChildren) {
        result.Add(
          childPath
        );

        if (recurse) {
          CacheArea childArea =
            this.GetCachedArea(
              childPath
            );

          this.CollectCachedAreas(
            childArea,
            true,
            result
          );
        }
      }
    }

    /// <summary>
    /// Reads one optional cached UTF-8 text file.
    /// </summary>
    private string ReadCachedText(
      string relativePath
    ) {
      if (string.IsNullOrEmpty(
            relativePath
          )) {
        return string.Empty;
      }

      string physicalPath =
        this.ResolveCurrentSnapshotPath(
          relativePath
        );

      if (!File.Exists(
            physicalPath
          )) {
        throw new InvalidOperationException(
          "The cached knowledge content file is missing."
        );
      }

      return File.ReadAllText(
        physicalPath,
        _Utf8WithoutBom
      );
    }

    /// <summary>
    /// Creates a detached public resource metadata object from one cached resource entry.
    /// </summary>
    private KnowledgeResourceInfo CreateResourceInfo(
      CacheResource resource
    ) {
      KnowledgeResourceInfo result =
        new KnowledgeResourceInfo();

      result.ResourceId =
        resource.ResourceId;

      result.FileName =
        resource.FileName;

      result.ContentType =
        resource.ContentType;

      result.Length =
        resource.Length;

      return result;
    }

    /// <summary>
    /// Resolves one manifest-relative file inside the published snapshot and prevents path
    /// traversal even if a persisted manifest has been tampered with.
    /// </summary>
    private string ResolveCurrentSnapshotPath(
      string relativePath
    ) {
      string currentDirectory =
        Path.GetFullPath(
          this.GetCurrentSnapshotDirectory()
        );

      string physicalPath =
        Path.GetFullPath(
          Path.Combine(
            currentDirectory,
            relativePath.Replace(
              '/',
              Path.DirectorySeparatorChar
            )
          )
        );

      string currentPrefix =
        currentDirectory.TrimEnd(
          Path.DirectorySeparatorChar,
          Path.AltDirectorySeparatorChar
        )
        + Path.DirectorySeparatorChar;

      if (!physicalPath.StartsWith(
            currentPrefix,
            StringComparison.OrdinalIgnoreCase
          )) {
        throw new InvalidOperationException(
          "The persisted cache manifest contains a path outside the current snapshot."
        );
      }

      return physicalPath;
    }

    /// <summary>
    /// Gets the directory containing the currently published complete snapshot.
    /// </summary>
    private string GetCurrentSnapshotDirectory() {
      return Path.Combine(
        _CacheFileSystemPath,
        _CurrentDirectoryName
      );
    }

    /// <summary>
    /// Creates a deterministic file-system-safe token from an opaque logical identifier.
    /// </summary>
    private string CreateStableToken(
      string value
    ) {
      byte[] hash =
        SHA256.HashData(
          Encoding.UTF8.GetBytes(
            value
          )
        );

      StringBuilder builder =
        new StringBuilder(
          hash.Length * 2
        );

      foreach (byte current in hash) {
        builder.Append(
          current.ToString(
            "x2",
            CultureInfo.InvariantCulture
          )
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Deletes one cache directory using best-effort cleanup semantics.
    /// </summary>
    private void TryDeleteDirectory(
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
    /// Describes one complete persisted cache snapshot.
    /// </summary>
    private sealed class CacheManifest {

      private int _FormatVersion;
      private DateTime _LastSuccessfulRefreshUtc;
      private CacheArea[] _Areas;
      private CacheResource[] _Resources;

      /// <summary>
      /// Creates an empty manifest.
      /// </summary>
      public CacheManifest() {
        _FormatVersion =
          _CacheFormatVersion;

        _LastSuccessfulRefreshUtc =
          DateTime.MinValue;

        _Areas =
          Array.Empty<CacheArea>();

        _Resources =
          Array.Empty<CacheResource>();
      }

      /// <summary>
      /// Gets or sets the cache serialization format version.
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
      /// Gets or sets the timestamp of the last complete successful source refresh.
      /// </summary>
      public DateTime LastSuccessfulRefreshUtc {
        get {
          return _LastSuccessfulRefreshUtc;
        }
        set {
          _LastSuccessfulRefreshUtc =
            value;
        }
      }

      /// <summary>
      /// Gets or sets all cached logical areas.
      /// </summary>
      public CacheArea[] Areas {
        get {
          return _Areas;
        }
        set {
          if (value == null) {
            _Areas =
              Array.Empty<CacheArea>();
          }
          else {
            _Areas =
              value;
          }
        }
      }

      /// <summary>
      /// Gets or sets all unique cached resources.
      /// </summary>
      public CacheResource[] Resources {
        get {
          return _Resources;
        }
        set {
          if (value == null) {
            _Resources =
              Array.Empty<CacheResource>();
          }
          else {
            _Resources =
              value;
          }
        }
      }
    }

    /// <summary>
    /// Describes one logical area inside the persisted cache snapshot.
    /// </summary>
    private sealed class CacheArea {

      private string _Path;
      private string _Name;
      private ContentLevel _ContentLevel;
      private bool _SupportsSubAreas;
      private bool _CanBeRenamed;
      private bool _CanBeDeleted;
      private bool _CanAddSubAreas;
      private bool _CanAppendContent;
      private bool _CanTruncate;
      private bool _SupportsResources;
      private bool _HasDirectContent;
      private string _DirectContentFile;
      private string _AggregatedContentFile;
      private string[] _DirectChildren;
      private string[] _ResourceIds;

      /// <summary>
      /// Creates one empty cache area entry.
      /// </summary>
      public CacheArea() {
        _Path =
          string.Empty;

        _Name =
          string.Empty;

        _DirectContentFile =
          string.Empty;

        _AggregatedContentFile =
          string.Empty;

        _DirectChildren =
          Array.Empty<string>();

        _ResourceIds =
          Array.Empty<string>();
      }

      public string Path {
        get {
          return _Path;
        }
        set {
          if (value == null) {
            _Path =
              string.Empty;
          }
          else {
            _Path =
              value;
          }
        }
      }

      public string Name {
        get {
          return _Name;
        }
        set {
          if (value == null) {
            _Name =
              string.Empty;
          }
          else {
            _Name =
              value;
          }
        }
      }

      public ContentLevel ContentLevel {
        get {
          return _ContentLevel;
        }
        set {
          _ContentLevel =
            value;
        }
      }

      public bool SupportsSubAreas {
        get {
          return _SupportsSubAreas;
        }
        set {
          _SupportsSubAreas =
            value;
        }
      }

      public bool CanBeRenamed {
        get {
          return _CanBeRenamed;
        }
        set {
          _CanBeRenamed =
            value;
        }
      }

      public bool CanBeDeleted {
        get {
          return _CanBeDeleted;
        }
        set {
          _CanBeDeleted =
            value;
        }
      }

      public bool CanAddSubAreas {
        get {
          return _CanAddSubAreas;
        }
        set {
          _CanAddSubAreas =
            value;
        }
      }

      public bool CanAppendContent {
        get {
          return _CanAppendContent;
        }
        set {
          _CanAppendContent =
            value;
        }
      }

      public bool CanTruncate {
        get {
          return _CanTruncate;
        }
        set {
          _CanTruncate =
            value;
        }
      }

      public bool SupportsResources {
        get {
          return _SupportsResources;
        }
        set {
          _SupportsResources =
            value;
        }
      }

      public bool HasDirectContent {
        get {
          return _HasDirectContent;
        }
        set {
          _HasDirectContent =
            value;
        }
      }

      public string DirectContentFile {
        get {
          return _DirectContentFile;
        }
        set {
          if (value == null) {
            _DirectContentFile =
              string.Empty;
          }
          else {
            _DirectContentFile =
              value;
          }
        }
      }

      public string AggregatedContentFile {
        get {
          return _AggregatedContentFile;
        }
        set {
          if (value == null) {
            _AggregatedContentFile =
              string.Empty;
          }
          else {
            _AggregatedContentFile =
              value;
          }
        }
      }

      public string[] DirectChildren {
        get {
          return _DirectChildren;
        }
        set {
          if (value == null) {
            _DirectChildren =
              Array.Empty<string>();
          }
          else {
            _DirectChildren =
              value;
          }
        }
      }

      public string[] ResourceIds {
        get {
          return _ResourceIds;
        }
        set {
          if (value == null) {
            _ResourceIds =
              Array.Empty<string>();
          }
          else {
            _ResourceIds =
              value;
          }
        }
      }
    }

    /// <summary>
    /// Describes one unique opaque resource inside the persisted cache snapshot.
    /// </summary>
    private sealed class CacheResource {

      private string _ResourceId;
      private string _FileName;
      private string _ContentType;
      private long _Length;
      private string _ContentFile;

      /// <summary>
      /// Creates one empty cache resource entry.
      /// </summary>
      public CacheResource() {
        _ResourceId =
          string.Empty;

        _FileName =
          string.Empty;

        _ContentType =
          string.Empty;

        _ContentFile =
          string.Empty;
      }

      public string ResourceId {
        get {
          return _ResourceId;
        }
        set {
          if (value == null) {
            _ResourceId =
              string.Empty;
          }
          else {
            _ResourceId =
              value;
          }
        }
      }

      public string FileName {
        get {
          return _FileName;
        }
        set {
          if (value == null) {
            _FileName =
              string.Empty;
          }
          else {
            _FileName =
              value;
          }
        }
      }

      public string ContentType {
        get {
          return _ContentType;
        }
        set {
          if (value == null) {
            _ContentType =
              string.Empty;
          }
          else {
            _ContentType =
              value;
          }
        }
      }

      public long Length {
        get {
          return _Length;
        }
        set {
          _Length =
            value;
        }
      }

      public string ContentFile {
        get {
          return _ContentFile;
        }
        set {
          if (value == null) {
            _ContentFile =
              string.Empty;
          }
          else {
            _ContentFile =
              value;
          }
        }
      }
    }
  }
}
