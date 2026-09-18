using System;

namespace KnowledgeManagement.SmartStandards {

  /// <summary>
  /// Provides ordered hierarchical access to a logical knowledge repository.
  /// 
  /// A knowledge repository exposes knowledge through absolute logical area paths.
  /// Areas form an ordered tree. The public contract deliberately abstracts from
  /// physical persistence concepts such as files, directories, Git repositories,
  /// Markdown documents, headings, notebooks, pages or database records.
  /// 
  /// Providers MAY internally use any such concepts. Providers MAY also expose
  /// virtual areas that have no direct physical representation, provided that their
  /// behavior follows this contract.
  /// 
  /// Area paths are absolute and begin with "/". The repository root itself is "/".
  /// Area paths are logical addresses and MUST NOT expose provider-specific physical
  /// storage paths.
  /// 
  /// Areas participate in textual content according to <see cref="ContentLevel"/>:
  /// <see cref="ContentLevel.BeyondContent"/> for pure navigation,
  /// <see cref="ContentLevel.ContentAggregation"/> for content-access scopes that do
  /// not own direct content, and <see cref="ContentLevel.ContentContainer"/> for
  /// concrete areas that may own direct textual content.
  /// 
  /// Area ordering is semantically significant. Providers MUST preserve the natural
  /// order of sibling areas. Recursive enumeration MUST use pre-order traversal:
  /// parents are returned before descendants and siblings remain in their natural
  /// provider-defined order.
  /// 
  /// Mutating operations are atomic from the consumer's perspective. An operation
  /// either completes fully or leaves the repository in its previous externally
  /// observable state.
  /// </summary>
  public interface IKnowledgeRepository {

    /// <summary>
    /// Returns logical area paths below the specified start area.
    /// 
    /// If <paramref name="recurse"/> is false, only direct children of
    /// <paramref name="startArea"/> are returned.
    /// 
    /// If <paramref name="recurse"/> is true, all descendants are returned using
    /// pre-order traversal. Each parent MUST appear before its descendants.
    /// Siblings MUST preserve their natural repository order.
    /// 
    /// Ordering is part of repository semantics. Providers MUST NOT arbitrarily
    /// reorder areas alphabetically, by creation time or by storage iteration order
    /// unless that order is explicitly defined as the provider's natural order for
    /// the affected structural level.
    /// 
    /// For Markdown-backed content structures, sibling order below the first
    /// content-bearing area MUST correspond to the order of the represented sections
    /// in the underlying content.
    /// 
    /// Virtual aggregation areas are returned in the same way as physical areas when
    /// they are part of the exposed logical tree. Their order MUST also be stable and
    /// deterministic.
    /// </summary>
    /// <param name="recurse">
    /// true to return the complete descendant tree; false to return direct children only.
    /// </param>
    /// <param name="startArea">
    /// The absolute logical area path at which enumeration starts. "/" represents
    /// the repository root.
    /// </param>
    /// <returns>
    /// The matching absolute logical area paths in stable natural hierarchical order.
    /// </returns>
    string[] GetAreas(bool recurse, string startArea = "/");

    /// <summary>
    /// Returns logical area paths that match the supplied keyword within the subtree
    /// rooted at <paramref name="startArea"/>.
    ///
    /// Matching semantics are provider-defined but MUST remain provider-neutral from the
    /// caller's perspective. A provider MAY match against logical area names, logical
    /// paths, direct textual content or another deterministic searchable representation.
    ///
    /// The operation is read-only and MUST NOT mutate, normalize or rewrite the
    /// underlying repository. Returned paths MUST be absolute logical area paths and MUST
    /// refer to areas that are addressable through the same repository instance.
    ///
    /// Providers SHOULD preserve their natural stable repository order in the returned
    /// result rather than introducing an unrelated ranking or storage order.
    /// </summary>
    /// <param name="keyword">The keyword or search term to match.</param>
    /// <param name="startArea">The absolute logical area path at which searching starts.</param>
    /// <returns>The matching absolute logical area paths in stable provider order.</returns>
    string[] GetAreasByKeyword(
      string keyword,
      string startArea = "/"
    );

