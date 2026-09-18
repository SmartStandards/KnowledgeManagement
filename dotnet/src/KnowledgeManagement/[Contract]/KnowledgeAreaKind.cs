using System;

namespace KnowledgeManagement.SmartStandards {

  /// <summary>
  /// Describes the semantic kind requested when a new direct child area is created.
  /// 
  /// This value expresses logical intent only. It deliberately does not prescribe a
  /// physical representation such as a file, directory, page, database row or remote
  /// resource.
  /// </summary>
  public enum KnowledgeAreaKind {

    /// <summary>
    /// The new area is intended as a structural/grouping scope that does not itself own
    /// direct textual content. Its effective <see cref="ContentLevel"/> may later depend
    /// on the provider and on the children that exist below it.
    /// </summary>
    Structural = 0,

    /// <summary>
    /// The new area is intended as a concrete content-bearing scope that can own direct
    /// textual content.
    /// </summary>
    Content = 1
  }

}
