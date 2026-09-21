using System;

namespace KnowledgeManagement.SmartStandards.Wrappers {

  /// <summary>
  /// Exposes optional local cache capabilities for consumers that execute directly on top
  /// of a cache-enabled knowledge repository.
  ///
  /// This interface is intentionally not part of the transport-neutral repository contract.
  /// It is a local optimization capability and is not expected to be serialized or exposed
  /// across HTTP, WebDAV or other remote repository boundaries.
  /// </summary>
  public interface IKnowledgeRepositoryCacheControl {

    /// <summary>
    /// Returns whether the requested logical area currently has enough locally cached data
    /// to serve a normal consumer navigation/read without requiring the wrapped source to be
    /// refreshed first.
    ///
    /// The check must be passive. Implementations must not access the wrapped source merely
    /// to answer this question.
    /// </summary>
    bool IsAreaCached(
      string area
    );

    /// <summary>
    /// Opens one local read scope in which existing cache entries are always preferred,
    /// regardless of their configured lifetime.
    ///
    /// Missing entries are still loaded from the authoritative source and cached normally.
    /// The returned scope must restore the previous behavior when disposed.
    /// </summary>
    IDisposable BeginPreferExistingScope();

  }

}
