using Logging.SmartStandards.CopyForKnowledgeManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KnowledgeManagement.SmartStandards.Endpoints.Joplin {

  /// <summary>
  /// Exposes one <see cref="IKnowledgeRepository"/> as a Joplin-compatible WebDAV
  /// synchronization target.
  /// 
  /// The facade deliberately separates provider-neutral knowledge from Joplin-specific
  /// synchronization artifacts:
  /// 
  /// - Knowledge areas are projected to Joplin notebook and note sync items.
  /// - Repository resources are projected to Joplin resource metadata and blob items.
  /// - Joplin locks, temporary files, sync metadata and unsupported item types are
  ///   persisted through <see cref="IJoplinSyncStateStore"/>.
  /// 
  /// Structural and aggregation areas are projected as Joplin notebooks. The first
  /// <see cref="ContentLevel.ContentContainer"/> below a non-container area is projected
  /// as one Joplin note whose body is the complete aggregated Markdown content of that
  /// container. Nested content containers remain headings inside that note rather than
  /// being duplicated as additional Joplin notes.
  /// 
  /// This projection allows a normal Joplin client to consume and edit repository
  /// knowledge without requiring a custom Joplin client or plugin.
  /// 
  /// This type is deliberately not an MVC controller and has no route or HTTP-method
  /// attributes. <see cref="JoplinKnowledgeRepositoryWebDavMiddleware"/> dispatches raw
  /// HTTP methods directly from <see cref="HttpRequest.Method"/>. MVC routing, ApiExplorer,
  /// Swagger and formatter negotiation therefore never interpret WebDAV methods such as
  /// PROPFIND, MKCOL or MOVE.
  /// </summary>
  public partial class JoplinKnowledgeRepositoryWebDavHandler : ControllerBase {

    private const string _ProjectionStateFileName = "projection.json";
    private const string _InfoFilePath = "/info.json";
    private const string _LocksCollection = "/locks";
    private const string _TempCollection = "/temp";
    private const string _ResourceCollection = "/.resource";
    private const string _LegacySyncCollection = "/.sync";
    private const string _LegacyLockCollection = "/.lock";
    private const string _MarkdownExtension = ".md";
    private const int _JoplinSyncVersion = 3;
    private const int _JoplinNoteType = 1;
    private const int _JoplinFolderType = 2;
    private const int _JoplinResourceType = 4;
    private const string _KnowledgeResourceReferencePrefix = "knowledge-resource:";
    private const string _KnowledgeAreaReferencePrefix = "knowledge-area:";

    private static readonly Regex _JoplinResourceReferenceRegex = new Regex(
      @":/(?<id>[0-9a-fA-F]{32})",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _KnowledgeResourceReferenceRegex = new Regex(
      @"knowledge-resource:(?<id>[A-Za-z0-9._~-]+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _KnowledgeAreaReferenceRegex = new Regex(
      @"knowledge-area:(?<area>[^\s\)\]\>""']+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private readonly object _SyncRoot;
    private readonly IKnowledgeRepository _KnowledgeRepository;
    private readonly IJoplinSyncStateStore _SyncStateStore;
    private readonly string _WebDavBasePath;

    /// <summary>
    /// Creates the Joplin WebDAV protocol handler using the default application-relative
    /// endpoint path.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The provider-neutral knowledge repository exposed as Joplin notebooks and notes.
    /// </param>
    /// <param name="syncStateStore">
    /// Persistent storage for Joplin-specific synchronization artifacts that do not
    /// belong in the knowledge repository itself.
    /// </param>
    public JoplinKnowledgeRepositoryWebDavHandler(
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStore syncStateStore
    ) : this(
      knowledgeRepository,
      syncStateStore,
      "/api/knowledge/joplin"
    ) {
    }

    /// <summary>
    /// Creates the Joplin WebDAV protocol handler for one concrete profile-scoped endpoint.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The provider-neutral knowledge repository exposed as Joplin notebooks and notes.
    /// </param>
    /// <param name="syncStateStore">
    /// Persistent storage for Joplin-specific synchronization artifacts that do not
    /// belong in the knowledge repository itself.
    /// </param>
    /// <param name="webDavBasePath">
    /// The application-relative WebDAV root including the profile URL segment, for example
    /// <c>/api/knowledge/joplin/123456789</c>.
    /// </param>
    public JoplinKnowledgeRepositoryWebDavHandler(
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStore syncStateStore,
      string webDavBasePath
    ) {
      if (knowledgeRepository == null) {
        throw new ArgumentNullException(
          nameof(knowledgeRepository)
        );
      }

      if (syncStateStore == null) {
        throw new ArgumentNullException(
          nameof(syncStateStore)
        );
      }

      if (string.IsNullOrWhiteSpace(webDavBasePath)) {
        throw new ArgumentException(
          "A Joplin WebDAV base path is required.",
          nameof(webDavBasePath)
        );
      }

      _SyncRoot = new object();
      _KnowledgeRepository = knowledgeRepository;
      _SyncStateStore = syncStateStore;
      _WebDavBasePath = this.NormalizeWebDavBasePath(
        webDavBasePath
      );

      this.EnsureJoplinInfrastructure();
    }

    /// <summary>
    /// Returns WebDAV capability information for the synchronization root.
    /// </summary>
    public IActionResult OptionsRoot() {
      return this.OptionsInternal();
    }

    /// <summary>
    /// Returns WebDAV capability information for any nested synchronization path.
    /// </summary>
    public IActionResult OptionsPath(string path) {
      return this.OptionsInternal();
    }

    /// <summary>
    /// Handles WebDAV PROPFIND for the synchronization root.
    /// </summary>
    public IActionResult PropFindRoot() {
      return this.PropFindInternal("/");
    }

    /// <summary>
    /// Handles WebDAV PROPFIND for a nested synchronization path.
    /// </summary>
    public IActionResult PropFindPath(string path) {
      return this.PropFindInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles GET for the synchronization root or a concrete WebDAV file.
    /// </summary>
    public IActionResult GetRoot() {
      return this.GetInternal("/");
    }

    /// <summary>
    /// Handles GET for a concrete nested WebDAV file.
    /// </summary>
    public IActionResult GetPath(string path) {
      return this.GetInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles HEAD for the synchronization root.
    /// </summary>
    public IActionResult HeadRoot() {
      return this.HeadInternal("/");
    }

    /// <summary>
    /// Handles HEAD for one nested WebDAV path.
    /// </summary>
    public IActionResult HeadPath(string path) {
      return this.HeadInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles PUT for one nested WebDAV file.
    /// </summary>
    public IActionResult PutPath(string path) {
      return this.PutInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles DELETE for one nested WebDAV file or collection.
    /// </summary>
    public IActionResult DeletePath(string path) {
      return this.DeleteInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles WebDAV MKCOL for one nested collection.
    /// </summary>
    public IActionResult MkColPath(string path) {
      return this.MkColInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles WebDAV MOVE for one nested file or collection.
    /// </summary>
    public IActionResult MovePath(string path) {
      return this.MoveInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Returns the WebDAV methods implemented by this facade.
    /// </summary>
    private IActionResult OptionsInternal() {
      this.TraceWebDavRequest();

      this.Response.Headers["DAV"] = "1";
      this.Response.Headers["Allow"] =
        "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, MOVE";

      return this.Ok();
    }

    /// <summary>
    /// Builds a standards-oriented WebDAV multi-status response for one resource and,
    /// when Depth is not zero, its direct children.
    /// </summary>
    private IActionResult PropFindInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinProjection projection = null;
        JoplinWebDavEntry entry;

        if (!string.Equals(
              path,
              "/",
              StringComparison.Ordinal
            ) &&
            this.TryResolveStateStoreEntry(
              path,
              out entry
            )) {
          // Opaque Joplin sync-state paths are completely independent of the dynamic
          // Knowledge projection and can therefore be served directly.
        }
        else {
          projection =
            this.BuildProjectionForWebDavPath(
              path
            );

          entry = this.ResolveWebDavEntry(
            path,
            projection
          );
        }

        if (entry == null) {
          return this.NotFound();
        }

        List<JoplinWebDavEntry> entries =
          new List<JoplinWebDavEntry>();

        entries.Add(
          entry
        );

        string depth = this.Request.Headers["Depth"].ToString();

        if (!string.Equals(
              depth,
              "0",
              StringComparison.Ordinal
            )) {
          JoplinWebDavEntry[] children;

          if (projection == null) {
            children = this.GetStateStoreChildren(
              path
            );
          }
          else {
            children = this.GetWebDavChildren(
              path,
              projection
            );
          }

          entries.AddRange(
            children
          );
        }

        XNamespace dav = "DAV:";
        XElement multiStatus = new XElement(dav + "multistatus");

        foreach (JoplinWebDavEntry currentEntry in entries) {
          XElement properties = new XElement(
            dav + "prop",
            new XElement(
              dav + "displayname",
              currentEntry.DisplayName
            ),
            new XElement(
              dav + "getlastmodified",
              currentEntry.LastModifiedUtc.ToString(
                "R",
                CultureInfo.InvariantCulture
              )
            ),
            new XElement(
              dav + "getcontentlength",
              this.GetWebDavContentLength(currentEntry)
            ),
            new XElement(
              dav + "getetag",
              "\"" + currentEntry.ETag + "\""
            )
          );

          if (currentEntry.IsCollection) {
            properties.Add(
              new XElement(
                dav + "resourcetype",
                new XElement(dav + "collection")
              )
            );
          }
          else {
            properties.Add(
              new XElement(dav + "resourcetype")
            );
          }

          XElement response = new XElement(
            dav + "response",
            new XElement(
              dav + "href",
              this.BuildWebDavHref(currentEntry.Path)
            ),
            new XElement(
              dav + "propstat",
              properties,
              new XElement(
                dav + "status",
                "HTTP/1.1 200 OK"
              )
            )
          );

          multiStatus.Add(response);
        }

        XDocument document = new XDocument(
          new XDeclaration("1.0", "utf-8", "yes"),
          multiStatus
        );

        this.Response.StatusCode = StatusCodes.Status207MultiStatus;

        return this.Content(
          document.ToString(SaveOptions.DisableFormatting),
          "application/xml; charset=utf-8",
          Encoding.UTF8
        );
      }
    }

    /// <summary>
    /// Returns a projected Joplin item or one opaque synchronization-state file.
    /// Collections are not returned as file content.
    /// </summary>
    private IActionResult GetInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinWebDavEntry stateEntry;

        if (this.TryResolveStateStoreEntry(
              path,
              out stateEntry
            )) {
          if (stateEntry.IsCollection) {
            return this.StatusCode(
              StatusCodes.Status405MethodNotAllowed
            );
          }

          byte[] stateContent = _SyncStateStore.ReadFile(
            stateEntry.Path
          );

          this.ApplyFileHeaders(
            stateEntry
          );

          return this.File(
            stateContent,
            "application/octet-stream"
          );
        }

        JoplinProjection projection =
          this.BuildProjectionForWebDavPath(
            path
          );

        JoplinWebDavEntry entry = this.ResolveWebDavEntry(
          path,
          projection
        );

        if (entry == null) {
          return this.NotFound();
        }

        if (entry.IsCollection) {
          return this.StatusCode(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        byte[] content = this.GetWebDavFileContent(
          entry,
          projection
        );

        this.ApplyFileHeaders(
          entry
        );

        return this.File(
          content,
          "application/octet-stream"
        );
      }
    }

    /// <summary>
    /// Returns only metadata headers for one WebDAV resource.
    /// </summary>
    private IActionResult HeadInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinWebDavEntry stateEntry;

        if (this.TryResolveStateStoreEntry(
              path,
              out stateEntry
            )) {
          this.ApplyFileHeaders(
            stateEntry
          );

          return this.Ok();
        }

        JoplinProjection projection =
          this.BuildProjectionForWebDavPath(
            path
          );

        JoplinWebDavEntry entry = this.ResolveWebDavEntry(
          path,
          projection
        );

        if (entry == null) {
          return this.NotFound();
        }

        this.ApplyFileHeaders(
          entry
        );

        return this.Ok();
      }
    }

    /// <summary>
    /// Accepts one Joplin synchronization file.
    /// 
    /// Joplin note, folder and resource item files are translated back into provider-neutral
    /// <see cref="IKnowledgeRepository"/> mutations whenever their dependencies are available.
    /// Unsupported Joplin item types and auxiliary synchronization files are persisted
    /// opaquely in the state store.
    /// </summary>
    private IActionResult PutInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        byte[] content;

        try {
          using MemoryStream buffer = new MemoryStream();
          this.Request.Body.CopyTo(buffer);
          content = buffer.ToArray();
        }
        catch (IOException ex) {
          DevLogger.LogError(ex);
          return this.BadRequest();
        }

        if (string.Equals(path, _InfoFilePath, StringComparison.Ordinal)) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (!this.IsRootItemFile(path)) {
          bool hadPreviousState = _SyncStateStore.FileExists(path);
          byte[] previousState = Array.Empty<byte>();

          if (hadPreviousState) {
            previousState = _SyncStateStore.ReadFile(path);
          }

          _SyncStateStore.WriteFile(path, content);

          if (path.StartsWith(
                _ResourceCollection + "/",
                StringComparison.Ordinal
              )) {
            string resourceId = path.Substring(
              (_ResourceCollection + "/").Length
            );

            JoplinProjectionState state = this.LoadProjectionState();
            ResourceApplyResult resourceApplyResult = this.TryApplyUploadedJoplinResource(
              resourceId,
              state
            );

            if (resourceApplyResult == ResourceApplyResult.TemporarilyUnavailable) {
              this.RestoreJoplinStateFile(
                path,
                hadPreviousState,
                previousState
              );

              this.Response.Headers["Retry-After"] = "1";
              return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (resourceApplyResult == ResourceApplyResult.Invalid) {
              this.RestoreJoplinStateFile(
                path,
                hadPreviousState,
                previousState
              );

              return this.BadRequest(
                "The Joplin resource item is not valid."
              );
            }

            this.SaveProjectionState(state);
            this.MaterializePendingKnowledgeItems();
          }

          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        string text = Encoding.UTF8.GetString(content);
        JoplinSerializedItem item = this.ParseJoplinItem(text);

        if (item == null) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (item.Type == _JoplinResourceType) {
          bool hadPreviousState = _SyncStateStore.FileExists(path);
          byte[] previousState = Array.Empty<byte>();

          if (hadPreviousState) {
            previousState = _SyncStateStore.ReadFile(path);
          }

          _SyncStateStore.WriteFile(path, content);

          JoplinProjectionState state = this.LoadProjectionState();
          ResourceApplyResult resourceApplyResult = this.TryApplyUploadedJoplinResource(
            item.Id,
            state
          );

          if (resourceApplyResult == ResourceApplyResult.TemporarilyUnavailable) {
            this.RestoreJoplinStateFile(
              path,
              hadPreviousState,
              previousState
            );

            this.Response.Headers["Retry-After"] = "1";
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
          }

          if (resourceApplyResult == ResourceApplyResult.Invalid) {
            this.RestoreJoplinStateFile(
              path,
              hadPreviousState,
              previousState
            );

            return this.BadRequest(
              "The Joplin resource metadata item is not valid."
            );
          }

          this.SaveProjectionState(state);
          this.MaterializePendingKnowledgeItems();
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (item.Type != _JoplinNoteType &&
            item.Type != _JoplinFolderType) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        // A real Joplin sync target must accept an item independently of whether its
        // semantic parent has already arrived. Persist the raw item first. It remains
        // authoritative for WebDAV reads until it can be materialized successfully into
        // the knowledge repository.
        _SyncStateStore.WriteFile(path, content);

        string itemId = Path.GetFileNameWithoutExtension(path);

        if (!string.Equals(
              item.Id,
              itemId,
              StringComparison.OrdinalIgnoreCase
            )) {
          return this.BadRequest(
            "The Joplin item ID does not match the WebDAV file name."
          );
        }

        JoplinProjection projection =
          this.BuildStateProjection();

        JoplinProjectionRecord existingRecord =
          projection.FindRecordById(
            itemId
          );

        MaterializationResult materializationResult;

        if (existingRecord == null) {
          materializationResult = this.CreateKnowledgeItemFromJoplin(
            item,
            projection
          );
        }
        else {
          materializationResult = this.UpdateKnowledgeItemFromJoplin(
            item,
            existingRecord,
            projection
          );
        }

        if (materializationResult == MaterializationResult.PendingDependency) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin item '"
            + item.Id
            + "' accepted as pending because a referenced parent item is not available yet."
          );

          this.SaveProjectionState(projection.State);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (materializationResult == MaterializationResult.TemporarilyUnavailable) {
          _SyncStateStore.Delete(
            path
          );

          this.Response.Headers["Retry-After"] = "1";

          DevLogger.LogTrace(
            0,
            99999,
            "Joplin PUT could not be committed because the knowledge storage is temporarily unavailable: id="
            + item.Id
            + "."
          );

          return this.StatusCode(
            StatusCodes.Status503ServiceUnavailable
          );
        }

        if (materializationResult == MaterializationResult.Failed) {
          return this.Conflict(
            "The Joplin item was accepted by the sync target but could not be mapped to the knowledge repository."
          );
        }

        // The knowledge repository now represents the item. Remove the temporary raw
        // sync copy so subsequent reads use the dynamic knowledge projection.
        _SyncStateStore.Delete(path);

        this.SaveProjectionState(projection.State);

        // A newly materialized notebook may unlock notes that arrived before it.
        this.MaterializePendingKnowledgeItems();

        return this.StatusCode(StatusCodes.Status204NoContent);
      }
    }

    /// <summary>
    /// Removes one Joplin synchronization item from the projection.
    ///
    /// A WebDAV DELETE is deliberately treated as synchronization state rather than as
    /// authorization to destroy provider-neutral knowledge. Projected notes, notebooks
    /// and resources are therefore suppressed from the Joplin projection without
    /// invoking destructive knowledge-repository operations.
    /// </summary>
    private IActionResult DeleteInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinProjection projection =
          this.BuildStateProjection();

        if (this.IsRootItemFile(path)) {
          string itemId = Path.GetFileNameWithoutExtension(path);
          JoplinProjectionRecord record = projection.FindRecordById(itemId);

          if (record != null) {
            // A WebDAV DELETE is a synchronization-protocol operation. It must not be
            // translated blindly into a destructive knowledge-repository delete because
            // Joplin may remove/reconcile remote sync items for reasons that do not mean
            // "physically destroy the source knowledge document".
            //
            // Suppress the item from the Joplin projection instead. The knowledge source
            // remains untouched and can therefore never be lost merely because of a
            // synchronization reconciliation cycle.
            record.IsSuppressed = true;
            record.ModifiedUtc = DateTime.UtcNow;

            this.SaveProjectionState(
              projection.State
            );

            DevLogger.LogTrace(
              0,
              99999,
              "Joplin DELETE suppressed projected item without deleting knowledge: id="
              + record.Id
              + " area='"
              + record.Area
              + "'."
            );

            return this.StatusCode(
              StatusCodes.Status204NoContent
            );
          }
        }

        JoplinResourceProjectionRecord resourceRecord =
          this.FindResourceProjectionRecordByPath(
            path,
            projection.State
          );

        if (resourceRecord != null) {
          resourceRecord.IsSuppressed = true;
          resourceRecord.ModifiedUtc = DateTime.UtcNow;
          this.SaveProjectionState(projection.State);

          _SyncStateStore.Delete(
            "/" + resourceRecord.JoplinId + _MarkdownExtension
          );
          _SyncStateStore.Delete(
            _ResourceCollection + "/" + resourceRecord.JoplinId
          );

          DevLogger.LogTrace(
            0,
            99999,
            "Joplin resource DELETE suppressed projection without deleting knowledge resource: joplinId="
            + resourceRecord.JoplinId
            + " resourceId="
            + resourceRecord.ResourceId
            + "."
          );

          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        bool stateDeleted = _SyncStateStore.Delete(path);

        if (!stateDeleted) {
          return this.NotFound();
        }

        return this.StatusCode(StatusCodes.Status204NoContent);
      }
    }

    /// <summary>
    /// Creates one opaque WebDAV collection used by the Joplin synchronizer.
    /// 
    /// Collections are synchronization infrastructure and are intentionally not mapped
    /// to knowledge areas.
    /// </summary>
    private IActionResult MkColInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        if (_SyncStateStore.CollectionExists(path)) {
          return this.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        bool created = _SyncStateStore.CreateCollection(path);

        if (!created) {
          return this.Conflict();
        }

        return this.StatusCode(StatusCodes.Status201Created);
      }
    }

    /// <summary>
    /// Moves one opaque WebDAV synchronization artifact.
    /// 
    /// Joplin commonly uses MOVE for temporary upload workflows. Projected knowledge
    /// item files themselves are not moved through WebDAV because their identity is
    /// represented by their Joplin item ID.
    /// </summary>
    private IActionResult MoveInternal(string sourcePath) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        if (this.IsRootItemFile(sourcePath)) {
          return this.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        string destinationHeader = this.Request.Headers["Destination"].ToString();

        if (string.IsNullOrWhiteSpace(destinationHeader)) {
          return this.BadRequest("The WebDAV Destination header is required.");
        }

        string destinationPath = this.ExtractDestinationPath(destinationHeader);

        bool overwrite = !string.Equals(
          this.Request.Headers["Overwrite"].ToString(),
          "F",
          StringComparison.OrdinalIgnoreCase
        );

        bool moved = _SyncStateStore.Move(
          sourcePath,
          destinationPath,
          overwrite
        );

        if (!moved) {
          return this.Conflict();
        }

        return this.StatusCode(StatusCodes.Status201Created);
      }
    }

    /// <summary>
    /// Finds an existing direct knowledge area that represents the supplied Joplin title
    /// and semantic item type without relying on provider-specific path syntax.
    /// </summary>
    private string FindExistingDirectArea(
      string parentArea,
      string title,
      int itemType
    ) {
      string[] children = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      foreach (string child in children) {
        string childTitle = _KnowledgeRepository.GetAreaName(
          child
        );

        if (!string.Equals(
              childTitle,
              title,
              StringComparison.Ordinal
            )) {
          continue;
        }

        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;
        bool supportsResources;

        _KnowledgeRepository.GetAreaCapabilities(
          child,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate,
          out supportsResources
        );

        if (itemType == _JoplinNoteType &&
            contentLevel == ContentLevel.ContentContainer) {
          return child;
        }

        if (itemType == _JoplinFolderType &&
            contentLevel != ContentLevel.ContentContainer) {
          return child;
        }
      }

      return string.Empty;
    }

    /// <summary>
    /// Finds a suppressed projection record bound to one existing logical area.
    /// </summary>
    private JoplinProjectionRecord FindSuppressedRecordByArea(
      JoplinProjectionState state,
      string area
    ) {
      foreach (JoplinProjectionRecord record in state.Records) {
        if (!record.IsSuppressed) {
          continue;
        }

        if (string.Equals(
              record.Area,
              area,
              StringComparison.Ordinal
            )) {
          return record;
        }
      }

      return null;
    }

    /// <summary>
    /// Rebinds a newly created Joplin sync identity to an existing knowledge area whose
    /// previous Joplin identity was suppressed during conflict reconciliation.
    /// </summary>
    private MaterializationResult RebindSuppressedArea(
      JoplinSerializedItem item,
      JoplinProjection projection,
      JoplinProjectionRecord suppressedRecord,
      string existingArea
    ) {
      if (item.Type == _JoplinNoteType) {
        if (!this.CanResolveJoplinReferenceDependencies(
              item.Body,
              projection.State
            )) {
          return MaterializationResult.PendingDependency;
        }

        string translatedBody;

        if (!this.TryTranslateJoplinBodyToKnowledge(
              existingArea,
              item.Body,
              projection.State,
              out translatedBody
            )) {
          return MaterializationResult.PendingDependency;
        }

        bool replaced = _KnowledgeRepository.TryReplace(
          existingArea,
          translatedBody
        );

        if (!replaced) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin conflict rebind temporarily blocked while replacing knowledge area '"
            + existingArea
            + "'."
          );

          return MaterializationResult.TemporarilyUnavailable;
        }
      }

      string oldId = suppressedRecord.Id;

      projection.State.Records.Remove(
        suppressedRecord
      );

      JoplinProjectionRecord record = new JoplinProjectionRecord();
      record.Id = item.Id;
      record.Area = existingArea;
      record.Type = item.Type;
      record.CreatedUtc = DateTime.MinValue;
      record.ModifiedUtc = DateTime.MinValue;
      record.ParentIdOverride = item.ParentId;
      record.LastContentHash = string.Empty;
      record.IsSuppressed = false;

      this.SynchronizeProjectionRecordAfterJoplinWrite(
        record,
        item,
        projection.State
      );

      projection.State.Records.Add(
        record
      );

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin conflict rebind: oldId="
        + oldId
        + " newId="
        + item.Id
        + " area='"
        + existingArea
        + "'."
      );

      return MaterializationResult.Success;
    }

    /// <summary>
    /// Restores one opaque Joplin sync-state file after a provider mutation failed.
    /// </summary>
    private void RestoreJoplinStateFile(
      string path,
      bool hadPreviousState,
      byte[] previousState
    ) {
      if (hadPreviousState) {
        _SyncStateStore.WriteFile(
          path,
          previousState
        );
      }
      else {
        _SyncStateStore.Delete(
          path
        );
      }
    }

    /// <summary>
    /// Applies an uploaded Joplin resource blob/metadata pair to an already mapped
    /// Knowledge ResourceId. Unmapped resources remain pending until a note reference
    /// supplies a concrete knowledge resource scope.
    /// </summary>
    private ResourceApplyResult TryApplyUploadedJoplinResource(
      string joplinId,
      JoplinProjectionState state
    ) {
      JoplinResourceProjectionRecord record = state.Resources
        .FirstOrDefault((JoplinResourceProjectionRecord candidate) =>
          string.Equals(
            candidate.JoplinId,
            joplinId,
            StringComparison.OrdinalIgnoreCase
          ));

      if (record == null || record.IsSuppressed) {
        return ResourceApplyResult.Pending;
      }

      if (string.IsNullOrWhiteSpace(record.AreaHint) ||
          !this.KnowledgeAreaSupportsResources(record.AreaHint)) {
        return ResourceApplyResult.Pending;
      }

      string metadataPath = "/" + joplinId + _MarkdownExtension;
      string blobPath = _ResourceCollection + "/" + joplinId;

      if (!_SyncStateStore.FileExists(metadataPath) ||
          !_SyncStateStore.FileExists(blobPath)) {
        return ResourceApplyResult.Pending;
      }

      JoplinSerializedItem metadataItem = this.ParseJoplinItem(
        Encoding.UTF8.GetString(
          _SyncStateStore.ReadFile(metadataPath)
        )
      );

      if (metadataItem == null ||
          metadataItem.Type != _JoplinResourceType) {
        return ResourceApplyResult.Invalid;
      }

      byte[] resourceContent = _SyncStateStore.ReadFile(
        blobPath
      );

      string contentHash = this.ComputeHash(
        resourceContent
      );

      string fileExtension = this.ResolveJoplinResourceExtension(
        metadataItem
      );

      bool providerResourceChanged =
        !string.Equals(
          contentHash,
          record.LastContentHash,
          StringComparison.Ordinal
        ) ||
        !string.Equals(
          fileExtension,
          record.FileExtension,
          StringComparison.OrdinalIgnoreCase
        ) ||
        !string.Equals(
          metadataItem.Mime,
          record.ContentType,
          StringComparison.OrdinalIgnoreCase
        );

      if (providerResourceChanged) {
        bool replaced = _KnowledgeRepository.TryReplaceResource(
          record.ResourceId,
          metadataItem.Mime,
          resourceContent
        );

        if (!replaced) {
          return ResourceApplyResult.TemporarilyUnavailable;
        }
      }

      record.FileExtension = fileExtension;
      record.ContentType = metadataItem.Mime;
      record.Title = metadataItem.Title;
      record.FileName = metadataItem.FileName;
      record.LastContentHash = contentHash;

      if (metadataItem.ModifiedUtc == DateTime.MinValue) {
        record.ModifiedUtc = DateTime.UtcNow;
      }
      else {
        record.ModifiedUtc = metadataItem.ModifiedUtc;
      }

      return ResourceApplyResult.Applied;
    }

    /// <summary>
    /// Applies repository resource identifier changes to the persistent Joplin mapping
    /// while preserving the stable Joplin resource identity.
    /// </summary>
    private void ApplyKnowledgeResourceIdChanges(
      JoplinProjectionState state,
      KnowledgeResourceIdChange[] resourceIdChanges
    ) {
      if (resourceIdChanges == null ||
          resourceIdChanges.Length == 0) {
        return;
      }

      foreach (KnowledgeResourceIdChange change in resourceIdChanges) {
        JoplinResourceProjectionRecord[] records = state.Resources
          .Where((JoplinResourceProjectionRecord candidate) =>
            string.Equals(
              candidate.ResourceId,
              change.PreviousResourceId,
              StringComparison.Ordinal
            ))
          .ToArray();

        foreach (JoplinResourceProjectionRecord record in records) {
          record.ResourceId = change.CurrentResourceId;
          record.ModifiedUtc = DateTime.UtcNow;
        }
      }
    }

    /// <summary>
    /// Returns the best descriptive physical file name supplied by one Joplin resource item.
    /// The value is only a provider hint and never becomes the repository resource identity.
    /// </summary>
    private string GetPreferredJoplinResourceFileName(
      JoplinSerializedItem resourceItem
    ) {
      // Only Joplin's explicit filename metadata represents a user-visible physical
      // filename that is worth preserving.
      //
      // In particular, the Joplin resource title is deliberately NOT used as a filename
      // fallback. Pasted images commonly have a title or other display metadata without
      // having a meaningful original filename. Such resources are known to belong to the
      // referencing note and should therefore receive the provider-owned FileBased fallback
      // <Document>.Res<Snowflake44>.<extension>.
      string fileName = resourceItem.FileName;

      if (string.IsNullOrWhiteSpace(fileName)) {
        return string.Empty;
      }

      string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(
        fileName
      );

      if (!string.IsNullOrWhiteSpace(resourceItem.Id) &&
          string.Equals(
            fileNameWithoutExtension,
            resourceItem.Id,
            StringComparison.OrdinalIgnoreCase
          )) {
        // A filename that consists only of the Joplin resource ID plus extension is
        // technical synchronization metadata rather than a meaningful original filename.
        // Treat it like a pasted resource so FileBased can express document ownership.
        return string.Empty;
      }

      string extension = Path.GetExtension(
        fileName
      );

      if (!string.IsNullOrWhiteSpace(extension)) {
        return fileName;
      }

      string resolvedExtension = this.ResolveJoplinResourceExtension(
        resourceItem
      );

      return fileName + resolvedExtension;
    }

    /// <summary>
    /// Returns the provider-neutral resource capability of one logical knowledge area.
    /// </summary>
    private bool KnowledgeAreaSupportsResources(
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

      _KnowledgeRepository.GetAreaCapabilities(
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
    /// Returns whether every Joplin-internal <c>:/&lt;id&gt;</c> reference in one note can
    /// currently be classified and translated safely.
    ///
    /// Joplin uses the same URL syntax for note links and resource links. The adapter must
    /// therefore inspect projection state before deciding whether one ID represents a
    /// provider-neutral knowledge area or resource.
    ///
    /// Resource references require either an existing resource projection mapping or the
    /// complete Joplin resource metadata/blob pair. Note and folder references require an
    /// existing knowledge projection record because their provider-neutral target is the
    /// mapped logical area. Unknown dependencies remain pending instead of leaking Joplin-
    /// specific <c>:/...</c> references into repository Markdown.
    /// </summary>
    private bool CanResolveJoplinReferenceDependencies(
      string body,
      JoplinProjectionState state
    ) {
      string sourceBody =
        body;

      if (sourceBody == null) {
        sourceBody =
          string.Empty;
      }

      MatchCollection matches =
        _JoplinResourceReferenceRegex.Matches(
          sourceBody
        );

      foreach (Match match in matches) {
        string joplinId =
          match.Groups["id"].Value.ToLowerInvariant();

        JoplinResourceProjectionRecord resourceRecord =
          state.Resources.FirstOrDefault(
            (JoplinResourceProjectionRecord candidate) =>
              string.Equals(
                candidate.JoplinId,
                joplinId,
                StringComparison.OrdinalIgnoreCase
              )
          );

        if (resourceRecord != null) {
          continue;
        }

        JoplinProjectionRecord knowledgeRecord =
          state.Records.FirstOrDefault(
            (JoplinProjectionRecord candidate) =>
              string.Equals(
                candidate.Id,
                joplinId,
                StringComparison.OrdinalIgnoreCase
              )
          );

        if (knowledgeRecord != null) {
          continue;
        }

        string metadataPath =
          "/"
          + joplinId
          + _MarkdownExtension;

        if (!_SyncStateStore.FileExists(
              metadataPath
            )) {
          return false;
        }

        JoplinSerializedItem referencedItem =
          this.ParseJoplinItem(
            Encoding.UTF8.GetString(
              _SyncStateStore.ReadFile(
                metadataPath
              )
            )
          );

        if (referencedItem == null) {
          return false;
        }

        if (referencedItem.Type == _JoplinResourceType) {
          string blobPath =
            _ResourceCollection
            + "/"
            + joplinId;

          if (!_SyncStateStore.FileExists(
                blobPath
              )) {
            return false;
          }

          continue;
        }

        if (referencedItem.Type == _JoplinNoteType ||
            referencedItem.Type == _JoplinFolderType) {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Translates Joplin-internal references in one note body to provider-neutral knowledge
    /// references.
    ///
    /// Joplin resources become <c>knowledge-resource:&lt;ResourceId&gt;</c>. Joplin notes and
    /// folders that are backed by projection records become
    /// <c>knowledge-area:&lt;logical area&gt;</c>. The logical area is copied exactly from the
    /// repository projection and is not URI-decoded or reinterpreted.
    ///
    /// Unknown note/folder references are rejected as pending dependencies rather than being
    /// persisted as Joplin-specific <c>:/...</c> syntax in provider-neutral repository
    /// content.
    /// </summary>
    private bool TryTranslateJoplinBodyToKnowledge(
      string area,
      string joplinBody,
      JoplinProjectionState state,
      out string translatedBody
    ) {
      translatedBody =
        joplinBody;

      if (translatedBody == null) {
        translatedBody =
          string.Empty;
      }

      MatchCollection matches =
        _JoplinResourceReferenceRegex.Matches(
          translatedBody
        );

      if (matches.Count == 0) {
        return true;
      }

      Dictionary<string, string> resourceMappings =
        new Dictionary<string, string>(
          StringComparer.OrdinalIgnoreCase
        );

      Dictionary<string, string> areaMappings =
        new Dictionary<string, string>(
          StringComparer.OrdinalIgnoreCase
        );

      foreach (Match match in matches) {
        string joplinId =
          match.Groups["id"].Value.ToLowerInvariant();

        if (resourceMappings.ContainsKey(
              joplinId
            ) ||
            areaMappings.ContainsKey(
              joplinId
            )) {
          continue;
        }

        JoplinResourceProjectionRecord resourceRecord =
          state.Resources.FirstOrDefault(
            (JoplinResourceProjectionRecord candidate) =>
              string.Equals(
                candidate.JoplinId,
                joplinId,
                StringComparison.OrdinalIgnoreCase
              )
          );

        if (resourceRecord != null) {
          if (!this.KnowledgeAreaSupportsResources(
                area
              )) {
            return false;
          }

          if (string.IsNullOrWhiteSpace(
                resourceRecord.ResourceId
              )) {
            return false;
          }

          resourceRecord.IsSuppressed =
            false;

          resourceRecord.AreaHint =
            area;

          resourceMappings[joplinId] =
            resourceRecord.ResourceId;

          continue;
        }

        JoplinProjectionRecord knowledgeRecord =
          state.Records.FirstOrDefault(
            (JoplinProjectionRecord candidate) =>
              string.Equals(
                candidate.Id,
                joplinId,
                StringComparison.OrdinalIgnoreCase
              )
          );

        if (knowledgeRecord != null) {
          if (string.IsNullOrWhiteSpace(
                knowledgeRecord.Area
              )) {
            return false;
          }

          areaMappings[joplinId] =
            knowledgeRecord.Area;

          continue;
        }

        string metadataPath =
          "/"
          + joplinId
          + _MarkdownExtension;

        if (!_SyncStateStore.FileExists(
              metadataPath
            )) {
          return false;
        }

        JoplinSerializedItem referencedItem =
          this.ParseJoplinItem(
            Encoding.UTF8.GetString(
              _SyncStateStore.ReadFile(
                metadataPath
              )
            )
          );

        if (referencedItem == null) {
          return false;
        }

        if (referencedItem.Type == _JoplinNoteType ||
            referencedItem.Type == _JoplinFolderType) {
          return false;
        }

        if (referencedItem.Type != _JoplinResourceType) {
          continue;
        }

        if (!this.KnowledgeAreaSupportsResources(
              area
            )) {
          return false;
        }

        string blobPath =
          _ResourceCollection
          + "/"
          + joplinId;

        if (!_SyncStateStore.FileExists(
              blobPath
            )) {
          return false;
        }

        byte[] resourceContent =
          _SyncStateStore.ReadFile(
            blobPath
          );

        string preferredFileName =
          this.GetPreferredJoplinResourceFileName(
            referencedItem
          );

        string resourceId;

        bool added =
          _KnowledgeRepository.TryAddResource(
            area,
            preferredFileName,
            referencedItem.Mime,
            resourceContent,
            out resourceId
          );

        if (!added) {
          return false;
        }

        resourceRecord =
          new JoplinResourceProjectionRecord();

        resourceRecord.JoplinId =
          joplinId;

        resourceRecord.ResourceId =
          resourceId;

        resourceRecord.AreaHint =
          area;

        if (referencedItem.CreatedUtc == DateTime.MinValue) {
          resourceRecord.CreatedUtc =
            DateTime.UtcNow;
        }
        else {
          resourceRecord.CreatedUtc =
            referencedItem.CreatedUtc;
        }

        if (referencedItem.ModifiedUtc == DateTime.MinValue) {
          resourceRecord.ModifiedUtc =
            DateTime.UtcNow;
        }
        else {
          resourceRecord.ModifiedUtc =
            referencedItem.ModifiedUtc;
        }

        resourceRecord.FileExtension =
          this.ResolveJoplinResourceExtension(
            referencedItem
          );

        resourceRecord.ContentType =
          referencedItem.Mime;

        resourceRecord.Title =
          referencedItem.Title;

        resourceRecord.FileName =
          referencedItem.FileName;

        resourceRecord.LastContentHash =
          this.ComputeHash(
            resourceContent
          );

        resourceRecord.IsSuppressed =
          false;

        state.Resources.Add(
          resourceRecord
        );

        resourceMappings[joplinId] =
          resourceId;
      }

      translatedBody =
        _JoplinResourceReferenceRegex.Replace(
          translatedBody,
          (Match match) => {
            string joplinId =
              match.Groups["id"].Value.ToLowerInvariant();

            if (resourceMappings.ContainsKey(
                  joplinId
                )) {
              return _KnowledgeResourceReferencePrefix
                + resourceMappings[joplinId];
            }

            if (areaMappings.ContainsKey(
                  joplinId
                )) {
              return _KnowledgeAreaReferencePrefix
                + areaMappings[joplinId];
            }

            return match.Value;
          }
        );

      return true;
    }

    /// <summary>
    /// Translates provider-neutral knowledge references into Joplin-internal references.
    ///
    /// <c>knowledge-resource:</c> references are projected to stable Joplin resource IDs and
    /// refresh the corresponding metadata/blob cache. <c>knowledge-area:</c> references are
    /// projected to the stable Joplin item ID of the exact logical area when that area is
    /// represented by a non-suppressed Joplin note or folder.
    /// </summary>
    private string TranslateKnowledgeBodyToJoplin(
      string area,
      string knowledgeBody,
      JoplinProjectionState state
    ) {
      string body =
        knowledgeBody;

      if (body == null) {
        body =
          string.Empty;
      }

      body =
        this.TranslateKnowledgeAreaReferencesToJoplin(
          body,
          state
        );

      MatchCollection matches = _KnowledgeResourceReferenceRegex.Matches(
        body
      );

      if (matches.Count == 0) {
        return body;
      }

      if (!this.KnowledgeAreaSupportsResources(area)) {
        throw new InvalidOperationException(
          "Knowledge content contains resource references although the addressed area does not support resources."
        );
      }

      KnowledgeResourceInfo[] resources = _KnowledgeRepository.GetResources(
        area
      );

      Dictionary<string, string> mappings =
        new Dictionary<string, string>(StringComparer.Ordinal);

      foreach (Match match in matches) {
        string resourceId = match.Groups["id"].Value;

        if (mappings.ContainsKey(resourceId)) {
          continue;
        }

        KnowledgeResourceInfo resource = resources.FirstOrDefault(
          (KnowledgeResourceInfo candidate) =>
            string.Equals(
              candidate.ResourceId,
              resourceId,
              StringComparison.Ordinal
            )
        );

        if (resource == null) {
          // Structural repository changes can invalidate a previously exposed provider-owned
          // ResourceId before every persisted knowledge reference has been rewritten. A
          // resource that has already been synchronized to Joplin can be reconciled safely
          // as a server-side deletion without aborting the complete projection.
          JoplinResourceProjectionRecord existingRecord =
            state.Resources.FirstOrDefault(
              (JoplinResourceProjectionRecord candidate) =>
                string.Equals(
                  candidate.ResourceId,
                  resourceId,
                  StringComparison.Ordinal
                )
            );

          if (existingRecord == null) {
            // Never interpret a new or previously unknown broken reference as a deletion.
            // Without an existing synchronization mapping there is no evidence that Joplin
            // has ever seen this resource successfully.
            throw new InvalidOperationException(
              "Knowledge content references a resource identifier that the provider does not expose in the current scope."
            );
          }

          if (!existingRecord.IsSuppressed) {
            existingRecord.IsSuppressed =
              true;

            existingRecord.ModifiedUtc =
              DateTime.UtcNow;

            _SyncStateStore.Delete(
              "/"
              + existingRecord.JoplinId
              + _MarkdownExtension
            );

            _SyncStateStore.Delete(
              _ResourceCollection
              + "/"
              + existingRecord.JoplinId
            );

            DevLogger.LogTrace(
              0,
              99999,
              "Joplin resource projection self-healed because previously synchronized Knowledge ResourceId '"
              + resourceId
              + "' is no longer exposed by the repository. JoplinId='"
              + existingRecord.JoplinId
              + "'. The resource is now suppressed so Joplin can reconcile the server-side deletion."
            );
          }

          // Keep the note reference mapped to the previously stable Joplin resource ID for
          // this projection. The corresponding resource metadata/blob is no longer exposed,
          // allowing the Joplin client to reconcile the server-side deletion itself.
          mappings[resourceId] =
            existingRecord.JoplinId;

          continue;
        }

        JoplinResourceProjectionRecord record = state.Resources
          .FirstOrDefault((JoplinResourceProjectionRecord candidate) =>
            string.Equals(
              candidate.ResourceId,
              resourceId,
              StringComparison.Ordinal
            ));

        if (record == null) {
          record = new JoplinResourceProjectionRecord();
          record.JoplinId = this.CreateDeterministicItemId(
            "resource:" + resourceId
          );
          record.ResourceId = resourceId;
          record.Title = string.Empty;
          record.FileName = string.Empty;
          record.CreatedUtc = DateTime.UtcNow;
          record.ModifiedUtc = record.CreatedUtc;
          record.LastContentHash = string.Empty;
          state.Resources.Add(record);
        }

        record.AreaHint = area;

        if (string.IsNullOrWhiteSpace(record.FileExtension)) {
          record.FileExtension = Path.GetExtension(
            resource.FileName
          );
        }

        if (string.IsNullOrWhiteSpace(record.FileName)) {
          record.FileName = resource.FileName;
        }

        record.ContentType = resource.ContentType;
        record.IsSuppressed = false;

        byte[] content = _KnowledgeRepository.GetResourceContent(
          resourceId
        );

        string contentHash = this.ComputeHash(
          content
        );

        if (!string.Equals(
              record.LastContentHash,
              contentHash,
              StringComparison.Ordinal
            )) {
          record.LastContentHash = contentHash;
          record.ModifiedUtc = DateTime.UtcNow;
        }

        byte[] metadata = Encoding.UTF8.GetBytes(
          this.SerializeJoplinResourceMetadata(
            record,
            resource,
            content.LongLength
          )
        );

        this.WriteStateFileIfChanged(
          "/" + record.JoplinId + _MarkdownExtension,
          metadata
        );

        this.WriteStateFileIfChanged(
          _ResourceCollection + "/" + record.JoplinId,
          content
        );

        mappings[resourceId] = record.JoplinId;
      }

      return _KnowledgeResourceReferenceRegex.Replace(
        body,
        (Match match) => {
          string resourceId = match.Groups["id"].Value;

          if (!mappings.ContainsKey(resourceId)) {
            return match.Value;
          }

          return ":/" + mappings[resourceId];
        }
      );
    }

    /// <summary>
    /// Translates provider-neutral knowledge-area references to stable Joplin item links
    /// whenever the exact logical target is currently projected as a note or folder.
    ///
    /// The area payload is already the canonical logical repository path. It is therefore
    /// compared exactly and is never URI-decoded, URI-encoded or interpreted as a physical
    /// provider path.
    /// </summary>
    private string TranslateKnowledgeAreaReferencesToJoplin(
      string body,
      JoplinProjectionState state
    ) {
      if (string.IsNullOrEmpty(
            body
          )) {
        return body;
      }

      return _KnowledgeAreaReferenceRegex.Replace(
        body,
        (Match match) => {
          string targetArea =
            match.Groups["area"].Value;

          JoplinProjectionRecord record =
            state.Records.FirstOrDefault(
              (JoplinProjectionRecord candidate) =>
                !candidate.IsSuppressed &&
                (candidate.Type == _JoplinNoteType ||
                 candidate.Type == _JoplinFolderType) &&
                string.Equals(
                  candidate.Area,
                  targetArea,
                  StringComparison.Ordinal
                )
            );

          if (record == null ||
              string.IsNullOrWhiteSpace(
                record.Id
              )) {
            return match.Value;
          }

          return ":/"
            + record.Id;
        }
      );
    }

    /// <summary>
    /// Serializes one Joplin resource metadata item in the same raw sync-item structure
    /// used for notes and folders.
    /// </summary>
    private string SerializeJoplinResourceMetadata(
      JoplinResourceProjectionRecord record,
      KnowledgeResourceInfo resource,
      long length
    ) {
      string extension = Path.GetExtension(
        resource.FileName
      );

      if (extension == null) {
        extension = string.Empty;
      }

      string fallbackFileName = resource.FileName;

      if (string.IsNullOrWhiteSpace(fallbackFileName)) {
        fallbackFileName = "Resource" + extension;
      }

      string fileName = record.FileName;

      if (string.IsNullOrWhiteSpace(fileName)) {
        fileName = fallbackFileName;
      }

      string title = record.Title;

      if (string.IsNullOrWhiteSpace(title)) {
        title = fileName;
      }

      StringBuilder properties = new StringBuilder();
      properties.Append("id: ");
      properties.Append(record.JoplinId);
      properties.Append('\n');
      properties.Append("mime: ");
      string contentType = resource.ContentType;

      if (contentType == null) {
        contentType = string.Empty;
      }

      properties.Append(contentType);
      properties.Append('\n');
      properties.Append("filename: ");
      properties.Append(fileName);
      properties.Append('\n');
      properties.Append("created_time: ");
      properties.Append(this.FormatJoplinTime(record.CreatedUtc));
      properties.Append('\n');
      properties.Append("updated_time: ");
      properties.Append(this.FormatJoplinTime(record.ModifiedUtc));
      properties.Append('\n');
      properties.Append("user_created_time: ");
      properties.Append(this.FormatJoplinTime(record.CreatedUtc));
      properties.Append('\n');
      properties.Append("user_updated_time: ");
      properties.Append(this.FormatJoplinTime(record.ModifiedUtc));
      properties.Append('\n');
      properties.Append("file_extension: ");
      properties.Append(extension.TrimStart('.'));
      properties.Append('\n');
      properties.Append("encryption_cipher_text: \n");
      properties.Append("encryption_applied: 0\n");
      properties.Append("encryption_blob_encrypted: 0\n");
      properties.Append("size: ");
      properties.Append(length.ToString(CultureInfo.InvariantCulture));
      properties.Append('\n');
      properties.Append("is_shared: 0\n");
      properties.Append("share_id: \n");
      properties.Append("master_key_id: \n");
      properties.Append("user_data: \n");
      properties.Append("blob_updated_time: ");
      properties.Append(
        this.FormatJoplinUnixTimeMilliseconds(
          record.ModifiedUtc
        )
      );
      properties.Append('\n');
      properties.Append("ocr_text: \n");
      properties.Append("ocr_details: \n");
      properties.Append("ocr_status: 0\n");
      properties.Append("ocr_error: \n");
      properties.Append("ocr_driver_id: 0\n");
      properties.Append("is_locked: 0\n");
      properties.Append("type_: 4");

      return title
        + "\n\n"
        + properties.ToString();
    }

    /// <summary>
    /// Writes a derived Joplin state file only when its bytes changed, keeping WebDAV
    /// timestamps stable across read-only projection passes.
    /// </summary>
    private void WriteStateFileIfChanged(
      string path,
      byte[] content
    ) {
      if (_SyncStateStore.FileExists(path)) {
        byte[] existing = _SyncStateStore.ReadFile(path);

        if (existing.SequenceEqual(content)) {
          return;
        }
      }

      _SyncStateStore.WriteFile(
        path,
        content
      );
    }

    /// <summary>
    /// Resolves the best physical extension from Joplin resource metadata.
    /// </summary>
    private string ResolveJoplinResourceExtension(
      JoplinSerializedItem item
    ) {
      string extension = item.FileExtension;

      if (string.IsNullOrWhiteSpace(extension) &&
          !string.IsNullOrWhiteSpace(item.FileName)) {
        extension = Path.GetExtension(
          item.FileName
        );
      }

      if (string.IsNullOrWhiteSpace(extension) &&
          !string.IsNullOrWhiteSpace(item.Title)) {
        extension = Path.GetExtension(
          item.Title
        );
      }

      if (string.IsNullOrWhiteSpace(extension)) {
        extension = this.GetExtensionFromMimeType(
          item.Mime
        );
      }

      if (string.IsNullOrWhiteSpace(extension)) {
        extension = ".bin";
      }

      if (!extension.StartsWith(".", StringComparison.Ordinal)) {
        extension = "." + extension;
      }

      return extension.ToLowerInvariant();
    }

    /// <summary>
    /// Maps common MIME types to file extensions for Joplin resources with incomplete
    /// legacy metadata.
    /// </summary>
    private string GetExtensionFromMimeType(string mimeType) {
      string value = mimeType;

      if (value == null) {
        value = string.Empty;
      }
      value = value.Trim().ToLowerInvariant();

      if (value == "image/png") {
        return ".png";
      }
      else if (value == "image/jpeg") {
        return ".jpg";
      }
      else if (value == "image/gif") {
        return ".gif";
      }
      else if (value == "image/webp") {
        return ".webp";
      }
      else if (value == "image/svg+xml") {
        return ".svg";
      }
      else if (value == "application/pdf") {
        return ".pdf";
      }
      else if (value == "text/plain") {
        return ".txt";
      }

      return string.Empty;
    }

    /// <summary>
    /// Finds a resource projection mapping represented by either its metadata item path or
    /// its blob path.
    /// </summary>
    private JoplinResourceProjectionRecord FindResourceProjectionRecordByPath(
      string path,
      JoplinProjectionState state
    ) {
      string id = string.Empty;

      if (path.StartsWith(
            _ResourceCollection + "/",
            StringComparison.Ordinal
          )) {
        id = path.Substring(
          (_ResourceCollection + "/").Length
        );
      }
      else if (this.IsRootItemFile(path)) {
        id = Path.GetFileNameWithoutExtension(
          path
        );
      }

      if (string.IsNullOrWhiteSpace(id)) {
        return null;
      }

      return state.Resources.FirstOrDefault(
        (JoplinResourceProjectionRecord candidate) => string.Equals(
          candidate.JoplinId,
          id,
          StringComparison.OrdinalIgnoreCase
        )
      );
    }

    /// <summary>
    /// Creates a new logical knowledge area from a Joplin note or notebook item.
    /// </summary>
    private MaterializationResult CreateKnowledgeItemFromJoplin(
      JoplinSerializedItem item,
      JoplinProjection projection
    ) {
      string parentArea = "/";

      if (!string.IsNullOrWhiteSpace(item.ParentId)) {
        JoplinProjectionRecord parentRecord = projection.FindRecordById(
          item.ParentId
        );

        if (parentRecord == null) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin create mapping deferred because parent_id '"
            + item.ParentId
            + "' is not known in the current projection."
          );

          return MaterializationResult.PendingDependency;
        }

        parentArea = parentRecord.Area;
      }

      if (item.Type == _JoplinNoteType &&
          !this.CanResolveJoplinReferenceDependencies(
            item.Body,
            projection.State
          )) {
        return MaterializationResult.PendingDependency;
      }

      string[] beforeAreas = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string childName = item.Title;
      KnowledgeAreaKind childKind = KnowledgeAreaKind.Content;

      if (item.Type == _JoplinFolderType) {
        childKind = KnowledgeAreaKind.Structural;
      }

      string existingArea = this.FindExistingDirectArea(
        parentArea,
        item.Title,
        item.Type
      );

      if (!string.IsNullOrEmpty(existingArea)) {
        JoplinProjectionRecord suppressedRecord =
          this.FindSuppressedRecordByArea(
            projection.State,
            existingArea
          );

        if (suppressedRecord != null) {
          MaterializationResult rebindResult = this.RebindSuppressedArea(
            item,
            projection,
            suppressedRecord,
            existingArea
          );

          if (rebindResult != MaterializationResult.Failed) {
            return rebindResult;
          }
        }
      }

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin create mapping: id="
        + item.Id
        + " type="
        + item.Type.ToString(CultureInfo.InvariantCulture)
        + " title='"
        + item.Title
        + "' parentArea='"
        + parentArea
        + "' childName='"
        + childName
        + "'"
      );

      bool added = _KnowledgeRepository.TryAddSubArea(
        parentArea,
        childName,
        childKind
      );

      if (!added) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin create mapping failed at TryAddSubArea: parentArea='"
          + parentArea
          + "' childName='"
          + childName
          + "'"
        );

        return MaterializationResult.Failed;
      }

      string[] afterAreas = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string newArea = this.FindAddedArea(
        beforeAreas,
        afterAreas
      );

      if (string.IsNullOrEmpty(newArea)) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin create mapping failed because the newly added area could not be identified uniquely below '"
          + parentArea
          + "'."
        );

        return MaterializationResult.Failed;
      }

      if (item.Type == _JoplinNoteType &&
          !string.IsNullOrEmpty(item.Body)) {
        string translatedBody;

        if (!this.TryTranslateJoplinBodyToKnowledge(
              newArea,
              item.Body,
              projection.State,
              out translatedBody
            )) {
          return MaterializationResult.PendingDependency;
        }

        bool appended = _KnowledgeRepository.TryAppendContent(
          newArea,
          translatedBody
        );

        if (!appended) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin create mapping failed while appending note content to '"
            + newArea
            + "'. The newly created area will be rolled back."
          );

          _KnowledgeRepository.TryDelete(newArea);
          return MaterializationResult.Failed;
        }
      }

      JoplinProjectionRecord record = new JoplinProjectionRecord();
      record.Id = item.Id;
      record.Area = newArea;
      record.Type = item.Type;
      record.CreatedUtc = DateTime.MinValue;
      record.ModifiedUtc = DateTime.MinValue;
      record.ParentIdOverride = item.ParentId;
      record.LastContentHash = string.Empty;
      record.IsSuppressed = false;

      this.SynchronizeProjectionRecordAfterJoplinWrite(
        record,
        item,
        projection.State
      );

      projection.State.Records.Add(record);
      return MaterializationResult.Success;
    }

    /// <summary>
    /// Applies a Joplin parent change through the provider-neutral repository move
    /// contract.
    ///
    /// This adapter deliberately has no knowledge of files, folders, Markdown rewrite
    /// mechanics or any other concrete repository representation. It resolves only the
    /// logical new parent and delegates the complete structural move to
    /// <see cref="IKnowledgeRepository.TryMoveContent(string, string)"/>.
    /// </summary>

  }
}
