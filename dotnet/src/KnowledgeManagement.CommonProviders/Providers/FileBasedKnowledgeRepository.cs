using Logging.SmartStandards.CopyForKnowledgeManagement;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Implements <see cref="IKnowledgeRepository"/> on top of a file-system directory
  /// containing an arbitrary hierarchy of directories and Markdown files.
  /// 
  /// The configured directory itself represents the logical repository root "/".
  /// Directories are exposed with square brackets in their logical area segment.
  /// A directory is <see cref="ContentLevel.ContentAggregation"/> only when it contains
  /// at least one Markdown file directly on that directory level. A directory without
  /// direct Markdown files is <see cref="ContentLevel.BeyondContent"/> and therefore acts
  /// as a pure navigation node.
  /// 
  /// Markdown files are exposed as unbracketed
  /// <see cref="ContentLevel.ContentContainer"/> areas. Markdown headings below a
  /// document are exposed as additional <see cref="ContentLevel.ContentContainer"/>
  /// areas.
  /// 
  /// Directory aggregation is deliberately limited to one physical directory level.
  /// Subdirectories are exposed as separate bracketed navigation areas and are never
  /// recursively folded into the aggregated Markdown content of their parent directory.
  /// 
  /// Files other than Markdown files are not exposed as knowledge areas. They are
  /// therefore ignored by content enumeration and content reads. Directory rename
  /// operations naturally move such files together with their containing directory,
  /// because the directory itself is the provider's physical representation of the
  /// logical aggregation area.
  ///
  /// Physical exposure is filtered before files, directories or binary resources enter the
  /// logical model. The default global blacklist excludes <c>**/.git</c> and <c>**/.vs</c>.
  /// Hierarchical <c>.gitignore</c> files are enabled by default and are scoped to their own
  /// directory subtree. Static blacklist and whitelist rules can be configured independently.
  /// 
  /// The implementation uses atomic mutation scopes. Before a mutating file-system
  /// operation is published, a provider-level snapshot is created. If the mutation
  /// cannot be completed, the previous state is restored.
  /// </summary>
  public class FileBasedKnowledgeRepository : IKnowledgeRepository {

    private const int _FileIoRetryCount = 5;
    private const int _FileIoRetryDelayMilliseconds = 100;

    private const string _MarkdownExtension = ".md";
    private const string _RootArea = "/";
    private const int _MaximumMarkdownHeadingLevel = 6;
    private const string _ResourceMarker = ".Res";
    private const string _KnowledgeResourceReferencePrefix = "knowledge-resource:";
    private const int _Snowflake44TimestampBits = 44;
    private const int _Snowflake44NodeBits = 10;
    private const int _Snowflake44SequenceBits = 9;
    private const long _Snowflake44TimestampMask = (1L << _Snowflake44TimestampBits) - 1L;
    private const long _Snowflake44SequenceMask = (1L << _Snowflake44SequenceBits) - 1L;
    private const long _Snowflake44NodeMask = (1L << _Snowflake44NodeBits) - 1L;
    private static readonly DateTime _Snowflake44EpochUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Regex _AtxHeadingRegex = new Regex(
      @"^( {0,3})(#{1,6})(?:[ \t]+|$)(.*?)(?:[ \t]+#+[ \t]*)?(?:\r\n|\n|\r)?$",
      RegexOptions.Compiled
    );

    private static readonly Regex _OwnedResourceFileRegex = new Regex(
      @"^(?<document>.+)\.Res(?<token>[0-9]+)(?<extension>\.[^\\/]+)$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _KnowledgeResourceReferenceRegex = new Regex(
      @"knowledge-resource:(?<id>[A-Za-z0-9._~-]+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _LegacyNumericKnowledgeResourceReferenceRegex = new Regex(
      @"knowledge-resource:(?<uid>[0-9]+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _MarkdownInlineLinkRegex = new Regex(
      @"(?<prefix>!?\[[^\]]*\]\()(?<target><[^>\r\n]+>|[^)\s\r\n]+)(?<suffix>\))",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly string[] _DefaultBlacklist = new string[] {
      "**/.git",
      "**/.vs"
    };

    protected readonly object _SyncRoot = new object();

    private string _RootDirectory;
    private readonly bool _ReadOnly;
    private readonly bool _UseSoftDelete;
    private string[] _Blacklist;
    private string[] _Whitelist;
    private bool _UseGitIgnoreFiles;
    private FileSystemExposureFilter _ExposureFilter;
    private bool _Initialized;
    private readonly object _GeneratedResourceNameSyncRoot = new object();
    private long _LastGeneratedResourceTimestamp;
    private long _LastGeneratedResourceSequence;

    /// <summary>
    /// Creates a file-based knowledge repository rooted at the specified directory.
    ///
    /// The directory is created when it does not yet exist and the repository is not
    /// read-only. A read-only repository requires the directory to exist.
    ///
    /// No global process configuration is modified. All operations are scoped to the
    /// supplied directory.
    ///
    /// The default exposure policy blocks <c>**/.git</c> and <c>**/.vs</c> everywhere and
    /// evaluates hierarchical <c>.gitignore</c> files.
    /// </summary>
    /// <param name="rootDirectory">
    /// The physical directory that represents the logical knowledge repository root.
    /// </param>
    /// <param name="readOnly">
    /// true to disable all mutating operations; false to allow mutations according to
    /// the concrete area's capabilities.
    /// </param>
    public FileBasedKnowledgeRepository(
      string rootDirectory,
      bool readOnly
    ) : this(
      rootDirectory,
      readOnly,
      false
    ) {
    }

    /// <summary>
    /// Creates a file-based knowledge repository rooted at the specified directory and
    /// optionally enables reversible soft deletion for Markdown documents.
    ///
    /// The default exposure policy blocks <c>**/.git</c> and <c>**/.vs</c> everywhere and
    /// evaluates hierarchical <c>.gitignore</c> files.
    /// </summary>
    /// <param name="rootDirectory">
    /// The physical directory that represents the logical knowledge repository root.
    /// </param>
    /// <param name="readOnly">
    /// true to disable all mutating operations; false to allow mutations according to
    /// the concrete area's capabilities.
    /// </param>
    /// <param name="useSoftDelete">
    /// true to rename deleted Markdown documents to
    /// &lt;original-name&gt;.DELETED.md instead of removing them physically. Soft-deleted
    /// documents are ignored by the logical repository model.
    /// </param>
    public FileBasedKnowledgeRepository(
      string rootDirectory,
      bool readOnly,
      bool useSoftDelete
    ) : this(
      rootDirectory,
      readOnly,
      useSoftDelete,
      _DefaultBlacklist,
      Array.Empty<string>(),
      true
    ) {
    }

    /// <summary>
    /// Creates a file-based repository with an explicit exposure policy.
    ///
    /// Static blacklist and whitelist rules are evaluated globally below the configured
    /// repository root. When <paramref name="useGitIgnoreFiles"/> is enabled, every
    /// <c>.gitignore</c> file is evaluated only for its own directory subtree and is combined
    /// with the static policy.
    /// </summary>
    /// <param name="rootDirectory">The physical repository root.</param>
    /// <param name="readOnly">Whether all repository mutations are disabled.</param>
    /// <param name="useSoftDelete">Whether provider deletes use reversible soft deletion.</param>
    /// <param name="blacklist">Global ignore patterns. Blacklist matches hide files and directories.</param>
    /// <param name="whitelist">
    /// Global include patterns evaluated after ignore-file and blacklist rules. Explicit
    /// whitelist matches can re-include a statically or dynamically ignored path.
    /// </param>
    /// <param name="useGitIgnoreFiles">
    /// Whether hierarchical <c>.gitignore</c> files participate in exposure decisions.
    /// </param>
    public FileBasedKnowledgeRepository(
      string rootDirectory,
      bool readOnly,
      bool useSoftDelete,
      string[] blacklist,
      string[] whitelist,
      bool useGitIgnoreFiles
    ) {
      _RootDirectory = string.Empty;
      _ReadOnly = readOnly;
      _UseSoftDelete = useSoftDelete;
      _Blacklist = this.NormalizeExposurePatterns(
        blacklist
      );
      _Whitelist = this.NormalizeExposurePatterns(
        whitelist
      );
      _UseGitIgnoreFiles = useGitIgnoreFiles;
      _ExposureFilter = null;
      _Initialized = false;
      _LastGeneratedResourceTimestamp = -1;
      _LastGeneratedResourceSequence = 0;

      this.InitializeRootDirectory(
        rootDirectory
      );
    }

    /// <summary>
    /// Initializes the base repository without assigning a physical root immediately.
    ///
    /// This constructor exists for derived providers that must prepare their storage
    /// before the file-based projection can be attached to it.
    /// </summary>
    /// <param name="readOnly">Whether the derived repository is read-only.</param>
    protected FileBasedKnowledgeRepository(
      bool readOnly
    ) : this(
      readOnly,
      false
    ) {
    }

    /// <summary>
    /// Initializes the base repository without assigning a physical root immediately and
    /// optionally enables reversible soft deletion.
    /// </summary>
    /// <param name="readOnly">Whether the derived repository is read-only.</param>
    /// <param name="useSoftDelete">
    /// Whether deleted Markdown documents are preserved as soft-deleted files.
    /// </param>
    protected FileBasedKnowledgeRepository(
      bool readOnly,
      bool useSoftDelete
    ) : this(
      readOnly,
      useSoftDelete,
      _DefaultBlacklist,
      Array.Empty<string>(),
      true
    ) {
    }

    /// <summary>
    /// Initializes the base repository for a derived provider with an explicit exposure
    /// policy while deferring assignment of the physical root.
    /// </summary>
    /// <param name="readOnly">Whether the derived repository is read-only.</param>
    /// <param name="useSoftDelete">Whether provider deletes use reversible soft deletion.</param>
    /// <param name="blacklist">Global ignore patterns.</param>
    /// <param name="whitelist">Global include patterns.</param>
    /// <param name="useGitIgnoreFiles">Whether hierarchical .gitignore files are evaluated.</param>
    protected FileBasedKnowledgeRepository(
      bool readOnly,
      bool useSoftDelete,
      string[] blacklist,
      string[] whitelist,
      bool useGitIgnoreFiles
    ) {
      _RootDirectory = string.Empty;
      _ReadOnly = readOnly;
      _UseSoftDelete = useSoftDelete;
      _Blacklist = this.NormalizeExposurePatterns(
        blacklist
      );
      _Whitelist = this.NormalizeExposurePatterns(
        whitelist
      );
      _UseGitIgnoreFiles = useGitIgnoreFiles;
      _ExposureFilter = null;
      _Initialized = false;
      _LastGeneratedResourceTimestamp = -1;
      _LastGeneratedResourceSequence = 0;
    }

    /// <summary>
    /// Gets or replaces the global exposure blacklist.
    ///
    /// The default value is <c>**/.git</c> and <c>**/.vs</c>. Rules use the same glob
    /// vocabulary as the exposure filter. Changes apply immediately to subsequent repository
    /// operations.
    /// </summary>
    public string[] Blacklist {
      get {
        lock (_SyncRoot) {
          return _Blacklist.ToArray();
        }
      }
      set {
        lock (_SyncRoot) {
          _Blacklist =
            this.NormalizeExposurePatterns(
              value
            );

          this.RebuildExposureFilter();
        }
      }
    }

    /// <summary>
    /// Gets or replaces the global exposure whitelist.
    ///
    /// Whitelist rules are explicit exceptions and are evaluated after .gitignore and static
    /// blacklist rules.
    /// </summary>
    public string[] Whitelist {
      get {
        lock (_SyncRoot) {
          return _Whitelist.ToArray();
        }
      }
      set {
        lock (_SyncRoot) {
          _Whitelist =
            this.NormalizeExposurePatterns(
              value
            );

          this.RebuildExposureFilter();
        }
      }
    }

    /// <summary>
    /// Gets or sets whether hierarchical .gitignore files participate in exposure filtering.
    ///
    /// Each .gitignore file affects only its own directory and descendants. Rules discovered
    /// below one branch never affect siblings or ancestors.
    /// </summary>
    public bool UseGitIgnoreFiles {
      get {
        lock (_SyncRoot) {
          return _UseGitIgnoreFiles;
        }
      }
      set {
        lock (_SyncRoot) {
          _UseGitIgnoreFiles =
            value;

          this.RebuildExposureFilter();
        }
      }
    }

    /// <summary>
    /// Gets whether this repository instance is read-only.
    /// </summary>
    protected bool IsReadOnly {
      get {
        return _ReadOnly;
      }
    }

    /// <summary>
    /// Gets the physical root directory used by this provider.
    /// </summary>
    protected string RootDirectory {
      get {
        this.EnsureInitialized();
        return _RootDirectory;
      }
    }

    /// <summary>
    /// Initializes the physical root directory used by the file-based provider.
    /// 
    /// Derived providers may call this once after preparing their storage.
    /// </summary>
    /// <param name="rootDirectory">The physical knowledge root directory.</param>
    protected void InitializeRootDirectory(string rootDirectory) {
      if (_Initialized) {
        throw new InvalidOperationException("The knowledge repository root directory has already been initialized.");
      }

      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentException("A knowledge repository root directory is required.", nameof(rootDirectory));
      }

      string fullPath = Path.GetFullPath(rootDirectory);

      if (!Directory.Exists(fullPath)) {
        if (_ReadOnly) {
          throw new DirectoryNotFoundException("The read-only knowledge repository directory does not exist: " + fullPath);
        }

        Directory.CreateDirectory(fullPath);
      }

      _RootDirectory = fullPath;
      _Initialized = true;

      this.RebuildExposureFilter();
    }

    /// <summary>
    /// Normalizes one externally supplied exposure pattern collection.
    /// </summary>
    private string[] NormalizeExposurePatterns(
      string[] patterns
    ) {
      if (patterns == null ||
          patterns.Length == 0) {
        return Array.Empty<string>();
      }

      return patterns
        .Where(
          (string pattern) => !string.IsNullOrWhiteSpace(
            pattern
          )
        )
        .Select(
          (string pattern) => pattern.Trim()
        )
        .Distinct(
          StringComparer.Ordinal
        )
        .ToArray();
    }

    /// <summary>
    /// Rebuilds the provider-local exposure filter after root or configuration changes.
    /// </summary>
    private void RebuildExposureFilter() {
      if (!_Initialized) {
        return;
      }

      _ExposureFilter =
        new FileSystemExposureFilter(
          _RootDirectory,
          _Blacklist,
          _Whitelist,
          _UseGitIgnoreFiles
        );
    }

    /// <summary>
    /// Returns whether one physical file-system path is allowed to participate in the
    /// repository's externally visible model.
    /// </summary>
    private bool IsPhysicalPathExposed(
      string physicalPath,
      bool isDirectory
    ) {
      this.EnsureInitialized();

      if (_ExposureFilter == null) {
        this.RebuildExposureFilter();
      }

      return _ExposureFilter.IsVisible(
        physicalPath,
        isDirectory
      );
    }

    /// <summary>
    /// Returns logical area paths below the specified start area.
    /// 
    /// Directories are returned in deterministic ordinal name order using this provider's
    /// bracketed logical path syntax. Markdown documents use unbracketed logical path
    /// segments and participate in the same deterministic directory ordering. Headings
    /// inside a Markdown document preserve their exact
    /// document order.
    /// 
    /// Recursive enumeration uses pre-order traversal. Each parent is returned before
    /// its descendants.
    /// </summary>
    /// <param name="recurse">
    /// true to return the complete descendant tree; false to return direct children only.
    /// </param>
    /// <param name="startArea">
    /// The absolute logical start area. "/" represents the repository root.
    /// </param>
    /// <returns>The matching logical area paths in deterministic hierarchical order.</returns>
    public virtual string[] GetAreas(bool recurse, string startArea = "/") {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor startDescriptor = this.ResolveArea(startArea, null);
        List<string> result = new List<string>();

        this.CollectChildAreas(startDescriptor, recurse, result, null);

        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the direct provider-neutral logical display name of one area.
    /// Consumers never need to decode this provider's path-segment syntax.
    /// </summary>
    public virtual string GetAreaName(
      string area
    ) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(
          area,
          null
        );

        if (descriptor.Kind == AreaKind.Root) {
          return "Knowledge";
        }

        return descriptor.DisplayName;
      }
    }

    /// <summary>
    /// Searches all descendant areas below <paramref name="startArea"/> for the supplied
    /// keyword.
    /// 
    /// The implementation searches each area's logical path and display name. For
    /// content-container areas it additionally searches direct textual content.
    /// 
    /// Matching is case-insensitive and ordinal. Results preserve the natural repository
    /// order returned by recursive area enumeration.
    /// </summary>
    /// <param name="keyword">The keyword to search for.</param>
    /// <param name="startArea">The absolute logical search scope.</param>
    /// <returns>Matching absolute logical area paths in deterministic order.</returns>
    public virtual string[] GetAreasByKeyword(string keyword, string startArea = "/") {
      if (string.IsNullOrWhiteSpace(keyword)) {
        return Array.Empty<string>();
      }

      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor startDescriptor = this.ResolveArea(startArea, null);
        List<string> allAreas = new List<string>();
        List<string> result = new List<string>();

        this.CollectChildAreas(startDescriptor, true, allAreas, null);

        foreach (string areaPath in allAreas) {
          AreaDescriptor descriptor = this.ResolveArea(areaPath, null);

          if (areaPath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            result.Add(areaPath);
            continue;
          }

          if (descriptor.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            result.Add(areaPath);
            continue;
          }

          if (descriptor.ContentLevel == ContentLevel.ContentContainer) {
            string directContent = this.GetDirectContentCore(descriptor);

            if (directContent.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
              result.Add(areaPath);
            }
          }
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the effective capabilities of a logical area.
    /// 
    /// Directories support child areas. A directory is exposed as a content aggregation
    /// only while it directly contains at least one visible Markdown document; otherwise
    /// it is a pure navigation area. When writable, structural and content-bearing child
    /// creation is selected through <see cref="KnowledgeAreaKind"/> rather than encoded
    /// into the requested logical name.
    /// 
    /// Markdown documents and headings are content containers. They may own direct
    /// content, may receive appended content, and may contain subordinate headings.
    /// 
    /// A Markdown heading at physical level 6 cannot create additional nested headings
    /// because standard Markdown does not provide a deeper ATX heading level.
    /// 
    /// In read-only mode every mutation capability is false regardless of physical
    /// file-system permissions.
    /// </summary>
    public virtual void GetAreaCapabilities(
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
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);

        contentLevel = descriptor.ContentLevel;
        supportsSubAreas = this.GetSupportsSubAreas(descriptor);
        supportsResources = descriptor.Kind == AreaKind.Document ||
          descriptor.Kind == AreaKind.Heading;

        if (_ReadOnly) {
          canBeRenamed = false;
          canBeDeleted = false;
          canAddSubAreas = false;
          canAppendContent = false;
          canTruncate = false;
          return;
        }

        canBeRenamed = descriptor.Kind != AreaKind.Root;
        canBeDeleted = descriptor.Kind != AreaKind.Root;
        canAddSubAreas = this.GetCanAddSubAreas(descriptor);
        canAppendContent = descriptor.ContentLevel != ContentLevel.BeyondContent;
        canTruncate = descriptor.ContentLevel != ContentLevel.BeyondContent;
      }
    }

    /// <summary>
    /// Returns all resources referenced by the Markdown document containing the addressed
    /// area. Physical relative Markdown paths are translated to opaque repository resource
    /// identifiers before they leave this provider.
    /// </summary>
    public virtual KnowledgeResourceInfo[] GetResources(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(
          area,
          null
        );

        string documentPath = this.GetResourceDocumentPath(
          descriptor
        );

        string physicalContent = File.ReadAllText(
          documentPath,
          Encoding.UTF8
        );

        string knowledgeContent = this.ConvertPhysicalMarkdownToKnowledgeMarkdown(
          documentPath,
          physicalContent
        );

        string[] resourceIds = this.GetReferencedResourceIds(
          knowledgeContent
        );

        List<KnowledgeResourceInfo> result = new List<KnowledgeResourceInfo>();

        foreach (string resourceId in resourceIds) {
          result.Add(
            this.GetResourceInfo(
              resourceId
            )
          );
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the complete binary content of one opaque FileBased resource identifier.
    /// </summary>
    public virtual byte[] GetResourceContent(string resourceId) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        string resourcePath = this.ResolveResourcePath(
          resourceId
        );

        if (!File.Exists(resourcePath)) {
          throw new InvalidOperationException(
            "The requested knowledge resource does not exist."
          );
        }

        return File.ReadAllBytes(
          resourcePath
        );
      }
    }

    /// <summary>
    /// Creates one new physical resource beside the Markdown document containing the
    /// addressed area.
    ///
    /// A safe preferred file name is preserved when possible. If no usable file name is
    /// available, the provider creates the owned-resource fallback
    /// &lt;Document&gt;.Res&lt;Snowflake44&gt;.&lt;extension&gt;.
    /// </summary>
    public virtual bool TryAddResource(
      string area,
      string preferredFileName,
      string contentType,
      byte[] content,
      out string resourceId
    ) {
      resourceId = string.Empty;

      if (content == null) {
        return false;
      }

      string allocatedResourceId = string.Empty;

      bool succeeded = this.ExecuteMutation(
        "Add knowledge resource below '" + area + "'",
        (MutationContext context) => {
          AreaDescriptor descriptor = this.ResolveArea(
            area,
            context
          );

          string documentPath = this.GetResourceDocumentPath(
            descriptor
          );

          string resourcePath = this.CreateNewResourcePath(
            documentPath,
            preferredFileName,
            contentType
          );

          this.WriteAllBytesAtomically(
            resourcePath,
            content
          );

          allocatedResourceId = this.CreateResourceId(
            resourcePath
          );

          return true;
        }
      );

      if (succeeded) {
        resourceId = allocatedResourceId;
      }

      return succeeded;
    }

    /// <summary>
    /// Replaces the binary content of one opaque resource identifier without changing its
    /// provider-native identity.
    /// </summary>
    public virtual bool TryReplaceResource(
      string resourceId,
      string contentType,
      byte[] content
    ) {
      if (string.IsNullOrWhiteSpace(resourceId) || content == null) {
        return false;
      }

      return this.ExecuteMutation(
        "Replace knowledge resource",
        (MutationContext context) => {
          string resourcePath = this.ResolveResourcePath(
            resourceId
          );

          if (!File.Exists(resourcePath)) {
            return false;
          }

          this.WriteAllBytesAtomically(
            resourcePath,
            content
          );

          return true;
        }
      );
    }

    /// <summary>
    /// Deletes one physical resource only when no exposed knowledge content references its
    /// opaque repository identifier anymore.
    /// </summary>
    public virtual bool TryDeleteResource(string resourceId) {
      if (string.IsNullOrWhiteSpace(resourceId)) {
        return false;
      }

      return this.ExecuteMutation(
        "Delete knowledge resource",
        (MutationContext context) => {
          string resourcePath = this.ResolveResourcePath(
            resourceId
          );

          if (!File.Exists(resourcePath)) {
            return false;
          }

          if (this.IsResourceReferencedAnywhere(
                resourceId,
                context
              )) {
            return false;
          }

          this.DeleteResourceFile(
            resourcePath
          );

          return true;
        }
      );
    }

    /// <summary>
    /// Determines whether the specified area owns non-empty direct textual content.
    /// 
    /// Content aggregation areas never own direct content and therefore always return
    /// false. Markdown document and heading containers return true only when their own
    /// direct content block contains non-whitespace text.
    /// </summary>
    public virtual bool HasDirectContent(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);

        if (descriptor.ContentLevel != ContentLevel.ContentContainer) {
          return false;
        }

        string content = this.GetDirectContentCore(descriptor);
        return !string.IsNullOrWhiteSpace(content);
      }
    }

    /// <summary>
    /// Returns only the direct textual content owned by the specified area.
    /// 
    /// Directory areas never own direct content and therefore return an empty string,
    /// regardless of whether the concrete directory is BeyondContent or ContentAggregation.
    /// A Markdown document returns its preamble before the first heading. A Markdown
    /// heading returns the text belonging to that heading before its first child heading.
    /// </summary>
    public virtual string GetDirectContent(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);
        return this.GetDirectContentCore(descriptor);
      }
    }

    /// <summary>
    /// Returns the complete textual content exposed through the specified area.
    /// 
    /// For a Markdown document or heading, the result consists of its direct content plus
    /// its complete subordinate heading tree. The addressed heading itself is not emitted
    /// as framing; descendants are rebased relative to the requested area.
    /// 
    /// For a directory aggregation, the result contains only Markdown documents located
    /// directly in that directory. Every direct document is rendered as one level-one
    /// section. Subdirectories are not recursively aggregated; they remain independent
    /// bracketed navigation areas that must be entered explicitly.
    /// </summary>
    public virtual string GetAggregatedContent(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);

        if (descriptor.ContentLevel == ContentLevel.BeyondContent) {
          return string.Empty;
        }

        if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
          StringBuilder aggregationBuilder = new StringBuilder();
          this.RenderDirectDocumentAggregation(
            descriptor,
            aggregationBuilder
          );
          return aggregationBuilder.ToString();
        }

        MarkdownDocumentModel document = descriptor.Document;
        MarkdownNode contentNode = descriptor.Node;

        if (descriptor.Kind == AreaKind.Document) {
          contentNode = document.Root;
        }

        return this.RenderContentSubtree(contentNode, document.NewLine);
      }
    }

    /// <summary>
    /// Atomically deletes the specified area and its complete descendant tree.
    /// 
    /// Deleting a directory removes the represented directory recursively. Deleting a
    /// Markdown document removes the represented `.md` file. Deleting a heading removes
    /// that heading, its direct content, and every subordinate heading in the same
    /// document.
    /// 
    /// Deleting the logical root is not allowed.
    /// </summary>
    public virtual bool TryDelete(string area) {
      return this.ExecuteMutation(
        "Delete knowledge area '" + area + "'",
        (MutationContext context) => this.TryDeleteCore(area, context)
      );
    }

    /// <summary>
    /// Atomically renames the specified logical area while preserving its complete
    /// logical subtree and sibling position.
    /// 
    /// The physical effect intentionally depends on the addressed area:
    /// 
    /// - Renaming a directory renames the physical directory. Every Markdown document
    ///   and nested directory below it therefore moves together with the directory.
    /// - Renaming a Markdown document renames the physical `.md` file while preserving
    ///   its content.
    /// - Renaming a heading changes the Markdown heading text while preserving its direct
    ///   content and subordinate headings.
    /// 
    /// This provider-specific physical behavior is intentionally hidden behind the
    /// logical rename operation.
    /// </summary>
    public virtual bool TryRename(
      string area,
      string newName,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      List<KnowledgeResourceIdChange> changes =
        new List<KnowledgeResourceIdChange>();

      bool succeeded = this.ExecuteMutation(
        "Rename knowledge area '" + area + "' to '" + newName + "'",
        (MutationContext context) => this.TryRenameCore(
          area,
          newName,
          context,
          changes
        )
      );

      if (succeeded) {
        resourceIdChanges = changes.ToArray();
      }
      else {
        resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();
      }

      return succeeded;
    }

    /// <summary>
    /// Atomically creates one direct child area using only provider-neutral semantic
    /// creation intent. The caller never encodes filesystem representation choices into
    /// the logical name.
    /// </summary>
    public virtual bool TryAddSubArea(
      string area,
      string name,
      KnowledgeAreaKind kind
    ) {
      return this.ExecuteMutation(
        "Add knowledge sub-area '" + name + "' below '" + area + "'",
        (MutationContext context) => this.TryAddSubAreaCore(
          area,
          name,
          kind,
          context
        )
      );
    }

    /// <summary>
    /// Atomically performs a non-destructive sparse hierarchical merge of the supplied
    /// Markdown-shaped content into the target area.
    /// 
    /// On a content container, free text before the first incoming heading is appended
    /// to the target's direct content block. Existing child headings are matched only
    /// among direct children and merged recursively. Missing headings are appended after
    /// existing siblings.
    /// 
    /// On a directory content aggregation, free root text is invalid because aggregation
    /// areas own no direct content. Incoming heading structure is interpreted as logical
    /// subordinate areas. Bracketed headings represent directory areas; unbracketed
    /// headings represent Markdown document containers. Once a document
    /// container is reached, subordinate headings represent content-container sections
    /// inside that document.
    /// 
    /// Existing siblings are never reordered. Existing content is never replaced or
    /// removed by append.
    /// </summary>
    public virtual bool TryAppendContent(string area, string content) {
      return this.ExecuteMutation(
        "Append content to knowledge area '" + area + "'",
        (MutationContext context) => this.TryAppendContentCore(area, content, context)
      );
    }

    /// <summary>
    /// Atomically clears the complete content scope of the addressed area while
    /// preserving the area itself.
    /// 
    /// For a Markdown document, its preamble and all headings are removed while the file
    /// remains present.
    /// 
    /// For a heading, its direct content and subordinate headings are removed while the
    /// addressed heading itself remains present.
    /// 
    /// For a directory aggregation, all Markdown documents located directly in that
    /// directory are removed. Subdirectories are independent navigation scopes and are
    /// preserved. Non-Markdown files remain untouched.
    /// </summary>
    public virtual bool TryTruncate(string area) {
      return this.ExecuteMutation(
        "Truncate knowledge area '" + area + "'",
        (MutationContext context) => this.TryTruncateCore(area, context)
      );
    }

    /// <summary>
    /// Atomically replaces the complete content scope of the addressed area.
    /// 
    /// The operation is semantically equivalent to truncating the area and then applying
    /// the supplied content through sparse hierarchical append, but both phases are
    /// executed within one mutation transaction.
    /// </summary>
    public virtual bool TryReplace(string area, string newContent) {
      return this.ExecuteMutation(
        "Replace content of knowledge area '" + area + "'",
        (MutationContext context) => {
          if (!this.TryTruncateCore(area, context)) {
            return false;
          }

          return this.TryAppendContentCore(area, newContent, context);
        }
      );
    }

    /// <summary>
    /// Atomically moves the complete content scope of one content-capable area into
    /// another content-capable area.
    /// 
    /// The source area itself is preserved and becomes empty after success. Its content
    /// is merged into the target using the same sparse hierarchical merge rules used by
    /// <see cref="TryAppendContent(string, string)"/>.
    /// 
    /// The source and target may belong to different Markdown files or different
    /// directory aggregation branches. The provider therefore may physically transfer
    /// content between files while exposing only one logical content-move operation.
    /// 
    /// The target may not equal the source and may not be located inside the source
    /// subtree.
    /// </summary>
    /// <summary>
    /// Atomically moves one complete logical area below a new logical parent.
    /// 
    /// The first argument identifies the logical element being moved. The second
    /// argument identifies its new parent; it is never interpreted as a replacement
    /// target whose own content should be truncated or overwritten.
    /// 
    /// This provider maps the abstract operation to the most natural filesystem or
    /// Markdown representation available. Documents and directories are physically
    /// moved, while Markdown heading scopes are reparented within or across documents.
    /// </summary>
    public virtual bool TryMoveContent(
      string contentAreaToMove,
      string newParentArea,
      out KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      List<KnowledgeResourceIdChange> changes =
        new List<KnowledgeResourceIdChange>();

      bool succeeded = this.ExecuteMutation(
        "Move knowledge area '"
        + contentAreaToMove
        + "' below '"
        + newParentArea
        + "'",
        (MutationContext context) => this.TryMoveContentCore(
          contentAreaToMove,
          newParentArea,
          context,
          changes
        )
      );

      if (succeeded) {
        resourceIdChanges = changes.ToArray();
      }
      else {
        resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();
      }

      return succeeded;
    }

    /// <summary>
    /// Allows derived providers to refresh or synchronize their backing storage before
    /// a read operation is resolved.
    /// </summary>
    protected virtual void PrepareForRead() {
    }

    /// <summary>
    /// Executes one logical mutation atomically.
    /// 
    /// The file-based implementation snapshots the configured knowledge root before
    /// applying the mutation. Derived providers may override this method to provide a
    /// more efficient transaction mechanism while retaining the same external atomicity
    /// guarantee.
    /// </summary>
    protected virtual bool ExecuteMutation(
      string operationDescription,
      Func<MutationContext, bool> mutation
    ) {
      if (_ReadOnly) {
        return false;
      }

      lock (_SyncRoot) {
        this.EnsureInitialized();

        string snapshotDirectory = string.Empty;

        try {
          snapshotDirectory = this.CreateMutationSnapshot();

          bool success = this.ApplyMutation(mutation);

          if (!success) {
            this.RestoreMutationSnapshot(snapshotDirectory);
            return false;
          }

          this.DeleteMutationSnapshot(snapshotDirectory);
          return true;
        }
        catch (IOException ex) {
          DevLogger.LogError(ex);
          this.TryRestoreMutationSnapshot(snapshotDirectory);
          return false;
        }
        catch (UnauthorizedAccessException ex) {
          DevLogger.LogError(ex);
          this.TryRestoreMutationSnapshot(snapshotDirectory);
          return false;
        }
      }
    }

    /// <summary>
    /// Applies one logical mutation to the currently prepared backing store and persists
    /// every changed Markdown document.
    /// 
    /// Derived providers can reuse this method inside their own transaction mechanism.
    /// </summary>
    protected bool ApplyMutation(Func<MutationContext, bool> mutation) {
      MutationContext context = new MutationContext(this);

      bool success = mutation(context);

      if (!success) {
        return false;
      }

      context.SaveChanges();
      return true;
    }

    /// <summary>
    /// Resolves a logical area path to its provider-specific representation.
    /// </summary>
    protected AreaDescriptor ResolveArea(string area, MutationContext context) {
      this.EnsureInitialized();

      string normalizedArea = this.NormalizeAreaPath(area);

      if (normalizedArea == _RootArea) {
        return AreaDescriptor.CreateRoot(
          _RootDirectory,
          this.GetDirectoryContentLevel(_RootDirectory)
        );
      }

      string[] segments = normalizedArea
        .Split('/', StringSplitOptions.RemoveEmptyEntries);

      AreaDescriptor current = AreaDescriptor.CreateRoot(
        _RootDirectory,
        this.GetDirectoryContentLevel(_RootDirectory)
      );

      foreach (string segment in segments) {
        if (current.Kind == AreaKind.Root || current.Kind == AreaKind.Directory) {
          if (this.IsDirectorySegment(segment)) {
            string directoryName = this.DecodeAreaSegment(
              segment.Substring(1, segment.Length - 2)
            );

            string directoryPath = this.GetSafePhysicalChildPath(
              current.PhysicalPath,
              directoryName,
              string.Empty
            );

            if (!this.IsPhysicalPathExposed(
                  directoryPath,
                  true
                ) ||
                !Directory.Exists(directoryPath)) {
              throw new InvalidOperationException(
                "The knowledge area does not exist: " + normalizedArea
              );
            }

            current = AreaDescriptor.CreateDirectory(
              this.CombineAreaPath(
                current.AreaPath,
                this.CreateDirectorySegment(directoryName)
              ),
              directoryName,
              directoryPath,
              this.GetDirectoryContentLevel(directoryPath)
            );
          }
          else {
            string documentName = this.DecodeAreaSegment(segment);
            string documentPath = this.GetSafePhysicalChildPath(
              current.PhysicalPath,
              documentName,
              _MarkdownExtension
            );

            if (!this.IsPhysicalPathExposed(
                  documentPath,
                  false
                ) ||
                !File.Exists(documentPath)) {
              throw new InvalidOperationException(
                "The knowledge area does not exist: " + normalizedArea
              );
            }

            MarkdownDocumentModel document = this.LoadDocument(documentPath, context);

            current = AreaDescriptor.CreateDocument(
              this.CombineAreaPath(
                current.AreaPath,
                this.EncodeAreaSegment(documentName)
              ),
              documentName,
              documentPath,
              document
            );
          }

          continue;
        }

        MarkdownNode parentNode;

        if (current.Kind == AreaKind.Document) {
          parentNode = current.Document.Root;
        }
        else {
          parentNode = current.Node;
        }

        MarkdownNode childNode = parentNode.Children
          .FirstOrDefault((MarkdownNode candidate) =>
            string.Equals(candidate.LogicalSegment, segment, StringComparison.Ordinal));

        if (childNode == null) {
          throw new InvalidOperationException("The knowledge area does not exist: " + normalizedArea);
        }

        current = AreaDescriptor.CreateHeading(
          this.CombineAreaPath(current.AreaPath, childNode.LogicalSegment),
          childNode.Title,
          current.PhysicalPath,
          current.Document,
          childNode
        );
      }

      return current;
    }

    /// <summary>
    /// Gets the repository-relative physical root used by this instance.
    /// </summary>
    protected string GetPhysicalRootDirectory() {
      return this.RootDirectory;
    }

    /// <summary>
    /// Returns whether a Markdown file is a soft-deleted artifact that must not appear in
    /// the logical knowledge repository.
    /// </summary>
    private bool IsSoftDeletedMarkdownFile(
      string filePath
    ) {
      string fileName = Path.GetFileName(
        filePath
      );

      if (fileName.EndsWith(
            ".DELETED.md",
            StringComparison.OrdinalIgnoreCase
          )) {
        return true;
      }

      return Regex.IsMatch(
        fileName,
        @"\.DELETED\.\d+\.md$",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant
      );
    }

    /// <summary>
    /// Deletes one Markdown document according to the configured delete policy.
    /// </summary>
    private void DeleteMarkdownDocument(
      string filePath
    ) {
      if (!_UseSoftDelete) {
        File.Delete(
          filePath
        );

        return;
      }

      string directoryPath = Path.GetDirectoryName(
        filePath
      );

      if (string.IsNullOrEmpty(directoryPath)) {
        throw new InvalidOperationException(
          "The Markdown document has no physical parent directory."
        );
      }

      string documentName = Path.GetFileNameWithoutExtension(
        filePath
      );

      string deletedPath = Path.Combine(
        directoryPath,
        documentName + ".DELETED.md"
      );

      int suffix = 2;

      while (File.Exists(deletedPath)) {
        deletedPath = Path.Combine(
          directoryPath,
          documentName
          + ".DELETED."
          + suffix.ToString(CultureInfo.InvariantCulture)
          + ".md"
        );

        suffix++;
      }

      File.Move(
        filePath,
        deletedPath
      );

      DevLogger.LogTrace(
        0,
        99999,
        "Knowledge soft-delete: '"
        + filePath
        + "' -> '"
        + deletedPath
        + "'."
      );
    }

    /// <summary>
    /// Enumerates matching files recursively without traversing symbolic links or other
    /// reparse-point directories that could escape the configured repository root.
    /// </summary>
    private string[] GetFilesRecursivelyWithoutReparsePoints(
      string directoryPath,
      string searchPattern
    ) {
      List<string> result = new List<string>();

      this.CollectFilesRecursivelyWithoutReparsePoints(
        directoryPath,
        searchPattern,
        result
      );

      return result.ToArray();
    }

    /// <summary>
    /// Recursively collects matching files while preserving filesystem enumeration order.
    /// </summary>
    private void CollectFilesRecursivelyWithoutReparsePoints(
      string directoryPath,
      string searchPattern,
      List<string> result
    ) {
      string[] files = Directory.GetFiles(
        directoryPath,
        searchPattern,
        SearchOption.TopDirectoryOnly
      );

      foreach (string file in files) {
        if (this.IsReparsePoint(file)) {
          continue;
        }

        if (!this.IsPhysicalPathExposed(
              file,
              false
            )) {
          continue;
        }

        result.Add(file);
      }

      string[] directories = Directory.GetDirectories(
        directoryPath,
        "*",
        SearchOption.TopDirectoryOnly
      );

      foreach (string childDirectory in directories) {
        if (this.IsReparsePoint(childDirectory)) {
          continue;
        }

        string directoryName = Path.GetFileName(
          childDirectory
        );

        if (this.IsProviderInternalDirectory(directoryName)) {
          continue;
        }

        if (!this.IsPhysicalPathExposed(
              childDirectory,
              true
            )) {
          continue;
        }

        this.CollectFilesRecursivelyWithoutReparsePoints(
          childDirectory,
          searchPattern,
          result
        );
      }
    }

    /// <summary>
    /// Soft-deletes all Markdown documents below one physical directory.
    ///
    /// Directory objects are deliberately preserved while soft-delete mode is enabled so
    /// no preserved document is removed indirectly by a recursive directory deletion.
    /// </summary>
    private void SoftDeleteMarkdownDocumentsBelow(
      string directoryPath
    ) {
      string[] markdownFiles = this.GetFilesRecursivelyWithoutReparsePoints(
        directoryPath,
        "*" + _MarkdownExtension
      );

      foreach (string markdownFile in markdownFiles) {
        if (this.IsReparsePoint(markdownFile)) {
          continue;
        }

        if (this.IsSoftDeletedMarkdownFile(markdownFile) ||
            this.IsResourceCompanionFile(markdownFile)) {
          continue;
        }

        this.DeleteDocumentResources(
          markdownFile
        );

        this.DeleteMarkdownDocument(
          markdownFile
        );
      }
    }

    /// <summary>
    /// Returns the physical Markdown document that owns the resource scope of one area.
    /// </summary>
    private string GetResourceDocumentPath(
      AreaDescriptor descriptor
    ) {
      if (descriptor.Kind == AreaKind.Document ||
          descriptor.Kind == AreaKind.Heading) {
        return descriptor.Document.FilePath;
      }

      throw new InvalidOperationException(
        "The addressed knowledge area does not support binary resources."
      );
    }

    /// <summary>
    /// Returns metadata for one opaque FileBased resource identifier.
    /// </summary>
    private KnowledgeResourceInfo GetResourceInfo(string resourceId) {
      string resourcePath = this.ResolveResourcePath(
        resourceId
      );

      if (!File.Exists(resourcePath)) {
        throw new InvalidOperationException(
          "The requested knowledge resource does not exist."
        );
      }

      FileInfo fileInfo = new FileInfo(
        resourcePath
      );

      KnowledgeResourceInfo info = new KnowledgeResourceInfo();
      info.ResourceId = resourceId;
      info.FileName = Path.GetFileName(resourcePath);
      info.ContentType = this.GetContentTypeFromExtension(
        Path.GetExtension(resourcePath)
      );
      info.Length = fileInfo.Length;
      return info;
    }

    /// <summary>
    /// Creates one safe new physical resource path beside the addressed Markdown document.
    /// </summary>
    private string CreateNewResourcePath(
      string documentPath,
      string preferredFileName,
      string contentType
    ) {
      string directory = Path.GetDirectoryName(
        documentPath
      );

      if (string.IsNullOrEmpty(directory)) {
        throw new InvalidOperationException(
          "The Markdown document has no physical parent directory."
        );
      }

      string candidateFileName = this.NormalizePreferredResourceFileName(
        preferredFileName
      );

      if (!string.IsNullOrEmpty(candidateFileName)) {
        string preferredPath = this.GetSafePhysicalChildPath(
          directory,
          Path.GetFileNameWithoutExtension(candidateFileName),
          Path.GetExtension(candidateFileName)
        );

        if (this.IsPhysicalPathExposed(
              preferredPath,
              false
            ) &&
            !File.Exists(preferredPath) &&
            !Directory.Exists(preferredPath)) {
          return preferredPath;
        }
      }

      string extension = this.NormalizeResourceExtension(
        Path.GetExtension(candidateFileName),
        contentType
      );

      string documentName = Path.GetFileNameWithoutExtension(
        documentPath
      );

      while (true) {
        long token = this.CreateGeneratedResourceToken();

        string generatedFileName = documentName
          + _ResourceMarker
          + token.ToString(CultureInfo.InvariantCulture)
          + extension;

        string generatedPath = this.GetSafePhysicalChildPath(
          directory,
          Path.GetFileNameWithoutExtension(generatedFileName),
          Path.GetExtension(generatedFileName)
        );

        if (!this.IsPhysicalPathExposed(
              generatedPath,
              false
            )) {
          throw new InvalidOperationException(
            "The exposure policy does not allow a resource file with the requested content type in this directory."
          );
        }

        if (!File.Exists(generatedPath) &&
            !Directory.Exists(generatedPath)) {
          return generatedPath;
        }
      }
    }

    /// <summary>
    /// Normalizes a preferred external resource file name without interpreting it as an
    /// identity. Directory components are intentionally removed.
    /// </summary>
    private string NormalizePreferredResourceFileName(string preferredFileName) {
      if (string.IsNullOrWhiteSpace(preferredFileName)) {
        return string.Empty;
      }

      string fileName = Path.GetFileName(
        preferredFileName.Trim()
      );

      if (string.IsNullOrWhiteSpace(fileName) ||
          fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
        return string.Empty;
      }

      return fileName;
    }

    /// <summary>
    /// Creates a positive Snowflake44 token used only for provider-generated physical
    /// fallback file names.
    /// </summary>
    private long CreateGeneratedResourceToken() {
      lock (_GeneratedResourceNameSyncRoot) {
        while (true) {
          long currentTimestamp = Convert.ToInt64(
            Math.Floor(
              (DateTime.UtcNow - _Snowflake44EpochUtc).TotalMilliseconds
            )
          );

          if (currentTimestamp < _LastGeneratedResourceTimestamp) {
            currentTimestamp = _LastGeneratedResourceTimestamp;
          }

          if (currentTimestamp > _Snowflake44TimestampMask) {
            throw new InvalidOperationException(
              "The Snowflake44 timestamp range has been exhausted."
            );
          }

          if (currentTimestamp == _LastGeneratedResourceTimestamp) {
            _LastGeneratedResourceSequence++;

            if (_LastGeneratedResourceSequence > _Snowflake44SequenceMask) {
              Thread.Sleep(1);
              continue;
            }
          }
          else {
            _LastGeneratedResourceTimestamp = currentTimestamp;
            _LastGeneratedResourceSequence = 0;
          }

          long nodeId = this.GetSnowflake44NodeId();

          long value =
            (currentTimestamp << (_Snowflake44NodeBits + _Snowflake44SequenceBits)) |
            ((nodeId & _Snowflake44NodeMask) << _Snowflake44SequenceBits) |
            (_LastGeneratedResourceSequence & _Snowflake44SequenceMask);

          if (value > 0) {
            return value;
          }
        }
      }
    }

    /// <summary>
    /// Derives the Snowflake44 node component from machine, process and repository identity.
    /// </summary>
    private long GetSnowflake44NodeId() {
      string identity = Environment.MachineName
        + "|"
        + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
        + "|"
        + _RootDirectory;

      using SHA256 sha256 = SHA256.Create();
      byte[] hash = sha256.ComputeHash(
        Encoding.UTF8.GetBytes(identity)
      );

      long value = BitConverter.ToUInt16(
        hash,
        0
      );

      return value & _Snowflake44NodeMask;
    }

    /// <summary>
    /// Normalizes a resource extension and falls back to a MIME-derived extension where possible.
    /// </summary>
    private string NormalizeResourceExtension(
      string fileExtension,
      string contentType
    ) {
      string extension = fileExtension;

      if (string.IsNullOrWhiteSpace(extension)) {
        extension = this.GetExtensionFromContentType(
          contentType
        );
      }

      if (string.IsNullOrWhiteSpace(extension)) {
        extension = ".bin";
      }

      extension = extension.Trim();

      if (!extension.StartsWith(".", StringComparison.Ordinal)) {
        extension = "." + extension;
      }

      if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
          extension.Contains("/", StringComparison.Ordinal) ||
          extension.Contains("\\", StringComparison.Ordinal)) {
        throw new InvalidOperationException(
          "The resource file extension is not valid."
        );
      }

      return extension.ToLowerInvariant();
    }

    /// <summary>
    /// Creates the opaque public resource identifier for one physical FileBased resource.
    ///
    /// The native identity is the normalized repository-relative path. The public value is
    /// versioned Base64Url so consumers are discouraged from deriving path semantics.
    /// </summary>
    private string CreateResourceId(string resourcePath) {
      string fullResourcePath = Path.GetFullPath(
        resourcePath
      );

      this.EnsurePathInsideRepository(
        fullResourcePath
      );

      if (!this.IsPhysicalPathExposed(
            fullResourcePath,
            false
          )) {
        throw new InvalidOperationException(
          "The requested resource is excluded by the file-system exposure policy."
        );
      }

      string relativePath = Path.GetRelativePath(
        _RootDirectory,
        fullResourcePath
      ).Replace(
        Path.DirectorySeparatorChar,
        '/'
      ).Replace(
        Path.AltDirectorySeparatorChar,
        '/'
      );

      byte[] bytes = Encoding.UTF8.GetBytes(
        relativePath
      );

      string encoded = Convert.ToBase64String(
        bytes
      ).TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

      return "1." + encoded;
    }

    /// <summary>
    /// Resolves one opaque FileBased resource identifier back to its physical resource path.
    /// </summary>
    private string ResolveResourcePath(string resourceId) {
      if (string.IsNullOrWhiteSpace(resourceId) ||
          !resourceId.StartsWith("1.", StringComparison.Ordinal)) {
        throw new InvalidOperationException(
          "The FileBased resource identifier is invalid or uses an unsupported encoding version."
        );
      }

      string encoded = resourceId.Substring(2)
        .Replace('-', '+')
        .Replace('_', '/');

      int remainder = encoded.Length % 4;

      if (remainder == 2) {
        encoded += "==";
      }
      else if (remainder == 3) {
        encoded += "=";
      }
      else if (remainder == 1) {
        throw new InvalidOperationException(
          "The FileBased resource identifier is malformed."
        );
      }

      byte[] bytes;

      try {
        bytes = Convert.FromBase64String(
          encoded
        );
      }
      catch (FormatException ex) {
        DevLogger.LogError(ex);
        throw new InvalidOperationException(
          "The FileBased resource identifier is malformed.",
          ex
        );
      }

      string relativePath = Encoding.UTF8.GetString(
        bytes
      );

      string physicalPath = Path.GetFullPath(
        Path.Combine(
          _RootDirectory,
          relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar
          )
        )
      );

      this.EnsurePathInsideRepository(
        physicalPath
      );

      if (!this.IsPhysicalPathExposed(
            physicalPath,
            false
          )) {
        throw new InvalidOperationException(
          "The requested knowledge resource does not exist."
        );
      }

      return physicalPath;
    }

    /// <summary>
    /// Returns whether one physical path is located below the configured repository root.
    ///
    /// This method performs no exception-based control flow and is therefore suitable for
    /// read-time Markdown path probing where an external editor path is an expected input.
    /// </summary>
    private bool IsPathInsideRepository(string physicalPath) {
      string root = Path.GetFullPath(
        _RootDirectory
      ).TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      );

      string candidate = Path.GetFullPath(
        physicalPath
      );

      if (string.Equals(
            candidate,
            root,
            StringComparison.OrdinalIgnoreCase
          )) {
        return false;
      }

      string prefix = root + Path.DirectorySeparatorChar;

      return candidate.StartsWith(
        prefix,
        StringComparison.OrdinalIgnoreCase
      );
    }

    /// <summary>
    /// Ensures that a resolved provider-owned resource path cannot escape the configured
    /// repository root.
    ///
    /// Unlike read-time Markdown probing, decoding an opaque FileBased ResourceId is a
    /// strict provider operation. An identifier resolving outside the repository therefore
    /// remains an exceptional contract violation.
    /// </summary>
    private void EnsurePathInsideRepository(string physicalPath) {
      if (!this.IsPathInsideRepository(
            physicalPath
          )) {
        throw new InvalidOperationException(
          "The resource identifier resolves outside the configured repository root."
        );
      }
    }

    /// <summary>
    /// Attempts to rebase an absolute editor-generated path from another checkout or
    /// workstation onto the current repository root.
    ///
    /// Path suffixes are tested from longest to shortest. The first suffix that resolves
    /// to an existing file below the current repository root is accepted.
    ///
    /// No physical file is changed. The resulting ResourceId is still derived exclusively
    /// from the current repository-relative path.
    /// </summary>
    private bool TryRebaseExternalAbsoluteResourcePath(
      string externalPhysicalPath,
      out string rebasedPhysicalPath
    ) {
      rebasedPhysicalPath = string.Empty;

      string normalizedExternalPath = externalPhysicalPath
        .Replace(
          Path.AltDirectorySeparatorChar,
          Path.DirectorySeparatorChar
        );

      string rootPath = Path.GetPathRoot(
        normalizedExternalPath
      );

      string pathWithoutRoot = normalizedExternalPath;

      if (!string.IsNullOrEmpty(rootPath) &&
          pathWithoutRoot.StartsWith(
            rootPath,
            StringComparison.OrdinalIgnoreCase
          )) {
        pathWithoutRoot = pathWithoutRoot.Substring(
          rootPath.Length
        );
      }

      string[] segments = pathWithoutRoot.Split(
        Path.DirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries
      );

      if (segments.Length == 0) {
        return false;
      }

      for (int startIndex = 0;
           startIndex < segments.Length;
           startIndex++) {

        string relativeCandidate = Path.Combine(
          segments.Skip(startIndex).ToArray()
        );

        string candidate = Path.GetFullPath(
          Path.Combine(
            _RootDirectory,
            relativeCandidate
          )
        );

        if (!this.IsPathInsideRepository(
              candidate
            )) {
          continue;
        }

        if (!this.IsPhysicalPathExposed(
              candidate,
              false
            )) {
          continue;
        }

        if (!File.Exists(candidate)) {
          continue;
        }

        rebasedPhysicalPath = candidate;
        return true;
      }

      return false;
    }

    /// <summary>
    /// Converts the legacy numeric knowledge-resource representation previously persisted by
    /// FileBased into the current opaque identifier representation without mutating the file.
    ///
    /// This compatibility path allows existing repositories to be read safely. The next
    /// successful write of the document naturally serializes a normal relative Markdown path.
    /// </summary>
    private string ConvertLegacyPhysicalKnowledgeResourceReferences(
      string documentPath,
      string physicalMarkdown
    ) {
      string directory = Path.GetDirectoryName(
        documentPath
      );

      if (string.IsNullOrEmpty(directory)) {
        return physicalMarkdown;
      }

      string documentName = Path.GetFileNameWithoutExtension(
        documentPath
      );

      return _LegacyNumericKnowledgeResourceReferenceRegex.Replace(
        physicalMarkdown,
        (Match match) => {
          string uid = match.Groups["uid"].Value;

          string[] candidates = Directory.GetFiles(
            directory,
            documentName + _ResourceMarker + uid + ".*",
            SearchOption.TopDirectoryOnly
          ).Where(
            (string candidate) => this.IsPhysicalPathExposed(
              candidate,
              false
            )
          ).ToArray();

          if (candidates.Length != 1) {
            return match.Value;
          }

          string resourceId = this.CreateResourceId(
            candidates[0]
          );

          return _KnowledgeResourceReferencePrefix
            + resourceId;
        }
      );
    }

    /// <summary>
    /// Converts physical Markdown file references to canonical knowledge-resource references.
    ///
    /// The resolver deliberately accepts both normal relative Markdown paths and unnecessarily
    /// qualified editor-generated paths such as absolute local file-system paths or relative
    /// paths containing redundant dot/dot-dot navigation. Every accepted path is canonicalized
    /// to the physical file below the configured repository root before its opaque ResourceId
    /// is created.
    ///
    /// Reading never modifies the Markdown file. If the document is written later for an
    /// unrelated mutation, the normal write mapping serializes the canonical resource back as
    /// the shortest normal relative path from the Markdown document to the resource.
    /// </summary>
    private string ConvertPhysicalMarkdownToKnowledgeMarkdown(
      string documentPath,
      string physicalMarkdown
    ) {
      if (string.IsNullOrEmpty(physicalMarkdown)) {
        return physicalMarkdown;
      }

      string normalizedPhysicalMarkdown =
        this.ConvertLegacyPhysicalKnowledgeResourceReferences(
          documentPath,
          physicalMarkdown
        );

      return _MarkdownInlineLinkRegex.Replace(
        normalizedPhysicalMarkdown,
        (Match match) => {
          string target = match.Groups["target"].Value;

          if (target.StartsWith("<", StringComparison.Ordinal) &&
              target.EndsWith(">", StringComparison.Ordinal) &&
              target.Length >= 2) {
            target = target.Substring(
              1,
              target.Length - 2
            );
          }

          string resourcePath;

          if (!this.TryResolvePhysicalResourceReference(
                documentPath,
                target,
                out resourcePath
              )) {
            return match.Value;
          }

          string resourceId = this.CreateResourceId(
            resourcePath
          );

          return match.Groups["prefix"].Value
            + _KnowledgeResourceReferencePrefix
            + resourceId
            + match.Groups["suffix"].Value;
        }
      );
    }

    /// <summary>
    /// Converts canonical knowledge-resource references into normal relative Markdown file
    /// paths suitable for direct use by ordinary Markdown editors.
    /// </summary>
    private string ConvertKnowledgeMarkdownToPhysicalMarkdown(
      string documentPath,
      string knowledgeMarkdown
    ) {
      if (string.IsNullOrEmpty(knowledgeMarkdown)) {
        return knowledgeMarkdown;
      }

      string documentDirectory = Path.GetDirectoryName(
        documentPath
      );

      if (string.IsNullOrEmpty(documentDirectory)) {
        return knowledgeMarkdown;
      }

      return _KnowledgeResourceReferenceRegex.Replace(
        knowledgeMarkdown,
        (Match match) => {
          string resourceId = match.Groups["id"].Value;
          string resourcePath;

          try {
            resourcePath = this.ResolveResourcePath(
              resourceId
            );
          }
          catch (InvalidOperationException) {
            return match.Value;
          }

          if (!File.Exists(resourcePath)) {
            return match.Value;
          }

          string relativePath = Path.GetRelativePath(
            documentDirectory,
            resourcePath
          ).Replace(
            Path.DirectorySeparatorChar,
            '/'
          ).Replace(
            Path.AltDirectorySeparatorChar,
            '/'
          );

          return this.EncodeMarkdownRelativePath(
            relativePath
          );
        }
      );
    }

    /// <summary>
    /// Resolves one physical Markdown target to an existing resource below the configured
    /// repository root.
    ///
    /// The method intentionally accepts absolute local paths produced by editors, file URIs,
    /// and non-canonical relative paths. The returned path is always fully normalized. Remote
    /// URIs, document anchors, Markdown documents and paths outside this repository are rejected.
    /// </summary>
    private bool TryResolvePhysicalResourceReference(
      string documentPath,
      string target,
      out string resourcePath
    ) {
      resourcePath = string.Empty;

      if (string.IsNullOrWhiteSpace(target)) {
        return false;
      }

      if (target.StartsWith(
            _KnowledgeResourceReferencePrefix,
            StringComparison.Ordinal
          ) ||
          target.StartsWith(
            "#",
            StringComparison.Ordinal
          )) {
        return false;
      }

      string decodedTarget;

      try {
        decodedTarget = Uri.UnescapeDataString(
          target
        );
      }
      catch (UriFormatException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      string physicalPath;

      if (Uri.TryCreate(
            decodedTarget,
            UriKind.Absolute,
            out Uri absoluteUri
          )) {
        if (!absoluteUri.IsFile) {
          return false;
        }

        physicalPath = absoluteUri.LocalPath;
      }
      else if (Path.IsPathRooted(decodedTarget)) {
        physicalPath = decodedTarget;
      }
      else {
        string documentDirectory = Path.GetDirectoryName(
          documentPath
        );

        if (string.IsNullOrEmpty(documentDirectory)) {
          return false;
        }

        physicalPath = Path.Combine(
          documentDirectory,
          decodedTarget.Replace(
            '/',
            Path.DirectorySeparatorChar
          )
        );
      }

      try {
        physicalPath = Path.GetFullPath(
          physicalPath
        );
      }
      catch (ArgumentException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (NotSupportedException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (PathTooLongException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      if (!this.IsPathInsideRepository(
            physicalPath
          )) {
        string rebasedPath;

        if (!this.TryRebaseExternalAbsoluteResourcePath(
              physicalPath,
              out rebasedPath
            )) {
          return false;
        }

        physicalPath = rebasedPath;
      }

      if (!this.IsPhysicalPathExposed(
            physicalPath,
            false
          )) {
        return false;
      }

      if (!File.Exists(physicalPath)) {
        return false;
      }

      if (string.Equals(
            Path.GetExtension(physicalPath),
            _MarkdownExtension,
            StringComparison.OrdinalIgnoreCase
          )) {
        return false;
      }

      resourcePath = physicalPath;
      return true;
    }

    /// <summary>
    /// Encodes one repository-local relative path for safe use as an inline Markdown link.
    /// </summary>
    private string EncodeMarkdownRelativePath(string relativePath) {
      string[] segments = relativePath.Split(
        '/'
      );

      for (int index = 0; index < segments.Length; index++) {
        if (segments[index] == "." ||
            segments[index] == "..") {
          continue;
        }

        segments[index] = Uri.EscapeDataString(
          segments[index]
        );
      }

      return string.Join(
        "/",
        segments
      );
    }

    /// <summary>
    /// Extracts all canonical opaque resource identifiers referenced by one textual block.
    /// </summary>
    private string[] GetReferencedResourceIds(string content) {
      if (string.IsNullOrEmpty(content)) {
        return Array.Empty<string>();
      }

      List<string> result = new List<string>();
      HashSet<string> known = new HashSet<string>(
        StringComparer.Ordinal
      );

      MatchCollection matches = _KnowledgeResourceReferenceRegex.Matches(
        content
      );

      foreach (Match match in matches) {
        string resourceId = match.Groups["id"].Value;

        if (string.IsNullOrEmpty(resourceId) ||
            known.Contains(resourceId)) {
          continue;
        }

        known.Add(resourceId);
        result.Add(resourceId);
      }

      return result.ToArray();
    }

    /// <summary>
    /// Verifies that every canonical resource reference resolves to an existing physical file.
    /// </summary>
    private bool AreReferencedResourcesAvailable(string content) {
      string[] resourceIds = this.GetReferencedResourceIds(
        content
      );

      foreach (string resourceId in resourceIds) {
        string resourcePath;

        try {
          resourcePath = this.ResolveResourcePath(
            resourceId
          );
        }
        catch (InvalidOperationException) {
          return false;
        }

        if (!File.Exists(resourcePath)) {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Returns whether any currently exposed Markdown content references one ResourceId.
    /// </summary>
    private bool IsResourceReferencedAnywhere(
      string resourceId,
      MutationContext context
    ) {
      string reference = _KnowledgeResourceReferencePrefix
        + resourceId;

      string[] markdownFiles = this.GetFilesRecursivelyWithoutReparsePoints(
        _RootDirectory,
        "*" + _MarkdownExtension
      );

      foreach (string markdownFile in markdownFiles) {
        if (this.IsReparsePoint(markdownFile) ||
            this.IsSoftDeletedMarkdownFile(markdownFile)) {
          continue;
        }

        string physicalContent = File.ReadAllText(
          markdownFile,
          Encoding.UTF8
        );

        string knowledgeContent = this.ConvertPhysicalMarkdownToKnowledgeMarkdown(
          markdownFile,
          physicalContent
        );

        if (knowledgeContent.Contains(
              reference,
              StringComparison.Ordinal
            )) {
          return true;
        }
      }

      return false;
    }

    /// <summary>
    /// Returns whether the supplied resource uses the strict provider-generated ownership
    /// convention of the supplied Markdown document.
    /// </summary>
    private bool IsOwnedResourceForDocument(
      string resourcePath,
      string documentPath
    ) {
      string resourceDirectory = Path.GetDirectoryName(
        resourcePath
      );

      string documentDirectory = Path.GetDirectoryName(
        documentPath
      );

      if (!string.Equals(
            resourceDirectory,
            documentDirectory,
            StringComparison.OrdinalIgnoreCase
          )) {
        return false;
      }

      Match match = _OwnedResourceFileRegex.Match(
        Path.GetFileName(resourcePath)
      );

      if (!match.Success) {
        return false;
      }

      string documentName = Path.GetFileNameWithoutExtension(
        documentPath
      );

      return string.Equals(
        match.Groups["document"].Value,
        documentName,
        StringComparison.Ordinal
      );
    }

    /// <summary>
    /// Moves a document-owned resource and returns the resulting target path. A collision
    /// is resolved with a fresh provider-generated fallback name.
    /// </summary>
    private string MoveOwnedResource(
      string sourceResourcePath,
      string sourceDocumentPath,
      string targetDocumentPath
    ) {
      string targetDirectory = Path.GetDirectoryName(
        targetDocumentPath
      );

      if (string.IsNullOrEmpty(targetDirectory)) {
        throw new InvalidOperationException(
          "The target Markdown document has no physical parent directory."
        );
      }

      string targetDocumentName = Path.GetFileNameWithoutExtension(
        targetDocumentPath
      );

      string extension = Path.GetExtension(
        sourceResourcePath
      );

      Match sourceMatch = _OwnedResourceFileRegex.Match(
        Path.GetFileName(sourceResourcePath)
      );

      string token = string.Empty;

      if (sourceMatch.Success) {
        token = sourceMatch.Groups["token"].Value;
      }

      string targetResourcePath;

      if (!string.IsNullOrEmpty(token)) {
        string fileName = targetDocumentName
          + _ResourceMarker
          + token
          + extension;

        targetResourcePath = this.GetSafePhysicalChildPath(
          targetDirectory,
          Path.GetFileNameWithoutExtension(fileName),
          extension
        );
      }
      else {
        targetResourcePath = this.CreateNewResourcePath(
          targetDocumentPath,
          string.Empty,
          this.GetContentTypeFromExtension(extension)
        );
      }

      if (!this.IsPhysicalPathExposed(
            targetResourcePath,
            false
          )) {
        targetResourcePath = this.CreateNewResourcePath(
          targetDocumentPath,
          string.Empty,
          this.GetContentTypeFromExtension(extension)
        );
      }

      if (File.Exists(targetResourcePath) ||
          Directory.Exists(targetResourcePath)) {
        targetResourcePath = this.CreateNewResourcePath(
          targetDocumentPath,
          string.Empty,
          this.GetContentTypeFromExtension(extension)
        );
      }

      File.Move(
        sourceResourcePath,
        targetResourcePath
      );

      return targetResourcePath;
    }

    /// <summary>
    /// Rewrites canonical resource references according to one set of completed resource
    /// identifier changes.
    /// </summary>
    private string ApplyResourceIdChanges(
      string content,
      KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      string result = content;

      foreach (KnowledgeResourceIdChange change in resourceIdChanges) {
        result = result.Replace(
          _KnowledgeResourceReferencePrefix + change.PreviousResourceId,
          _KnowledgeResourceReferencePrefix + change.CurrentResourceId,
          StringComparison.Ordinal
        );
      }

      return result;
    }

    /// <summary>
    /// Moves or renames one Markdown document while preserving normal physical Markdown
    /// references and reporting opaque resource identifier changes for owned resources.
    /// </summary>
    private void MoveDocumentWithResourceMapping(
      string sourceDocumentPath,
      string targetDocumentPath,
      List<KnowledgeResourceIdChange> resourceIdChanges
    ) {
      string physicalContent = File.ReadAllText(
        sourceDocumentPath,
        Encoding.UTF8
      );

      string knowledgeContent = this.ConvertPhysicalMarkdownToKnowledgeMarkdown(
        sourceDocumentPath,
        physicalContent
      );

      string[] referencedResourceIds = this.GetReferencedResourceIds(
        knowledgeContent
      );

      List<KnowledgeResourceIdChange> localChanges =
        new List<KnowledgeResourceIdChange>();

      foreach (string resourceId in referencedResourceIds) {
        string resourcePath = this.ResolveResourcePath(
          resourceId
        );

        if (!File.Exists(resourcePath)) {
          throw new InvalidOperationException(
            "A referenced knowledge resource no longer exists."
          );
        }

        if (!this.IsOwnedResourceForDocument(
              resourcePath,
              sourceDocumentPath
            )) {
          continue;
        }

        string targetResourcePath = this.MoveOwnedResource(
          resourcePath,
          sourceDocumentPath,
          targetDocumentPath
        );

        string currentResourceId = this.CreateResourceId(
          targetResourcePath
        );

        KnowledgeResourceIdChange change =
          new KnowledgeResourceIdChange();

        change.PreviousResourceId = resourceId;
        change.CurrentResourceId = currentResourceId;

        localChanges.Add(change);
        resourceIdChanges.Add(change);
      }

      string updatedKnowledgeContent = this.ApplyResourceIdChanges(
        knowledgeContent,
        localChanges.ToArray()
      );

      File.Move(
        sourceDocumentPath,
        targetDocumentPath
      );

      string targetPhysicalContent = this.ConvertKnowledgeMarkdownToPhysicalMarkdown(
        targetDocumentPath,
        updatedKnowledgeContent
      );

      this.WriteAllTextAtomically(
        targetDocumentPath,
        targetPhysicalContent
      );
    }

    /// <summary>
    /// Moves one complete physical directory while preserving repository resource semantics.
    ///
    /// Resource files inside the moved directory receive new opaque ResourceIds because their
    /// repository-relative native paths change. Markdown files are first read into canonical
    /// knowledge form and then re-rendered at their new physical location so references to
    /// resources outside the moved directory remain valid.
    /// </summary>
    private void MoveDirectoryWithResourceMapping(
      string sourceDirectory,
      string targetDirectory,
      List<KnowledgeResourceIdChange> resourceIdChanges
    ) {
      string[] markdownFiles = this.GetFilesRecursivelyWithoutReparsePoints(
        sourceDirectory,
        "*" + _MarkdownExtension
      );

      Dictionary<string, string> canonicalDocuments =
        new Dictionary<string, string>(
          StringComparer.OrdinalIgnoreCase
        );

      foreach (string markdownFile in markdownFiles) {
        if (this.IsReparsePoint(markdownFile) ||
            this.IsSoftDeletedMarkdownFile(markdownFile) ||
            this.IsResourceCompanionFile(markdownFile)) {
          continue;
        }

        string relativeDocumentPath = Path.GetRelativePath(
          sourceDirectory,
          markdownFile
        );

        string physicalContent = File.ReadAllText(
          markdownFile,
          Encoding.UTF8
        );

        canonicalDocuments[relativeDocumentPath] =
          this.ConvertPhysicalMarkdownToKnowledgeMarkdown(
            markdownFile,
            physicalContent
          );
      }

      string[] files = this.GetFilesRecursivelyWithoutReparsePoints(
        sourceDirectory,
        "*"
      );

      List<KnowledgeResourceIdChange> localChanges =
        new List<KnowledgeResourceIdChange>();

      foreach (string sourceFile in files) {
        if (this.IsReparsePoint(sourceFile) ||
            string.Equals(
              Path.GetExtension(sourceFile),
              _MarkdownExtension,
              StringComparison.OrdinalIgnoreCase
            )) {
          continue;
        }

        string relativePath = Path.GetRelativePath(
          sourceDirectory,
          sourceFile
        );

        string targetFile = Path.Combine(
          targetDirectory,
          relativePath
        );

        KnowledgeResourceIdChange change =
          new KnowledgeResourceIdChange();

        change.PreviousResourceId = this.CreateResourceId(
          sourceFile
        );

        change.CurrentResourceId = this.CreateResourceId(
          targetFile
        );

        localChanges.Add(change);
        resourceIdChanges.Add(change);
      }

      Directory.Move(
        sourceDirectory,
        targetDirectory
      );

      foreach (KeyValuePair<string, string> document in canonicalDocuments) {
        string targetDocumentPath = Path.Combine(
          targetDirectory,
          document.Key
        );

        string updatedKnowledgeContent = this.ApplyResourceIdChanges(
          document.Value,
          localChanges.ToArray()
        );

        string physicalContent = this.ConvertKnowledgeMarkdownToPhysicalMarkdown(
          targetDocumentPath,
          updatedKnowledgeContent
        );

        this.WriteAllTextAtomically(
          targetDocumentPath,
          physicalContent
        );
      }
    }

    /// <summary>
    /// Deletes one resource file according to the repository soft-delete policy.
    /// </summary>
    private void DeleteResourceFile(string resourcePath) {
      if (!_UseSoftDelete) {
        File.Delete(
          resourcePath
        );
        return;
      }

      string deletedPath = resourcePath + ".DELETED";
      int suffix = 2;

      while (File.Exists(deletedPath)) {
        deletedPath = resourcePath
          + ".DELETED."
          + suffix.ToString(CultureInfo.InvariantCulture);
        suffix++;
      }

      File.Move(
        resourcePath,
        deletedPath
      );
    }

    /// <summary>
    /// Deletes document-owned resources physically associated with one document.
    /// Free/shared resources are deliberately preserved.
    /// </summary>
    private void DeleteDocumentResources(string documentPath) {
      string directory = Path.GetDirectoryName(
        documentPath
      );

      if (string.IsNullOrEmpty(directory)) {
        return;
      }

      string documentName = Path.GetFileNameWithoutExtension(
        documentPath
      );

      string[] files = Directory.GetFiles(
        directory,
        documentName + _ResourceMarker + "*",
        SearchOption.TopDirectoryOnly
      );

      foreach (string file in files) {
        if (!this.IsPhysicalPathExposed(
              file,
              false
            )) {
          continue;
        }

        if (this.IsOwnedResourceForDocument(
              file,
              documentPath
            )) {
          this.DeleteResourceFile(
            file
          );
        }
      }
    }

    /// <summary>
    /// Returns whether one file matches the strict provider-generated owned-resource naming
    /// convention and therefore must never be exposed as a Markdown knowledge document.
    /// </summary>
    private bool IsResourceCompanionFile(string filePath) {
      return _OwnedResourceFileRegex.IsMatch(
        Path.GetFileName(filePath)
      );
    }

    /// <summary>
    /// Maps common file extensions to MIME types without depending on hosting infrastructure.
    /// </summary>
    private string GetContentTypeFromExtension(string extension) {
      string normalized = extension.ToLowerInvariant();

      if (normalized == ".png") {
        return "image/png";
      }
      else if (normalized == ".jpg" || normalized == ".jpeg") {
        return "image/jpeg";
      }
      else if (normalized == ".gif") {
        return "image/gif";
      }
      else if (normalized == ".svg") {
        return "image/svg+xml";
      }
      else if (normalized == ".webp") {
        return "image/webp";
      }
      else if (normalized == ".pdf") {
        return "application/pdf";
      }
      else if (normalized == ".txt") {
        return "text/plain";
      }
      else if (normalized == ".md") {
        return "text/markdown";
      }

      return "application/octet-stream";
    }

    /// <summary>
    /// Maps common MIME types to physical resource file extensions.
    /// </summary>
    private string GetExtensionFromContentType(string contentType) {
      if (string.IsNullOrWhiteSpace(contentType)) {
        return string.Empty;
      }

      string normalized = contentType.Trim().ToLowerInvariant();

      if (normalized == "image/png") {
        return ".png";
      }
      else if (normalized == "image/jpeg") {
        return ".jpg";
      }
      else if (normalized == "image/gif") {
        return ".gif";
      }
      else if (normalized == "image/svg+xml") {
        return ".svg";
      }
      else if (normalized == "image/webp") {
        return ".webp";
      }
      else if (normalized == "application/pdf") {
        return ".pdf";
      }
      else if (normalized == "text/plain") {
        return ".txt";
      }
      else if (normalized == "text/markdown") {
        return ".md";
      }

      return string.Empty;
    }

    /// <summary>
    /// Returns whether one physical directory contains an artifact that is deliberately
    /// outside the repository exposure model.
    ///
    /// Directory-level destructive or relocating mutations are rejected in that case so a
    /// caller cannot indirectly delete, rename or move protected content such as nested
    /// <c>.git</c> metadata through an otherwise visible parent area.
    /// </summary>
    private bool ContainsProtectedPhysicalDescendant(
      string directoryPath
    ) {
      string[] files =
        Directory.GetFiles(
          directoryPath,
          "*",
          SearchOption.TopDirectoryOnly
        );

      foreach (string file in files) {
        if (this.IsReparsePoint(
              file
            )) {
          return true;
        }

        if (!this.IsPhysicalPathExposed(
              file,
              false
            )) {
          return true;
        }
      }

      string[] directories =
        Directory.GetDirectories(
          directoryPath,
          "*",
          SearchOption.TopDirectoryOnly
        );

      foreach (string childDirectory in directories) {
        string directoryName =
          Path.GetFileName(
            childDirectory
          );

        if (this.IsProviderInternalDirectory(
              directoryName
            )) {
          return true;
        }

        if (this.IsReparsePoint(
              childDirectory
            )) {
          return true;
        }

        if (!this.IsPhysicalPathExposed(
              childDirectory,
              true
            )) {
          return true;
        }

        if (this.ContainsProtectedPhysicalDescendant(
              childDirectory
            )) {
          return true;
        }
      }

      return false;
    }

    private bool TryDeleteCore(string area, MutationContext context) {
      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.Kind == AreaKind.Root) {
        return false;
      }

      if (descriptor.Kind == AreaKind.Directory) {
        if (this.ContainsProtectedPhysicalDescendant(
              descriptor.PhysicalPath
            )) {
          return false;
        }

        context.ForgetDocumentsBelow(descriptor.PhysicalPath);

        if (_UseSoftDelete) {
          this.SoftDeleteMarkdownDocumentsBelow(
            descriptor.PhysicalPath
          );
        }
        else {
          Directory.Delete(
            descriptor.PhysicalPath,
            true
          );
        }

        return true;
      }

      if (descriptor.Kind == AreaKind.Document) {
        context.ForgetDocument(descriptor.PhysicalPath);

        this.DeleteDocumentResources(
          descriptor.PhysicalPath
        );

        this.DeleteMarkdownDocument(
          descriptor.PhysicalPath
        );

        return true;
      }

      MarkdownNode parent = descriptor.Node.Parent;

      if (parent == null) {
        return false;
      }

      bool removed = parent.Children.Remove(descriptor.Node);

      if (!removed) {
        return false;
      }

      descriptor.Document.MarkChanged();
      return true;
    }

    private bool TryRenameCore(
      string area,
      string newName,
      MutationContext context,
      List<KnowledgeResourceIdChange> resourceIdChanges
    ) {
      if (string.IsNullOrWhiteSpace(newName)) {
        return false;
      }

      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.Kind == AreaKind.Root) {
        return false;
      }

      if (descriptor.Kind == AreaKind.Directory) {
        if (this.ContainsProtectedPhysicalDescendant(
              descriptor.PhysicalPath
            )) {
          return false;
        }

        string directoryName = newName.Trim();

        if (this.IsDirectorySegment(directoryName)) {
          directoryName = directoryName.Substring(
            1,
            directoryName.Length - 2
          );
        }

        string parentDirectory = Path.GetDirectoryName(descriptor.PhysicalPath);

        if (string.IsNullOrEmpty(parentDirectory)) {
          return false;
        }

        if (!this.IsValidPhysicalName(directoryName)) {
          return false;
        }

        string targetPath = this.GetSafePhysicalChildPath(
          parentDirectory,
          directoryName,
          string.Empty
        );

        if (!this.IsPhysicalPathExposed(
              targetPath,
              true
            )) {
          return false;
        }

        if (Directory.Exists(targetPath) || File.Exists(targetPath)) {
          return false;
        }

        context.ForgetDocumentsBelow(
          descriptor.PhysicalPath
        );

        this.MoveDirectoryWithResourceMapping(
          descriptor.PhysicalPath,
          targetPath,
          resourceIdChanges
        );

        return true;
      }

      if (descriptor.Kind == AreaKind.Document) {
        string documentName = newName.Trim();

        if (this.IsDirectorySegment(documentName)) {
          return false;
        }

        if (string.IsNullOrWhiteSpace(documentName)) {
          return false;
        }

        if (!this.IsValidPhysicalName(documentName)) {
          return false;
        }

        string parentDirectory = Path.GetDirectoryName(descriptor.PhysicalPath);

        if (string.IsNullOrEmpty(parentDirectory)) {
          return false;
        }

        string targetPath = this.GetSafePhysicalChildPath(
          parentDirectory,
          documentName,
          _MarkdownExtension
        );

        if (!this.IsPhysicalPathExposed(
              targetPath,
              false
            )) {
          return false;
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath)) {
          return false;
        }

        context.ForgetDocument(
          descriptor.PhysicalPath
        );

        this.MoveDocumentWithResourceMapping(
          descriptor.PhysicalPath,
          targetPath,
          resourceIdChanges
        );

        return true;
      }

      string headingName = newName.Trim();

      if (string.IsNullOrWhiteSpace(headingName)) {
        return false;
      }

      MarkdownNode parent = descriptor.Node.Parent;

      if (parent == null) {
        return false;
      }

      bool duplicate = parent.Children.Any((MarkdownNode sibling) =>
        sibling != descriptor.Node &&
        string.Equals(sibling.Title, headingName, StringComparison.Ordinal));

      if (duplicate) {
        return false;
      }

      descriptor.Node.Title = headingName;
      descriptor.Node.HeadingLineModified = true;
      this.RebuildLogicalSegments(parent);
      descriptor.Document.MarkChanged();

      return true;
    }

    private bool TryAddSubAreaCore(
      string area,
      string name,
      KnowledgeAreaKind kind,
      MutationContext context
    ) {
      if (string.IsNullOrWhiteSpace(name)) {
        return false;
      }

      string logicalName = name.Trim();
      AreaDescriptor descriptor = this.ResolveArea(
        area,
        context
      );

      if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
        if (!this.IsValidPhysicalName(logicalName)) {
          return false;
        }

        if (kind == KnowledgeAreaKind.Structural) {
          string directoryPath = this.GetSafePhysicalChildPath(
            descriptor.PhysicalPath,
            logicalName,
            string.Empty
          );

          if (!this.IsPhysicalPathExposed(
                directoryPath,
                true
              )) {
            return false;
          }

          if (Directory.Exists(directoryPath) || File.Exists(directoryPath)) {
            return false;
          }

          Directory.CreateDirectory(
            directoryPath
          );

          return true;
        }

        if (kind != KnowledgeAreaKind.Content) {
          return false;
        }

        string documentPath = this.GetSafePhysicalChildPath(
          descriptor.PhysicalPath,
          logicalName,
          _MarkdownExtension
        );

        if (!this.IsPhysicalPathExposed(
              documentPath,
              false
            )) {
          return false;
        }

        if (File.Exists(documentPath) || Directory.Exists(documentPath)) {
          return false;
        }

        File.WriteAllText(
          documentPath,
          string.Empty,
          new UTF8Encoding(false)
        );

        context.ForgetDocument(
          documentPath
        );

        return true;
      }

      if (kind != KnowledgeAreaKind.Content) {
        return false;
      }

      MarkdownNode parentNode;
      int newHeadingLevel;

      if (descriptor.Kind == AreaKind.Document) {
        parentNode = descriptor.Document.Root;
        newHeadingLevel = 1;
      }
      else {
        parentNode = descriptor.Node;
        newHeadingLevel = descriptor.Node.HeadingLevel + 1;
      }

      if (newHeadingLevel > _MaximumMarkdownHeadingLevel) {
        return false;
      }

      bool duplicate = parentNode.Children.Any((MarkdownNode child) =>
        string.Equals(child.Title, logicalName, StringComparison.Ordinal));

      if (duplicate) {
        return false;
      }

      MarkdownNode newNode = MarkdownNode.CreateNewHeading(
        logicalName,
        newHeadingLevel,
        descriptor.Document.NewLine
      );

      newNode.Parent = parentNode;
      parentNode.Children.Add(newNode);
      this.RebuildLogicalSegments(parentNode);
      descriptor.Document.MarkChanged();

      return true;
    }

    private bool TryAppendContentCore(string area, string content, MutationContext context) {
      if (content == null) {
        return false;
      }

      AreaDescriptor target = this.ResolveArea(area, context);

      if (target.ContentLevel == ContentLevel.BeyondContent) {
        return false;
      }

      if (target.Kind == AreaKind.Document ||
          target.Kind == AreaKind.Heading) {
        if (!this.AreReferencedResourcesAvailable(
              content
            )) {
          return false;
        }
      }

      ParsedIncomingContent incoming = this.ParseIncomingContent(content);

      if (target.ContentLevel == ContentLevel.ContentAggregation) {
        if (!string.IsNullOrWhiteSpace(incoming.Root.DirectContent)) {
          return false;
        }

        return this.MergeIntoAggregation(target, incoming.Root, context);
      }

      MarkdownNode targetNode;
      int targetHeadingLevel;

      if (target.Kind == AreaKind.Document) {
        targetNode = target.Document.Root;
        targetHeadingLevel = 0;
      }
      else {
        targetNode = target.Node;
        targetHeadingLevel = target.Node.HeadingLevel;
      }

      bool merged = this.MergeIntoContentContainer(
        target.Document,
        targetNode,
        targetHeadingLevel,
        incoming.Root
      );

      if (merged) {
        target.Document.MarkChanged();
      }

      return merged;
    }

    private bool TryTruncateCore(string area, MutationContext context) {
      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.ContentLevel == ContentLevel.BeyondContent) {
        return false;
      }

      if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
        string[] markdownFiles = Directory.GetFiles(
          descriptor.PhysicalPath,
          "*" + _MarkdownExtension,
          SearchOption.TopDirectoryOnly
        );

        foreach (string markdownFile in markdownFiles) {
          if (this.IsReparsePoint(markdownFile)) {
            continue;
          }

          if (this.IsSoftDeletedMarkdownFile(markdownFile)) {
            continue;
          }

          if (this.IsResourceCompanionFile(markdownFile)) {
            continue;
          }

          if (!this.IsPhysicalPathExposed(
                markdownFile,
                false
              )) {
            continue;
          }

          context.ForgetDocument(markdownFile);

          this.DeleteMarkdownDocument(
            markdownFile
          );
        }

        return true;
      }

      MarkdownNode targetNode;

      if (descriptor.Kind == AreaKind.Document) {
        targetNode = descriptor.Document.Root;
      }
      else {
        targetNode = descriptor.Node;
      }

      targetNode.DirectContent = string.Empty;
      targetNode.Children.Clear();
      descriptor.Document.MarkChanged();

      return true;
    }

    /// <summary>
    /// Implements provider-neutral logical reparenting using the concrete file/Markdown
    /// representation of the addressed areas.
    /// </summary>
    private bool TryMoveContentCore(
      string contentAreaToMove,
      string newParentArea,
      MutationContext context,
      List<KnowledgeResourceIdChange> resourceIdChanges
    ) {
      string normalizedContentAreaToMove = this.NormalizeAreaPath(
        contentAreaToMove
      );

      string normalizedNewParentArea = this.NormalizeAreaPath(
        newParentArea
      );

      if (string.Equals(
            normalizedContentAreaToMove,
            _RootArea,
            StringComparison.Ordinal
          )) {
        return false;
      }

      if (string.Equals(
            normalizedContentAreaToMove,
            normalizedNewParentArea,
            StringComparison.Ordinal
          )) {
        return false;
      }

      string movedSubtreePrefix = normalizedContentAreaToMove;

      if (!movedSubtreePrefix.EndsWith(
            "/",
            StringComparison.Ordinal
          )) {
        movedSubtreePrefix += "/";
      }

      if (normalizedNewParentArea.StartsWith(
            movedSubtreePrefix,
            StringComparison.Ordinal
          )) {
        return false;
      }

      AreaDescriptor contentToMove = this.ResolveArea(
        normalizedContentAreaToMove,
        context
      );

      AreaDescriptor newParent = this.ResolveArea(
        normalizedNewParentArea,
        context
      );

      string currentParentArea = this.GetLogicalParentArea(
        normalizedContentAreaToMove
      );

      if (string.Equals(
            currentParentArea,
            normalizedNewParentArea,
            StringComparison.Ordinal
          )) {
        return true;
      }

      if (contentToMove.Kind == AreaKind.Directory) {
        return this.MoveDirectoryArea(
          contentToMove,
          newParent,
          context,
          resourceIdChanges
        );
      }

      if (contentToMove.Kind == AreaKind.Document) {
        return this.MoveDocumentArea(
          contentToMove,
          newParent,
          context,
          resourceIdChanges
        );
      }

      if (contentToMove.Kind == AreaKind.Heading) {
        return this.MoveHeadingArea(
          contentToMove,
          newParent
        );
      }

      return false;
    }

    /// <summary>
    /// Moves one physical directory below a new physical directory/root parent.
    /// </summary>
    private bool MoveDirectoryArea(
      AreaDescriptor contentToMove,
      AreaDescriptor newParent,
      MutationContext context,
      List<KnowledgeResourceIdChange> resourceIdChanges
    ) {
      if (newParent.Kind != AreaKind.Root &&
          newParent.Kind != AreaKind.Directory) {
        return false;
      }

      if (this.ContainsProtectedPhysicalDescendant(
            contentToMove.PhysicalPath
          )) {
        return false;
      }

      string directoryName = Path.GetFileName(
        contentToMove.PhysicalPath
      );

      string targetPath = this.GetSafePhysicalChildPath(
        newParent.PhysicalPath,
        directoryName,
        string.Empty
      );

      if (!this.IsPhysicalPathExposed(
            targetPath,
            true
          )) {
        return false;
      }

      if (Directory.Exists(targetPath) || File.Exists(targetPath)) {
        return false;
      }

      context.ForgetDocumentsBelow(
        contentToMove.PhysicalPath
      );

      this.MoveDirectoryWithResourceMapping(
        contentToMove.PhysicalPath,
        targetPath,
        resourceIdChanges
      );

      return true;
    }

    /// <summary>
    /// Moves one physical Markdown document below a new physical directory/root parent.
    /// </summary>
    private bool MoveDocumentArea(
      AreaDescriptor contentToMove,
      AreaDescriptor newParent,
      MutationContext context,
      List<KnowledgeResourceIdChange> resourceIdChanges
    ) {
      if (newParent.Kind != AreaKind.Root &&
          newParent.Kind != AreaKind.Directory) {
        return false;
      }

      string documentName = Path.GetFileNameWithoutExtension(
        contentToMove.PhysicalPath
      );

      string targetPath = this.GetSafePhysicalChildPath(
        newParent.PhysicalPath,
        documentName,
        _MarkdownExtension
      );

      if (!this.IsPhysicalPathExposed(
            targetPath,
            false
          )) {
        return false;
      }

      if (File.Exists(targetPath) || Directory.Exists(targetPath)) {
        return false;
      }

      context.ForgetDocument(
        contentToMove.PhysicalPath
      );

      this.MoveDocumentWithResourceMapping(
        contentToMove.PhysicalPath,
        targetPath,
        resourceIdChanges
      );

      return true;
    }

    /// <summary>
    /// Reparents one complete Markdown heading subtree below a document or heading.
    /// </summary>
    private bool MoveHeadingArea(
      AreaDescriptor contentToMove,
      AreaDescriptor newParent
    ) {
      if (newParent.Kind != AreaKind.Document &&
          newParent.Kind != AreaKind.Heading) {
        return false;
      }

      MarkdownNode sourceNode = contentToMove.Node;
      MarkdownNode oldParentNode = sourceNode.Parent;

      if (oldParentNode == null) {
        return false;
      }

      MarkdownNode newParentNode;
      int requiredHeadingLevel;

      if (newParent.Kind == AreaKind.Document) {
        newParentNode = newParent.Document.Root;
        requiredHeadingLevel = 1;
      }
      else {
        newParentNode = newParent.Node;
        requiredHeadingLevel = newParent.Node.HeadingLevel + 1;
      }

      if (object.ReferenceEquals(sourceNode, newParentNode) ||
          this.IsMarkdownDescendant(newParentNode, sourceNode)) {
        return false;
      }

      bool duplicate = newParentNode.Children.Any((MarkdownNode child) =>
        !object.ReferenceEquals(child, sourceNode) &&
        string.Equals(
          child.Title,
          sourceNode.Title,
          StringComparison.Ordinal
        ));

      if (duplicate) {
        return false;
      }

      int headingLevelDelta = requiredHeadingLevel - sourceNode.HeadingLevel;

      if (!this.CanRebaseHeadingSubtree(
            sourceNode,
            headingLevelDelta
          )) {
        return false;
      }

      oldParentNode.Children.Remove(
        sourceNode
      );

      sourceNode.Parent = newParentNode;
      newParentNode.Children.Add(
        sourceNode
      );

      this.RebaseHeadingSubtree(
        sourceNode,
        headingLevelDelta
      );

      this.RebuildLogicalSegments(
        oldParentNode
      );

      if (!object.ReferenceEquals(oldParentNode, newParentNode)) {
        this.RebuildLogicalSegments(
          newParentNode
        );
      }

      contentToMove.Document.MarkChanged();
      newParent.Document.MarkChanged();

      return true;
    }

    /// <summary>
    /// Returns whether one Markdown node is located below another Markdown node.
    /// </summary>
    private bool IsMarkdownDescendant(
      MarkdownNode candidate,
      MarkdownNode ancestor
    ) {
      MarkdownNode current = candidate.Parent;

      while (current != null) {
        if (object.ReferenceEquals(current, ancestor)) {
          return true;
        }

        current = current.Parent;
      }

      return false;
    }

    /// <summary>
    /// Validates that rebasing a complete Markdown subtree keeps all headings within the
    /// supported Markdown heading range.
    /// </summary>
    private bool CanRebaseHeadingSubtree(
      MarkdownNode node,
      int headingLevelDelta
    ) {
      int newLevel = node.HeadingLevel + headingLevelDelta;

      if (newLevel < 1 || newLevel > _MaximumMarkdownHeadingLevel) {
        return false;
      }

      foreach (MarkdownNode child in node.Children) {
        if (!this.CanRebaseHeadingSubtree(
              child,
              headingLevelDelta
            )) {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Rebases all Markdown heading levels in one complete subtree.
    /// </summary>
    private void RebaseHeadingSubtree(
      MarkdownNode node,
      int headingLevelDelta
    ) {
      node.HeadingLevel += headingLevelDelta;
      node.HeadingLineModified = true;

      foreach (MarkdownNode child in node.Children) {
        this.RebaseHeadingSubtree(
          child,
          headingLevelDelta
        );
      }
    }

    /// <summary>
    /// Returns the logical parent path of an already normalized area path.
    /// </summary>
    private string GetLogicalParentArea(
      string normalizedArea
    ) {
      int separatorIndex = normalizedArea.LastIndexOf(
        "/",
        StringComparison.Ordinal
      );

      if (separatorIndex <= 0) {
        return _RootArea;
      }

      return normalizedArea.Substring(
        0,
        separatorIndex
      );
    }

    private bool MergeIntoAggregation(
      AreaDescriptor target,
      MarkdownNode incomingRoot,
      MutationContext context
    ) {
      if (!string.IsNullOrWhiteSpace(incomingRoot.DirectContent)) {
        return false;
      }

      foreach (MarkdownNode incomingChild in incomingRoot.Children) {
        bool directoryChild = this.IsDirectoryDisplayName(
          incomingChild.Title
        );

        if (directoryChild) {
          string directoryName = incomingChild.Title.Substring(
            1,
            incomingChild.Title.Length - 2
          ).Trim();

          if (!this.IsValidPhysicalName(directoryName)) {
            return false;
          }

          if (!string.IsNullOrWhiteSpace(incomingChild.DirectContent)) {
            return false;
          }

          string directoryPath = this.GetSafePhysicalChildPath(
            target.PhysicalPath,
            directoryName,
            string.Empty
          );

          if (!this.IsPhysicalPathExposed(
                directoryPath,
                true
              )) {
            return false;
          }

          if (!Directory.Exists(directoryPath)) {
            if (File.Exists(directoryPath)) {
              return false;
            }

            Directory.CreateDirectory(directoryPath);
          }

          AreaDescriptor directoryDescriptor = AreaDescriptor.CreateDirectory(
            this.CombineAreaPath(
              target.AreaPath,
              this.CreateDirectorySegment(directoryName)
            ),
            directoryName,
            directoryPath,
            this.GetDirectoryContentLevel(directoryPath)
          );

          bool directoryMerged = this.MergeIntoAggregation(
            directoryDescriptor,
            incomingChild,
            context
          );

          if (!directoryMerged) {
            return false;
          }

          continue;
        }

        string documentName = incomingChild.Title.Trim();

        if (!this.IsValidPhysicalName(documentName)) {
          return false;
        }

        string documentPath = this.GetSafePhysicalChildPath(
          target.PhysicalPath,
          documentName,
          _MarkdownExtension
        );

        if (!this.IsPhysicalPathExposed(
              documentPath,
              false
            )) {
          return false;
        }

        if (!File.Exists(documentPath)) {
          if (Directory.Exists(documentPath)) {
            return false;
          }

          File.WriteAllText(
            documentPath,
            string.Empty,
            new UTF8Encoding(false)
          );
        }

        MarkdownDocumentModel document = this.LoadDocument(
          documentPath,
          context
        );

        MarkdownNode virtualIncomingRoot = new MarkdownNode();
        virtualIncomingRoot.DirectContent = incomingChild.DirectContent;

        foreach (MarkdownNode child in incomingChild.Children) {
          virtualIncomingRoot.Children.Add(child);
        }

        bool documentMerged = this.MergeIntoContentContainer(
          document,
          document.Root,
          0,
          virtualIncomingRoot
        );

        if (!documentMerged) {
          return false;
        }

        document.MarkChanged();
      }

      return true;
    }

    private bool MergeIntoContentContainer(
      MarkdownDocumentModel document,
      MarkdownNode targetNode,
      int targetHeadingLevel,
      MarkdownNode incomingRoot
    ) {
      if (!string.IsNullOrWhiteSpace(incomingRoot.DirectContent)) {
        targetNode.DirectContent = this.AppendDirectContent(
          targetNode.DirectContent,
          incomingRoot.DirectContent,
          document.NewLine
        );
      }

      foreach (MarkdownNode incomingChild in incomingRoot.Children) {
        MarkdownNode[] matchingChildren = targetNode.Children
          .Where((MarkdownNode child) =>
            string.Equals(child.Title, incomingChild.Title, StringComparison.Ordinal))
          .ToArray();

        if (matchingChildren.Length > 1) {
          return false;
        }

        if (matchingChildren.Length == 1) {
          MarkdownNode existingChild = matchingChildren[0];

          bool merged = this.MergeIntoContentContainer(
            document,
            existingChild,
            existingChild.HeadingLevel,
            incomingChild
          );

          if (!merged) {
            return false;
          }

          continue;
        }

        int childHeadingLevel = targetHeadingLevel + 1;

        if (childHeadingLevel > _MaximumMarkdownHeadingLevel) {
          return false;
        }

        MarkdownNode newChild = this.CloneIncomingAsDocumentNode(
          incomingChild,
          childHeadingLevel,
          document.NewLine
        );

        if (newChild == null) {
          return false;
        }

        newChild.Parent = targetNode;
        targetNode.Children.Add(newChild);
      }

      this.RebuildLogicalSegments(targetNode);
      return true;
    }

    private MarkdownNode CloneIncomingAsDocumentNode(
      MarkdownNode incoming,
      int headingLevel,
      string newLine
    ) {
      if (headingLevel > _MaximumMarkdownHeadingLevel) {
        return null;
      }

      MarkdownNode clone = MarkdownNode.CreateNewHeading(
        incoming.Title,
        headingLevel,
        newLine
      );

      clone.DirectContent = incoming.DirectContent;

      foreach (MarkdownNode incomingChild in incoming.Children) {
        MarkdownNode childClone = this.CloneIncomingAsDocumentNode(
          incomingChild,
          headingLevel + 1,
          newLine
        );

        if (childClone == null) {
          return null;
        }

        childClone.Parent = clone;
        clone.Children.Add(childClone);
      }

      this.RebuildLogicalSegments(clone);
      return clone;
    }

    private ParsedIncomingContent CreateIncomingContentFromArea(AreaDescriptor source) {
      MarkdownNode root = new MarkdownNode();

      if (source.Kind == AreaKind.Root || source.Kind == AreaKind.Directory) {
        this.PopulateIncomingFromAggregation(source, root);
        return new ParsedIncomingContent(root);
      }

      MarkdownNode sourceNode;

      if (source.Kind == AreaKind.Document) {
        sourceNode = source.Document.Root;
      }
      else {
        sourceNode = source.Node;
      }

      root.DirectContent = sourceNode.DirectContent;

      foreach (MarkdownNode child in sourceNode.Children) {
        MarkdownNode cloned = this.CloneIncomingNode(child);
        cloned.Parent = root;
        root.Children.Add(cloned);
      }

      return new ParsedIncomingContent(root);
    }

    private void PopulateIncomingFromAggregation(
      AreaDescriptor aggregation,
      MarkdownNode root
    ) {
      AreaDescriptor[] children = this.GetDirectoryChildren(aggregation);

      foreach (AreaDescriptor child in children) {
        MarkdownNode incomingChild = new MarkdownNode();

        if (child.Kind == AreaKind.Document) {
          incomingChild.Title = child.DisplayName;
          incomingChild.DirectContent = child.Document.Root.DirectContent;

          foreach (MarkdownNode documentChild in child.Document.Root.Children) {
            MarkdownNode clonedDocumentChild = this.CloneIncomingNode(documentChild);
            clonedDocumentChild.Parent = incomingChild;
            incomingChild.Children.Add(clonedDocumentChild);
          }
        }
        else {
          incomingChild.Title = "[" + child.DisplayName + "]";
          this.PopulateIncomingFromAggregation(child, incomingChild);
        }

        incomingChild.Parent = root;
        root.Children.Add(incomingChild);
      }
    }

    private MarkdownNode CloneIncomingNode(MarkdownNode source) {
      MarkdownNode clone = new MarkdownNode();
      clone.Title = source.Title;
      clone.DirectContent = source.DirectContent;

      foreach (MarkdownNode sourceChild in source.Children) {
        MarkdownNode childClone = this.CloneIncomingNode(sourceChild);
        childClone.Parent = clone;
        clone.Children.Add(childClone);
      }

      return clone;
    }

    private string GetDirectContentCore(AreaDescriptor descriptor) {
      if (descriptor.ContentLevel != ContentLevel.ContentContainer) {
        return string.Empty;
      }

      if (descriptor.Kind == AreaKind.Document) {
        return this.NormalizeContentForRead(descriptor.Document.Root.DirectContent);
      }

      return this.NormalizeContentForRead(descriptor.Node.DirectContent);
    }

    private string RenderContentSubtree(MarkdownNode node, string newLine) {
      StringBuilder builder = new StringBuilder();

      if (!string.IsNullOrEmpty(node.DirectContent)) {
        builder.Append(this.NormalizeContentForRead(node.DirectContent));

        if (node.Children.Count > 0) {
          builder.Append(newLine);
          builder.Append(newLine);
        }
      }

      foreach (MarkdownNode child in node.Children) {
        this.RenderRebasedNode(child, builder, 1, newLine);
      }

      return builder.ToString();
    }

    private void RenderRebasedNode(
      MarkdownNode node,
      StringBuilder builder,
      int headingLevel,
      string newLine
    ) {
      int effectiveHeadingLevel = headingLevel;

      if (effectiveHeadingLevel > _MaximumMarkdownHeadingLevel) {
        effectiveHeadingLevel = _MaximumMarkdownHeadingLevel;
      }

      builder.Append(new string('#', effectiveHeadingLevel));
      builder.Append(' ');
      builder.Append(node.Title);
      builder.Append(newLine);

      string directContent = this.NormalizeContentForRead(node.DirectContent);

      if (!string.IsNullOrEmpty(directContent)) {
        builder.Append(newLine);
        builder.Append(directContent);
        builder.Append(newLine);
      }

      if (node.Children.Count > 0) {
        builder.Append(newLine);
      }

      foreach (MarkdownNode child in node.Children) {
        this.RenderRebasedNode(child, builder, headingLevel + 1, newLine);
      }
    }

    /// <summary>
    /// Renders only Markdown documents located directly inside one directory aggregation.
    /// 
    /// Subdirectories are deliberately excluded. They are separate navigation scopes and
    /// must be entered explicitly by the caller. Every direct Markdown document is
    /// rendered as one level-one section.
    /// </summary>
    private void RenderDirectDocumentAggregation(
      AreaDescriptor aggregation,
      StringBuilder builder
    ) {
      AreaDescriptor[] children = this.GetDirectoryChildren(aggregation);

      foreach (AreaDescriptor child in children) {
        if (child.Kind != AreaKind.Document) {
          continue;
        }

        builder.Append("# ");
        builder.Append(child.DisplayName);
        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);

        string directContent = this.NormalizeContentForRead(
          child.Document.Root.DirectContent
        );

        if (!string.IsNullOrEmpty(directContent)) {
          builder.Append(directContent);
          builder.Append(Environment.NewLine);
          builder.Append(Environment.NewLine);
        }

        foreach (MarkdownNode documentChild in child.Document.Root.Children) {
          this.RenderRebasedNode(
            documentChild,
            builder,
            2,
            child.Document.NewLine
          );
        }

        if (builder.Length > 0) {
          builder.Append(Environment.NewLine);
        }
      }
    }

    private void CollectChildAreas(
      AreaDescriptor parent,
      bool recurse,
      List<string> result,
      MutationContext context
    ) {
      if (parent.Kind == AreaKind.Root || parent.Kind == AreaKind.Directory) {
        AreaDescriptor[] children = this.GetDirectoryChildren(parent);

        foreach (AreaDescriptor child in children) {
          result.Add(child.AreaPath);

          if (recurse) {
            this.CollectChildAreas(child, true, result, context);
          }
        }

        return;
      }

      MarkdownNode parentNode;

      if (parent.Kind == AreaKind.Document) {
        parentNode = parent.Document.Root;
      }
      else {
        parentNode = parent.Node;
      }

      foreach (MarkdownNode childNode in parentNode.Children) {
        string childAreaPath = this.CombineAreaPath(parent.AreaPath, childNode.LogicalSegment);
        result.Add(childAreaPath);

        if (recurse) {
          AreaDescriptor childDescriptor = AreaDescriptor.CreateHeading(
            childAreaPath,
            childNode.Title,
            parent.PhysicalPath,
            parent.Document,
            childNode
          );

          this.CollectChildAreas(childDescriptor, true, result, context);
        }
      }
    }

    private AreaDescriptor[] GetDirectoryChildren(AreaDescriptor directoryDescriptor) {
      List<AreaDescriptor> children = new List<AreaDescriptor>();

      string[] directories = Directory.GetDirectories(directoryDescriptor.PhysicalPath);

      foreach (string directory in directories) {
        DirectoryInfo directoryInfo = new DirectoryInfo(directory);

        if (this.IsProviderInternalDirectory(directoryInfo.Name)) {
          continue;
        }

        if (this.IsReparsePoint(directoryInfo.FullName)) {
          continue;
        }

        if (!this.IsPhysicalPathExposed(
              directoryInfo.FullName,
              true
            )) {
          continue;
        }

        string areaPath = this.CombineAreaPath(
          directoryDescriptor.AreaPath,
          this.CreateDirectorySegment(directoryInfo.Name)
        );

        children.Add(
          AreaDescriptor.CreateDirectory(
            areaPath,
            directoryInfo.Name,
            directoryInfo.FullName,
            this.GetDirectoryContentLevel(directoryInfo.FullName)
          )
        );
      }

      string[] markdownFiles = Directory.GetFiles(
        directoryDescriptor.PhysicalPath,
        "*" + _MarkdownExtension,
        SearchOption.TopDirectoryOnly
      );

      foreach (string markdownFile in markdownFiles) {
        if (this.IsReparsePoint(markdownFile)) {
          continue;
        }

        if (this.IsSoftDeletedMarkdownFile(markdownFile) ||
            this.IsResourceCompanionFile(markdownFile)) {
          continue;
        }

        if (!this.IsPhysicalPathExposed(
              markdownFile,
              false
            )) {
          continue;
        }

        string documentName = Path.GetFileNameWithoutExtension(markdownFile);
        string areaPath = this.CombineAreaPath(
          directoryDescriptor.AreaPath,
          this.EncodeAreaSegment(documentName)
        );

        MarkdownDocumentModel document = this.LoadDocument(markdownFile, null);

        children.Add(
          AreaDescriptor.CreateDocument(
            areaPath,
            documentName,
            markdownFile,
            document
          )
        );
      }

      return children
        .OrderBy((AreaDescriptor descriptor) => descriptor.AreaPath, StringComparer.Ordinal)
        .ToArray();
    }

    private bool GetSupportsSubAreas(AreaDescriptor descriptor) {
      if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
        return true;
      }

      if (descriptor.Kind == AreaKind.Document) {
        return true;
      }

      return descriptor.Node.HeadingLevel < _MaximumMarkdownHeadingLevel;
    }

    private bool GetCanAddSubAreas(AreaDescriptor descriptor) {
      if (_ReadOnly) {
        return false;
      }

      return this.GetSupportsSubAreas(descriptor);
    }

    private MarkdownDocumentModel LoadDocument(
      string documentPath,
      MutationContext context
    ) {
      if (context != null) {
        return context.GetDocument(documentPath);
      }

      string physicalContent = File.ReadAllText(
        documentPath,
        Encoding.UTF8
      );

      string knowledgeContent = this.ConvertPhysicalMarkdownToKnowledgeMarkdown(
        documentPath,
        physicalContent
      );

      return this.ParseMarkdownDocument(
        documentPath,
        knowledgeContent
      );
    }

    private MarkdownDocumentModel ParseMarkdownDocument(
      string documentPath,
      string content
    ) {
      string newLine = this.DetectNewLine(content);
      MarkdownDocumentModel document = new MarkdownDocumentModel(documentPath, newLine);
      MarkdownNode root = document.Root;

      MatchCollection lines = Regex.Matches(content, @".*?(?:\r\n|\n|\r|$)", RegexOptions.Singleline);
      Stack<MarkdownNode> stack = new Stack<MarkdownNode>();
      stack.Push(root);

      char fenceCharacter = '\0';
      int fenceLength = 0;

      foreach (Match lineMatch in lines) {
        string line = lineMatch.Value;

        if (line.Length == 0) {
          continue;
        }

        string lineWithoutEnding = line.TrimEnd('\r', '\n');
        string trimmedStart = lineWithoutEnding.TrimStart();

        if (this.TryUpdateFenceState(trimmedStart, ref fenceCharacter, ref fenceLength)) {
          stack.Peek().DirectContent += line;
          continue;
        }

        if (fenceCharacter != '\0') {
          stack.Peek().DirectContent += line;
          continue;
        }

        Match headingMatch = _AtxHeadingRegex.Match(line);

        if (!headingMatch.Success) {
          stack.Peek().DirectContent += line;
          continue;
        }

        string title = headingMatch.Groups[3].Value.Trim();

        if (string.IsNullOrWhiteSpace(title)) {
          stack.Peek().DirectContent += line;
          continue;
        }

        int headingLevel = headingMatch.Groups[2].Value.Length;

        while (stack.Count > 1 && stack.Peek().HeadingLevel >= headingLevel) {
          stack.Pop();
        }

        MarkdownNode parent = stack.Peek();
        MarkdownNode node = new MarkdownNode();
        node.Title = title;
        node.HeadingLevel = headingLevel;
        node.OriginalHeadingLine = line;
        node.HeadingLineModified = false;
        node.Parent = parent;

        parent.Children.Add(node);
        stack.Push(node);
      }

      this.RebuildLogicalSegments(root);
      return document;
    }

    private ParsedIncomingContent ParseIncomingContent(string content) {
      MarkdownDocumentModel parsed = this.ParseMarkdownDocument(string.Empty, content);
      MarkdownNode normalizedRoot = new MarkdownNode();
      normalizedRoot.DirectContent = parsed.Root.DirectContent;

      this.NormalizeIncomingHierarchy(parsed.Root, normalizedRoot);

      return new ParsedIncomingContent(normalizedRoot);
    }

    private void NormalizeIncomingHierarchy(
      MarkdownNode sourceRoot,
      MarkdownNode targetRoot
    ) {
      foreach (MarkdownNode sourceChild in sourceRoot.Children) {
        MarkdownNode cloned = this.CloneIncomingNode(sourceChild);
        cloned.Parent = targetRoot;
        targetRoot.Children.Add(cloned);
      }
    }

    private bool TryUpdateFenceState(
      string trimmedLine,
      ref char fenceCharacter,
      ref int fenceLength
    ) {
      if (string.IsNullOrEmpty(trimmedLine)) {
        return false;
      }

      char candidate = trimmedLine[0];

      if (candidate != '`' && candidate != '~') {
        return false;
      }

      int count = 0;

      while (count < trimmedLine.Length && trimmedLine[count] == candidate) {
        count++;
      }

      if (count < 3) {
        return false;
      }

      if (fenceCharacter == '\0') {
        fenceCharacter = candidate;
        fenceLength = count;
        return true;
      }

      if (fenceCharacter == candidate && count >= fenceLength) {
        fenceCharacter = '\0';
        fenceLength = 0;
        return true;
      }

      return false;
    }

    private void RebuildLogicalSegments(MarkdownNode parent) {
      Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

      foreach (MarkdownNode child in parent.Children) {
        string baseSegment = this.EncodeAreaSegment(child.Title);
        int occurrence = 1;

        if (counts.TryGetValue(baseSegment, out int existingCount)) {
          occurrence = existingCount + 1;
        }

        counts[baseSegment] = occurrence;

        if (occurrence == 1) {
          child.LogicalSegment = baseSegment;
        }
        else {
          child.LogicalSegment = baseSegment + "~" + occurrence.ToString();
        }

        this.RebuildLogicalSegments(child);
      }
    }

    private string AppendDirectContent(
      string existing,
      string incoming,
      string newLine
    ) {
      string incomingTrimmed = incoming.Trim('\r', '\n');

      if (string.IsNullOrWhiteSpace(incomingTrimmed)) {
        return existing;
      }

      if (string.IsNullOrEmpty(existing)) {
        return incomingTrimmed + newLine;
      }

      string existingWithoutTrailingBreaks = existing.TrimEnd('\r', '\n');

      return existingWithoutTrailingBreaks
        + newLine
        + newLine
        + incomingTrimmed
        + newLine;
    }

    private string NormalizeContentForRead(string content) {
      return content.Trim('\r', '\n');
    }

    private string RenderDocument(MarkdownDocumentModel document) {
      StringBuilder builder = new StringBuilder();
      builder.Append(document.Root.DirectContent);

      foreach (MarkdownNode child in document.Root.Children) {
        this.RenderPhysicalNode(child, builder, document.NewLine);
      }

      return builder.ToString();
    }

    private void RenderPhysicalNode(
      MarkdownNode node,
      StringBuilder builder,
      string newLine
    ) {
      if (node.HeadingLineModified || string.IsNullOrEmpty(node.OriginalHeadingLine)) {
        builder.Append(new string('#', node.HeadingLevel));
        builder.Append(' ');
        builder.Append(node.Title);
        builder.Append(newLine);
      }
      else {
        builder.Append(node.OriginalHeadingLine);
      }

      builder.Append(node.DirectContent);

      foreach (MarkdownNode child in node.Children) {
        this.RenderPhysicalNode(child, builder, newLine);
      }
    }

    private string CreateMutationSnapshot() {
      string snapshotRoot = Path.Combine(
        Path.GetTempPath(),
        ".knowledge-repository-transactions",
        Guid.NewGuid().ToString("N")
      );

      Directory.CreateDirectory(snapshotRoot);

      string snapshotDirectory = Path.Combine(snapshotRoot, "snapshot");
      Directory.CreateDirectory(snapshotDirectory);

      this.CopyDirectory(_RootDirectory, snapshotDirectory);

      return snapshotRoot;
    }

    private void RestoreMutationSnapshot(string snapshotRoot) {
      if (string.IsNullOrEmpty(snapshotRoot) || !Directory.Exists(snapshotRoot)) {
        return;
      }

      string snapshotDirectory = Path.Combine(
        snapshotRoot,
        "snapshot"
      );

      if (!Directory.Exists(snapshotDirectory)) {
        return;
      }

      // Restore every original artifact first. The previous implementation deleted the
      // complete repository before copying the snapshot back. If one target file was
      // locked by an editor, the restore could then fail after unrelated files had
      // already been deleted. Restoring first makes rollback fail-safe with regard to the
      // pre-mutation source data.
      this.RestoreDirectoryFromSnapshot(
        snapshotDirectory,
        _RootDirectory
      );

      // Only after every original artifact has been restored successfully do we remove
      // artifacts that were created by the failed mutation.
      this.RemoveArtifactsMissingFromSnapshot(
        snapshotDirectory,
        _RootDirectory
      );

      this.DeleteMutationSnapshot(
        snapshotRoot
      );
    }

    /// <summary>
    /// Restores all files and directories that existed in a mutation snapshot without
    /// deleting any current repository artifact first.
    /// </summary>
    private void RestoreDirectoryFromSnapshot(
      string snapshotDirectory,
      string targetDirectory
    ) {
      Directory.CreateDirectory(
        targetDirectory
      );

      string[] snapshotFiles = Directory.GetFiles(
        snapshotDirectory
      );

      foreach (string snapshotFile in snapshotFiles) {
        string targetFile = Path.Combine(
          targetDirectory,
          Path.GetFileName(snapshotFile)
        );

        this.CopyFileWithRetry(
          snapshotFile,
          targetFile,
          true
        );
      }

      string[] snapshotDirectories = Directory.GetDirectories(
        snapshotDirectory
      );

      foreach (string snapshotChildDirectory in snapshotDirectories) {
        string directoryName = Path.GetFileName(
          snapshotChildDirectory
        );

        string targetChildDirectory = Path.Combine(
          targetDirectory,
          directoryName
        );

        this.RestoreDirectoryFromSnapshot(
          snapshotChildDirectory,
          targetChildDirectory
        );
      }
    }

    /// <summary>
    /// Removes only artifacts that did not exist in the mutation snapshot.
    ///
    /// This cleanup is intentionally executed after the original snapshot contents have
    /// been restored. A cleanup failure can therefore leave an extra artifact behind but
    /// cannot remove the original repository state.
    /// </summary>
    private void RemoveArtifactsMissingFromSnapshot(
      string snapshotDirectory,
      string targetDirectory
    ) {
      string[] targetFiles = Directory.GetFiles(
        targetDirectory
      );

      foreach (string targetFile in targetFiles) {
        string snapshotFile = Path.Combine(
          snapshotDirectory,
          Path.GetFileName(targetFile)
        );

        if (!File.Exists(snapshotFile)) {
          this.DeleteFileWithRetry(
            targetFile
          );
        }
      }

      string[] targetDirectories = Directory.GetDirectories(
        targetDirectory
      );

      foreach (string targetChildDirectory in targetDirectories) {
        string directoryName = Path.GetFileName(
          targetChildDirectory
        );

        if (this.IsProviderInternalDirectory(directoryName)) {
          continue;
        }

        string snapshotChildDirectory = Path.Combine(
          snapshotDirectory,
          directoryName
        );

        if (!Directory.Exists(snapshotChildDirectory)) {
          Directory.Delete(
            targetChildDirectory,
            true
          );

          continue;
        }

        this.RemoveArtifactsMissingFromSnapshot(
          snapshotChildDirectory,
          targetChildDirectory
        );
      }
    }

    private void TryRestoreMutationSnapshot(string snapshotRoot) {
      try {
        this.RestoreMutationSnapshot(snapshotRoot);
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
      }
    }

    private void DeleteMutationSnapshot(string snapshotRoot) {
      if (string.IsNullOrEmpty(snapshotRoot)) {
        return;
      }

      if (Directory.Exists(snapshotRoot)) {
        Directory.Delete(snapshotRoot, true);
      }
    }

    /// <summary>
    /// Copies one file and retries transient sharing violations caused by editors or
    /// antivirus/indexing processes that temporarily hold an incompatible file handle.
    /// </summary>
    private void CopyFileWithRetry(
      string sourceFile,
      string targetFile,
      bool overwrite
    ) {
      for (int attempt = 1; attempt <= _FileIoRetryCount; attempt++) {
        try {
          File.Copy(
            sourceFile,
            targetFile,
            overwrite
          );

          return;
        }
        catch (IOException ex) {
          if (attempt >= _FileIoRetryCount) {
            DevLogger.LogError(ex);
            throw;
          }

          DevLogger.LogTrace(
            0,
            99999,
            "Knowledge file copy temporarily blocked. Retry "
            + attempt.ToString(CultureInfo.InvariantCulture)
            + "/"
            + _FileIoRetryCount.ToString(CultureInfo.InvariantCulture)
            + ": '"
            + targetFile
            + "'."
          );

          Thread.Sleep(
            _FileIoRetryDelayMilliseconds
          );
        }
      }
    }

    /// <summary>
    /// Deletes one file and retries transient sharing violations.
    /// </summary>
    private void DeleteFileWithRetry(
      string filePath
    ) {
      for (int attempt = 1; attempt <= _FileIoRetryCount; attempt++) {
        try {
          File.Delete(
            filePath
          );

          return;
        }
        catch (IOException ex) {
          if (attempt >= _FileIoRetryCount) {
            DevLogger.LogError(ex);
            throw;
          }

          DevLogger.LogTrace(
            0,
            99999,
            "Knowledge file delete temporarily blocked. Retry "
            + attempt.ToString(CultureInfo.InvariantCulture)
            + "/"
            + _FileIoRetryCount.ToString(CultureInfo.InvariantCulture)
            + ": '"
            + filePath
            + "'."
          );

          Thread.Sleep(
            _FileIoRetryDelayMilliseconds
          );
        }
      }
    }

    private void CopyDirectory(string sourceDirectory, string targetDirectory) {
      Directory.CreateDirectory(targetDirectory);

      string[] files = Directory.GetFiles(sourceDirectory);

      foreach (string file in files) {
        if (this.IsReparsePoint(file)) {
          continue;
        }

        string targetFile = Path.Combine(targetDirectory, Path.GetFileName(file));
        File.Copy(file, targetFile, true);
      }

      string[] directories = Directory.GetDirectories(sourceDirectory);

      foreach (string directory in directories) {
        string directoryName = Path.GetFileName(directory);

        if (this.IsProviderInternalDirectory(directoryName)) {
          continue;
        }

        if (this.IsReparsePoint(directory)) {
          continue;
        }

        string targetChild = Path.Combine(targetDirectory, directoryName);
        this.CopyDirectory(directory, targetChild);
      }
    }

    private void DeleteDirectoryContents(string directory) {
      string[] files = Directory.GetFiles(directory);

      foreach (string file in files) {
        File.Delete(file);
      }

      string[] directories = Directory.GetDirectories(directory);

      foreach (string childDirectory in directories) {
        Directory.Delete(childDirectory, true);
      }
    }

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

      if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }

    private string CombineAreaPath(string parent, string segment) {
      if (parent == _RootArea) {
        return _RootArea + segment;
      }

      return parent + "/" + segment;
    }

    /// <summary>
    /// Creates the canonical logical path segment used for a physical directory.
    /// </summary>
    private string CreateDirectorySegment(string directoryName) {
      return "[" + this.EncodeAreaSegment(directoryName) + "]";
    }

    /// <summary>
    /// Determines whether a logical segment explicitly addresses a physical directory.
    /// </summary>
    private bool IsDirectorySegment(string segment) {
      return segment.Length >= 2
        && segment.StartsWith("[", StringComparison.Ordinal)
        && segment.EndsWith("]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether an incoming aggregation heading explicitly denotes a directory.
    /// </summary>
    private bool IsDirectoryDisplayName(string name) {
      return this.IsDirectorySegment(name.Trim());
    }

    /// <summary>
    /// Resolves whether a directory is a pure navigation area or a one-level content
    /// aggregation area.
    /// 
    /// A directory becomes ContentAggregation only when it contains at least one
    /// Markdown file directly. Markdown files located in descendant directories do not
    /// affect the content level of this directory.
    /// </summary>
    private ContentLevel GetDirectoryContentLevel(string directoryPath) {
      string[] markdownFiles = Directory.GetFiles(
        directoryPath,
        "*" + _MarkdownExtension,
        SearchOption.TopDirectoryOnly
      );

      foreach (string markdownFile in markdownFiles) {
        if (this.IsReparsePoint(markdownFile)) {
          continue;
        }

        if (this.IsSoftDeletedMarkdownFile(markdownFile) ||
            this.IsResourceCompanionFile(markdownFile)) {
          continue;
        }

        if (!this.IsPhysicalPathExposed(
              markdownFile,
              false
            )) {
          continue;
        }

        return ContentLevel.ContentAggregation;
      }

      return ContentLevel.BeyondContent;
    }

    private string EncodeAreaSegment(string value) {
      return value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("/", "%2F", StringComparison.Ordinal)
        .Replace("\\", "%5C", StringComparison.Ordinal)
        .Replace("[", "%5B", StringComparison.Ordinal)
        .Replace("]", "%5D", StringComparison.Ordinal);
    }

    private string DecodeAreaSegment(string value) {
      return value
        .Replace("%5D", "]", StringComparison.OrdinalIgnoreCase)
        .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase)
        .Replace("%5C", "\\", StringComparison.OrdinalIgnoreCase)
        .Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
        .Replace("%25", "%", StringComparison.OrdinalIgnoreCase);
    }

    private string DetectNewLine(string content) {
      if (content.Contains("\r\n", StringComparison.Ordinal)) {
        return "\r\n";
      }

      if (content.Contains("\n", StringComparison.Ordinal)) {
        return "\n";
      }

      if (content.Contains("\r", StringComparison.Ordinal)) {
        return "\r";
      }

      return Environment.NewLine;
    }

    private bool IsProviderInternalDirectory(string directoryName) {
      return directoryName.StartsWith(".knowledge-repository-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a provider-visible physical directory or Markdown base name
    /// is safe to use as exactly one physical child path segment.
    /// </summary>
    private bool IsValidPhysicalName(string name) {
      if (string.IsNullOrWhiteSpace(name)) {
        return false;
      }

      if (name == "." || name == "..") {
        return false;
      }

      if (name.IndexOf(Path.DirectorySeparatorChar) >= 0) {
        return false;
      }

      if (name.IndexOf(Path.AltDirectorySeparatorChar) >= 0) {
        return false;
      }

      char[] invalidCharacters = Path.GetInvalidFileNameChars();

      if (name.IndexOfAny(invalidCharacters) >= 0) {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Builds one physical child path and verifies that the resulting path remains
    /// strictly below the supplied parent directory.
    /// </summary>
    private string GetSafePhysicalChildPath(
      string parentDirectory,
      string childName,
      string extension
    ) {
      if (!this.IsValidPhysicalName(childName)) {
        throw new InvalidOperationException(
          "The logical area name cannot be mapped to a safe physical path segment: " + childName
        );
      }

      string normalizedParent = Path.GetFullPath(parentDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

      string childPath = Path.GetFullPath(
        Path.Combine(normalizedParent, childName + extension)
      );

      string expectedPrefix = normalizedParent + Path.DirectorySeparatorChar;

      if (!childPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException(
          "The resolved physical path escapes the configured knowledge repository."
        );
      }

      return childPath;
    }

    /// <summary>
    /// Determines whether a path is a symbolic link or another file-system reparse point.
    /// Such entries are not traversed as knowledge areas because they could escape the
    /// configured knowledge root or create recursive directory graphs.
    /// </summary>
    private bool IsReparsePoint(string path) {
      FileAttributes attributes = File.GetAttributes(path);
      return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private void EnsureInitialized() {
      if (!_Initialized) {
        throw new InvalidOperationException("The knowledge repository has not been initialized.");
      }
    }

    /// <summary>
    /// Describes one resolved logical area.
    /// </summary>
    protected sealed class AreaDescriptor {

      private AreaKind _Kind;
      private string _AreaPath;
      private string _DisplayName;
      private string _PhysicalPath;
      private ContentLevel _ContentLevel;
      private MarkdownDocumentModel _Document;
      private MarkdownNode _Node;

      private AreaDescriptor() {
        _AreaPath = string.Empty;
        _DisplayName = string.Empty;
        _PhysicalPath = string.Empty;
      }

      /// <summary>
      /// Gets the provider-specific area kind.
      /// </summary>
      public AreaKind Kind {
        get {
          return _Kind;
        }
        private set {
          _Kind = value;
        }
      }

      /// <summary>
      /// Gets the absolute logical area path.
      /// </summary>
      public string AreaPath {
        get {
          return _AreaPath;
        }
        private set {
          _AreaPath = value;
        }
      }

      /// <summary>
      /// Gets the human-readable direct area name.
      /// </summary>
      public string DisplayName {
        get {
          return _DisplayName;
        }
        private set {
          _DisplayName = value;
        }
      }

      /// <summary>
      /// Gets the physical file-system path represented by the area.
      /// </summary>
      public string PhysicalPath {
        get {
          return _PhysicalPath;
        }
        private set {
          _PhysicalPath = value;
        }
      }

      /// <summary>
      /// Gets the effective content level.
      /// </summary>
      public ContentLevel ContentLevel {
        get {
          return _ContentLevel;
        }
        private set {
          _ContentLevel = value;
        }
      }

      /// <summary>
      /// Gets the loaded Markdown document for document and heading areas.
      /// </summary>
      public MarkdownDocumentModel Document {
        get {
          return _Document;
        }
        private set {
          _Document = value;
        }
      }

      /// <summary>
      /// Gets the concrete heading node for heading areas.
      /// </summary>
      public MarkdownNode Node {
        get {
          return _Node;
        }
        private set {
          _Node = value;
        }
      }

      /// <summary>
      /// Creates the logical root descriptor.
      /// </summary>
      public static AreaDescriptor CreateRoot(
        string physicalPath,
        ContentLevel contentLevel
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Root;
        descriptor.AreaPath = "/";
        descriptor.DisplayName = "/";
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = contentLevel;
        return descriptor;
      }

      /// <summary>
      /// Creates a directory aggregation descriptor.
      /// </summary>
      public static AreaDescriptor CreateDirectory(
        string areaPath,
        string displayName,
        string physicalPath,
        ContentLevel contentLevel
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Directory;
        descriptor.AreaPath = areaPath;
        descriptor.DisplayName = displayName;
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = contentLevel;
        return descriptor;
      }

      /// <summary>
      /// Creates a Markdown document descriptor.
      /// </summary>
      public static AreaDescriptor CreateDocument(
        string areaPath,
        string displayName,
        string physicalPath,
        MarkdownDocumentModel document
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Document;
        descriptor.AreaPath = areaPath;
        descriptor.DisplayName = displayName;
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = ContentLevel.ContentContainer;
        descriptor.Document = document;
        return descriptor;
      }

      /// <summary>
      /// Creates a Markdown heading descriptor.
      /// </summary>
      public static AreaDescriptor CreateHeading(
        string areaPath,
        string displayName,
        string physicalPath,
        MarkdownDocumentModel document,
        MarkdownNode node
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Heading;
        descriptor.AreaPath = areaPath;
        descriptor.DisplayName = displayName;
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = ContentLevel.ContentContainer;
        descriptor.Document = document;
        descriptor.Node = node;
        return descriptor;
      }
    }

    /// <summary>
    /// Identifies the provider-specific physical representation of an area.
    /// </summary>
    protected enum AreaKind {
      Root = 0,
      Directory = 1,
      Document = 2,
      Heading = 3
    }

    /// <summary>
    /// Represents one parsed Markdown document.
    /// </summary>
    protected sealed class MarkdownDocumentModel {

      private readonly string _FilePath;
      private readonly string _NewLine;
      private readonly MarkdownNode _Root;
      private bool _Changed;

      /// <summary>
      /// Creates a parsed document model.
      /// </summary>
      public MarkdownDocumentModel(string filePath, string newLine) {
        _FilePath = filePath;
        _NewLine = newLine;
        _Root = new MarkdownNode();
        _Changed = false;
      }

      /// <summary>
      /// Gets the physical Markdown file path.
      /// </summary>
      public string FilePath {
        get {
          return _FilePath;
        }
      }

      /// <summary>
      /// Gets the preferred document line ending.
      /// </summary>
      public string NewLine {
        get {
          return _NewLine;
        }
      }

      /// <summary>
      /// Gets the virtual document root node.
      /// </summary>
      public MarkdownNode Root {
        get {
          return _Root;
        }
      }

      /// <summary>
      /// Gets whether the document has been mutated.
      /// </summary>
      public bool Changed {
        get {
          return _Changed;
        }
      }

      /// <summary>
      /// Marks the document as mutated.
      /// </summary>
      public void MarkChanged() {
        _Changed = true;
      }
    }

    /// <summary>
    /// Represents one logical Markdown content node.
    /// </summary>
    protected sealed class MarkdownNode {

      private string _Title;
      private string _LogicalSegment;
      private int _HeadingLevel;
      private string _DirectContent;
      private string _OriginalHeadingLine;
      private bool _HeadingLineModified;
      private MarkdownNode _Parent;
      private readonly List<MarkdownNode> _Children;

      /// <summary>
      /// Creates an empty Markdown node.
      /// </summary>
      public MarkdownNode() {
        _Title = string.Empty;
        _LogicalSegment = string.Empty;
        _HeadingLevel = 0;
        _DirectContent = string.Empty;
        _OriginalHeadingLine = string.Empty;
        _HeadingLineModified = false;
        _Children = new List<MarkdownNode>();
      }

      /// <summary>
      /// Gets or sets the display title.
      /// </summary>
      public string Title {
        get {
          return _Title;
        }
        set {
          _Title = value;
        }
      }

      /// <summary>
      /// Gets or sets the canonical logical path segment.
      /// </summary>
      public string LogicalSegment {
        get {
          return _LogicalSegment;
        }
        set {
          _LogicalSegment = value;
        }
      }

      /// <summary>
      /// Gets or sets the physical Markdown heading level.
      /// </summary>
      public int HeadingLevel {
        get {
          return _HeadingLevel;
        }
        set {
          _HeadingLevel = value;
        }
      }

      /// <summary>
      /// Gets or sets direct textual content owned by this node.
      /// </summary>
      public string DirectContent {
        get {
          return _DirectContent;
        }
        set {
          _DirectContent = value;
        }
      }

      /// <summary>
      /// Gets or sets the original heading line for minimally invasive rendering.
      /// </summary>
      public string OriginalHeadingLine {
        get {
          return _OriginalHeadingLine;
        }
        set {
          _OriginalHeadingLine = value;
        }
      }

      /// <summary>
      /// Gets or sets whether the heading line must be regenerated.
      /// </summary>
      public bool HeadingLineModified {
        get {
          return _HeadingLineModified;
        }
        set {
          _HeadingLineModified = value;
        }
      }

      /// <summary>
      /// Gets or sets the logical parent node.
      /// </summary>
      public MarkdownNode Parent {
        get {
          return _Parent;
        }
        set {
          _Parent = value;
        }
      }

      /// <summary>
      /// Gets the ordered child-node collection.
      /// </summary>
      public List<MarkdownNode> Children {
        get {
          return _Children;
        }
      }

      /// <summary>
      /// Creates a new physical ATX heading node.
      /// </summary>
      public static MarkdownNode CreateNewHeading(
        string title,
        int headingLevel,
        string newLine
      ) {
        MarkdownNode node = new MarkdownNode();
        node.Title = title;
        node.HeadingLevel = headingLevel;
        node.OriginalHeadingLine = string.Empty;
        node.HeadingLineModified = true;
        node.DirectContent = string.Empty;
        return node;
      }
    }

    /// <summary>
    /// Encapsulates the parsed root of one incoming structured content payload.
    /// </summary>
    protected sealed class ParsedIncomingContent {

      private readonly MarkdownNode _Root;

      /// <summary>
      /// Creates the parsed incoming content wrapper.
      /// </summary>
      public ParsedIncomingContent(MarkdownNode root) {
        _Root = root;
      }

      /// <summary>
      /// Gets the virtual incoming root.
      /// </summary>
      public MarkdownNode Root {
        get {
          return _Root;
        }
      }
    }

    /// <summary>
    /// Caches parsed Markdown documents during one logical mutation so cross-document
    /// operations modify one coherent in-memory view before persistence.
    /// </summary>
    protected sealed class MutationContext {

      private readonly FileBasedKnowledgeRepository _Owner;
      private readonly Dictionary<string, MarkdownDocumentModel> _Documents;

      /// <summary>
      /// Creates a mutation context.
      /// </summary>
      public MutationContext(FileBasedKnowledgeRepository owner) {
        _Owner = owner;
        _Documents = new Dictionary<string, MarkdownDocumentModel>(StringComparer.OrdinalIgnoreCase);
      }

      /// <summary>
      /// Gets or parses one Markdown document.
      /// </summary>
      public MarkdownDocumentModel GetDocument(string filePath) {
        string fullPath = Path.GetFullPath(filePath);

        if (_Documents.TryGetValue(fullPath, out MarkdownDocumentModel document)) {
          return document;
        }

        string physicalContent = File.ReadAllText(
          fullPath,
          Encoding.UTF8
        );

        string knowledgeContent = _Owner.ConvertPhysicalMarkdownToKnowledgeMarkdown(
          fullPath,
          physicalContent
        );

        document = _Owner.ParseMarkdownDocument(
          fullPath,
          knowledgeContent
        );
        _Documents.Add(fullPath, document);
        return document;
      }

      /// <summary>
      /// Removes a document from the mutation cache.
      /// </summary>
      public void ForgetDocument(string filePath) {
        string fullPath = Path.GetFullPath(filePath);
        _Documents.Remove(fullPath);
      }

      /// <summary>
      /// Removes all cached documents located below a physical directory.
      /// </summary>
      public void ForgetDocumentsBelow(string directoryPath) {
        string normalizedDirectory = Path.GetFullPath(directoryPath)
          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
          + Path.DirectorySeparatorChar;

        string[] keys = _Documents.Keys
          .Where((string key) =>
            key.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
          .ToArray();

        foreach (string key in keys) {
          _Documents.Remove(key);
        }
      }

      /// <summary>
      /// Persists every changed Markdown document using an atomic file replacement.
      /// </summary>
      public void SaveChanges() {
        foreach (MarkdownDocumentModel document in _Documents.Values) {
          if (!document.Changed) {
            continue;
          }

          string knowledgeContent = _Owner.RenderDocument(
            document
          );

          string physicalContent = _Owner.ConvertKnowledgeMarkdownToPhysicalMarkdown(
            document.FilePath,
            knowledgeContent
          );

          _Owner.WriteAllTextAtomically(
            document.FilePath,
            physicalContent
          );
        }
      }
    }

    /// <summary>
    /// Writes binary content through a temporary file and retries transient replacement
    /// failures caused by editors, antivirus software or indexing processes.
    /// </summary>
    private void WriteAllBytesAtomically(
      string filePath,
      byte[] content
    ) {
      string directory = Path.GetDirectoryName(filePath);

      if (string.IsNullOrEmpty(directory)) {
        throw new InvalidOperationException(
          "Cannot determine the resource directory."
        );
      }

      string temporaryFile = Path.Combine(
        directory,
        ".knowledge-repository-resource-write-"
        + Guid.NewGuid().ToString("N")
        + ".tmp"
      );

      File.WriteAllBytes(
        temporaryFile,
        content
      );

      try {
        for (int attempt = 1; attempt <= _FileIoRetryCount; attempt++) {
          try {
            File.Move(
              temporaryFile,
              filePath,
              true
            );

            return;
          }
          catch (IOException ex) {
            if (attempt >= _FileIoRetryCount) {
              DevLogger.LogError(ex);
              throw;
            }

            DevLogger.LogTrace(
              0,
              99999,
              "Knowledge resource write temporarily blocked. Retry "
              + attempt.ToString(CultureInfo.InvariantCulture)
              + "/"
              + _FileIoRetryCount.ToString(CultureInfo.InvariantCulture)
              + ": '"
              + filePath
              + "'."
            );

            Thread.Sleep(
              _FileIoRetryDelayMilliseconds
            );
          }
        }
      }
      finally {
        if (File.Exists(temporaryFile)) {
          try {
            File.Delete(
              temporaryFile
            );
          }
          catch (IOException ex) {
            DevLogger.LogError(ex);
          }
          catch (UnauthorizedAccessException ex) {
            DevLogger.LogError(ex);
          }
        }
      }
    }

    private void WriteAllTextAtomically(string filePath, string content) {
      string directory = Path.GetDirectoryName(filePath);

      if (string.IsNullOrEmpty(directory)) {
        throw new InvalidOperationException(
          "Cannot determine the document directory."
        );
      }

      string temporaryFile = Path.Combine(
        directory,
        ".knowledge-repository-write-"
        + Guid.NewGuid().ToString("N")
        + ".tmp"
      );

      File.WriteAllText(
        temporaryFile,
        content,
        new UTF8Encoding(false)
      );

      try {
        for (int attempt = 1; attempt <= _FileIoRetryCount; attempt++) {
          try {
            File.Move(
              temporaryFile,
              filePath,
              true
            );

            return;
          }
          catch (IOException ex) {
            if (attempt >= _FileIoRetryCount) {
              DevLogger.LogError(ex);
              throw;
            }

            DevLogger.LogTrace(
              0,
              99999,
              "Knowledge atomic write temporarily blocked. Retry "
              + attempt.ToString(CultureInfo.InvariantCulture)
              + "/"
              + _FileIoRetryCount.ToString(CultureInfo.InvariantCulture)
              + ": '"
              + filePath
              + "'."
            );

            Thread.Sleep(
              _FileIoRetryDelayMilliseconds
            );
          }
        }
      }
      finally {
        if (File.Exists(temporaryFile)) {
          try {
            File.Delete(
              temporaryFile
            );
          }
          catch (IOException ex) {
            DevLogger.LogError(ex);
          }
          catch (UnauthorizedAccessException ex) {
            DevLogger.LogError(ex);
          }
        }
      }
    }
  }
}
