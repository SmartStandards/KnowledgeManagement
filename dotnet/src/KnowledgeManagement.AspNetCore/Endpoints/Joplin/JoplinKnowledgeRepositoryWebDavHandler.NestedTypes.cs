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

  public partial class JoplinKnowledgeRepositoryWebDavHandler : ControllerBase {
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