    /// <summary>
    /// Returns the provider-neutral logical display name of the specified area.
    /// 
    /// Consumers MUST use this method when they need the human-readable identity of an
    /// area. They MUST NOT derive display names by decoding logical path segments because
    /// path-segment encoding is provider-specific.
    /// 
    /// The returned name represents only the addressed area's direct logical name and
    /// does not include ancestor names or physical storage syntax.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <returns>The direct logical display name of the addressed area.</returns>
    string GetAreaName(string area);

    /// <summary>
    /// Returns the effective capabilities of the specified logical area.
    /// 
    /// Capabilities describe what the current provider permits for this concrete area.
    /// They MAY depend on provider type, source configuration, permissions, physical
    /// storage restrictions, logical depth, virtual-area semantics or other
    /// provider-specific constraints.
    /// 
    /// <paramref name="contentLevel"/> describes how the area participates in content:
    /// 
    /// - <see cref="ContentLevel.BeyondContent"/> means the area is purely structural.
    /// - <see cref="ContentLevel.ContentAggregation"/> means aggregated content can be
    ///   obtained through the area, but the area owns no direct textual content.
    /// - <see cref="ContentLevel.ContentContainer"/> means the area may own direct
    ///   textual content and may additionally contain subordinate content.
    /// 
    /// <paramref name="supportsSubAreas"/> indicates whether the area can structurally
    /// contain direct child areas. This is intentionally independent from
    /// <paramref name="canAddSubAreas"/>. A read-only or preconfigured area may expose
    /// existing children while creation of additional children is forbidden.
    /// 
    /// <paramref name="canBeRenamed"/> indicates whether the addressed area itself may
    /// be renamed while preserving its direct content, descendants and sibling position.
    /// 
    /// <paramref name="canBeDeleted"/> indicates whether the addressed area itself and
    /// its complete descendant tree may be removed.
    /// 
    /// <paramref name="canAddSubAreas"/> indicates whether new direct child areas may be
    /// created below the area.
    /// 
    /// <paramref name="canAppendContent"/> indicates whether
    /// <see cref="TryAppendContent(string, string)"/> is generally supported.
    /// 
    /// For a <see cref="ContentLevel.ContentAggregation"/> area, direct unstructured
    /// text cannot be appended because the area owns no direct textual content.
    /// Appending may nevertheless be valid when the supplied content contains
    /// sufficient subordinate structure so that all content can be routed into
    /// subordinate content areas.
    /// 
    /// <paramref name="supportsResources"/> indicates whether textual content addressed
    /// through the area may use canonical <c>knowledge-resource:&lt;ResourceId&gt;</c>
    /// references and the repository can resolve the associated binary resources. Resource
    /// support does not make resources part of the area hierarchy.
    /// 
    /// <paramref name="canTruncate"/> indicates whether
    /// <see cref="TryTruncate(string)"/> may clear the content tree below the area while
    /// preserving the addressed area itself. This may be true for
    /// <see cref="ContentLevel.ContentAggregation"/> even though that area has no direct
    /// content, because truncation can remove its subordinate content structure.
    /// 
    /// Composite operations derive their effective permission from these capabilities.
    /// <see cref="TryReplace(string, string)"/> requires both truncate and append
    /// capability.
    /// 
    /// <see cref="TryMoveContent(string, string)"/> is intentionally NOT derived from
    /// truncate and append capabilities. Moving a logical content scope to a new parent
    /// is structurally different from copying textual payload into another area and then
    /// clearing the source. Whether a concrete pair of areas can participate in a move is
    /// therefore validated by the provider when the move is attempted.
    /// 
    /// A purely virtual aggregation area may legitimately report all mutation
    /// capabilities as false while still supporting aggregated reads.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <param name="contentLevel">The area's effective content level.</param>
    /// <param name="supportsSubAreas">Whether the area structurally supports child areas.</param>
    /// <param name="canBeRenamed">Whether the addressed area itself may be renamed.</param>
    /// <param name="canBeDeleted">Whether the addressed area and descendants may be deleted.</param>
    /// <param name="canAddSubAreas">Whether new direct child areas may be created.</param>
    /// <param name="canAppendContent">Whether hierarchical content append is generally supported.</param>
    /// <param name="canTruncate">Whether the area's complete subordinate content tree may be cleared.</param>
    /// <param name="supportsResources">
    /// Whether textual content addressed through this area can reference and resolve binary
    /// resources through the repository resource contract. This capability is independent
    /// from whether the area itself is structural or content-bearing.
    /// </param>
    void GetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate,
      out bool supportsResources
    );

