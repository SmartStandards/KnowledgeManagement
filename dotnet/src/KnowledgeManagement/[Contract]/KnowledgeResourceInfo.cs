using System;

namespace KnowledgeManagement.SmartStandards {

  /// <summary>
  /// Describes one binary resource that is logically available to textual knowledge content.
  ///
  /// Resources are deliberately not areas. Textual content references a resource through
  /// the provider-neutral URI form <c>knowledge-resource:&lt;ResourceId&gt;</c>.
  ///
  /// <see cref="ResourceId"/> is provider-defined and opaque. Consumers MUST NOT decode,
  /// split, compose or otherwise derive semantics from it. A provider-native move or rename
  /// may legitimately change a resource identifier.
  /// </summary>
  public sealed class KnowledgeResourceInfo {

    private string _ResourceId;
    private string _FileName;
    private string _ContentType;
    private long _Length;

    /// <summary>
    /// Gets or sets the opaque provider-defined repository resource identifier.
    /// </summary>
    public string ResourceId {
      get {
        return _ResourceId;
      }
      set {
        _ResourceId = value;
      }
    }

    /// <summary>
    /// Gets or sets a descriptive file name when the provider can expose one.
    /// The file name is metadata and MUST NOT be interpreted as the logical resource identity.
    /// </summary>
    public string FileName {
      get {
        return _FileName;
      }
      set {
        _FileName = value;
      }
    }

    /// <summary>
    /// Gets or sets the MIME content type when known.
    /// </summary>
    public string ContentType {
      get {
        return _ContentType;
      }
      set {
        _ContentType = value;
      }
    }

    /// <summary>
    /// Gets or sets the binary resource length in bytes.
    /// </summary>
    public long Length {
      get {
        return _Length;
      }
      set {
        _Length = value;
      }
    }

  }

}
