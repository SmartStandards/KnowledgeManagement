using KnowledgeManagement.SmartStandards;
using Logging.SmartStandards;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeManagement.SmartStandards.Wrappers {

  /// <summary>
  /// Aggregates an arbitrary number of <see cref="IKnowledgeRepository"/> implementations
  /// into one ordered logical knowledge repository.
  /// 
  /// Every repository is added at an absolute logical mount point. Repositories may be
  /// mounted side by side, below synthetic parent paths, or directly on top of already
  /// mounted repositories.
  /// 
  /// The aggregated repository exposes the union of all mounted area trees. When several
  /// repositories expose the same resulting global area path, that path is represented
  /// exactly once and behaves as an overlay for read operations.
  /// 
  /// Read operations combine all matching mounted providers in mount-registration order.
  /// Existing area ordering from every mounted repository is preserved as far as possible:
  /// the first appearance of a logical child fixes its position, while later overlays of
  /// the same child merge into that existing position.
  /// 
  /// Synthetic mount ancestors are created automatically. For example, mounting a
  /// repository at "/Company/Engineering" implicitly exposes "/Company" even when no
  /// provider directly owns that area.
  /// 
  /// Mutation operations deliberately use conservative routing. A mutation is forwarded
  /// only when exactly one mounted repository unambiguously owns the addressed target and
  /// reports the required capability. The implementation never guesses which provider
  /// should be modified when several repositories overlap the same logical area.
  /// 
  /// <see cref="TryMoveContent(string, string)"/> is supported only when the content area
  /// to move and its new parent resolve uniquely to the same concrete repository instance.
  /// Cross-repository moves are
  /// intentionally rejected because the <see cref="IKnowledgeRepository"/> contract
  /// requires atomic mutations and arbitrary repository implementations cannot provide a
  /// shared distributed transaction.
  /// </summary>
  public class AggregatedKnowledgeRepository : IKnowledgeRepository, IKnowledgeRepositoryCacheControl {

    private const string _RootArea = "/";
    private const string _KnowledgeResourceReferencePrefix = "knowledge-resource:";
    private const string _KnowledgeAreaReferencePrefix = "knowledge-area:";

    private static readonly TimeSpan _TreeReadBurstWindow =
      TimeSpan.FromMilliseconds(500);

    private static readonly Regex _KnowledgeResourceReferenceRegex = new Regex(
      @"knowledge-resource:(?<id>[A-Za-z0-9._~-]+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _KnowledgeAreaReferenceRegex = new Regex(
      "knowledge-area:(?<area>[^\\s\\)\\]\\>\\\"']+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private readonly object _SyncRoot;
    private readonly List<MountedRepository> _Repositories;
    private AggregatedTree _CachedTree;
    private DateTime _CachedTreeLastAccessUtc;

    /// <summary>
    /// Creates an empty aggregated knowledge repository.
    /// </summary>
    public AggregatedKnowledgeRepository() {
      _SyncRoot = new object();
      _Repositories = new List<MountedRepository>();
      _CachedTree = null;
      _CachedTreeLastAccessUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Returns whether the requested aggregated area is already available from at least one
    /// directly contributing local cache without accessing any mounted source repository.
    ///
    /// This method is intentionally passive. It never materializes the aggregate tree,
    /// enumerates provider children or calls any normal repository read operation.
    ///
    /// Synthetic mount ancestors are considered locally available because their structure
    /// is fully known from the aggregator's mount registrations. When no contributing
    /// repository exposes cache inspection at all, the area is also treated as available so
    /// consumers do not incorrectly render remote or non-cache repositories as cache misses.
    /// </summary>
    public bool IsAreaCached(
      string area
    ) {
      string normalizedArea =
        this.NormalizeAreaPath(
          area
        );

      MountedRepository[] mountedRepositories;

      lock (_SyncRoot) {
        mountedRepositories =
          _Repositories.ToArray();
      }

      bool hasExactContribution =
        false;

      bool hasCacheInspector =
        false;

      bool hasSuccessfulInspectorResult =
        false;

      bool hasSyntheticMountDescendant =
        false;

      foreach (MountedRepository mountedRepository in mountedRepositories) {
        if (!string.Equals(
              normalizedArea,
              mountedRepository.MountPoint,
              StringComparison.Ordinal
            ) &&
            this.IsSameOrDescendant(
              mountedRepository.MountPoint,
              normalizedArea
            )) {
          hasSyntheticMountDescendant =
            true;
        }

        string localArea;

        if (!this.TryTranslateGlobalAreaToMountedLocalArea(
              mountedRepository,
              normalizedArea,
              out localArea
            )) {
          continue;
        }

        hasExactContribution =
          true;

        IKnowledgeRepositoryCacheControl cacheControl =
          mountedRepository.Repository as IKnowledgeRepositoryCacheControl;

        if (cacheControl == null) {
          continue;
        }

        hasCacheInspector =
          true;

        try {
          bool isCached =
            cacheControl.IsAreaCached(
              localArea
            );

          hasSuccessfulInspectorResult =
            true;

          if (isCached) {
            return true;
          }
        }
        catch (Exception ex) {
          DevLogger.LogError(
            ex
          );

          DevLogger.LogTrace(
            0,
            99999,
            "Aggregated knowledge cache inspection failed for mount '"
            + mountedRepository.MountPoint
            + "' and local area '"
            + localArea
            + "'. The cache state is treated as unknown."
          );
        }
      }

      if (!hasExactContribution &&
          hasSyntheticMountDescendant) {
        return true;
      }

      if (!hasCacheInspector) {
        return true;
      }

      if (!hasSuccessfulInspectorResult) {
        return true;
      }

      return false;
    }

    /// <summary>
    /// Opens a prefer-existing cache scope on every unique mounted repository that exposes
    /// the optional local cache-control capability.
    ///
    /// The aggregator itself does not cache repository content. It only propagates the
    /// consumer's local cache policy through aggregation layers. Nested aggregated
    /// repositories therefore propagate the scope recursively without introducing any
    /// transport-level cache contract.
    /// </summary>
    public IDisposable BeginPreferExistingScope() {
      MountedRepository[] mountedRepositories;

      lock (_SyncRoot) {
        mountedRepositories =
          _Repositories.ToArray();
      }

      List<IDisposable> openedScopes =
        new List<IDisposable>();

      List<IKnowledgeRepository> scopedRepositories =
        new List<IKnowledgeRepository>();

      foreach (MountedRepository mountedRepository in mountedRepositories) {
        bool alreadyScoped =
          scopedRepositories.Any(
            (IKnowledgeRepository repository) => object.ReferenceEquals(
              repository,
              mountedRepository.Repository
            )
          );

        if (alreadyScoped) {
          continue;
        }

        scopedRepositories.Add(
          mountedRepository.Repository
        );

        IKnowledgeRepositoryCacheControl cacheControl =
          mountedRepository.Repository as IKnowledgeRepositoryCacheControl;

        if (cacheControl == null) {
          continue;
        }

        try {
          IDisposable childScope =
            cacheControl.BeginPreferExistingScope();

          if (childScope != null) {
            openedScopes.Add(
              childScope
            );
          }
        }
        catch (Exception ex) {
          DevLogger.LogError(
            ex
          );

          DevLogger.LogTrace(
            0,
            99999,
            "Aggregated knowledge cache scope could not be opened for mount '"
            + mountedRepository.MountPoint
            + "'. Other cache-capable repositories remain active."
          );
        }
      }

      return new CompositeCacheReadScope(
        openedScopes.ToArray()
      );
    }

    /// <summary>
    /// Adds a knowledge repository at the specified logical mount point.
    /// 
    /// The mount point is an absolute logical area path. "/" mounts the repository
    /// directly into the aggregated root. A deeper path such as "/Projects/Foo" exposes
    /// the mounted repository below that path and creates missing ancestor areas
    /// virtually.
    /// 
    /// Multiple repositories may use the same mount point. Their visible area trees then
    /// overlap and are merged for read operations.
    /// 
    /// Registration order is significant. It contributes to deterministic area ordering
    /// and to deterministic read aggregation when several providers expose the same
    /// logical area.
    /// 
    /// Adding a repository does not transfer lifetime ownership. This class does not
    /// dispose mounted repositories.
    /// </summary>
    /// <param name="repository">The knowledge repository to mount.</param>
    /// <param name="mountPoint">
    /// The absolute logical mount point. "/" mounts directly into the aggregated root.
    /// </param>
    public void Add(IKnowledgeRepository repository, string mountPoint = "/") {
      if (repository == null) {
        throw new ArgumentNullException(nameof(repository));
      }

      string normalizedMountPoint = this.NormalizeAreaPath(mountPoint);

      lock (_SyncRoot) {
        int mountPointOrdinal = _Repositories.Count(
          (MountedRepository candidate) => string.Equals(
            candidate.MountPoint,
            normalizedMountPoint,
            StringComparison.Ordinal
          )
        );

        MountedRepository mountedRepository = new MountedRepository(
          repository,
          normalizedMountPoint,
          mountPointOrdinal,
          _Repositories.Count
        );

        _Repositories.Add(
          mountedRepository
        );

        this.InvalidateTreeCache();
      }
    }

    /// <summary>
    /// Returns the visible logical areas below the specified global start area.
    /// 
    /// The result is the ordered union of all mounted repositories plus automatically
    /// synthesized mount ancestors.
    /// 
    /// If several providers expose the same global area, that area appears only once.
    /// Its position is determined by the first registered provider or synthetic mount
    /// structure that exposes it. Later providers overlay content and capabilities onto
    /// the same logical path without moving it.
    /// 
    /// Recursive enumeration uses pre-order traversal.
    /// </summary>
    /// <param name="recurse">
    /// true to include all descendants recursively; false to return direct children only.
    /// </param>
    /// <param name="startArea">
    /// The absolute aggregated area path at which enumeration starts.
    /// </param>
    /// <returns>The ordered global logical area paths.</returns>
    public string[] GetAreas(bool recurse, string startArea = "/") {
      lock (_SyncRoot) {
        AggregatedTree tree = this.BuildTree();
        string normalizedStartArea = this.NormalizeAreaPath(startArea);

        AggregatedNode startNode =
          this.EnsureAreaPathMaterialized(
            tree,
            normalizedStartArea
          );

        if (startNode == null) {
          return Array.Empty<string>();
        }

        this.EnsureDirectChildrenMaterialized(
          tree,
          startNode
        );

        if (!recurse) {
          return startNode.Children
            .Select((AggregatedNode child) => child.Path)
            .ToArray();
        }

        // Recursive enumeration remains available as an explicit bulk operation, but it
        // is deliberately implemented as iterative one-level traversal. No child provider
        // ever receives GetAreas(true, ...), preventing one recursive provider request from
        // materializing an arbitrarily large tree or overflowing its own call stack.
        this.EnsureSubtreeMaterialized(
          tree,
          startNode
        );

        List<string> result =
          new List<string>();

        Stack<AggregatedNode> pending =
          new Stack<AggregatedNode>();

        for (int index = startNode.Children.Count - 1;
             index >= 0;
             index--) {
          pending.Push(
            startNode.Children[index]
          );
        }

        while (pending.Count > 0) {
          AggregatedNode current =
            pending.Pop();

          result.Add(
            current.Path
          );

          for (int index = current.Children.Count - 1;
               index >= 0;
               index--) {
            pending.Push(
              current.Children[index]
            );
          }
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the direct logical display name exposed by the aggregated tree.
    /// </summary>
    public string GetAreaName(
      string area
    ) {
      lock (_SyncRoot) {
        AggregatedTree tree = this.BuildTree();
        string normalizedArea =
          this.NormalizeAreaPath(
            area
          );

        AggregatedNode node =
          this.EnsureAreaPathMaterialized(
            tree,
            normalizedArea
          );

        if (node == null) {
          throw new InvalidOperationException(
            "The aggregated knowledge area does not exist: " + area
          );
        }

        if (node.Path == _RootArea) {
          return "Knowledge";
        }

        return node.DisplayName;
      }
    }

    /// <summary>
    /// Searches the aggregated logical repository for areas matching the specified
    /// keyword.
    /// 
    /// Provider-native keyword searches are executed within every mounted repository and
    /// translated into global paths. Synthetic mount areas are additionally matched by
    /// their logical path and direct display name.
    /// 
    /// Duplicate global paths are collapsed. Final results follow the natural global
    /// area order rather than provider-specific search ranking.
    /// </summary>
    /// <param name="keyword">The keyword to search for.</param>
    /// <param name="startArea">The absolute global search scope.</param>
    /// <returns>Matching global area paths in deterministic repository order.</returns>
    public string[] GetAreasByKeyword(string keyword, string startArea = "/") {
      if (string.IsNullOrWhiteSpace(keyword)) {
        return Array.Empty<string>();
      }

      lock (_SyncRoot) {
        string normalizedStartArea = this.NormalizeAreaPath(startArea);
        AggregatedTree tree = this.BuildTree();
        AggregatedNode startNode =
          this.EnsureAreaPathMaterialized(
            tree,
            normalizedStartArea
          );

        if (startNode == null) {
          return Array.Empty<string>();
        }

        HashSet<string> matches = new HashSet<string>(StringComparer.Ordinal);

        this.EnsureSubtreeMaterialized(
          tree,
          startNode
        );

        string[] visibleAreas =
          this.GetMaterializedDescendantPaths(
            startNode
          );

        foreach (string visibleArea in visibleAreas) {
          AggregatedNode node = tree.Find(visibleArea);

          if (node == null) {
            continue;
          }

          if (visibleArea.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            matches.Add(visibleArea);
            continue;
          }

          if (node.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            matches.Add(visibleArea);
          }
        }

        foreach (MountedRepository mountedRepository in _Repositories) {
          string localSearchStart;

          if (!this.TryTranslateGlobalScopeToLocal(
                mountedRepository,
                normalizedStartArea,
                out localSearchStart
              )) {
            continue;
          }

          string[] localMatches;

          try {
            localMatches =
              mountedRepository.Repository.GetAreasByKeyword(
                keyword,
                localSearchStart
              );
          }
          catch (Exception ex) {
            this.LogProviderReadFailure(
              ex,
              mountedRepository,
              "GetAreasByKeyword",
              localSearchStart
            );

            continue;
          }

          foreach (string localMatch in localMatches) {
            string globalMatch = this.ToGlobalPath(
              mountedRepository,
              localMatch
            );

            if (this.IsSameOrDescendant(globalMatch, normalizedStartArea)) {
              matches.Add(globalMatch);
            }
          }
        }

        List<string> orderedResult = new List<string>();

        foreach (string visibleArea in visibleAreas) {
          if (matches.Contains(visibleArea)) {
            orderedResult.Add(visibleArea);
          }
        }

        if (normalizedStartArea != _RootArea &&
            matches.Contains(normalizedStartArea)) {
          orderedResult.Insert(0, normalizedStartArea);
        }

        return orderedResult.ToArray();
      }
    }

    /// <summary>
    /// Returns the effective capabilities of one global aggregated area.
    /// 
    /// Content level is combined for read semantics:
    /// 
    /// - If any concrete contributor is a <see cref="ContentLevel.ContentContainer"/>,
    ///   the global area is exposed as <see cref="ContentLevel.ContentContainer"/>.
    /// - Otherwise, if any contributor is a <see cref="ContentLevel.ContentAggregation"/>
    ///   or the global area is a synthetic mount node with content descendants, it is
    ///   exposed as <see cref="ContentLevel.ContentAggregation"/>.
    /// - Otherwise it is <see cref="ContentLevel.BeyondContent"/>.
    /// 
    /// Structural child support is the union of all contributors and synthetic children.
    /// 
    /// Mutation capabilities are intentionally stricter. A mutation capability is true
    /// only when exactly one concrete mounted repository unambiguously represents the
    /// global area and that repository reports the corresponding capability. Overlay
    /// areas owned by several repositories are therefore read-mergeable but not directly
    /// mutable through this aggregator.
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
        string normalizedArea = this.NormalizeAreaPath(area);
        AggregatedTree tree = this.BuildTree();
        AggregatedNode node =
          this.EnsureAreaPathMaterialized(
            tree,
            normalizedArea
          );

        if (node == null) {
          throw new InvalidOperationException(
            "The aggregated knowledge area does not exist: " + normalizedArea
          );
        }

        this.EnsureDirectChildrenMaterialized(
          tree,
          node
        );

        contentLevel = this.ResolveCombinedContentLevel(node);
        supportsSubAreas = node.Children.Count > 0;
        supportsResources = false;

        foreach (AreaContribution contribution in node.Contributions) {
          ContentLevel providerContentLevel;
          bool providerSupportsSubAreas;
          bool providerCanBeRenamed;
          bool providerCanBeDeleted;
          bool providerCanAddSubAreas;
          bool providerCanAppendContent;
          bool providerCanTruncate;
          bool providerSupportsResources;

          try {
            contribution.MountedRepository.Repository.GetAreaCapabilities(
              contribution.LocalArea,
              out providerContentLevel,
              out providerSupportsSubAreas,
              out providerCanBeRenamed,
              out providerCanBeDeleted,
              out providerCanAddSubAreas,
              out providerCanAppendContent,
              out providerCanTruncate,
              out providerSupportsResources
            );
          }
          catch (Exception ex) {
            this.LogProviderReadFailure(
              ex,
              contribution,
              "GetAreaCapabilities",
              contribution.LocalArea
            );

            continue;
          }

          if (providerSupportsSubAreas) {
            supportsSubAreas = true;
          }

          if (providerSupportsResources) {
            supportsResources = true;
          }
        }

        canBeRenamed = false;
        canBeDeleted = false;
        canAddSubAreas = false;
        canAppendContent = false;
        canTruncate = false;

        if (node.Contributions.Count != 1) {
          return;
        }

        AreaContribution uniqueContribution = node.Contributions[0];

        ContentLevel uniqueContentLevel;
        bool uniqueSupportsSubAreas;
        bool uniqueCanBeRenamed;
        bool uniqueCanBeDeleted;
        bool uniqueCanAddSubAreas;
        bool uniqueCanAppendContent;
        bool uniqueCanTruncate;
        bool uniqueSupportsResources;

        try {
          uniqueContribution.MountedRepository.Repository.GetAreaCapabilities(
            uniqueContribution.LocalArea,
            out uniqueContentLevel,
            out uniqueSupportsSubAreas,
            out uniqueCanBeRenamed,
            out uniqueCanBeDeleted,
            out uniqueCanAddSubAreas,
            out uniqueCanAppendContent,
            out uniqueCanTruncate,
            out uniqueSupportsResources
          );
        }
        catch (Exception ex) {
          this.LogProviderReadFailure(
            ex,
            uniqueContribution,
            "GetAreaCapabilities",
            uniqueContribution.LocalArea
          );

          return;
        }

        canBeRenamed = uniqueCanBeRenamed;
        canBeDeleted = uniqueCanBeDeleted;
        canAddSubAreas = uniqueCanAddSubAreas;
        canAppendContent = uniqueCanAppendContent;
        canTruncate = uniqueCanTruncate;
      }
    }

    /// <summary>
    /// Returns the resource capability reported by one mounted repository area.
    /// </summary>
    private bool RepositorySupportsResources(
      IKnowledgeRepository repository,
      string area
    ) {
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

      return supportsResources;
    }

    /// <summary>
    /// Returns resources exposed by all resource-capable contributors.
    ///
    /// Child-provider resource identifiers are wrapped in an opaque aggregator-owned
    /// identifier so callers never need to infer contributor identity or child-provider
    /// semantics from the value.
    /// </summary>
    public KnowledgeResourceInfo[] GetResources(string area) {
      lock (_SyncRoot) {
        AggregatedNode node = this.RequireNode(
          area
        );

        List<KnowledgeResourceInfo> resources =
          new List<KnowledgeResourceInfo>();

        foreach (AreaContribution contribution in node.Contributions) {
          IKnowledgeRepository repository =
            contribution.MountedRepository.Repository;

          try {
            if (!this.RepositorySupportsResources(
                  repository,
                  contribution.LocalArea
                )) {
              continue;
            }

            KnowledgeResourceInfo[] providerResources =
              repository.GetResources(
                contribution.LocalArea
              );

            foreach (KnowledgeResourceInfo providerResource in providerResources) {
              KnowledgeResourceInfo aggregateResource =
                this.CloneResourceInfo(
                  providerResource
                );

              aggregateResource.ResourceId = this.CreateAggregatedResourceId(
                contribution.MountedRepository,
                providerResource.ResourceId
              );

              resources.Add(
                aggregateResource
              );
            }
          }
          catch (Exception ex) {
            this.LogProviderReadFailure(
              ex,
              contribution,
              "GetResources",
              contribution.LocalArea
            );
          }
        }

        return resources
          .OrderBy(
            (KnowledgeResourceInfo resource) => resource.ResourceId,
            StringComparer.Ordinal
          )
          .ToArray();
      }
    }

    /// <summary>
    /// Resolves one aggregator-owned opaque resource identifier to the corresponding child
    /// provider and returns its binary content.
    /// </summary>
    public byte[] GetResourceContent(string resourceId) {
      lock (_SyncRoot) {
        MountedRepository mountedRepository;
        string childResourceId;

        if (!this.TryResolveAggregatedResourceId(
              resourceId,
              out mountedRepository,
              out childResourceId
            )) {
          throw new InvalidOperationException(
            "The aggregated resource identifier is invalid or its provider is no longer mounted."
          );
        }

        return mountedRepository.Repository.GetResourceContent(
          childResourceId
        );
      }
    }

    /// <summary>
    /// Adds a resource only when exactly one concrete contributor unambiguously owns the
    /// addressed area.
    /// </summary>
    public bool TryAddResource(
      string area,
      string preferredFileName,
      string contentType,
      byte[] content,
      out string resourceId
    ) {
      lock (_SyncRoot) {
        resourceId = string.Empty;

        AggregatedNode node = this.RequireNode(
          area
        );

        if (node.Contributions.Count != 1) {
          return false;
        }

        AreaContribution contribution =
          node.Contributions[0];

        IKnowledgeRepository repository =
          contribution.MountedRepository.Repository;

        if (!this.RepositorySupportsResources(
                repository,
                contribution.LocalArea
              )) {
          return false;
        }

        string childResourceId;

        bool added = repository.TryAddResource(
          contribution.LocalArea,
          preferredFileName,
          contentType,
          content,
          out childResourceId
        );

        if (!added) {
          return false;
        }

        resourceId = this.CreateAggregatedResourceId(
          contribution.MountedRepository,
          childResourceId
        );

        this.InvalidateTreeCache();
        return true;
      }
    }

    /// <summary>
    /// Replaces one resource through the child provider encoded by the aggregator-owned
    /// opaque resource identifier.
    /// </summary>
    public bool TryReplaceResource(
      string resourceId,
      string contentType,
      byte[] content
    ) {
      lock (_SyncRoot) {
        MountedRepository mountedRepository;
        string childResourceId;

        if (!this.TryResolveAggregatedResourceId(
              resourceId,
              out mountedRepository,
              out childResourceId
            )) {
          return false;
        }

        bool replaced =
          mountedRepository.Repository.TryReplaceResource(
            childResourceId,
            contentType,
            content
          );

        if (replaced) {
          this.InvalidateTreeCache();
        }

        return replaced;
      }
    }

    /// <summary>
    /// Deletes one resource through the child provider encoded by the aggregator-owned
    /// opaque resource identifier.
    /// </summary>
    public bool TryDeleteResource(string resourceId) {
      lock (_SyncRoot) {
        MountedRepository mountedRepository;
        string childResourceId;

        if (!this.TryResolveAggregatedResourceId(
              resourceId,
              out mountedRepository,
              out childResourceId
            )) {
          return false;
        }

        bool deleted =
          mountedRepository.Repository.TryDeleteResource(
            childResourceId
          );

        if (deleted) {
          this.InvalidateTreeCache();
        }

        return deleted;
      }
    }

    /// <summary>
    /// Determines whether any concrete contributor to the global area owns non-empty
    /// direct textual content.
    /// 
    /// Direct content from several overlaid repositories is allowed for read purposes.
    /// The global result is true when at least one contributing provider reports direct
    /// content.
    /// </summary>
    public bool HasDirectContent(string area) {
      lock (_SyncRoot) {
        AggregatedNode node = this.RequireNode(area);

        foreach (AreaContribution contribution in node.Contributions) {
          try {
            if (contribution.MountedRepository.Repository.HasDirectContent(
                  contribution.LocalArea
                )) {
              return true;
            }
          }
          catch (Exception ex) {
            this.LogProviderReadFailure(
              ex,
              contribution,
              "HasDirectContent",
              contribution.LocalArea
            );
          }
        }

        return false;
      }
    }

    /// <summary>
    /// Returns direct textual content from all concrete contributors to the global area
    /// in mount-registration order.
    /// 
    /// Empty contributor content is omitted. When several repositories provide non-empty
    /// direct content at the same overlay path, their direct content blocks are separated
    /// by one empty line.
    /// 
    /// Synthetic areas and pure aggregation contributors add no direct content.
    /// </summary>
    public string GetDirectContent(string area) {
      lock (_SyncRoot) {
        AggregatedNode node = this.RequireNode(
          area
        );

        List<string> blocks = new List<string>();

        foreach (AreaContribution contribution in node.Contributions) {
          try {
            string providerContent =
              contribution.MountedRepository.Repository.GetDirectContent(
                contribution.LocalArea
              );

            string aggregatedContent =
              this.TranslateProviderContentToAggregated(
                contribution,
                providerContent
              );

            if (!string.IsNullOrWhiteSpace(aggregatedContent)) {
              blocks.Add(
                aggregatedContent.Trim(
                  '\r',
                  '\n'
                )
              );
            }
          }
          catch (Exception ex) {
            this.LogProviderReadFailure(
              ex,
              contribution,
              "GetDirectContent",
              contribution.LocalArea
            );
          }
        }

        return string.Join(
          Environment.NewLine + Environment.NewLine,
          blocks
        );
      }
    }


    /// <summary>
    /// Returns the complete aggregated textual view rooted at the specified global area.
    /// 
    /// The method renders the global overlay tree rather than blindly concatenating whole
    /// provider documents. This prevents duplicate structural branches when several
    /// repositories overlap the same logical area.
    /// 
    /// For every global node:
    /// 
    /// - direct content from all concrete content contributors is concatenated in
    ///   registration order;
    /// - visible child areas are rendered once in global natural order;
    /// - synthetic mount areas participate like content aggregations;
    /// - a content aggregation contributor that exposes no child areas but still returns
    ///   provider-native aggregated content is treated as an opaque aggregate leaf and
    ///   its aggregated content is included.
    /// 
    /// The returned representation uses Markdown-style headings as the neutral textual
    /// projection of the aggregated area tree.
    /// </summary>
    public string GetAggregatedContent(string area) {
      lock (_SyncRoot) {
        string normalizedArea = this.NormalizeAreaPath(area);
        AggregatedTree tree = this.BuildTree();
        AggregatedNode node =
          this.EnsureAreaPathMaterialized(
            tree,
            normalizedArea
          );

        if (node == null) {
          return string.Empty;
        }

        // Aggregated content is an explicit subtree operation. Materialize only the
        // requested scope, one provider level at a time, instead of eagerly building the
        // complete repository tree from the global root.
        this.EnsureSubtreeMaterialized(
          tree,
          node
        );

        ContentLevel contentLevel = this.ResolveCombinedContentLevel(node);

        if (contentLevel == ContentLevel.BeyondContent) {
          return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        this.RenderAggregatedNodeContent(node, builder, 1);

        return builder.ToString().TrimEnd('\r', '\n');
      }
    }

    /// <summary>
    /// Deletes a global area only when exactly one mounted repository owns that concrete
    /// path and reports deletion capability.
    /// 
    /// Synthetic mount nodes and overlay paths contributed by multiple repositories are
    /// not deleted because there is no unambiguous single mutation target.
    /// </summary>
    public bool TryDelete(string area) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.Delete,
              out contribution
            )) {
          return false;
        }

        bool deleted =
          contribution.MountedRepository.Repository.TryDelete(
            contribution.LocalArea
          );

        if (deleted) {
          this.InvalidateTreeCache();
        }

        return deleted;
      }
    }

    /// <summary>
    /// Renames a global area only when it resolves to exactly one concrete mounted
    /// repository and that provider reports rename capability.
    /// 
    /// The physical effect remains entirely provider-specific. The underlying repository
    /// may rename a heading, document, directory, notebook page or another provider
    /// artifact.
    /// 
    /// Synthetic and multiply overlaid areas are not renameable through this aggregator.
    /// </summary>
    public bool TryRename(
      string area,
      string newName,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      lock (_SyncRoot) {
        resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();

        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.Rename,
              out contribution
            )) {
          return false;
        }

        KnowledgeResourceIdChange[] childChanges;

        bool renamed =
          contribution.MountedRepository.Repository.TryRename(
            contribution.LocalArea,
            newName,
            out childChanges
          );

        if (!renamed) {
          return false;
        }

        resourceIdChanges = this.WrapResourceIdChanges(
          contribution.MountedRepository,
          childChanges
        );

        this.InvalidateTreeCache();
        return true;
      }
    }


    /// <summary>
    /// Adds a direct sub-area only when the global parent resolves to exactly one concrete
    /// repository and that provider allows child creation.
    /// 
    /// The aggregator does not infer which mounted repository should receive a new child
    /// when several providers overlap the same parent.
    /// </summary>
    public bool TryAddSubArea(
      string area,
      string name,
      KnowledgeAreaKind kind
    ) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.AddSubArea,
              out contribution
            )) {
          return false;
        }

        bool added =
          contribution.MountedRepository.Repository.TryAddSubArea(
            contribution.LocalArea,
            name,
            kind
          );

        if (added) {
          this.InvalidateTreeCache();
        }

        return added;
      }
    }

    /// <summary>
    /// Performs sparse hierarchical append only when the global target resolves to one
    /// unambiguous concrete repository and that provider allows append.
    /// 
    /// The aggregator deliberately does not split one sparse append payload across
    /// several mounted repositories because doing so would violate the atomic mutation
    /// guarantee of <see cref="IKnowledgeRepository"/>.
    /// </summary>
    public bool TryAppendContent(string area, string content) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.AppendContent,
              out contribution
            )) {
          return false;
        }

        string providerContent =
          this.TranslateAggregatedContentToProvider(
            contribution,
            content
          );

        bool appended =
          contribution.MountedRepository.Repository.TryAppendContent(
            contribution.LocalArea,
            providerContent
          );

        if (appended) {
          this.InvalidateTreeCache();
        }

        return appended;
      }
    }

    /// <summary>
    /// Truncates a global area only when it resolves uniquely to one concrete mounted
    /// repository and that provider reports truncate capability.
    /// </summary>
    public bool TryTruncate(string area) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.Truncate,
              out contribution
            )) {
          return false;
        }

        bool truncated =
          contribution.MountedRepository.Repository.TryTruncate(
            contribution.LocalArea
          );

        if (truncated) {
          this.InvalidateTreeCache();
        }

        return truncated;
      }
    }

    /// <summary>
    /// Replaces a global content scope only when exactly one provider owns the global
    /// target and that provider supports both truncate and append semantics.
    /// 
    /// The replacement is delegated as one operation to the underlying repository so its
    /// provider-specific atomicity guarantee remains intact.
    /// </summary>
    public bool TryReplace(string area, string newContent) {
      lock (_SyncRoot) {
        string normalizedArea = this.NormalizeAreaPath(area);
        AggregatedNode node = this.RequireNode(normalizedArea);

        if (node.Contributions.Count != 1) {
          return false;
        }

        AreaContribution contribution = node.Contributions[0];

        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;
        bool supportsResources;

        contribution.MountedRepository.Repository.GetAreaCapabilities(
          contribution.LocalArea,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate,
          out supportsResources
        );

        if (!canAppendContent || !canTruncate) {
          return false;
        }

        string providerContent =
          this.TranslateAggregatedContentToProvider(
            contribution,
            newContent
          );

        bool replaced =
          contribution.MountedRepository.Repository.TryReplace(
            contribution.LocalArea,
            providerContent
          );

        if (replaced) {
          this.InvalidateTreeCache();
        }

        return replaced;
      }
    }

    /// <summary>
    /// Reparents one logical content area only when the area being moved and its new
    /// parent each resolve uniquely to the same mounted repository registration.
    ///
    /// Cross-repository movement is intentionally rejected. Arbitrary
    /// <see cref="IKnowledgeRepository"/> implementations do not share a distributed
    /// transaction, so this aggregator cannot preserve the atomicity required by the
    /// provider-neutral move contract across repository boundaries.
    ///
    /// No truncate/append capability inference is performed. The underlying repository
    /// receives the move request unchanged and decides whether that concrete logical
    /// source/new-parent combination is representable.
    /// </summary>
    public bool TryMoveContent(
      string contentAreaToMove,
      string newParentArea,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      lock (_SyncRoot) {
        resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();

        AggregatedNode contentNode = this.RequireNode(
          contentAreaToMove
        );

        AggregatedNode newParentNode = this.RequireNode(
          newParentArea
        );

        if (contentNode.Contributions.Count != 1 ||
            newParentNode.Contributions.Count != 1) {
          return false;
        }

        AreaContribution contentContribution =
          contentNode.Contributions[0];

        AreaContribution newParentContribution =
          newParentNode.Contributions[0];

        if (!object.ReferenceEquals(
              contentContribution.MountedRepository,
              newParentContribution.MountedRepository
            )) {
          return false;
        }

        KnowledgeResourceIdChange[] childChanges;

        bool moved =
          contentContribution.MountedRepository.Repository.TryMoveContent(
            contentContribution.LocalArea,
            newParentContribution.LocalArea,
            out childChanges
          );

        if (!moved) {
          return false;
        }

        resourceIdChanges = this.WrapResourceIdChanges(
          contentContribution.MountedRepository,
          childChanges
        );

        this.InvalidateTreeCache();
        return true;
      }
    }


    /// <summary>
    /// Translates provider-local canonical resource references into aggregator-owned opaque
    /// resource identifiers before textual content leaves this repository.
    ///
    /// This translation is mandatory because <see cref="GetResources(string)"/> exposes
    /// aggregate-level ResourceIds rather than child-provider ResourceIds. Content and
    /// resource metadata must therefore always use the same identifier namespace.
    /// </summary>
    private string TranslateProviderContentToAggregated(
      AreaContribution contribution,
      string providerContent
    ) {
      if (string.IsNullOrEmpty(
            providerContent
          )) {
        return providerContent;
      }

      string translatedContent =
        _KnowledgeAreaReferenceRegex.Replace(
          providerContent,
          (Match match) => {
            string encodedLocalArea =
              match.Groups["area"].Value;

            string localArea =
              this.DecodeKnowledgeAreaReferenceTarget(
                encodedLocalArea
              );

            string globalArea =
              this.ToGlobalPath(
                contribution.MountedRepository,
                localArea
              );

            return _KnowledgeAreaReferencePrefix
              + this.EncodeKnowledgeAreaReferenceTarget(
                globalArea
              );
          }
        );

      MatchCollection matches =
        _KnowledgeResourceReferenceRegex.Matches(
          translatedContent
        );

      if (matches.Count == 0) {
        return translatedContent;
      }

      IKnowledgeRepository repository =
        contribution.MountedRepository.Repository;

      if (!this.RepositorySupportsResources(
            repository,
            contribution.LocalArea
          )) {
        return translatedContent;
      }

      KnowledgeResourceInfo[] resources =
        repository.GetResources(
          contribution.LocalArea
        );

      Dictionary<string, string> mappings =
        new Dictionary<string, string>(
          StringComparer.Ordinal
        );

      foreach (KnowledgeResourceInfo resource in resources) {
        if (string.IsNullOrWhiteSpace(
              resource.ResourceId
            )) {
          continue;
        }

        mappings[resource.ResourceId] =
          this.CreateAggregatedResourceId(
            contribution.MountedRepository,
            resource.ResourceId
          );
      }

      return _KnowledgeResourceReferenceRegex.Replace(
        translatedContent,
        (Match match) => {
          string childResourceId =
            match.Groups["id"].Value;

          if (!mappings.ContainsKey(
                childResourceId
              )) {
            return match.Value;
          }

          return _KnowledgeResourceReferencePrefix
            + mappings[childResourceId];
        }
      );
    }

    /// <summary>
    /// Translates aggregator-owned canonical resource references back into the child
    /// provider's opaque ResourceId namespace before a mutation is delegated.
    ///
    /// Resource identifiers belonging to another mounted repository are rejected by
    /// leaving the reference unchanged. The child provider will consequently reject the
    /// mutation rather than accidentally receiving a foreign resource identity.
    /// </summary>
    private string TranslateAggregatedContentToProvider(
      AreaContribution contribution,
      string aggregatedContent
    ) {
      if (string.IsNullOrEmpty(
            aggregatedContent
          )) {
        return aggregatedContent;
      }

      string translatedContent =
        _KnowledgeAreaReferenceRegex.Replace(
          aggregatedContent,
          (Match match) => {
            string encodedGlobalArea =
              match.Groups["area"].Value;

            string globalArea =
              this.DecodeKnowledgeAreaReferenceTarget(
                encodedGlobalArea
              );

            string localArea;

            if (!this.TryTranslateGlobalAreaToMountedLocalArea(
                  contribution.MountedRepository,
                  globalArea,
                  out localArea
                )) {
              return match.Value;
            }

            return _KnowledgeAreaReferencePrefix
              + this.EncodeKnowledgeAreaReferenceTarget(
                localArea
              );
          }
        );

      return _KnowledgeResourceReferenceRegex.Replace(
        translatedContent,
        (Match match) => {
          string aggregatedResourceId =
            match.Groups["id"].Value;

          MountedRepository mountedRepository;
          string childResourceId;

          if (!this.TryResolveAggregatedResourceId(
                aggregatedResourceId,
                out mountedRepository,
                out childResourceId
              )) {
            return match.Value;
          }

          if (!object.ReferenceEquals(
                mountedRepository,
                contribution.MountedRepository
              )) {
            return match.Value;
          }

          return _KnowledgeResourceReferencePrefix
            + childResourceId;
        }
      );
    }

    /// <summary>
    /// Decodes one provider-neutral knowledge-area URI target exactly once into the
    /// repository-local logical area representation.
    /// </summary>
    private string DecodeKnowledgeAreaReferenceTarget(
      string encodedArea
    ) {
      if (string.IsNullOrWhiteSpace(
            encodedArea
          ) ||
          string.Equals(
            encodedArea,
            _RootArea,
            StringComparison.Ordinal
          )) {
        return _RootArea;
      }

      string decodedArea;

      try {
        decodedArea =
          Uri.UnescapeDataString(
            encodedArea
          );
      }
      catch (UriFormatException ex) {
        DevLogger.LogError(
          ex
        );

        decodedArea =
          encodedArea;
      }

      return this.NormalizeAreaPath(
        decodedArea
      );
    }

    /// <summary>
    /// Encodes one logical repository area as a provider-neutral knowledge-area URI target
    /// while preserving slash separators as hierarchy delimiters.
    /// </summary>
    private string EncodeKnowledgeAreaReferenceTarget(
      string area
    ) {
      string normalizedArea =
        this.NormalizeAreaPath(
          area
        );

      if (normalizedArea == _RootArea) {
        return _RootArea;
      }

      string[] segments =
        normalizedArea.Split(
          '/',
          StringSplitOptions.RemoveEmptyEntries
        );

      StringBuilder builder =
        new StringBuilder();

      foreach (string segment in segments) {
        builder.Append(
          '/'
        );

        builder.Append(
          Uri.EscapeDataString(
            segment
          )
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Creates a detached resource metadata copy for the aggregated read model.
    /// </summary>
    private KnowledgeResourceInfo CloneResourceInfo(
      KnowledgeResourceInfo source
    ) {
      KnowledgeResourceInfo clone = new KnowledgeResourceInfo();
      clone.ResourceId = source.ResourceId;
      clone.FileName = source.FileName;
      clone.ContentType = source.ContentType;
      clone.Length = source.Length;
      return clone;
    }

    /// <summary>
    /// Creates one opaque aggregator-owned resource identifier.
    ///
    /// The external identifier contains two separately Base64Url-encoded components:
    /// a mount-instance namespace and the child provider's opaque resource identifier.
    /// Consumers MUST treat the complete value as opaque.
    /// </summary>
    private string CreateAggregatedResourceId(
      MountedRepository mountedRepository,
      string childResourceId
    ) {
      if (mountedRepository == null) {
        throw new ArgumentNullException(
          nameof(mountedRepository)
        );
      }

      if (string.IsNullOrWhiteSpace(childResourceId)) {
        throw new ArgumentException(
          "A child resource identifier is required.",
          nameof(childResourceId)
        );
      }

      string providerToken = mountedRepository.ResourceNamespaceToken;

      string childToken = this.EncodeBase64Url(
        childResourceId
      );

      return "2."
        + providerToken
        + "."
        + childToken;
    }

    /// <summary>
    /// Resolves one aggregator-owned resource identifier to the exact mounted repository
    /// instance and the original opaque child provider resource identifier.
    ///
    /// Version 2 uses mount-point plus mount-local ordinal as the provider namespace.
    /// Version 1 remains readable for compatibility with projection state created by an
    /// earlier aggregator implementation that used global registration order.
    /// </summary>
    private bool TryResolveAggregatedResourceId(
      string resourceId,
      out MountedRepository mountedRepository,
      out string childResourceId
    ) {
      mountedRepository = null;
      childResourceId = string.Empty;

      if (string.IsNullOrWhiteSpace(resourceId)) {
        return false;
      }

      if (resourceId.StartsWith(
            "2.",
            StringComparison.Ordinal
          )) {
        return this.TryResolveVersion2AggregatedResourceId(
          resourceId,
          out mountedRepository,
          out childResourceId
        );
      }

      if (resourceId.StartsWith(
            "1.",
            StringComparison.Ordinal
          )) {
        return this.TryResolveLegacyVersion1AggregatedResourceId(
          resourceId,
          out mountedRepository,
          out childResourceId
        );
      }

      return false;
    }

    /// <summary>
    /// Resolves a version-2 aggregate resource identifier.
    /// </summary>
    private bool TryResolveVersion2AggregatedResourceId(
      string resourceId,
      out MountedRepository mountedRepository,
      out string childResourceId
    ) {
      mountedRepository = null;
      childResourceId = string.Empty;

      string[] parts = resourceId.Split(
        '.',
        StringSplitOptions.None
      );

      if (parts.Length != 3 ||
          !string.Equals(
            parts[0],
            "2",
            StringComparison.Ordinal
          ) ||
          string.IsNullOrWhiteSpace(parts[1]) ||
          string.IsNullOrWhiteSpace(parts[2])) {
        return false;
      }

      string decodedChildResourceId;

      if (!this.TryDecodeBase64Url(
            parts[2],
            out decodedChildResourceId
          )) {
        return false;
      }

      MountedRepository provider =
        _Repositories.FirstOrDefault(
          (MountedRepository candidate) => string.Equals(
            candidate.ResourceNamespaceToken,
            parts[1],
            StringComparison.Ordinal
          )
        );

      if (provider == null) {
        return false;
      }

      mountedRepository = provider;
      childResourceId = decodedChildResourceId;
      return true;
    }

    /// <summary>
    /// Resolves the previous version-1 aggregate resource identifier that encoded global
    /// registration order and child ResourceId into one Base64Url payload.
    ///
    /// This compatibility path is intentionally read-only. Newly exposed identifiers always
    /// use the version-2 mount-instance namespace format.
    /// </summary>
    private bool TryResolveLegacyVersion1AggregatedResourceId(
      string resourceId,
      out MountedRepository mountedRepository,
      out string childResourceId
    ) {
      mountedRepository = null;
      childResourceId = string.Empty;

      string encoded = resourceId.Substring(2);
      string nativeIdentity;

      if (!this.TryDecodeBase64Url(
            encoded,
            out nativeIdentity
          )) {
        return false;
      }

      int separatorIndex = nativeIdentity.IndexOf(
        '|'
      );

      if (separatorIndex <= 0 ||
          separatorIndex >= nativeIdentity.Length - 1) {
        return false;
      }

      int registrationOrder;

      if (!int.TryParse(
            nativeIdentity.Substring(
              0,
              separatorIndex
            ),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out registrationOrder
          )) {
        return false;
      }

      MountedRepository provider =
        _Repositories.FirstOrDefault(
          (MountedRepository candidate) =>
            candidate.RegistrationOrder == registrationOrder
        );

      if (provider == null) {
        return false;
      }

      mountedRepository = provider;
      childResourceId = nativeIdentity.Substring(
        separatorIndex + 1
      );

      return !string.IsNullOrWhiteSpace(
        childResourceId
      );
    }

    /// <summary>
    /// Encodes one UTF-8 string using unpadded Base64Url.
    /// </summary>
    private string EncodeBase64Url(string value) {
      string encoded = Convert.ToBase64String(
        Encoding.UTF8.GetBytes(value)
      );

      return encoded
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    }

    /// <summary>
    /// Decodes one unpadded Base64Url UTF-8 string.
    /// </summary>
    private bool TryDecodeBase64Url(
      string encoded,
      out string value
    ) {
      value = string.Empty;

      if (string.IsNullOrWhiteSpace(encoded)) {
        return false;
      }

      string normalized = encoded
        .Replace('-', '+')
        .Replace('_', '/');

      int remainder = normalized.Length % 4;

      if (remainder == 2) {
        normalized += "==";
      }
      else if (remainder == 3) {
        normalized += "=";
      }
      else if (remainder == 1) {
        return false;
      }

      byte[] bytes;

      try {
        bytes = Convert.FromBase64String(
          normalized
        );
      }
      catch (FormatException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      value = Encoding.UTF8.GetString(
        bytes
      );

      return !string.IsNullOrEmpty(
        value
      );
    }

    /// <summary>
    /// Wraps child-provider resource identifier changes in aggregate-level opaque IDs.
    /// </summary>
    private KnowledgeResourceIdChange[] WrapResourceIdChanges(
      MountedRepository mountedRepository,
      KnowledgeResourceIdChange[] childChanges
    ) {
      if (childChanges == null ||
          childChanges.Length == 0) {
        return Array.Empty<KnowledgeResourceIdChange>();
      }

      KnowledgeResourceIdChange[] result =
        new KnowledgeResourceIdChange[childChanges.Length];

      for (int index = 0; index < childChanges.Length; index++) {
        KnowledgeResourceIdChange change =
          new KnowledgeResourceIdChange();

        change.PreviousResourceId = this.CreateAggregatedResourceId(
          mountedRepository,
          childChanges[index].PreviousResourceId
        );

        change.CurrentResourceId = this.CreateAggregatedResourceId(
          mountedRepository,
          childChanges[index].CurrentResourceId
        );

        result[index] = change;
      }

      return result;
    }

    /// <summary>
    /// Logs one isolated provider read failure. Read failures are deliberately contained
    /// at the mounted-provider boundary so healthy contributors and siblings remain
    /// available through the aggregate repository.
    /// </summary>
    private void LogProviderReadFailure(
      Exception ex,
      AreaContribution contribution,
      string operation,
      string area
    ) {
      this.LogProviderReadFailure(
        ex,
        contribution.MountedRepository,
        operation,
        area
      );
    }

    /// <summary>
    /// Logs one isolated provider read failure for a mounted repository.
    /// </summary>
    private void LogProviderReadFailure(
      Exception ex,
      MountedRepository mountedRepository,
      string operation,
      string area
    ) {
      DevLogger.LogError(
        ex
      );

      DevLogger.LogTrace(
        0,
        99999,
        "Aggregated knowledge provider read failed and was isolated: operation='"
        + operation
        + "' mount='"
        + mountedRepository.MountPoint
        + "' area='"
        + area
        + "'. Healthy providers remain available."
      );
    }

    /// <summary>
    /// Returns one structurally consistent aggregate-tree snapshot for a contiguous burst
    /// of read operations.
    ///
    /// Higher-level consumers such as the Joplin projection perform many logically related
    /// repository reads in immediate succession. Rebuilding the complete overlay tree for
    /// every GetAreaName, capability, content and resource call would recursively enumerate
    /// every mounted provider again and can repeatedly trigger remote Git refreshes.
    ///
    /// The cache is deliberately a short sliding read-burst cache rather than long-lived
    /// repository state. After a short idle period the next read rebuilds the tree and
    /// therefore observes independently changed providers. Mutations routed through this
    /// aggregator invalidate the snapshot immediately.
    /// </summary>
    private AggregatedTree BuildTree() {
      DateTime now = DateTime.UtcNow;

      if (_CachedTree != null &&
          now - _CachedTreeLastAccessUtc <= _TreeReadBurstWindow) {
        _CachedTreeLastAccessUtc = now;
        return _CachedTree;
      }

      AggregatedTree tree = this.BuildTreeCore();

      _CachedTree = tree;
      _CachedTreeLastAccessUtc = DateTime.UtcNow;

      return tree;
    }

    /// <summary>
    /// Invalidates the current aggregate-tree snapshot.
    /// </summary>
    private void InvalidateTreeCache() {
      _CachedTree = null;
      _CachedTreeLastAccessUtc = DateTime.MinValue;
    }

    private AggregatedTree BuildTreeCore() {
      AggregatedTree tree = new AggregatedTree();

      // Only mount structure and provider roots are materialized eagerly. Concrete
      // provider descendants are discovered later, one direct level at a time.
      foreach (MountedRepository mountedRepository in _Repositories) {
        this.EnsureMountPath(
          tree,
          mountedRepository
        );

        AggregatedNode mountNode = tree.GetOrCreate(
          mountedRepository.MountPoint,
          this.GetLastAreaSegment(
            mountedRepository.MountPoint
          )
        );

        mountNode.AddContribution(
          new AreaContribution(
            mountedRepository,
            _RootArea
          )
        );
      }

      return tree;
    }

    /// <summary>
    /// Materializes the path from the global root to one requested area by loading only
    /// direct child levels that are actually required to resolve that path.
    /// </summary>
    private AggregatedNode EnsureAreaPathMaterialized(
      AggregatedTree tree,
      string area
    ) {
      string normalizedArea =
        this.NormalizeAreaPath(
          area
        );

      AggregatedNode current =
        tree.Find(
          _RootArea
        );

      if (normalizedArea == _RootArea) {
        return current;
      }

      string[] segments =
        normalizedArea.Split(
          '/',
          StringSplitOptions.RemoveEmptyEntries
        );

      string currentPath =
        _RootArea;

      foreach (string segment in segments) {
        this.EnsureDirectChildrenMaterialized(
          tree,
          current
        );

        currentPath =
          this.CombineAreaPath(
            currentPath,
            segment
          );

        current =
          tree.Find(
            currentPath
          );

        if (current == null) {
          return null;
        }
      }

      return current;
    }

    /// <summary>
    /// Loads direct children of one aggregate node from every concrete provider
    /// contribution. Child providers are always queried with recurse=false.
    /// </summary>
    private void EnsureDirectChildrenMaterialized(
      AggregatedTree tree,
      AggregatedNode node
    ) {
      if (node.ChildrenMaterialized) {
        return;
      }

      bool hadProviderFailure =
        false;

      foreach (AreaContribution contribution in node.Contributions) {
        string[] localChildren;

        try {
          localChildren =
            contribution.MountedRepository.Repository.GetAreas(
              false,
              contribution.LocalArea
            );
        }
        catch (Exception ex) {
          hadProviderFailure =
            true;

          this.LogProviderReadFailure(
            ex,
            contribution,
            "GetAreas",
            contribution.LocalArea
          );

          continue;
        }

        foreach (string localChild in localChildren) {
          string globalChild =
            this.ToGlobalPath(
              contribution.MountedRepository,
              localChild
            );

          string expectedParent =
            this.GetParentAreaPath(
              globalChild
            );

          if (!string.Equals(
                expectedParent,
                node.Path,
                StringComparison.Ordinal
              )) {
            continue;
          }

          string displayName;

          try {
            displayName =
              contribution.MountedRepository.Repository.GetAreaName(
                localChild
              );
          }
          catch (Exception ex) {
            hadProviderFailure =
              true;

            this.LogProviderReadFailure(
              ex,
              contribution,
              "GetAreaName",
              localChild
            );

            displayName =
              this.GetLastAreaSegment(
                globalChild
              );
          }

          AggregatedNode globalNode =
            tree.GetOrCreate(
              globalChild,
              displayName
            );

          globalNode.AddContribution(
            new AreaContribution(
              contribution.MountedRepository,
              localChild
            )
          );
        }
      }

      // A partial materialization remains usable immediately, but a failed contributor
      // must be retried on a later read burst instead of permanently marking the node as
      // complete.
      node.ChildrenMaterialized =
        !hadProviderFailure;
    }

    /// <summary>
    /// Returns all already materialized descendants in deterministic depth-first pre-order.
    /// </summary>
    private string[] GetMaterializedDescendantPaths(
      AggregatedNode startNode
    ) {
      List<string> result =
        new List<string>();

      Stack<AggregatedNode> pending =
        new Stack<AggregatedNode>();

      for (int index = startNode.Children.Count - 1;
           index >= 0;
           index--) {
        pending.Push(
          startNode.Children[index]
        );
      }

      while (pending.Count > 0) {
        AggregatedNode current =
          pending.Pop();

        result.Add(
          current.Path
        );

        for (int index = current.Children.Count - 1;
             index >= 0;
             index--) {
          pending.Push(
            current.Children[index]
          );
        }
      }

      return result.ToArray();
    }

    /// <summary>
    /// Materializes one requested aggregate subtree iteratively. This is used only by
    /// explicit bulk operations such as recursive enumeration and aggregated-content
    /// rendering.
    /// </summary>
    private void EnsureSubtreeMaterialized(
      AggregatedTree tree,
      AggregatedNode startNode
    ) {
      Stack<AggregatedNode> pending =
        new Stack<AggregatedNode>();

      pending.Push(
        startNode
      );

      while (pending.Count > 0) {
        AggregatedNode current =
          pending.Pop();

        this.EnsureDirectChildrenMaterialized(
          tree,
          current
        );

        for (int index = current.Children.Count - 1;
             index >= 0;
             index--) {
          pending.Push(
            current.Children[index]
          );
        }
      }
    }

    /// <summary>
    /// Returns the canonical parent path of one absolute logical area.
    /// </summary>
    private string GetParentAreaPath(
      string area
    ) {
      string normalizedArea =
        this.NormalizeAreaPath(
          area
        );

      if (normalizedArea == _RootArea) {
        return _RootArea;
      }

      int separatorIndex =
        normalizedArea.LastIndexOf('/');

      if (separatorIndex <= 0) {
        return _RootArea;
      }

      return normalizedArea.Substring(
        0,
        separatorIndex
      );
    }

    /// <summary>
    /// Creates synthetic mount ancestors so a deep mount point is navigable even when no
    /// concrete repository owns the intermediate areas.
    /// </summary>
    private void EnsureMountPath(
      AggregatedTree tree,
      MountedRepository mountedRepository
    ) {
      if (mountedRepository.MountPoint == _RootArea) {
        return;
      }

      string[] segments = mountedRepository.MountPoint
        .Split('/', StringSplitOptions.RemoveEmptyEntries);

      string currentPath = _RootArea;

      foreach (string segment in segments) {
        currentPath = this.CombineAreaPath(currentPath, segment);

        tree.GetOrCreate(
          currentPath,
          segment
        );
      }
    }


    /// <summary>
    /// Resolves the effective read-oriented content level of a merged global area.
    /// </summary>
    private ContentLevel ResolveCombinedContentLevel(AggregatedNode node) {
      bool hasAggregation =
        false;

      foreach (AreaContribution contribution in node.Contributions) {
        try {
          contribution.MountedRepository.Repository.GetAreaCapabilities(
            contribution.LocalArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          );

          if (contentLevel == ContentLevel.ContentContainer) {
            return ContentLevel.ContentContainer;
          }

          if (contentLevel == ContentLevel.ContentAggregation) {
            hasAggregation =
              true;
          }
        }
        catch (Exception ex) {
          this.LogProviderReadFailure(
            ex,
            contribution,
            "GetAreaCapabilities",
            contribution.LocalArea
          );
        }
      }

      if (hasAggregation) {
        return ContentLevel.ContentAggregation;
      }

      if (node.Children.Count > 0 &&
          this.HasContentDescendant(
            node
          )) {
        return ContentLevel.ContentAggregation;
      }

      return ContentLevel.BeyondContent;
    }

    /// <summary>
    /// Determines whether a synthetic or structural area has any content-capable
    /// descendant without allowing one failing contributor to hide healthy siblings.
    /// </summary>
    private bool HasContentDescendant(AggregatedNode node) {
      Stack<AggregatedNode> pending =
        new Stack<AggregatedNode>();

      for (int index = node.Children.Count - 1;
           index >= 0;
           index--) {
        pending.Push(
          node.Children[index]
        );
      }

      while (pending.Count > 0) {
        AggregatedNode current =
          pending.Pop();

        foreach (AreaContribution contribution in current.Contributions) {
          try {
            contribution.MountedRepository.Repository.GetAreaCapabilities(
              contribution.LocalArea,
              out ContentLevel contentLevel,
              out bool supportsSubAreas,
              out bool canBeRenamed,
              out bool canBeDeleted,
              out bool canAddSubAreas,
              out bool canAppendContent,
              out bool canTruncate,
              out bool supportsResources
            );

            if (contentLevel != ContentLevel.BeyondContent) {
              return true;
            }
          }
          catch (Exception ex) {
            this.LogProviderReadFailure(
              ex,
              contribution,
              "GetAreaCapabilities",
              contribution.LocalArea
            );
          }
        }

        for (int index = current.Children.Count - 1;
             index >= 0;
             index--) {
          pending.Push(
            current.Children[index]
          );
        }
      }

      return false;
    }

    /// <summary>
    /// Renders the global overlay tree as a Markdown-like neutral aggregate projection.
    /// </summary>
    private void RenderAggregatedNodeContent(
      AggregatedNode node,
      StringBuilder builder,
      int childHeadingLevel
    ) {
      string directContent = this.GetDirectContent(node.Path);

      if (!string.IsNullOrWhiteSpace(directContent)) {
        builder.Append(directContent.Trim('\r', '\n'));
        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);
      }

      this.RenderOpaqueAggregationLeafContent(node, builder);

      foreach (AggregatedNode child in node.Children) {
        ContentLevel childContentLevel = this.ResolveCombinedContentLevel(child);

        if (childContentLevel == ContentLevel.BeyondContent &&
            !this.HasContentDescendant(child)) {
          continue;
        }

        int effectiveHeadingLevel = childHeadingLevel;

        if (effectiveHeadingLevel > 6) {
          effectiveHeadingLevel = 6;
        }

        builder.Append(new string('#', effectiveHeadingLevel));
        builder.Append(' ');
        builder.Append(child.DisplayName);
        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);

        this.RenderAggregatedNodeContent(
          child,
          builder,
          childHeadingLevel + 1
        );

        if (builder.Length > 0 &&
            !builder.ToString().EndsWith(
              Environment.NewLine + Environment.NewLine,
              StringComparison.Ordinal
            )) {
          builder.Append(Environment.NewLine);
        }
      }
    }

    /// <summary>
    /// Preserves provider-native aggregate content for leaf aggregation areas whose
    /// content cannot be reconstructed from visible child areas.
    /// 
    /// This is particularly important for virtual cross-cutting aggregation providers.
    /// </summary>
    private void RenderOpaqueAggregationLeafContent(
      AggregatedNode node,
      StringBuilder builder
    ) {
      foreach (AreaContribution contribution in node.Contributions) {
        try {
          contribution.MountedRepository.Repository.GetAreaCapabilities(
            contribution.LocalArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          );

          if (contentLevel != ContentLevel.ContentAggregation) {
            continue;
          }

          string[] providerChildren =
            contribution.MountedRepository.Repository.GetAreas(
              false,
              contribution.LocalArea
            );

          if (providerChildren.Length > 0) {
            continue;
          }

          string providerContent =
            contribution.MountedRepository.Repository.GetAggregatedContent(
              contribution.LocalArea
            );

          string aggregatedContent =
            this.TranslateProviderContentToAggregated(
              contribution,
              providerContent
            );

          if (string.IsNullOrWhiteSpace(aggregatedContent)) {
            continue;
          }

          builder.Append(
            aggregatedContent.Trim(
              '\r',
              '\n'
            )
          );
          builder.Append(Environment.NewLine);
          builder.Append(Environment.NewLine);
        }
        catch (Exception ex) {
          this.LogProviderReadFailure(
            ex,
            contribution,
            "GetAggregatedContent",
            contribution.LocalArea
          );
        }
      }
    }

    /// <summary>
    /// Resolves one mutation target and verifies that exactly one concrete provider owns
    /// the global area and supports the requested capability.
    /// </summary>
    private bool TryGetUniqueContributionForCapability(
      string area,
      CapabilityKind capabilityKind,
      out AreaContribution contribution
    ) {
      contribution = null;

      AggregatedNode node = this.RequireNode(area);

      if (node.Contributions.Count != 1) {
        return false;
      }

      AreaContribution candidate = node.Contributions[0];

      ContentLevel contentLevel;
      bool supportsSubAreas;
      bool canBeRenamed;
      bool canBeDeleted;
      bool canAddSubAreas;
      bool canAppendContent;
      bool canTruncate;
      bool supportsResources;

      candidate.MountedRepository.Repository.GetAreaCapabilities(
        candidate.LocalArea,
        out contentLevel,
        out supportsSubAreas,
        out canBeRenamed,
        out canBeDeleted,
        out canAddSubAreas,
        out canAppendContent,
        out canTruncate,
        out supportsResources
      );

      bool capabilityAvailable = false;

      if (capabilityKind == CapabilityKind.Rename) {
        capabilityAvailable = canBeRenamed;
      }
      else if (capabilityKind == CapabilityKind.Delete) {
        capabilityAvailable = canBeDeleted;
      }
      else if (capabilityKind == CapabilityKind.AddSubArea) {
        capabilityAvailable = canAddSubAreas;
      }
      else if (capabilityKind == CapabilityKind.AppendContent) {
        capabilityAvailable = canAppendContent;
      }
      else if (capabilityKind == CapabilityKind.Truncate) {
        capabilityAvailable = canTruncate;
      }

      if (!capabilityAvailable) {
        return false;
      }

      contribution = candidate;
      return true;
    }

    /// <summary>
    /// Returns a required global node or throws when the area does not exist.
    /// </summary>
    private AggregatedNode RequireNode(string area) {
      string normalizedArea = this.NormalizeAreaPath(area);
      AggregatedTree tree = this.BuildTree();
      AggregatedNode node =
        this.EnsureAreaPathMaterialized(
          tree,
          normalizedArea
        );

      if (node == null) {
        throw new InvalidOperationException(
          "The aggregated knowledge area does not exist: " + normalizedArea
        );
      }

      return node;
    }

    /// <summary>
    /// Translates a local mounted-repository area path into its global aggregated path.
    /// </summary>
    private string ToGlobalPath(
      MountedRepository mountedRepository,
      string localArea
    ) {
      string normalizedLocalArea = this.NormalizeAreaPath(localArea);

      if (normalizedLocalArea == _RootArea) {
        return mountedRepository.MountPoint;
      }

      if (mountedRepository.MountPoint == _RootArea) {
        return normalizedLocalArea;
      }

      return mountedRepository.MountPoint + normalizedLocalArea;
    }

    /// <summary>
    /// Maps one exact global aggregated area to the corresponding local area of a mounted
    /// repository without performing any provider read.
    ///
    /// Global ancestors above a mount point intentionally do not map to the provider root;
    /// those nodes are synthetic aggregator structure rather than concrete provider areas.
    /// </summary>
    private bool TryTranslateGlobalAreaToMountedLocalArea(
      MountedRepository mountedRepository,
      string globalArea,
      out string localArea
    ) {
      string normalizedGlobalArea =
        this.NormalizeAreaPath(
          globalArea
        );

      if (mountedRepository.MountPoint == _RootArea) {
        localArea =
          normalizedGlobalArea;

        return true;
      }

      if (string.Equals(
            normalizedGlobalArea,
            mountedRepository.MountPoint,
            StringComparison.Ordinal
          )) {
        localArea =
          _RootArea;

        return true;
      }

      if (!this.IsSameOrDescendant(
            normalizedGlobalArea,
            mountedRepository.MountPoint
          )) {
        localArea =
          _RootArea;

        return false;
      }

      string suffix =
        normalizedGlobalArea.Substring(
          mountedRepository.MountPoint.Length
        );

      if (string.IsNullOrEmpty(
            suffix
          )) {
        localArea =
          _RootArea;

        return true;
      }

      localArea =
        this.NormalizeAreaPath(
          suffix
        );

      return true;
    }

    /// <summary>
    /// Determines whether a requested global search scope intersects a mounted repository
    /// and maps that scope to the corresponding local provider path.
    /// </summary>
    private bool TryTranslateGlobalScopeToLocal(
      MountedRepository mountedRepository,
      string globalStartArea,
      out string localStartArea
    ) {
      localStartArea = _RootArea;

      if (globalStartArea == _RootArea) {
        return true;
      }

      if (this.IsSameOrDescendant(
            mountedRepository.MountPoint,
            globalStartArea
          )) {
        localStartArea = _RootArea;
        return true;
      }

      if (!this.IsSameOrDescendant(
            globalStartArea,
            mountedRepository.MountPoint
          )) {
        return false;
      }

      if (mountedRepository.MountPoint == _RootArea) {
        localStartArea = globalStartArea;
        return true;
      }

      string suffix = globalStartArea.Substring(
        mountedRepository.MountPoint.Length
      );

      if (string.IsNullOrEmpty(suffix)) {
        localStartArea = _RootArea;
      }
      else {
        localStartArea = this.NormalizeAreaPath(suffix);
      }

      return true;
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is equal to or located below
    /// <paramref name="ancestor"/>.
    /// </summary>
    private bool IsSameOrDescendant(
      string candidate,
      string ancestor
    ) {
      string normalizedCandidate = this.NormalizeAreaPath(candidate);
      string normalizedAncestor = this.NormalizeAreaPath(ancestor);

      if (normalizedAncestor == _RootArea) {
        return true;
      }

      if (string.Equals(
            normalizedCandidate,
            normalizedAncestor,
            StringComparison.Ordinal
          )) {
        return true;
      }

      return normalizedCandidate.StartsWith(
        normalizedAncestor + "/",
        StringComparison.Ordinal
      );
    }

    /// <summary>
    /// Normalizes one absolute logical aggregated area path.
    /// </summary>
    private string NormalizeAreaPath(string area) {
      if (string.IsNullOrWhiteSpace(area)) {
        return _RootArea;
      }

      string normalized = area.Trim().Replace('\\', '/');

      if (!normalized.StartsWith("/", StringComparison.Ordinal)) {
        normalized = "/" + normalized;
      }

      while (normalized.Contains("//", StringComparison.Ordinal)) {
        normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
      }

      if (normalized.Length > 1 &&
          normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }

    /// <summary>
    /// Combines one logical parent path with one already encoded logical child segment.
    /// </summary>
    private string CombineAreaPath(
      string parent,
      string childSegment
    ) {
      if (parent == _RootArea) {
        return _RootArea + childSegment;
      }

      return parent + "/" + childSegment;
    }

    /// <summary>
    /// Returns the final logical path segment for display in the aggregated projection.
    /// </summary>
    private string GetLastAreaSegment(string area) {
      string normalizedArea = this.NormalizeAreaPath(area);

      if (normalizedArea == _RootArea) {
        return _RootArea;
      }

      int separatorIndex = normalizedArea.LastIndexOf('/');

      if (separatorIndex < 0 ||
          separatorIndex >= normalizedArea.Length - 1) {
        return normalizedArea;
      }

      return normalizedArea.Substring(separatorIndex + 1);
    }

    /// <summary>
    /// Identifies a mutation capability used by unique-provider routing.
    /// </summary>
    private enum CapabilityKind {
      Rename = 0,
      Delete = 1,
      AddSubArea = 2,
      AppendContent = 3,
      Truncate = 4
    }

    /// <summary>
    /// Owns all child cache-policy scopes opened for one aggregated consumer request.
    /// </summary>
    private sealed class CompositeCacheReadScope : IDisposable {

      private readonly IDisposable[] _Scopes;
      private bool _Disposed;

      /// <summary>
      /// Creates one composite scope from already opened child scopes.
      /// </summary>
      public CompositeCacheReadScope(
        IDisposable[] scopes
      ) {
        if (scopes == null) {
          _Scopes =
            Array.Empty<IDisposable>();
        }
        else {
          _Scopes =
            scopes;
        }
      }

      /// <summary>
      /// Closes all child scopes in reverse opening order.
      ///
      /// A failure in one arbitrary child implementation is isolated so the remaining
      /// child scopes are still restored.
      /// </summary>
      public void Dispose() {
        if (_Disposed) {
          return;
        }

        _Disposed =
          true;

        for (int index = _Scopes.Length - 1;
             index >= 0;
             index--) {
          try {
            _Scopes[index].Dispose();
          }
          catch (Exception ex) {
            DevLogger.LogError(
              ex
            );

            DevLogger.LogTrace(
              0,
              99999,
              "Aggregated knowledge cache child scope disposal failed. Remaining child scopes continue to be disposed."
            );
          }
        }
      }
    }

    /// <summary>
    /// Represents one mounted repository registration.
    /// </summary>
    private sealed class MountedRepository {

      private readonly IKnowledgeRepository _Repository;
      private readonly string _MountPoint;
      private readonly int _MountPointOrdinal;
      private readonly int _RegistrationOrder;
      private readonly string _ResourceNamespaceToken;

      /// <summary>
      /// Creates one repository mount registration.
      /// </summary>
      public MountedRepository(
        IKnowledgeRepository repository,
        string mountPoint,
        int mountPointOrdinal,
        int registrationOrder
      ) {
        _Repository = repository;
        _MountPoint = mountPoint;
        _MountPointOrdinal = mountPointOrdinal;
        _RegistrationOrder = registrationOrder;
        _ResourceNamespaceToken = this.CreateResourceNamespaceToken(
          mountPoint,
          mountPointOrdinal
        );
      }

      /// <summary>
      /// Gets the mounted repository.
      /// </summary>
      public IKnowledgeRepository Repository {
        get {
          return _Repository;
        }
      }

      /// <summary>
      /// Gets the absolute global mount point.
      /// </summary>
      public string MountPoint {
        get {
          return _MountPoint;
        }
      }

      /// <summary>
      /// Gets the zero-based provider ordinal within this exact mount point.
      /// </summary>
      public int MountPointOrdinal {
        get {
          return _MountPointOrdinal;
        }
      }

      /// <summary>
      /// Gets the global registration order used exclusively for deterministic aggregate
      /// ordering and legacy resource-ID compatibility.
      /// </summary>
      public int RegistrationOrder {
        get {
          return _RegistrationOrder;
        }
      }

      /// <summary>
      /// Gets the opaque resource namespace token for this mounted provider instance.
      /// </summary>
      public string ResourceNamespaceToken {
        get {
          return _ResourceNamespaceToken;
        }
      }

      /// <summary>
      /// Creates the stable mount-instance namespace token used by aggregate ResourceIds.
      /// </summary>
      private string CreateResourceNamespaceToken(
        string mountPoint,
        int mountPointOrdinal
      ) {
        string nativeIdentity =
          mountPoint
          + "|"
          + mountPointOrdinal.ToString(
            CultureInfo.InvariantCulture
          );

        string encoded = Convert.ToBase64String(
          Encoding.UTF8.GetBytes(
            nativeIdentity
          )
        );

        return encoded
          .TrimEnd('=')
          .Replace('+', '-')
          .Replace('/', '_');
      }
    }

    /// <summary>
    /// Represents one provider's contribution to one global overlay area.
    /// </summary>
    private sealed class AreaContribution {

      private readonly MountedRepository _MountedRepository;
      private readonly string _LocalArea;

      /// <summary>
      /// Creates one concrete area contribution.
      /// </summary>
      public AreaContribution(
        MountedRepository mountedRepository,
        string localArea
      ) {
        _MountedRepository = mountedRepository;
        _LocalArea = localArea;
      }

      /// <summary>
      /// Gets the contributing mounted repository.
      /// </summary>
      public MountedRepository MountedRepository {
        get {
          return _MountedRepository;
        }
      }

      /// <summary>
      /// Gets the local area path within the mounted repository.
      /// </summary>
      public string LocalArea {
        get {
          return _LocalArea;
        }
      }
    }

    /// <summary>
    /// Represents one global overlay-tree node.
    /// </summary>
    private sealed class AggregatedNode {

      private readonly string _Path;
      private readonly string _DisplayName;
      private AggregatedNode _Parent;
      private readonly List<AggregatedNode> _Children;
      private readonly List<AreaContribution> _Contributions;
      private bool _ChildrenMaterialized;

      /// <summary>
      /// Creates one global tree node.
      /// </summary>
      public AggregatedNode(
        string path,
        string displayName
      ) {
        _Path = path;
        _DisplayName = displayName;
        _Children = new List<AggregatedNode>();
        _Contributions = new List<AreaContribution>();
        _ChildrenMaterialized = false;
      }

      /// <summary>
      /// Gets the absolute global area path.
      /// </summary>
      public string Path {
        get {
          return _Path;
        }
      }

      /// <summary>
      /// Gets the direct display name.
      /// </summary>
      public string DisplayName {
        get {
          return _DisplayName;
        }
      }

      /// <summary>
      /// Gets or sets the global parent node.
      /// </summary>
      public AggregatedNode Parent {
        get {
          return _Parent;
        }
        set {
          _Parent = value;
        }
      }

      /// <summary>
      /// Gets the ordered global child nodes.
      /// </summary>
      public List<AggregatedNode> Children {
        get {
          return _Children;
        }
      }

      /// <summary>
      /// Gets all concrete mounted-provider contributions to this global area.
      /// </summary>
      public List<AreaContribution> Contributions {
        get {
          return _Contributions;
        }
      }

      /// <summary>
      /// Gets or sets whether direct provider children have already been materialized for
      /// this node.
      /// </summary>
      public bool ChildrenMaterialized {
        get {
          return _ChildrenMaterialized;
        }
        set {
          _ChildrenMaterialized = value;
        }
      }

      /// <summary>
      /// Adds one provider contribution when the same mount registration and local area
      /// have not already been registered.
      /// </summary>
      public void AddContribution(AreaContribution contribution) {
        bool exists = _Contributions.Any((AreaContribution candidate) =>
          object.ReferenceEquals(
            candidate.MountedRepository,
            contribution.MountedRepository
          ) &&
          string.Equals(
            candidate.LocalArea,
            contribution.LocalArea,
            StringComparison.Ordinal
          ));

        if (!exists) {
          _Contributions.Add(contribution);
        }
      }
    }

    /// <summary>
    /// Represents the complete current global overlay tree.
    /// </summary>
    private sealed class AggregatedTree {

      private readonly Dictionary<string, AggregatedNode> _Nodes;
      private readonly AggregatedNode _Root;

      /// <summary>
      /// Creates an empty aggregated tree containing only the global root.
      /// </summary>
      public AggregatedTree() {
        _Nodes = new Dictionary<string, AggregatedNode>(StringComparer.Ordinal);
        _Root = new AggregatedNode("/", "/");
        _Nodes.Add("/", _Root);
      }

      /// <summary>
      /// Finds a global node by canonical absolute path.
      /// </summary>
      public AggregatedNode Find(string path) {
        if (_Nodes.TryGetValue(path, out AggregatedNode node)) {
          return node;
        }

        return null;
      }

      /// <summary>
      /// Gets an existing node or creates it together with any required parent relation.
      /// 
      /// Child ordering follows first appearance. Once a global child exists, later
      /// provider overlays do not change its position.
      /// </summary>
      public AggregatedNode GetOrCreate(
        string path,
        string displayName
      ) {
        if (_Nodes.TryGetValue(path, out AggregatedNode existing)) {
          return existing;
        }

        string parentPath = this.GetParentPath(path);
        AggregatedNode parent = this.GetOrCreate(
          parentPath,
          this.GetDisplayName(parentPath)
        );

        AggregatedNode node = new AggregatedNode(
          path,
          displayName
        );

        node.Parent = parent;
        parent.Children.Add(node);
        _Nodes.Add(path, node);

        return node;
      }

      /// <summary>
      /// Gets the parent path of one absolute logical path.
      /// </summary>
      private string GetParentPath(string path) {
        if (path == "/") {
          return "/";
        }

        int separatorIndex = path.LastIndexOf('/');

        if (separatorIndex <= 0) {
          return "/";
        }

        return path.Substring(0, separatorIndex);
      }

      /// <summary>
      /// Gets the display segment of one absolute logical path.
      /// </summary>
      private string GetDisplayName(string path) {
        if (path == "/") {
          return "/";
        }

        int separatorIndex = path.LastIndexOf('/');

        if (separatorIndex < 0 ||
            separatorIndex >= path.Length - 1) {
          return path;
        }

        return path.Substring(separatorIndex + 1);
      }
    }
  }
}