    /// <summary>
    /// Returns the resources referenced by the resource-capable document scope containing
    /// the specified area.
    ///
    /// Every returned <see cref="KnowledgeResourceInfo.ResourceId"/> is opaque and
    /// provider-defined. Consumers MUST NOT interpret its value.
    /// </summary>
    KnowledgeResourceInfo[] GetResources(string area);

    /// <summary>
    /// Returns the complete binary content of one logical resource.
    /// </summary>
    /// <param name="resourceId">The opaque provider-defined resource identifier.</param>
    byte[] GetResourceContent(string resourceId);

    /// <summary>
    /// Atomically creates a new binary resource in the resource scope containing the
    /// supplied area.
    ///
    /// <paramref name="preferredFileName"/> is a non-binding descriptive hint. A provider
    /// may preserve it when its storage model supports human-readable file names, or may
    /// choose another physical representation when the supplied value is absent, invalid
    /// or collides with existing storage.
    /// </summary>
    bool TryAddResource(
      string area,
      string preferredFileName,
      string contentType,
      byte[] content,
      out string resourceId
    );

    /// <summary>
    /// Atomically replaces the binary content of an existing logical resource.
    /// </summary>
    bool TryReplaceResource(
      string resourceId,
      string contentType,
      byte[] content
    );

    /// <summary>
    /// Atomically deletes an unreferenced logical resource.
    ///
    /// Providers MUST reject deletion while exposed textual content still references
    /// <c>knowledge-resource:&lt;ResourceId&gt;</c>.
    /// </summary>
    bool TryDeleteResource(string resourceId);

    /// <summary>
    /// Determines whether the specified area currently owns non-empty direct textual
    /// content.
    /// 
    /// Direct content is content logically owned by the addressed area itself and
    /// explicitly excludes all content belonging to descendant areas.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, this method MUST always return
    /// false because aggregation areas never own direct textual content.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, the result reflects whether
    /// direct textual content currently exists.
    /// 
    /// For <see cref="ContentLevel.BeyondContent"/>, direct textual content is not
    /// semantically applicable and the provider should handle the request consistently
    /// with its general invalid-operation policy.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <returns>true if direct textual content exists; otherwise false.</returns>
    bool HasDirectContent(string area);

    /// <summary>
    /// Returns only the direct textual content logically owned by the specified area.
    /// Descendant content MUST NOT be included.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, this method MUST return
    /// <see cref="string.Empty"/> because aggregation areas do not own direct content.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, the provider returns the direct
    /// content owned by the area.
    /// 
    /// In a Markdown-oriented provider, if a content container is represented by a
    /// heading, direct content corresponds to the textual block after that heading and
    /// before the first subordinate heading. The heading used to identify the area is
    /// structural representation and is not itself direct content.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <returns>The direct textual content of the area, or an empty string for a content aggregation area.</returns>
    string GetDirectContent(string area);

    /// <summary>
    /// Returns the complete textual representation accessible through the specified
    /// content-capable area, including subordinate content areas in natural hierarchical
    /// order.
    /// 
    /// This method is valid for both <see cref="ContentLevel.ContentAggregation"/> and
    /// <see cref="ContentLevel.ContentContainer"/>.
    /// 
    /// For a content aggregation area, the returned value is assembled entirely from
    /// subordinate content because the aggregation area itself owns no direct content.
    /// The aggregation may represent a physical parent such as a notebook or may be
    /// virtual and synthesized from content located in multiple unrelated physical or
    /// logical source areas.
    /// 
    /// For a content container, the result includes the area's own direct content plus
    /// all subordinate content.
    /// 
    /// Aggregation MUST preserve the logical order exposed by the provider. The provider
    /// is responsible for rendering subordinate structure in a valid textual form.
    /// 
    /// A virtual cross-cutting aggregation MAY intentionally project selected content
    /// from multiple source branches, for example all "Conclusion" sections or all code
    /// example sections. Such an aggregation MUST be deterministic, read-consistent and
    /// explicit through its area identity and reported capabilities.
    /// 
    /// This operation is read-only and MUST NOT mutate, normalize or rewrite the
    /// underlying source repository.
    /// </summary>
    /// <param name="area">The absolute logical content-capable area path.</param>
    /// <returns>The complete aggregated textual content exposed through the area.</returns>
    string GetAggregatedContent(string area);

