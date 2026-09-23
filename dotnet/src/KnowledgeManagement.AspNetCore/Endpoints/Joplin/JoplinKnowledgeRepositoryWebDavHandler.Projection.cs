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
        try {
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

            DevLogger.LogTrace(
              0,
              99999,
              "Joplin projection discovered new knowledge area: area='"
              + area
              + "', joplinId='"
              + record.Id
              + "', type="
              + type.ToString(CultureInfo.InvariantCulture)
              + "."
            );
          }

          // Only mark the area as current after the provider has successfully confirmed its
          // capabilities. If a later read discovers that the area vanished meanwhile, the
          // catch block below removes this marker again so stale projection state can heal.
          string projectionKey =
            type.ToString(CultureInfo.InvariantCulture)
            + ":"
            + area;

          currentlyProjectedAreas.Add(
            projectionKey
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
        catch (InvalidOperationException ex) {
          if (!this.IsMissingAggregatedKnowledgeAreaException(
                ex,
                area
              )) {
            throw;
          }

          // The area was returned by the repository enumeration but disappeared before all
          // projection reads completed. Do not keep its projection key alive. The stale-record
          // cleanup below will remove any previous Joplin mapping for this area.
          string noteProjectionKey =
            _JoplinNoteType.ToString(CultureInfo.InvariantCulture)
            + ":"
            + area;

          string folderProjectionKey =
            _JoplinFolderType.ToString(CultureInfo.InvariantCulture)
            + ":"
            + area;

          currentlyProjectedAreas.Remove(
            noteProjectionKey
          );

          currentlyProjectedAreas.Remove(
            folderProjectionKey
          );

          DevLogger.LogTrace(
            0,
            99999,
            "Joplin projection ignored vanished knowledge area: area='"
            + area
            + "', reason='area disappeared between enumeration and projection materialization'."
          );
        }
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
        // Projection records are identity mappings, not a snapshot of the currently visible
        // repository tree. Keep mappings for temporarily absent knowledge areas so a later
        // reappearance receives exactly the same Joplin item ID. The record is deliberately
        // not emitted as an item during this projection pass because the area is absent from
        // the current repository enumeration.
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin projection retained inactive mapping: area='"
          + staleRecord.Area
          + "', joplinId='"
          + staleRecord.Id
          + "', type="
          + staleRecord.Type.ToString(CultureInfo.InvariantCulture)
          + ", reason='knowledge area is not present in the current repository snapshot'."
        );
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

    private bool IsMissingAggregatedKnowledgeAreaException(
      InvalidOperationException exception,
      string area
    ) {
      if (exception == null) {
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

  }
}
