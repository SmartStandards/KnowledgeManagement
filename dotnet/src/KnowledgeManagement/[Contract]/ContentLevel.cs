using System;

namespace KnowledgeManagement.SmartStandards {

  /// <summary>
  /// Describes how a logical knowledge area participates in textual content access.
  /// </summary>
  public enum ContentLevel {

    /// <summary>
    /// The area is located outside the content-accessible hierarchy.
    /// It may provide structural navigation and sub-areas, but textual content
    /// retrieval and textual content mutation operations are not applicable to
    /// the area itself.
    /// </summary>
    BeyondContent = 0,

    /// <summary>
    /// The area represents an aggregation scope for subordinate content.
    /// 
    /// A content aggregation area participates in content traversal and aggregated
    /// content retrieval but does not own direct textual content itself.
    /// Consequently, <see cref="IKnowledgeRepository.HasDirectContent(string)"/>
    /// MUST return false for such an area and
    /// <see cref="IKnowledgeRepository.GetDirectContent(string)"/> MUST return an
    /// empty string.
    /// 
    /// A content aggregation area may represent a physical provider concept such as
    /// a notebook, book, documentation collection or another grouping construct.
    /// It may also be purely virtual and synthesized by the provider, for example to
    /// expose a read-only cross-cutting projection that aggregates similarly named
    /// sections, conclusions, examples, code snippets or other content collected
    /// from multiple unrelated source areas.
    /// 
    /// Whether mutation operations are available for a concrete aggregation area is
    /// determined by its reported capabilities. A provider may expose fully mutable,
    /// partially mutable or completely read-only aggregation areas.
    /// </summary>
    ContentAggregation = 1,

    /// <summary>
    /// The area represents a concrete content-bearing unit.
    /// It may own direct textual content and may additionally contain subordinate
    /// content areas.
    /// 
    /// Direct content retrieval and direct content mutation operations are meaningful
    /// for this level, subject to the capabilities reported for the concrete area.
    /// 
    /// In a Markdown-oriented provider this typically corresponds to a concrete
    /// document or to a nested section represented by a heading. The exact physical
    /// representation is provider-specific and MUST NOT be assumed by consumers.
    /// </summary>
    ContentContainer = 2

  }

}