    /// <summary>
    /// Atomically deletes the specified logical area together with its complete
    /// descendant tree.
    /// 
    /// This operation removes the addressed area itself and is therefore intentionally
    /// different from <see cref="TryTruncate(string)"/>, which preserves the addressed
    /// area and only clears its content scope.
    /// 
    /// Physical deletion semantics are provider-specific. A provider may delete a
    /// directory, document, page, virtual definition or another backing artifact.
    /// Consumers observe only the logical result.
    /// 
    /// Virtual or synthesized areas may legitimately be non-deletable.
    /// 
    /// Unaffected siblings MUST retain their content and relative order.
    /// </summary>
    /// <param name="area">The absolute logical area path to delete.</param>
    /// <returns>true if the complete deletion succeeded atomically; otherwise false.</returns>
    bool TryDelete(string area);

    /// <summary>
    /// Atomically renames the specified logical area while preserving its direct
    /// content, complete descendant tree and sibling position.
    /// 
    /// Only the addressed area's own logical name changes.
    /// Descendants remain descendants of the renamed area and preserve their order.
    /// 
    /// The provider MUST reject a rename that would create an ambiguous logical sibling
    /// address.
    /// 
    /// A provider may map this operation to a directory rename, document rename, heading
    /// rename, page rename or another provider-specific operation.
    /// 
    /// Virtual aggregation areas may legitimately report that rename is unsupported.
    ///
    /// A provider-native rename may also change one or more opaque resource identifiers.
    /// Such changes MUST be returned through <paramref name="resourceIdChanges"/> so
    /// stateful adapters can update their mappings without recreating their own external
    /// resource identities.
    /// </summary>
    /// <param name="area">The absolute logical area path to rename.</param>
    /// <param name="newName">The new direct logical name.</param>
    /// <param name="resourceIdChanges">
    /// Receives provider resource identifier changes caused by the rename.
    /// </param>
    /// <returns>true if the rename succeeded atomically; otherwise false.</returns>
    bool TryRename(
      string area,
      string newName,
      out KnowledgeResourceIdChange[] resourceIdChanges
    );

    /// <summary>
    /// Atomically creates one new direct child area below the specified parent area.
    ///
    /// <paramref name="kind"/> communicates only the semantic role requested by the
    /// caller. It MUST NOT be encoded indirectly into <paramref name="name"/>. Consumers
    /// must therefore never need provider-specific naming conventions in order to request
    /// a structural child versus a content-bearing child.
    ///
    /// A provider maps the requested semantic kind to its own representation and may
    /// reject combinations that it cannot represent below the supplied parent.
    ///
    /// The new child is appended after existing direct children unless the provider's
    /// logical model explicitly defines another stable natural insertion rule. Existing
    /// siblings MUST NOT be reordered.
    ///
    /// The method creates only the requested direct child area. Complex subtrees and
    /// distributed additive changes should be performed through
    /// <see cref="TryAppendContent(string, string)"/>.
    /// </summary>
    /// <param name="area">The absolute logical parent area path.</param>
    /// <param name="name">The direct logical name of the new child area.</param>
    /// <param name="kind">The provider-neutral semantic kind requested for the new child.</param>
    /// <returns>true if the child area was created atomically; otherwise false.</returns>
    bool TryAddSubArea(
      string area,
      string name,
      KnowledgeAreaKind kind
    );


