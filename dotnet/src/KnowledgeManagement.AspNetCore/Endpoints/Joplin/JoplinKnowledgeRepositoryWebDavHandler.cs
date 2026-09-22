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
  public class JoplinKnowledgeRepositoryWebDavHandler : ControllerBase {

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
          throw new InvalidOperationException(
            "Knowledge content references a resource identifier that the provider does not expose in the current scope."
          );
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
    private MaterializationResult MoveExistingItemToUpdatedParent(
      JoplinSerializedItem item,
      JoplinProjectionRecord record,
      JoplinProjection projection
    ) {
      string newParentArea = "/";

      if (!string.IsNullOrWhiteSpace(item.ParentId)) {
        JoplinProjectionRecord newParentRecord =
          projection.FindRecordById(
            item.ParentId
          );

        if (newParentRecord == null ||
            newParentRecord.IsSuppressed) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin move deferred because parent_id '"
            + item.ParentId
            + "' is not available in the current logical projection."
          );

          return MaterializationResult.PendingDependency;
        }

        newParentArea = newParentRecord.Area;
      }

      string currentParentArea = this.GetParentArea(
        record.Area
      );

      if (string.Equals(
            currentParentArea,
            newParentArea,
            StringComparison.Ordinal
          )) {
        record.ParentIdOverride = item.ParentId;
        return MaterializationResult.Success;
      }

      string oldArea = record.Area;
      string logicalName = _KnowledgeRepository.GetAreaName(
        oldArea
      );

      KnowledgeResourceIdChange[] resourceIdChanges;

      bool moved = _KnowledgeRepository.TryMoveContent(
        oldArea,
        newParentArea,
        out resourceIdChanges
      );

      if (moved) {
        this.ApplyKnowledgeResourceIdChanges(
          projection.State,
          resourceIdChanges
        );
      }

      if (!moved) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin logical move temporarily unavailable: id="
          + item.Id
          + " contentAreaToMove='"
          + oldArea
          + "' newParentArea='"
          + newParentArea
          + "'."
        );

        return MaterializationResult.TemporarilyUnavailable;
      }

      string movedArea = this.FindExistingDirectArea(
        newParentArea,
        logicalName,
        item.Type
      );

      if (string.IsNullOrEmpty(movedArea)) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin logical move completed but the moved area could not be resolved below its new parent: id="
          + item.Id
          + " oldArea='"
          + oldArea
          + "' newParentArea='"
          + newParentArea
          + "'."
        );

        return MaterializationResult.Failed;
      }

      record.Area = movedArea;
      record.ParentIdOverride = item.ParentId;

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin logical move completed: id="
        + item.Id
        + " oldArea='"
        + oldArea
        + "' newArea='"
        + movedArea
        + "'."
      );

      return MaterializationResult.Success;
    }

    /// <summary>
    /// Applies a Joplin note or notebook update to an existing logical knowledge area.
    /// Parent changes are delegated exclusively through the provider-neutral
    /// <see cref="IKnowledgeRepository.TryMoveContent(string, string)"/> operation.
    /// </summary>
    private MaterializationResult UpdateKnowledgeItemFromJoplin(
      JoplinSerializedItem item,
      JoplinProjectionRecord record,
      JoplinProjection projection
    ) {
      if (item.Type == _JoplinNoteType &&
          !this.CanResolveJoplinReferenceDependencies(
            item.Body,
            projection.State
          )) {
        return MaterializationResult.PendingDependency;
      }

      MaterializationResult parentMoveResult =
        this.MoveExistingItemToUpdatedParent(
          item,
          record,
          projection
        );

      if (parentMoveResult != MaterializationResult.Success) {
        return parentMoveResult;
      }

      string currentTitle = _KnowledgeRepository.GetAreaName(record.Area);

      if (!string.Equals(
            currentTitle,
            item.Title,
            StringComparison.Ordinal
          )) {
        string oldArea = record.Area;
        string parentArea = this.GetParentArea(oldArea);

        KnowledgeResourceIdChange[] resourceIdChanges;

        bool renamed = _KnowledgeRepository.TryRename(
          oldArea,
          item.Title,
          out resourceIdChanges
        );

        if (renamed) {
          this.ApplyKnowledgeResourceIdChanges(
            projection.State,
            resourceIdChanges
          );
        }

        if (!renamed) {
          return MaterializationResult.Failed;
        }

        string renamedArea = this.FindRenamedArea(
          parentArea,
          oldArea,
          item.Title
        );

        if (string.IsNullOrEmpty(renamedArea)) {
          return MaterializationResult.Failed;
        }

        record.Area = renamedArea;
      }

      if (item.Type == _JoplinNoteType) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin note update: id="
          + item.Id
          + " area='"
          + record.Area
          + "' bodyLength="
          + item.Body.Length.ToString(CultureInfo.InvariantCulture)
        );

        string translatedBody;

        if (!this.TryTranslateJoplinBodyToKnowledge(
              record.Area,
              item.Body,
              projection.State,
              out translatedBody
            )) {
          return MaterializationResult.PendingDependency;
        }

        bool replaced = _KnowledgeRepository.TryReplace(
          record.Area,
          translatedBody
        );

        if (!replaced) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin note update failed at TryReplace: id="
            + item.Id
            + " area='"
            + record.Area
            + "'"
          );

          return MaterializationResult.TemporarilyUnavailable;
        }

        string repositoryContent = _KnowledgeRepository.GetAggregatedContent(
          record.Area
        );

        string uploadedHash = this.ComputeHash(
          this.NormalizeContentForComparison(translatedBody)
        );

        string repositoryHash = this.ComputeHash(
          this.NormalizeContentForComparison(repositoryContent)
        );

        DevLogger.LogTrace(
          0,
          99999,
          "Joplin note update persisted: id="
          + item.Id
          + " area='"
          + record.Area
          + "' uploadedHash="
          + uploadedHash
          + " repositoryHash="
          + repositoryHash
          + " equal="
          + string.Equals(
              uploadedHash,
              repositoryHash,
              StringComparison.Ordinal
            ).ToString()
        );
      }

      this.SynchronizeProjectionRecordAfterJoplinWrite(
        record,
        item,
        projection.State
      );

      return MaterializationResult.Success;
    }

    /// <summary>
    /// Attempts to materialize accepted Joplin note and notebook items that previously
    /// could not be projected because their parent item had not yet arrived.
    /// 
    /// The method repeatedly scans root sync-item files until no additional item can be
    /// materialized. This handles arbitrary parent-before-child upload ordering without
    /// requiring Joplin to retry the original PUT.
    /// </summary>
    private void MaterializePendingKnowledgeItems() {
      bool progress = true;

      while (progress) {
        progress = false;

        JoplinSyncStateEntry[] rootEntries = _SyncStateStore.GetChildren("/");
        JoplinProjection projection =
          this.BuildStateProjection();

        foreach (JoplinSyncStateEntry entry in rootEntries) {
          if (entry.IsCollection) {
            continue;
          }

          if (!this.IsRootItemFile(entry.Path)) {
            continue;
          }

          byte[] content = _SyncStateStore.ReadFile(entry.Path);
          JoplinSerializedItem item = this.ParseJoplinItem(
            Encoding.UTF8.GetString(content)
          );

          if (item == null) {
            continue;
          }

          if (item.Type != _JoplinNoteType &&
              item.Type != _JoplinFolderType) {
            continue;
          }

          JoplinProjectionRecord existingRecord =
            projection.FindRecordById(item.Id);

          MaterializationResult result;

          if (existingRecord == null) {
            result = this.CreateKnowledgeItemFromJoplin(
              item,
              projection
            );
          }
          else {
            result = this.UpdateKnowledgeItemFromJoplin(
              item,
              existingRecord,
              projection
            );
          }

          if (result == MaterializationResult.PendingDependency) {
            continue;
          }

          if (result == MaterializationResult.Failed) {
            DevLogger.LogTrace(
              0,
              99999,
              "Pending Joplin item '"
              + item.Id
              + "' still cannot be materialized."
            );

            continue;
          }

          _SyncStateStore.Delete(entry.Path);
          this.SaveProjectionState(projection.State);

          DevLogger.LogTrace(
            0,
            99999,
            "Pending Joplin item '"
            + item.Id
            + "' was materialized successfully."
          );

          progress = true;
          break;
        }
      }
    }

    /// <summary>
    /// Creates a lightweight projection containing only the persistent Joplin mapping
    /// state. Mutation paths use this form because parent and existing-item resolution do
    /// not require a complete traversal of the knowledge repository.
    /// </summary>
    private JoplinProjection BuildStateProjection() {
      return new JoplinProjection(
        this.LoadProjectionState(),
        Array.Empty<JoplinProjectedItem>()
      );
    }

    /// <summary>
    /// Builds only the projection required to resolve one WebDAV path.
    ///
    /// The synchronization root is the single protocol location that legitimately needs
    /// the complete flat list of projected Joplin items. Exact note/folder GET, HEAD and
    /// PROPFIND requests are resolved from the persistent mapping and materialize at most
    /// one knowledge-backed item.
    /// </summary>
    private JoplinProjection BuildProjectionForWebDavPath(
      string path
    ) {
      if (string.Equals(
            path,
            "/",
            StringComparison.Ordinal
          )) {
        return this.BuildProjection();
      }

      if (!this.IsRootItemFile(
            path
          )) {
        return this.BuildStateProjection();
      }

      string itemId =
        Path.GetFileNameWithoutExtension(
          path
        );

      JoplinProjectionState state =
        this.LoadProjectionState();

      JoplinProjectionRecord record = state.Records
        .FirstOrDefault((JoplinProjectionRecord candidate) =>
          string.Equals(
            candidate.Id,
            itemId,
            StringComparison.OrdinalIgnoreCase
          ));

      if (record == null ||
          record.IsSuppressed) {
        // Exact item requests never trigger repository-wide discovery. Joplin learns the
        // complete item set through root PROPFIND; an unknown item is therefore simply not
        // part of the currently known projection.
        return new JoplinProjection(
          state,
          Array.Empty<JoplinProjectedItem>()
        );
      }

      JoplinProjectedItem item =
        this.BuildProjectedItem(
          record,
          state
        );

      this.SaveProjectionState(
        state
      );

      return new JoplinProjection(
        state,
        new JoplinProjectedItem[] {
          item
        }
      );
    }

    /// <summary>
    /// Enumerates the complete knowledge tree iteratively using only direct-child provider
    /// requests.
    ///
    /// Joplin's root PROPFIND must expose a flat list of every synchronization item, so a
    /// complete traversal of navigable notebook/document boundaries is unavoidable there.
    /// The important boundary is that no provider ever receives GetAreas(true, ...): every
    /// structural level is requested independently, and traversal stops at the first content
    /// container because subordinate containers are headings inside that note.
    /// </summary>
    private string[] GetAllKnowledgeAreasIteratively() {
      List<string> result =
        new List<string>();

      Stack<string> pending =
        new Stack<string>();

      string[] rootChildren =
        _KnowledgeRepository.GetAreas(
          false,
          "/"
        );

      for (int index = rootChildren.Length - 1;
           index >= 0;
           index--) {
        pending.Push(
          rootChildren[index]
        );
      }

      int processed =
        0;

      while (pending.Count > 0) {
        string area =
          pending.Pop();

        result.Add(
          area
        );

        ContentLevel contentLevel;

        if (!this.TryGetContentLevel(
              area,
              out contentLevel
            )) {
          continue;
        }

        // The first content container is the Joplin note boundary. Descendant content
        // containers are Markdown headings inside that note and must neither become
        // additional sync items nor be traversed just to discover them.
        if (contentLevel != ContentLevel.ContentContainer) {
          string[] children =
            _KnowledgeRepository.GetAreas(
              false,
              area
            );

          for (int index = children.Length - 1;
               index >= 0;
               index--) {
            pending.Push(
              children[index]
            );
          }
        }

        processed++;

        // Large remote knowledge sources should not monopolize the executing thread for
        // an arbitrarily long uninterrupted traversal.
        if (processed % 128 == 0) {
          System.Threading.Thread.Yield();
        }
      }

      return result.ToArray();
    }

    /// <summary>
    /// Materializes one already mapped Joplin note or folder from its exact knowledge area.
    /// </summary>
    private JoplinProjectedItem BuildProjectedItem(
      JoplinProjectionRecord record,
      JoplinProjectionState state
    ) {
      JoplinProjectedItem projectedItem =
        new JoplinProjectedItem();

      projectedItem.Record =
        record;

      projectedItem.Title =
        _KnowledgeRepository.GetAreaName(
          record.Area
        );

      projectedItem.ParentId =
        this.ResolveProjectedParentId(
          record.Area,
          record,
          state
        );

      if (record.Type == _JoplinNoteType) {
        string knowledgeBody =
          _KnowledgeRepository.GetAggregatedContent(
            record.Area
          );

        projectedItem.Body =
          this.TranslateKnowledgeBodyToJoplin(
            record.Area,
            knowledgeBody,
            state
          );
      }
      else {
        projectedItem.Body =
          string.Empty;
      }

      string semanticHash =
        this.ComputeProjectedSemanticHash(
          projectedItem
        );

      if (!string.Equals(
            record.LastContentHash,
            semanticHash,
            StringComparison.Ordinal
          )) {
        record.LastContentHash =
          semanticHash;

        record.ModifiedUtc =
          DateTime.UtcNow;
      }

      string serialized =
        this.SerializeJoplinItem(
          projectedItem
        );

      projectedItem.SerializedContent =
        serialized;

      projectedItem.ContentHash =
        this.ComputeHash(
          serialized
        );

      return projectedItem;
    }

    /// <summary>
    /// Builds the current deterministic projection from logical knowledge areas to flat
    /// Joplin WebDAV sync-item files.
    ///
    /// This is intentionally an explicit bulk operation used for the Joplin synchronization
    /// root. It walks the repository level by level and never delegates recursive traversal
    /// to a provider.
    /// </summary>
    private JoplinProjection BuildProjection() {
      JoplinProjectionState state = this.LoadProjectionState();

      string[] areas =
        this.GetAllKnowledgeAreasIteratively();

      HashSet<string> currentlyProjectedAreas =
        new HashSet<string>(StringComparer.Ordinal);

      List<JoplinProjectedItem> items = new List<JoplinProjectedItem>();

      foreach (string area in areas) {
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

        bool projectAsFolder =
          contentLevel == ContentLevel.BeyondContent ||
          contentLevel == ContentLevel.ContentAggregation;

        bool projectAsNote = false;

        if (contentLevel == ContentLevel.ContentContainer) {
          string parentArea = this.GetParentArea(area);
          ContentLevel parentContentLevel = ContentLevel.BeyondContent;

          if (parentArea != "/") {
            bool parentResolved = this.TryGetContentLevel(
              parentArea,
              out parentContentLevel
            );

            if (!parentResolved) {
              parentContentLevel = ContentLevel.BeyondContent;
            }
          }

          if (parentContentLevel != ContentLevel.ContentContainer) {
            projectAsNote = true;
          }
        }

        if (!projectAsFolder && !projectAsNote) {
          continue;
        }

        int type = _JoplinFolderType;

        if (projectAsNote) {
          type = _JoplinNoteType;
        }

        JoplinProjectionRecord record = state.Records
          .FirstOrDefault((JoplinProjectionRecord candidate) =>
            string.Equals(
              candidate.Area,
              area,
              StringComparison.Ordinal
            ) &&
            candidate.Type == type
          );

        if (record == null) {
          record = new JoplinProjectionRecord();
          record.Id = this.CreateDeterministicItemId(
            type.ToString(CultureInfo.InvariantCulture) + ":" + area
          );
          record.Area = area;
          record.Type = type;
          record.CreatedUtc = DateTime.UtcNow;
          record.ModifiedUtc = record.CreatedUtc;
          record.ParentIdOverride = string.Empty;
          record.LastContentHash = string.Empty;
          record.IsSuppressed = false;

          state.Records.Add(record);
        }

        currentlyProjectedAreas.Add(
          type.ToString(CultureInfo.InvariantCulture) + ":" + area
        );

        if (record.IsSuppressed) {
          continue;
        }

        JoplinProjectedItem projectedItem = new JoplinProjectedItem();
        projectedItem.Record = record;
        projectedItem.Title = _KnowledgeRepository.GetAreaName(area);
        projectedItem.ParentId = this.ResolveProjectedParentId(
          area,
          record,
          state
        );

        if (type == _JoplinNoteType) {
          string knowledgeBody = _KnowledgeRepository.GetAggregatedContent(
            area
          );

          projectedItem.Body = this.TranslateKnowledgeBodyToJoplin(
            area,
            knowledgeBody,
            state
          );
        }
        else {
          projectedItem.Body = string.Empty;
        }

        string semanticHash = this.ComputeProjectedSemanticHash(
          projectedItem
        );

        if (!string.Equals(
              record.LastContentHash,
              semanticHash,
              StringComparison.Ordinal
            )) {
          record.LastContentHash = semanticHash;
          record.ModifiedUtc = DateTime.UtcNow;
        }

        // Serialize only after the semantic modification timestamp has reached its final
        // value for this projection pass. Otherwise updated_time becomes part of the
        // previous hash decision and causes every subsequent projection to modify itself.
        string serialized = this.SerializeJoplinItem(
          projectedItem
        );

        string transportHash = this.ComputeHash(serialized);

        projectedItem.SerializedContent = serialized;
        projectedItem.ContentHash = transportHash;
        items.Add(projectedItem);
      }

      JoplinProjectionRecord[] staleRecords = state.Records
        .Where((JoplinProjectionRecord record) =>
          (record.Type == _JoplinNoteType ||
           record.Type == _JoplinFolderType) &&
          !currentlyProjectedAreas.Contains(
            record.Type.ToString(CultureInfo.InvariantCulture)
            + ":"
            + record.Area
          ))
        .ToArray();

      foreach (JoplinProjectionRecord staleRecord in staleRecords) {
        state.Records.Remove(staleRecord);
      }

      this.SaveProjectionState(state);

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin projection state: records="
        + state.Records.Count.ToString(CultureInfo.InvariantCulture)
        + " items="
        + items.Count.ToString(CultureInfo.InvariantCulture)
      );

      return new JoplinProjection(
        state,
        items.ToArray()
      );
    }

    /// <summary>
    /// Resolves the Joplin parent notebook ID for one projected area.
    /// </summary>
    private string ResolveProjectedParentId(
      string area,
      JoplinProjectionRecord record,
      JoplinProjectionState state
    ) {
      if (!string.IsNullOrWhiteSpace(record.ParentIdOverride)) {
        JoplinProjectionRecord overriddenParent = state.Records
          .FirstOrDefault((JoplinProjectionRecord candidate) =>
            string.Equals(
              candidate.Id,
              record.ParentIdOverride,
              StringComparison.OrdinalIgnoreCase
            ) &&
            candidate.Type == _JoplinFolderType
          );

        if (overriddenParent != null) {
          return overriddenParent.Id;
        }
      }

      string parentArea = this.GetParentArea(area);

      while (parentArea != "/") {
        JoplinProjectionRecord parentRecord = state.Records
          .FirstOrDefault((JoplinProjectionRecord candidate) =>
            string.Equals(
              candidate.Area,
              parentArea,
              StringComparison.Ordinal
            ) &&
            candidate.Type == _JoplinFolderType
          );

        if (parentRecord != null) {
          return parentRecord.Id;
        }

        parentArea = this.GetParentArea(parentArea);
      }

      return string.Empty;
    }

    /// <summary>
    /// Normalizes textual content for diagnostic write-through comparison.
    /// </summary>
    private string NormalizeContentForComparison(
      string content
    ) {
      if (content == null) {
        return string.Empty;
      }

      return content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .TrimEnd('\n');
    }

    /// <summary>
    /// Synchronizes the persistent projection record with a successfully materialized
    /// Joplin item so the immediately following projection pass is byte-stable and does
    /// not manufacture another remote modification.
    /// </summary>
    private void SynchronizeProjectionRecordAfterJoplinWrite(
      JoplinProjectionRecord record,
      JoplinSerializedItem item,
      JoplinProjectionState state
    ) {
      if (item.CreatedUtc != DateTime.MinValue) {
        record.CreatedUtc = item.CreatedUtc;
      }
      else if (record.CreatedUtc == DateTime.MinValue) {
        record.CreatedUtc = DateTime.UtcNow;
      }

      if (item.ModifiedUtc != DateTime.MinValue) {
        record.ModifiedUtc = item.ModifiedUtc;
      }
      else {
        record.ModifiedUtc = DateTime.UtcNow;
      }

      record.ParentIdOverride = item.ParentId;

      JoplinProjectedItem projectedItem = new JoplinProjectedItem();
      projectedItem.Record = record;
      projectedItem.Title = _KnowledgeRepository.GetAreaName(
        record.Area
      );
      projectedItem.ParentId = item.ParentId;

      if (record.Type == _JoplinNoteType) {
        string knowledgeBody =
          _KnowledgeRepository.GetAggregatedContent(
            record.Area
          );

        projectedItem.Body =
          this.TranslateKnowledgeBodyToJoplin(
            record.Area,
            knowledgeBody,
            state
          );
      }
      else {
        projectedItem.Body = string.Empty;
      }

      record.LastContentHash = this.ComputeProjectedSemanticHash(
        projectedItem
      );
    }

    /// <summary>
    /// Computes a stable semantic fingerprint for one projected Joplin item.
    ///
    /// Transport metadata such as <c>updated_time</c> is deliberately excluded. Including
    /// the modification timestamp in the change detector would make the projection
    /// self-modifying: changing the timestamp would change the hash, which would change
    /// the timestamp again during the next projection pass.
    /// </summary>
    private string ComputeProjectedSemanticHash(
      JoplinProjectedItem item
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append(
        item.Record.Type.ToString(
          CultureInfo.InvariantCulture
        )
      );
      builder.Append('\n');
      builder.Append(item.Record.Area);
      builder.Append('\n');
      builder.Append(item.Title);
      builder.Append('\n');
      builder.Append(item.ParentId);
      builder.Append('\n');
      builder.Append(item.Body);

      return this.ComputeHash(
        builder.ToString()
      );
    }

    private string SerializeJoplinItem(JoplinProjectedItem item) {
      List<string> blocks = new List<string>();

      blocks.Add(
        item.Title.TrimEnd('\r', '\n')
      );

      if (item.Record.Type == _JoplinNoteType &&
          !string.IsNullOrEmpty(item.Body)) {
        blocks.Add(
          item.Body.TrimEnd('\r', '\n')
        );
      }

      StringBuilder properties = new StringBuilder();

      properties.Append("id: ");
      properties.Append(item.Record.Id);
      properties.Append('\n');
      properties.Append("parent_id: ");
      properties.Append(item.ParentId);
      properties.Append('\n');
      properties.Append("created_time: ");
      properties.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
      properties.Append('\n');
      properties.Append("updated_time: ");
      properties.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
      properties.Append('\n');

      if (item.Record.Type == _JoplinNoteType) {
        properties.Append("is_conflict: 0\n");
        properties.Append("latitude: 0.00000000\n");
        properties.Append("longitude: 0.00000000\n");
        properties.Append("altitude: 0.0000\n");
        properties.Append("author: \n");
        properties.Append("source_url: \n");
        properties.Append("is_todo: 0\n");
        properties.Append("todo_due: 0\n");
        properties.Append("todo_completed: 0\n");
        properties.Append("source: knowledge-repository\n");
        properties.Append("source_application: knowledge-repository\n");
        properties.Append("application_data: \n");
        properties.Append("order: 0\n");
        properties.Append("user_created_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
        properties.Append('\n');
        properties.Append("user_updated_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
        properties.Append('\n');
        properties.Append("encryption_cipher_text: \n");
        properties.Append("encryption_applied: 0\n");
        properties.Append("markup_language: 1\n");
        properties.Append("is_shared: 0\n");
        properties.Append("share_id: \n");
        properties.Append("conflict_original_id: \n");
        properties.Append("master_key_id: \n");
        properties.Append("user_data: \n");
        properties.Append("deleted_time: 0\n");
      }
      else {
        properties.Append("user_created_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
        properties.Append('\n');
        properties.Append("user_updated_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
        properties.Append('\n');
        properties.Append("encryption_cipher_text: \n");
        properties.Append("encryption_applied: 0\n");
        properties.Append("is_shared: 0\n");
        properties.Append("share_id: \n");
        properties.Append("master_key_id: \n");
        properties.Append("user_data: \n");
        properties.Append("deleted_time: 0\n");
      }

      properties.Append("type_: ");
      properties.Append(
        item.Record.Type.ToString(
          CultureInfo.InvariantCulture
        )
      );

      blocks.Add(
        properties.ToString()
      );

      // Joplin serializes title, optional note body and the property block by joining
      // them with exactly one empty line. There must be no trailing line break after
      // the final property. BaseItem.unserialize() scans from the end and would treat
      // such a trailing empty line as the body/property separator before reading any
      // metadata, causing "Missing required property: type_".
      return string.Join(
        "\n\n",
        blocks.ToArray()
      );
    }

    /// <summary>
    /// Parses one UTC timestamp from Joplin sync-item metadata.
    /// </summary>
    private DateTime ParseJoplinTime(
      Dictionary<string, string> metadata,
      string key
    ) {
      if (!metadata.TryGetValue(
            key,
            out string value
          )) {
        return DateTime.MinValue;
      }

      DateTime parsed;

      if (!DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out parsed
          )) {
        return DateTime.MinValue;
      }

      return parsed;
    }

    /// <summary>
    /// Parses the subset of Joplin sync-item metadata required to map notes and notebooks
    /// back to the provider-neutral knowledge repository.
    /// </summary>
    private JoplinSerializedItem ParseJoplinItem(string content) {
      string normalized = content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

      string[] lines = normalized.Split('\n');

      Dictionary<string, string> metadata =
        new Dictionary<string, string>(StringComparer.Ordinal);

      int metadataStartIndex = lines.Length;

      for (int index = lines.Length - 1; index >= 0; index--) {
        string line = lines[index];

        if (line.Length == 0) {
          if (metadata.Count > 0) {
            metadataStartIndex = index + 1;
            break;
          }

          continue;
        }

        int separator = line.IndexOf(": ", StringComparison.Ordinal);

        if (separator <= 0) {
          if (metadata.Count > 0) {
            metadataStartIndex = index + 1;
            break;
          }

          return null;
        }

        string key = line.Substring(0, separator);
        string value = line.Substring(separator + 2);

        metadata[key] = value;
        metadataStartIndex = index;
      }

      if (!metadata.ContainsKey("id") ||
          !metadata.ContainsKey("type_")) {
        return null;
      }

      int type;

      if (!int.TryParse(
            metadata["type_"],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out type
          )) {
        return null;
      }

      JoplinSerializedItem item = new JoplinSerializedItem();
      item.Id = metadata["id"];
      item.Type = type;

      item.CreatedUtc = this.ParseJoplinTime(
        metadata,
        "created_time"
      );

      item.ModifiedUtc = this.ParseJoplinTime(
        metadata,
        "updated_time"
      );

      if (metadata.TryGetValue("parent_id", out string parentId)) {
        item.ParentId = parentId;
      }
      else {
        item.ParentId = string.Empty;
      }

      if (metadata.TryGetValue("mime", out string mime)) {
        item.Mime = mime;
      }

      if (metadata.TryGetValue("filename", out string fileName)) {
        item.FileName = fileName;
      }

      if (metadata.TryGetValue("file_extension", out string fileExtension)) {
        item.FileExtension = fileExtension;
      }

      int contentEndIndex = metadataStartIndex - 1;

      while (contentEndIndex >= 0 &&
             lines[contentEndIndex].Length == 0) {
        contentEndIndex--;
      }

      if (contentEndIndex < 0) {
        return null;
      }

      item.Title = lines[0].Trim();

      if (string.IsNullOrWhiteSpace(item.Title)) {
        if (type == _JoplinResourceType) {
          item.Title = item.FileName;

          if (string.IsNullOrWhiteSpace(item.Title)) {
            item.Title = item.Id;
          }
        }
        else {
          return null;
        }
      }

      if (type == _JoplinNoteType) {
        int bodyStartIndex = 1;

        while (bodyStartIndex <= contentEndIndex &&
               lines[bodyStartIndex].Length == 0) {
          bodyStartIndex++;
        }

        if (bodyStartIndex <= contentEndIndex) {
          item.Body = string.Join(
            "\n",
            lines,
            bodyStartIndex,
            contentEndIndex - bodyStartIndex + 1
          );
        }
        else {
          item.Body = string.Empty;
        }
      }
      else {
        item.Body = string.Empty;
      }

      return item;
    }

    /// <summary>
    /// Resolves one WebDAV resource from the dynamic knowledge projection or opaque
    /// synchronization-state store.
    /// </summary>
    private JoplinWebDavEntry ResolveWebDavEntry(
      string path,
      JoplinProjection projection
    ) {
      if (path == "/") {
        JoplinWebDavEntry root = new JoplinWebDavEntry();
        root.Path = "/";
        root.DisplayName = "Joplin";
        root.IsCollection = true;
        root.Length = 0;
        root.LastModifiedUtc = DateTime.UnixEpoch;
        root.ETag = "root";
        root.SourceKind = JoplinWebDavSourceKind.Root;
        return root;
      }

      // A raw state-store item represents Joplin protocol/projection state and always
      // takes precedence over the dynamic Knowledge projection.
      JoplinWebDavEntry stateBackedEntry;

      if (this.TryResolveStateStoreEntry(
            path,
            out stateBackedEntry
          )) {
        return stateBackedEntry;
      }

      if (this.IsRootItemFile(path)) {
        string itemId = Path.GetFileNameWithoutExtension(path);
        JoplinProjectedItem item = projection.FindItemById(itemId);

        if (item != null) {
          byte[] bytes = Encoding.UTF8.GetBytes(item.SerializedContent);

          JoplinWebDavEntry projectedEntry = new JoplinWebDavEntry();
          projectedEntry.Path = path;
          projectedEntry.DisplayName = Path.GetFileName(path);
          projectedEntry.IsCollection = false;
          projectedEntry.Length = bytes.LongLength;
          projectedEntry.LastModifiedUtc = item.Record.ModifiedUtc;
          projectedEntry.ETag = item.ContentHash;
          projectedEntry.SourceKind = JoplinWebDavSourceKind.ProjectedKnowledgeItem;
          projectedEntry.ProjectedItem = item;
          return projectedEntry;
        }
      }


      return null;
    }

    /// <summary>
    /// Returns direct WebDAV children for a collection.
    /// </summary>
    /// <summary>
    /// Resolves one exact Joplin sync-state path without building or consulting the dynamic
    /// Knowledge projection.
    ///
    /// This fast path is essential for protocol files such as <c>/info.json</c>, lock files,
    /// temporary files and resource blobs. Reading those files must never trigger Git fetches,
    /// repository enumeration or projection-state mutation.
    /// </summary>
    private bool TryResolveStateStoreEntry(
      string path,
      out JoplinWebDavEntry entry
    ) {
      entry = null;

      JoplinSyncStateEntry stateEntry =
        _SyncStateStore.GetEntry(
          path
        );

      if (stateEntry == null) {
        return false;
      }

      JoplinWebDavEntry resolvedEntry =
        new JoplinWebDavEntry();

      resolvedEntry.Path = stateEntry.Path;
      resolvedEntry.DisplayName = this.GetWebDavDisplayName(
        stateEntry.Path
      );
      resolvedEntry.IsCollection = stateEntry.IsCollection;
      resolvedEntry.Length = stateEntry.Length;
      resolvedEntry.LastModifiedUtc = stateEntry.LastModifiedUtc;
      resolvedEntry.SourceKind = JoplinWebDavSourceKind.StateStore;

      if (stateEntry.IsCollection) {
        resolvedEntry.ETag = this.ComputeHash(
          stateEntry.Path
          + ":"
          + stateEntry.LastModifiedUtc.Ticks.ToString(
            CultureInfo.InvariantCulture
          )
        );
      }
      else {
        byte[] stateBytes = _SyncStateStore.ReadFile(
          path
        );

        resolvedEntry.ETag = this.ComputeHash(
          stateBytes
        );
      }

      entry = resolvedEntry;
      return true;
    }

    /// <summary>
    /// Returns direct children of one opaque Joplin sync-state collection without building
    /// the dynamic Knowledge projection.
    /// </summary>
    private JoplinWebDavEntry[] GetStateStoreChildren(
      string path
    ) {
      JoplinSyncStateEntry[] stateChildren =
        _SyncStateStore.GetChildren(
          path
        );

      List<JoplinWebDavEntry> children =
        new List<JoplinWebDavEntry>();

      foreach (JoplinSyncStateEntry stateChild in stateChildren) {
        JoplinWebDavEntry child;

        if (this.TryResolveStateStoreEntry(
              stateChild.Path,
              out child
            )) {
          children.Add(
            child
          );
        }
      }

      return children.ToArray();
    }

    /// <summary>
    /// Returns direct WebDAV children for one projected or state-backed path.
    /// </summary>
    private JoplinWebDavEntry[] GetWebDavChildren(
      string path,
      JoplinProjection projection
    ) {
      List<JoplinWebDavEntry> children = new List<JoplinWebDavEntry>();

      if (path == "/") {
        JoplinSyncStateEntry[] stateChildren = _SyncStateStore.GetChildren("/");

        foreach (JoplinSyncStateEntry stateChild in stateChildren) {
          JoplinWebDavEntry entry = this.ResolveWebDavEntry(
            stateChild.Path,
            projection
          );

          if (entry != null) {
            children.Add(entry);
          }
        }

        foreach (JoplinProjectedItem item in projection.Items) {
          string itemPath = "/" + item.Record.Id + _MarkdownExtension;

          if (children.Any((JoplinWebDavEntry existing) =>
                string.Equals(
                  existing.Path,
                  itemPath,
                  StringComparison.OrdinalIgnoreCase
                ))) {
            continue;
          }

          JoplinWebDavEntry entry = this.ResolveWebDavEntry(
            itemPath,
            projection
          );

          if (entry != null) {
            children.Add(entry);
          }
        }

        return children.ToArray();
      }

      JoplinSyncStateEntry[] nestedChildren = _SyncStateStore.GetChildren(
        path
      );

      foreach (JoplinSyncStateEntry nestedChild in nestedChildren) {
        JoplinWebDavEntry entry = this.ResolveWebDavEntry(
          nestedChild.Path,
          projection
        );

        if (entry != null) {
          children.Add(entry);
        }
      }

      return children.ToArray();
    }

    /// <summary>
    /// Returns bytes for a projected or opaque WebDAV file.
    /// </summary>
    private byte[] GetWebDavFileContent(
      JoplinWebDavEntry entry,
      JoplinProjection projection
    ) {
      if (entry.SourceKind == JoplinWebDavSourceKind.ProjectedKnowledgeItem) {
        return Encoding.UTF8.GetBytes(
          entry.ProjectedItem.SerializedContent
        );
      }

      return _SyncStateStore.ReadFile(
        entry.Path
      );
    }

    /// <summary>
    /// Returns the WebDAV content length value used in PROPFIND responses.
    /// </summary>
    private string GetWebDavContentLength(JoplinWebDavEntry entry) {
      if (entry.IsCollection) {
        return "0";
      }

      return entry.Length.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Applies HTTP metadata expected by WebDAV clients.
    /// </summary>
    private void ApplyFileHeaders(JoplinWebDavEntry entry) {
      this.Response.Headers["ETag"] = "\"" + entry.ETag + "\"";
      this.Response.Headers["Last-Modified"] = entry.LastModifiedUtc.ToString(
        "R",
        CultureInfo.InvariantCulture
      );

      if (!entry.IsCollection) {
        this.Response.ContentLength = entry.Length;
      }
    }

    /// <summary>
    /// Ensures the persistent auxiliary directories and sync-target metadata expected by
    /// Joplin are present.
    /// </summary>
    private void EnsureJoplinInfrastructure() {
      lock (_SyncRoot) {
        this.EnsureCollection(_LocksCollection);
        this.EnsureCollection(_TempCollection);
        this.EnsureCollection(_ResourceCollection);
        this.EnsureCollection(_LegacySyncCollection);
        this.EnsureCollection(_LegacyLockCollection);

        if (!_SyncStateStore.FileExists(_InfoFilePath)) {
          JoplinSyncTargetInfo info = new JoplinSyncTargetInfo();
          info.Version = _JoplinSyncVersion;

          string json = JsonConvert.SerializeObject(
            info,
            Formatting.Indented
          );

          _SyncStateStore.WriteFile(
            _InfoFilePath,
            Encoding.UTF8.GetBytes(json)
          );
        }
      }
    }

    /// <summary>
    /// Creates one auxiliary collection when it does not already exist.
    /// </summary>
    private void EnsureCollection(string path) {
      if (!_SyncStateStore.CollectionExists(path)) {
        _SyncStateStore.CreateCollection(path);
      }
    }

    /// <summary>
    /// Loads persisted stable item mappings and modification metadata.
    /// </summary>
    private JoplinProjectionState LoadProjectionState() {
      string json = _SyncStateStore.ReadInternalText(
        _ProjectionStateFileName
      );

      if (string.IsNullOrWhiteSpace(json)) {
        return new JoplinProjectionState();
      }

      try {
        JoplinProjectionState state =
          JsonConvert.DeserializeObject<JoplinProjectionState>(json);

        if (state == null) {
          return new JoplinProjectionState();
        }

        if (state.Records == null) {
          state.Records = new List<JoplinProjectionRecord>();
        }

        if (state.Resources == null) {
          state.Resources = new List<JoplinResourceProjectionRecord>();
        }

        return state;
      }
      catch (JsonException ex) {
        DevLogger.LogError(ex);
        return new JoplinProjectionState();
      }
    }

    /// <summary>
    /// Persists stable Joplin item mappings and projection timestamps.
    /// </summary>
    private void SaveProjectionState(JoplinProjectionState state) {
      string json = JsonConvert.SerializeObject(
        state,
        Formatting.Indented
      );

      _SyncStateStore.WriteInternalText(
        _ProjectionStateFileName,
        json
      );
    }

    /// <summary>
    /// Attempts to resolve the content level of one logical area.
    /// </summary>
    private bool TryGetContentLevel(
      string area,
      out ContentLevel contentLevel
    ) {
      contentLevel = ContentLevel.BeyondContent;

      try {
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

        return true;
      }
      catch (InvalidOperationException) {
        return false;
      }
    }

    /// <summary>
    /// Finds the direct child added by comparing repository state before and after
    /// TryAddSubArea.
    /// </summary>
    private string FindAddedArea(
      string[] beforeAreas,
      string[] afterAreas
    ) {
      HashSet<string> before = new HashSet<string>(
        beforeAreas,
        StringComparer.Ordinal
      );

      string[] added = afterAreas
        .Where((string area) => !before.Contains(area))
        .ToArray();

      if (added.Length != 1) {
        return string.Empty;
      }

      return added[0];
    }

    /// <summary>
    /// Finds the renamed direct child after a successful provider-level rename.
    /// </summary>
    private string FindRenamedArea(
      string parentArea,
      string oldArea,
      string newTitle
    ) {
      string[] children = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string normalizedTitle = this.NormalizeDisplayName(newTitle);

      foreach (string child in children) {
        if (string.Equals(
              child,
              oldArea,
              StringComparison.Ordinal
            )) {
          continue;
        }

        string childTitle = _KnowledgeRepository.GetAreaName(child);

        if (string.Equals(
              this.NormalizeDisplayName(childTitle),
              normalizedTitle,
              StringComparison.OrdinalIgnoreCase
            )) {
          return child;
        }
      }

      return string.Empty;
    }


    /// <summary>
    /// Normalizes a display title for post-rename matching.
    /// </summary>
    private string NormalizeDisplayName(string value) {
      return value.Trim();
    }

    /// <summary>
    /// Gets the logical parent area.
    /// </summary>
    private string GetParentArea(string area) {
      if (string.IsNullOrWhiteSpace(area) || area == "/") {
        return "/";
      }

      string normalized = area.TrimEnd('/');
      int separator = normalized.LastIndexOf('/');

      if (separator <= 0) {
        return "/";
      }

      return normalized.Substring(0, separator);
    }

    /// <summary>
    /// Creates a deterministic Joplin-compatible 32-character hexadecimal item ID.
    /// Persisted projection records may later retain client-generated Joplin IDs.
    /// </summary>
    private string CreateDeterministicItemId(string value) {
      byte[] input = Encoding.UTF8.GetBytes(value);
      byte[] hash = SHA256.HashData(input);
      StringBuilder builder = new StringBuilder();

      for (int index = 0; index < 16; index++) {
        builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
      }

      return builder.ToString();
    }

    /// <summary>
    /// Computes a hexadecimal SHA-256 content hash.
    /// </summary>
    private string ComputeHash(string value) {
      return this.ComputeHash(
        Encoding.UTF8.GetBytes(value)
      );
    }

    /// <summary>
    /// Computes a hexadecimal SHA-256 content hash.
    /// </summary>
    private string ComputeHash(byte[] value) {
      byte[] hash = SHA256.HashData(value);
      StringBuilder builder = new StringBuilder();

      foreach (byte current in hash) {
        builder.Append(
          current.ToString("x2", CultureInfo.InvariantCulture)
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Formats one UTC timestamp using the timestamp format used by Joplin item exports.
    /// </summary>
    private string FormatJoplinTime(DateTime value) {
      return value
        .ToUniversalTime()
        .ToString(
          "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
          CultureInfo.InvariantCulture
        );
    }

    /// <summary>
    /// Formats one UTC timestamp as Unix epoch milliseconds for Joplin properties that are
    /// explicitly numeric, such as resource blob_updated_time.
    /// </summary>
    private string FormatJoplinUnixTimeMilliseconds(DateTime value) {
      DateTimeOffset timestamp = new DateTimeOffset(
        value.ToUniversalTime()
      );

      return timestamp
        .ToUnixTimeMilliseconds()
        .ToString(
          CultureInfo.InvariantCulture
        );
    }

    /// <summary>
    /// Generates a fallback title for a note whose incoming sync item does not provide
    /// one explicitly.
    /// </summary>
    private string GetFallbackNoteTitle(string body) {
      using StringReader reader = new StringReader(body);

      string firstLine = reader.ReadLine();

      if (string.IsNullOrWhiteSpace(firstLine)) {
        return "Untitled";
      }

      string title = firstLine.Trim().TrimStart('#').Trim();

      if (string.IsNullOrWhiteSpace(title)) {
        return "Untitled";
      }

      if (title.Length > 100) {
        return title.Substring(0, 100);
      }

      return title;
    }

    /// <summary>
    /// Determines whether one WebDAV root file name has Joplin sync-item shape.
    /// </summary>
    private bool IsRootItemFile(string path) {
      if (string.IsNullOrWhiteSpace(path)) {
        return false;
      }

      if (path.Count((char value) => value == '/') != 1) {
        return false;
      }

      if (!path.EndsWith(_MarkdownExtension, StringComparison.OrdinalIgnoreCase)) {
        return false;
      }

      string id = Path.GetFileNameWithoutExtension(path);

      if (id.Length != 32) {
        return false;
      }

      return id.All((char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'a' && value <= 'f') ||
        (value >= 'A' && value <= 'F'));
    }

    /// <summary>
    /// Builds the WebDAV href expected in PROPFIND responses.
    /// </summary>
    private string BuildWebDavHref(string path) {
      StringBuilder builder = new StringBuilder();

      builder.Append(
        this.Request.PathBase.Value
      );
      builder.Append(
        _WebDavBasePath
      );

      if (path != "/") {
        string[] segments = path
          .Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries
          );

        foreach (string segment in segments) {
          builder.Append('/');
          builder.Append(
            Uri.EscapeDataString(segment)
          );
        }
      }
      else {
        builder.Append('/');
      }

      return builder.ToString();
    }

    /// <summary>
    /// Extracts the facade-relative path from an absolute or relative WebDAV Destination
    /// header.
    /// </summary>
    private string ExtractDestinationPath(string destination) {
      string value = destination;

      if (Uri.TryCreate(destination, UriKind.Absolute, out Uri absoluteUri)) {
        value = absoluteUri.AbsolutePath;
      }

      string basePath = this.Request.PathBase.Value
        + _WebDavBasePath;

      int index = value.IndexOf(
        basePath,
        StringComparison.OrdinalIgnoreCase
      );

      if (index >= 0) {
        value = value.Substring(index + basePath.Length);
      }

      return this.NormalizeWebDavPath(value);
    }

    /// <summary>
    /// Normalizes the application-relative WebDAV root used when emitting href and
    /// Destination values.
    /// </summary>
    private string NormalizeWebDavBasePath(string path) {
      string normalized = path
        .Trim()
        .Replace('\\', '/');

      if (!normalized.StartsWith(
            "/",
            StringComparison.Ordinal
          )) {
        normalized = "/" + normalized;
      }

      while (normalized.Contains(
        "//",
        StringComparison.Ordinal
      )) {
        normalized = normalized.Replace(
          "//",
          "/",
          StringComparison.Ordinal
        );
      }

      if (normalized.Length > 1 &&
          normalized.EndsWith(
            "/",
            StringComparison.Ordinal
          )) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }

    /// <summary>
    /// Normalizes one controller catch-all value into a WebDAV-relative absolute path.
    /// </summary>
    private string NormalizeWebDavPath(string path) {
      if (string.IsNullOrWhiteSpace(path)) {
        return "/";
      }

      string normalized = path
        .Replace('\\', '/')
        .Trim();

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
    /// Gets the display name of one WebDAV path.
    /// </summary>
    private string GetWebDavDisplayName(string path) {
      if (path == "/") {
        return "Joplin";
      }

      int separator = path.LastIndexOf('/');

      if (separator >= 0 && separator < path.Length - 1) {
        return path.Substring(separator + 1);
      }

      return path;
    }

    /// <summary>
    /// Writes one lightweight trace entry for every incoming WebDAV request.
    /// 
    /// The trace is useful during Joplin compatibility testing because the Joplin UI
    /// often reports only the resulting HTTP status code.
    /// </summary>
    private void TraceWebDavRequest() {
      string contentType = this.Request.ContentType;

      if (string.IsNullOrWhiteSpace(contentType)) {
        contentType = "<none>";
      }

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin WebDAV request: "
        + this.Request.Method
        + " "
        + this.Request.Path.Value
        + " Content-Type="
        + contentType
      );
    }

    /// <summary>
    /// Represents persisted Joplin sync-target metadata.
    /// </summary>
    private sealed class JoplinSyncTargetInfo {

      private int _Version;

      /// <summary>
      /// Gets or sets the Joplin synchronization-target version.
      /// </summary>
      [JsonProperty("version")]
      public int Version {
        get {
          return _Version;
        }
        set {
          _Version = value;
        }
      }
    }

    /// <summary>
    /// Contains persistent projection state.
    /// </summary>
    private sealed class JoplinProjectionState {

      private List<JoplinProjectionRecord> _Records;
      private List<JoplinResourceProjectionRecord> _Resources;

      /// <summary>
      /// Creates empty projection state.
      /// </summary>
      public JoplinProjectionState() {
        _Records = new List<JoplinProjectionRecord>();
        _Resources = new List<JoplinResourceProjectionRecord>();
      }

      /// <summary>
      /// Gets or sets stable item mappings.
      /// </summary>
      public List<JoplinProjectionRecord> Records {
        get {
          return _Records;
        }
        set {
          _Records = value;
        }
      }

      /// <summary>
      /// Gets or sets stable Joplin resource-to-Knowledge ResourceId mappings.
      /// </summary>
      public List<JoplinResourceProjectionRecord> Resources {
        get {
          return _Resources;
        }
        set {
          _Resources = value;
        }
      }
    }

    /// <summary>
    /// Stores one stable Joplin item identity and its corresponding knowledge area.
    /// </summary>
    private sealed class JoplinProjectionRecord {

      private string _Id;
      private string _Area;
      private int _Type;
      private DateTime _CreatedUtc;
      private DateTime _ModifiedUtc;
      private string _ParentIdOverride;
      private string _LastContentHash;
      private bool _IsSuppressed;

      /// <summary>
      /// Gets or sets the stable Joplin item ID.
      /// </summary>
      public string Id {
        get {
          return _Id;
        }
        set {
          _Id = value;
        }
      }

      /// <summary>
      /// Gets or sets the backing logical knowledge area.
      /// </summary>
      public string Area {
        get {
          return _Area;
        }
        set {
          _Area = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin item type.
      /// </summary>
      public int Type {
        get {
          return _Type;
        }
        set {
          _Type = value;
        }
      }

      /// <summary>
      /// Gets or sets the first projection time.
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
      /// Gets or sets the most recent projected-content modification time.
      /// </summary>
      public DateTime ModifiedUtc {
        get {
          return _ModifiedUtc;
        }
        set {
          _ModifiedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets an optional Joplin-only parent override.
      /// </summary>
      public string ParentIdOverride {
        get {
          return _ParentIdOverride;
        }
        set {
          _ParentIdOverride = value;
        }
      }

      /// <summary>
      /// Gets or sets the most recent serialized-content hash.
      /// </summary>
      public string LastContentHash {
        get {
          return _LastContentHash;
        }
        set {
          _LastContentHash = value;
        }
      }

      /// <summary>
      /// Gets or sets whether this knowledge-backed item is intentionally hidden from the
      /// Joplin projection after a WebDAV DELETE.
      ///
      /// Suppression is persistent synchronization state only. It never deletes or
      /// modifies the backing knowledge area.
      /// </summary>
      public bool IsSuppressed {
        get {
          return _IsSuppressed;
        }
        set {
          _IsSuppressed = value;
        }
      }
    }

    /// <summary>
    /// Stores the stable mapping between one Joplin resource item and one provider-neutral
    /// provider-neutral Knowledge ResourceId.
    /// </summary>
    private sealed class JoplinResourceProjectionRecord {

      private string _JoplinId;
      private string _ResourceId;
      private string _AreaHint;
      private DateTime _CreatedUtc;
      private DateTime _ModifiedUtc;
      private string _FileExtension;
      private string _ContentType;
      private string _Title;
      private string _FileName;
      private string _LastContentHash;
      private bool _IsSuppressed;

      public string JoplinId {
        get {
          return _JoplinId;
        }
        set {
          _JoplinId = value;
        }
      }

      public string ResourceId {
        get {
          return _ResourceId;
        }
        set {
          _ResourceId = value;
        }
      }

      public string AreaHint {
        get {
          return _AreaHint;
        }
        set {
          _AreaHint = value;
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

      public DateTime ModifiedUtc {
        get {
          return _ModifiedUtc;
        }
        set {
          _ModifiedUtc = value;
        }
      }

      public string FileExtension {
        get {
          return _FileExtension;
        }
        set {
          _FileExtension = value;
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

      public string Title {
        get {
          return _Title;
        }
        set {
          _Title = value;
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

      public string LastContentHash {
        get {
          return _LastContentHash;
        }
        set {
          _LastContentHash = value;
        }
      }

      public bool IsSuppressed {
        get {
          return _IsSuppressed;
        }
        set {
          _IsSuppressed = value;
        }
      }
    }

    /// <summary>
    /// Represents the complete current dynamic Joplin projection.
    /// </summary>
    private sealed class JoplinProjection {

      private readonly JoplinProjectionState _State;
      private readonly JoplinProjectedItem[] _Items;

      /// <summary>
      /// Creates one projection snapshot.
      /// </summary>
      public JoplinProjection(
        JoplinProjectionState state,
        JoplinProjectedItem[] items
      ) {
        _State = state;
        _Items = items;
      }

      /// <summary>
      /// Gets persistent projection state.
      /// </summary>
      public JoplinProjectionState State {
        get {
          return _State;
        }
      }

      /// <summary>
      /// Gets all projected Joplin items.
      /// </summary>
      public JoplinProjectedItem[] Items {
        get {
          return _Items;
        }
      }

      /// <summary>
      /// Finds one projected item by Joplin ID.
      /// </summary>
      public JoplinProjectedItem FindItemById(string id) {
        return _Items.FirstOrDefault((JoplinProjectedItem item) =>
          string.Equals(
            item.Record.Id,
            id,
            StringComparison.OrdinalIgnoreCase
          ));
      }

      /// <summary>
      /// Finds one persistent mapping record by Joplin ID.
      /// </summary>
      public JoplinProjectionRecord FindRecordById(string id) {
        return _State.Records.FirstOrDefault((JoplinProjectionRecord record) =>
          string.Equals(
            record.Id,
            id,
            StringComparison.OrdinalIgnoreCase
          ));
      }
    }

    /// <summary>
    /// Represents one projected Joplin notebook or note.
    /// </summary>
    private sealed class JoplinProjectedItem {

      private JoplinProjectionRecord _Record;
      private string _Title;
      private string _ParentId;
      private string _Body;
      private string _SerializedContent;
      private string _ContentHash;

      /// <summary>
      /// Gets or sets the persistent projection record.
      /// </summary>
      public JoplinProjectionRecord Record {
        get {
          return _Record;
        }
        set {
          _Record = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin title.
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
      /// Gets or sets the parent notebook ID.
      /// </summary>
      public string ParentId {
        get {
          return _ParentId;
        }
        set {
          _ParentId = value;
        }
      }

      /// <summary>
      /// Gets or sets note Markdown content.
      /// </summary>
      public string Body {
        get {
          return _Body;
        }
        set {
          _Body = value;
        }
      }

      /// <summary>
      /// Gets or sets the complete serialized sync-item content.
      /// </summary>
      public string SerializedContent {
        get {
          return _SerializedContent;
        }
        set {
          _SerializedContent = value;
        }
      }

      /// <summary>
      /// Gets or sets the content ETag hash.
      /// </summary>
      public string ContentHash {
        get {
          return _ContentHash;
        }
        set {
          _ContentHash = value;
        }
      }
    }

    /// <summary>
    /// Represents one parsed Joplin sync item uploaded by a client.
    /// </summary>
    private sealed class JoplinSerializedItem {

      private string _Id;
      private int _Type;
      private string _ParentId;
      private string _Title;
      private string _Body;
      private DateTime _CreatedUtc;
      private DateTime _ModifiedUtc;
      private string _Mime;
      private string _FileName;
      private string _FileExtension;

      /// <summary>
      /// Gets or sets the Joplin item ID.
      /// </summary>
      public string Id {
        get {
          return _Id;
        }
        set {
          _Id = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin item type.
      /// </summary>
      public int Type {
        get {
          return _Type;
        }
        set {
          _Type = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin parent notebook ID.
      /// </summary>
      public string ParentId {
        get {
          return _ParentId;
        }
        set {
          _ParentId = value;
        }
      }

      /// <summary>
      /// Gets or sets the item title.
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
      /// Gets or sets the note body.
      /// </summary>
      public string Body {
        get {
          return _Body;
        }
        set {
          _Body = value;
        }
      }


      /// <summary>
      /// Gets or sets the Joplin creation timestamp.
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
      /// Gets or sets the Joplin modification timestamp.
      /// </summary>
      public DateTime ModifiedUtc {
        get {
          return _ModifiedUtc;
        }
        set {
          _ModifiedUtc = value;
        }
      }

      public string Mime {
        get {
          return _Mime;
        }
        set {
          _Mime = value;
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

      public string FileExtension {
        get {
          return _FileExtension;
        }
        set {
          _FileExtension = value;
        }
      }
    }

    /// <summary>
    /// Describes the result of translating an accepted Joplin sync item into the
    /// provider-neutral knowledge repository.
    /// </summary>
    private enum MaterializationResult {
      Success = 0,
      PendingDependency = 1,
      Failed = 2,
      TemporarilyUnavailable = 3
    }

    /// <summary>
    /// Describes whether an uploaded Joplin resource could be projected into the
    /// provider-neutral repository immediately.
    /// </summary>
    private enum ResourceApplyResult {
      Pending = 0,
      Applied = 1,
      TemporarilyUnavailable = 2,
      Invalid = 3
    }

    /// <summary>
    /// Identifies how one WebDAV resource is backed.
    /// </summary>
    private enum JoplinWebDavSourceKind {
      Root = 0,
      ProjectedKnowledgeItem = 1,
      StateStore = 2
    }

    /// <summary>
    /// Represents one WebDAV resource exposed to Joplin.
    /// </summary>
    private sealed class JoplinWebDavEntry {

      private string _Path;
      private string _DisplayName;
      private bool _IsCollection;
      private long _Length;
      private DateTime _LastModifiedUtc;
      private string _ETag;
      private JoplinWebDavSourceKind _SourceKind;
      private JoplinProjectedItem _ProjectedItem;

      /// <summary>
      /// Gets or sets the WebDAV-relative path.
      /// </summary>
      public string Path {
        get {
          return _Path;
        }
        set {
          _Path = value;
        }
      }

      /// <summary>
      /// Gets or sets the display name.
      /// </summary>
      public string DisplayName {
        get {
          return _DisplayName;
        }
        set {
          _DisplayName = value;
        }
      }

      /// <summary>
      /// Gets or sets whether the resource is a collection.
      /// </summary>
      public bool IsCollection {
        get {
          return _IsCollection;
        }
        set {
          _IsCollection = value;
        }
      }

      /// <summary>
      /// Gets or sets the resource length.
      /// </summary>
      public long Length {
        get {
          return _Length;
        }
        set {
          _Length = value;
        }
      }

      /// <summary>
      /// Gets or sets the resource modification time.
      /// </summary>
      public DateTime LastModifiedUtc {
        get {
          return _LastModifiedUtc;
        }
        set {
          _LastModifiedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets the ETag.
      /// </summary>
      public string ETag {
        get {
          return _ETag;
        }
        set {
          _ETag = value;
        }
      }

      /// <summary>
      /// Gets or sets the backing source kind.
      /// </summary>
      public JoplinWebDavSourceKind SourceKind {
        get {
          return _SourceKind;
        }
        set {
          _SourceKind = value;
        }
      }

      /// <summary>
      /// Gets or sets the projected knowledge item when applicable.
      /// </summary>
      public JoplinProjectedItem ProjectedItem {
        get {
          return _ProjectedItem;
        }
        set {
          _ProjectedItem = value;
        }
      }
    }
  }
}
