using System;

namespace KnowledgeManagement.SmartStandards {

  /// <summary>
  /// Describes one resource identifier change caused by a successful repository mutation.
  ///
  /// Resource identifiers are provider-owned identities. A provider may therefore need to
  /// replace an identifier when its native identity changes, for example because a physical
  /// resource path changes during a move or rename.
  /// </summary>
  public sealed class KnowledgeResourceIdChange {

    private string _PreviousResourceId;
    private string _CurrentResourceId;

    /// <summary>
    /// Gets or sets the resource identifier that was valid before the mutation.
    /// </summary>
    public string PreviousResourceId {
      get {
        return _PreviousResourceId;
      }
      set {
        _PreviousResourceId = value;
      }
    }

    /// <summary>
    /// Gets or sets the resource identifier that is valid after the mutation.
    /// </summary>
    public string CurrentResourceId {
      get {
        return _CurrentResourceId;
      }
      set {
        _CurrentResourceId = value;
      }
    }

  }

}