    /// <summary>
    /// Atomically performs a non-destructive sparse hierarchical merge of the supplied
    /// textual content into the specified content-capable target area.
    /// 
    /// This operation is intentionally stronger than physical end-of-file appending.
    /// The supplied content is interpreted as a relative logical content tree rooted at
    /// the target area.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>:
    /// 
    /// - Unstructured content appearing before the first structural child in the input
    ///   is appended to the target area's existing direct-content region.
    /// - Structured child blocks are merged recursively into matching direct children
    ///   or appended as newly created children when no match exists.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>:
    /// 
    /// - The incoming payload MUST NOT contain direct unstructured content for the
    ///   aggregation area itself because aggregation areas own no direct content.
    /// - The payload MAY contain subordinate structure.
    /// - That subordinate structure is merged recursively into existing descendants or
    ///   used to create new descendants when permitted.
    /// 
    /// For every incoming direct child at every recursion level:
    /// 
    /// 1. If a matching direct child already exists, the operation recursively merges
    ///    into that child.
    /// 2. If no matching direct child exists, the incoming child subtree is appended as
    ///    a new child after existing siblings.
    /// 3. Existing areas are never deleted, replaced, moved or reordered.
    /// 4. Existing sibling order is preserved exactly.
    /// 5. Newly created siblings preserve their incoming relative order.
    /// 
    /// Matching is always performed against direct children of the current merge target.
    /// It MUST NOT perform an implicit recursive global name search.
    /// 
    /// The incoming payload may therefore be sparse and may address multiple existing
    /// branches in one call. A single append can update several distributed descendant
    /// branches while also creating missing branches.
    /// 
    /// In providers that interpret structured text such as Markdown, structural levels
    /// in the payload MUST be interpreted relative to the target area's current logical
    /// depth. Physical levels may need rebasing. The logical parent-child relationships
    /// are authoritative.
    /// 
    /// When the payload contains canonical <c>knowledge-resource:&lt;ResourceId&gt;</c>
    /// references and the provider reports resource support for the affected content scope,
    /// every successfully committed reference MUST remain resolvable after the mutation. A
    /// provider whose physical resource storage is scope-local is responsible for creating
    /// any additional physical materialization required by the new content location without
    /// changing the referenced ResourceId.
    /// 
    /// Providers SHOULD avoid unnecessary rewrites of unaffected existing content.
    /// This is especially important for version-controlled providers where small logical
    /// mutations should ideally create small physical diffs.
    /// 
    /// If any part of the incoming payload is invalid, cannot be routed, violates naming
    /// constraints, exceeds provider-specific structural limits or cannot be persisted
    /// atomically, the complete operation MUST fail and leave the repository unchanged.
    /// 
    /// This method is intended as the primary efficient additive mutation primitive,
    /// especially for AI agents. A caller can address the nearest common ancestor of
    /// multiple intended changes and provide one sparse structured payload rather than
    /// performing many separate round trips.
    /// </summary>
    /// <param name="area">The absolute logical content-capable target area path.</param>
    /// <param name="content">The direct and/or structured textual content to merge.</param>
    /// <returns>true if the complete hierarchical append succeeded atomically; otherwise false.</returns>
    bool TryAppendContent(string area, string content);

    /// <summary>
    /// Atomically clears the complete content scope represented by the specified
    /// content-capable area while preserving the addressed area itself.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, truncation removes:
    /// 
    /// - all direct textual content owned by the area, and
    /// - all descendant areas together with their content.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, the area owns no direct content,
    /// so truncation removes its complete subordinate content structure while preserving
    /// the aggregation area itself.
    /// 
    /// The addressed area retains its own identity, name, parent relationship and sibling
    /// position.
    /// 
    /// Read-only or purely virtual aggregations may report truncation as unsupported.
    /// 
    /// The operation MUST be atomic. If any part of the represented content scope cannot
    /// be removed, no externally observable partial mutation may remain.
    /// </summary>
    /// <param name="area">The absolute logical content-capable area path.</param>
    /// <returns>true if truncation completed atomically; otherwise false.</returns>
    bool TryTruncate(string area);

    /// <summary>
    /// Atomically replaces the complete content scope represented by the specified area
    /// with the supplied new content.
    /// 
    /// The operation is semantically equivalent to a successful
    /// <see cref="TryTruncate(string)"/> followed by a successful
    /// <see cref="TryAppendContent(string, string)"/>, but MUST be implemented as one
    /// externally atomic mutation.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, the replacement payload may
    /// contain direct content and subordinate structure.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, the replacement payload MUST
    /// not contain direct unstructured content for the aggregation area itself. It may
    /// contain subordinate structure that reconstructs the aggregation's content scope.
    /// 
    /// Canonical <c>knowledge-resource:&lt;ResourceId&gt;</c> references in the replacement
    /// content are part of the textual contract. A resource-capable provider MUST preserve
    /// their validity and MUST NOT silently rewrite a ResourceId merely because the physical
    /// resource scope or storage location changes.
    /// 
    /// The addressed area itself is preserved.
    /// 
    /// Both truncate and append capabilities are required. The provider SHOULD validate
    /// the complete replacement payload before publishing any destructive changes.
    /// If any part cannot be completed, the previous repository state MUST remain
    /// externally unchanged.
    /// </summary>
    /// <param name="area">The absolute logical content-capable target area path.</param>
    /// <param name="newContent">The complete replacement content scope.</param>
    /// <returns>true if replacement completed atomically; otherwise false.</returns>
    bool TryReplace(string area, string newContent);

    /// <summary>
    /// Atomically reparents the complete logical scope addressed by
    /// <paramref name="contentAreaToMove"/> below
    /// <paramref name="newParentArea"/>.
    /// 
    /// This operation moves the addressed logical element itself. It does NOT copy the
    /// source payload into <paramref name="newParentArea"/>, does NOT replace or truncate
    /// <paramref name="newParentArea"/>, and does NOT interpret the new parent as the
    /// resulting address of the moved element.
    /// 
    /// The two parameters deliberately represent different structural levels:
    /// 
    /// - <paramref name="contentAreaToMove"/> identifies the existing logical child scope
    ///   whose parent relationship shall change.
    /// - <paramref name="newParentArea"/> identifies the existing logical area that shall
    ///   become the parent of that moved scope.
    /// 
    /// After a successful move, the moved scope keeps its logical name, its direct
    /// content and its complete descendant tree. Its former parent remains present and
    /// merely loses that child. The new parent remains present and unchanged except for
    /// gaining the moved child at the provider-defined insertion position.
    /// 
    /// Examples of the same abstract operation include:
    /// 
    /// - moving a Markdown section from one section to another section,
    /// - moving a section from one document to another document,
    /// - moving a document from one collection/folder scope to another,
    /// - moving an equivalent content scope in a database-, API-, Git- or virtual-backed
    ///   provider.
    /// 
    /// Resources referenced by the moved textual scope are part of the moved knowledge
    /// semantics. A provider MUST preserve their resolvability after the move.
    ///
    /// Resource identifiers are provider-owned and may change when the provider-native
    /// identity changes, for example when a FileBased resource path changes. Every such
    /// identifier transition MUST be returned through
    /// <paramref name="resourceIdChanges"/>. Resources that remain at their provider-native
    /// location keep their identifiers unchanged. Existing references outside the moved
    /// scope MUST remain valid as well.
    /// 
    /// The physical mechanism is entirely provider-specific. A provider may implement the
    /// operation through a filesystem move, a Markdown subtree rewrite, a database parent
    /// update, a Git rename, an API call or any other representation-specific mechanism.
    /// Consumers MUST NOT depend on any such representation detail.
    /// 
    /// <paramref name="newParentArea"/> does not need to own direct textual content. A
    /// purely structural or aggregating area may be a valid new parent when the provider
    /// can represent the moved scope below it. Conversely, a content-bearing area may be
    /// an invalid parent for a particular source type. The provider validates the concrete
    /// source/parent combination.
    /// 
    /// The provider MUST reject a move when the new parent is the moved area itself, lies
    /// inside the moved area's descendant subtree, cannot structurally contain the moved
    /// scope, or would create an ambiguous/colliding direct child identity.
    /// 
    /// Moving an area below its current parent MAY be treated as an idempotent successful
    /// no-op. Providers MUST preserve the natural relative order of unaffected siblings.
    /// 
    /// The complete operation MUST be atomic from the consumer's perspective. If the move
    /// cannot be completed, the previously observable logical tree and content MUST remain
    /// unchanged.
    /// </summary>
    /// <param name="contentAreaToMove">
    /// The absolute logical area path of the existing scope that shall change its parent.
    /// This path addresses the element being moved, not its former parent.
    /// </param>
    /// <param name="newParentArea">
    /// The absolute logical area path that shall become the parent of the moved scope.
    /// This area is not replaced, truncated or otherwise used as the destination content
    /// payload itself.
    /// </param>
    /// <param name="resourceIdChanges">
    /// Receives provider resource identifier changes caused by the move.
    /// </param>
    /// <returns>
    /// true if the complete reparenting operation succeeded atomically or was already in
    /// the requested parent relationship; otherwise false.
    /// </returns>
    bool TryMoveContent(
      string contentAreaToMove,
      string newParentArea,
      out KnowledgeResourceIdChange[] resourceIdChanges
    );

  }

}
