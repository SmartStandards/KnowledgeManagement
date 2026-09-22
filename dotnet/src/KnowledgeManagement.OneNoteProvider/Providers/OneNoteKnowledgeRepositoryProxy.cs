using HtmlAgilityPack;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using Markdig;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReverseMarkdown;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Projects SharePoint-hosted OneNote content into IKnowledgeRepository.
  /// Notebooks, section groups and sections are aggregations; pages and H1-H6 elements
  /// are hierarchical content containers.
  /// Existing pages are changed by targeted Graph PATCH operations rather than by
  /// destructive whole-page Markdown roundtrips.
  /// </summary>
  public sealed class OneNoteKnowledgeRepositoryProxy : IKnowledgeRepository, IDisposable {

    private const string _GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string _ResourcePrefix = "knowledge-resource:";
    private const string _AreaPrefix = "knowledge-area:";
    private const int _MaximumGraphRetryCount = 6;
    private const int _MinimumGraphRequestIntervalMilliseconds = 550;
    private const int _InitialBackoffMilliseconds = 2000;
    private const int _MaximumBackoffMilliseconds = 60000;
    private readonly object _SyncRoot = new object();
    private readonly object _GraphRequestSyncRoot = new object();
    private readonly ConcurrentDictionary<string, HtmlDocument> _PageCache = new ConcurrentDictionary<string, HtmlDocument>(StringComparer.Ordinal);
    private readonly bool _ReadOnly;
    private readonly string _NotebookName;
    private readonly HttpClient _HttpClient;
    private readonly Converter _HtmlToMarkdown;
    private readonly string _SiteId;
    private AreaNode _CachedTree;
    private NotebookInfo[] _CachedNotebooks;
    private DateTime _LastGraphRequestUtc = DateTime.MinValue;
    private bool _Disposed;

    /// <summary>
    /// Creates the OneNote repository.
    /// </summary>
    public OneNoteKnowledgeRepositoryProxy(
      string oneNoteOrSiteUrl,
      IOneNoteGraphAuthenticationProvider authenticationProvider,
      bool readOnly = true,
      string notebookName = "") {

      if (string.IsNullOrWhiteSpace(oneNoteOrSiteUrl)) {
        throw new ArgumentException("The OneNote or SharePoint URL is required.", nameof(oneNoteOrSiteUrl));
      }
      if (authenticationProvider == null) {
        throw new ArgumentNullException(nameof(authenticationProvider));
      }

      _ReadOnly = readOnly;
      if (notebookName == null) {
        _NotebookName = string.Empty;
      }
      else {
        _NotebookName = notebookName.Trim();
      }
      _HttpClient = authenticationProvider.CreateHttpClient(_ReadOnly);
      if (_HttpClient == null) {
        throw new InvalidOperationException("The authentication provider returned no HTTP client.");
      }

      Config configuration = new Config();
      configuration.UnknownTags = Config.UnknownTagsOption.Drop;
      configuration.GithubFlavored = true;
      configuration.RemoveComments = true;
      configuration.SmartHrefHandling = true;
      _HtmlToMarkdown = new Converter(configuration);
      _SiteId = this.ResolveSiteId(oneNoteOrSiteUrl);

      if (!string.IsNullOrWhiteSpace(_NotebookName)) {
        this.GetConfiguredNotebook(this.LoadNotebooks());
      }
    }

    /// <summary>
    /// Returns child areas in stable OneNote order and optionally all descendants in pre-order.
    /// </summary>
    public string[] GetAreas(bool recurse, string startArea = "/") {
      lock (_SyncRoot) {
        AreaNode root = this.BuildTree();
        AreaNode start = this.ResolveArea(root, startArea);
        this.EnsureAreaChildrenLoaded(start);
        List<string> result = new List<string>();
        foreach (AreaNode child in start.Children) {
          result.Add(child.Path);
          if (recurse) {
            this.AddPaths(child, result);
          }
        }
        return result.ToArray();
      }
    }

    /// <summary>
    /// Searches names, paths and direct heading content while preserving repository order.
    /// </summary>
    public string[] GetAreasByKeyword(string keyword, string startArea = "/") {
      if (string.IsNullOrWhiteSpace(keyword)) {
        return Array.Empty<string>();
      }
      lock (_SyncRoot) {
        AreaNode root = this.BuildTree();
        AreaNode start = this.ResolveArea(root, startArea);
        List<AreaNode> nodes = new List<AreaNode>();
        this.AddNodes(start, nodes);
        List<string> result = new List<string>();
        foreach (AreaNode node in nodes) {
          bool match = node.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
          if (!match && node.Kind == NodeKind.Heading) {
            match = this.GetHeadingDirectMarkdown(node).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
          }
          if (match) {
            result.Add(node.Path);
          }
        }
        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the direct display name of an area.
    /// </summary>
    public string GetAreaName(string area) {
      lock (_SyncRoot) {
        return this.ResolveArea(this.BuildTree(), area).Name;
      }
    }

    /// <summary>
    /// Returns effective capabilities for the concrete OneNote area.
    /// </summary>
    public void GetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate,
      out bool supportsResources) {

      lock (_SyncRoot) {
        AreaNode node = this.ResolveArea(this.BuildTree(), area);
        contentLevel = node.Level;
        supportsSubAreas = node.Kind != NodeKind.Heading || node.HeadingLevel < 6;
        supportsResources = node.Kind == NodeKind.Page || node.Kind == NodeKind.Heading;
        canBeRenamed = !_ReadOnly && node.Kind != NodeKind.Root && node.Kind != NodeKind.Notebook;
        canBeDeleted = !_ReadOnly && node.Kind != NodeKind.Root && node.Kind != NodeKind.Notebook;
        canAddSubAreas = !_ReadOnly && node.Kind != NodeKind.Root && (node.Kind != NodeKind.Heading || node.HeadingLevel < 6);
        canAppendContent = !_ReadOnly && (node.Kind == NodeKind.Page || node.Kind == NodeKind.Heading);
        canTruncate = canAppendContent;
      }
    }

    /// <summary>
    /// Returns resources of the page containing the addressed page or heading.
    /// </summary>
    public KnowledgeResourceInfo[] GetResources(string area) {
      lock (_SyncRoot) {
        AreaNode node = this.ResolveArea(this.BuildTree(), area);
        string pageId = this.RequirePageId(node);
        HtmlDocument document = this.LoadPage(pageId);
        List<KnowledgeResourceInfo> result = new List<KnowledgeResourceInfo>();
        HtmlNodeCollection resources = document.DocumentNode.SelectNodes("//img|//object");
        if (resources == null) {
          return result.ToArray();
        }
        foreach (HtmlNode resource in resources) {
          string nativeId = this.ExtractNativeResourceId(resource);
          if (string.IsNullOrWhiteSpace(nativeId)) {
            continue;
          }
          KnowledgeResourceInfo info = new KnowledgeResourceInfo();
          info.ResourceId = this.CreateResourceId(pageId, nativeId);
          info.FileName = resource.GetAttributeValue("data-attachment", resource.GetAttributeValue("alt", string.Empty));
          info.ContentType = resource.GetAttributeValue("data-fullres-src-type", resource.GetAttributeValue("type", "application/octet-stream"));
          info.Length = 0;
          result.Add(info);
        }
        return result.ToArray();
      }
    }

    /// <summary>
    /// Loads binary resource content through its opaque repository ID.
    /// </summary>
    public byte[] GetResourceContent(string resourceId) {
      ResourceIdentity identity = this.DecodeResourceId(resourceId);
      string url = _GraphBaseUrl + "/sites/" + Uri.EscapeDataString(_SiteId) + "/onenote/resources/" + Uri.EscapeDataString(identity.NativeId) + "/content";
      using (HttpResponseMessage response = this.SendGraphRequest(HttpMethod.Get, url, null)) {
        this.EnsureSuccess(response, "OneNote resource read failed.");
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
      }
    }

    /// <summary>
    /// Adds an image or attachment to the addressed page scope and returns its opaque ID.
    /// </summary>
    public bool TryAddResource(string area, string preferredFileName, string contentType, byte[] content, out string resourceId) {
      resourceId = string.Empty;
      if (_ReadOnly || content == null || content.Length == 0 || string.IsNullOrWhiteSpace(contentType)) {
        return false;
      }
      lock (_SyncRoot) {
        try {
          AreaNode node = this.ResolveArea(this.BuildTree(), area);
          string pageId = this.RequirePageId(node);
          string[] before = this.GetResources(area).Select((item) => item.ResourceId).ToArray();
          HtmlDocument document = this.LoadPageFresh(pageId);
          Target target = this.GetInsertionTarget(node, document);
          string part = "Resource" + Guid.NewGuid().ToString("N");
          string name;
          if (string.IsNullOrWhiteSpace(preferredFileName)) {
            name = "Resource.bin";
          }
          else {
            name = preferredFileName.Trim();
          }
          string html;
          if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) {
            html = "<img src=\"name:" + part + "\" alt=\"" + WebUtility.HtmlEncode(name) + "\" />";
          }
          else {
            html = "<object data-attachment=\"" + WebUtility.HtmlEncode(name) + "\" data=\"name:" + part + "\" type=\"" + WebUtility.HtmlEncode(contentType) + "\" />";
          }
          this.SendMultipartPatch(pageId, new Patch[] { new Patch(target.Id, "insert", target.Position, html) }, part, contentType, content);
          KnowledgeResourceInfo[] after = this.GetResources(area);
          KnowledgeResourceInfo created = after.FirstOrDefault((item) => !before.Contains(item.ResourceId, StringComparer.Ordinal));
          if (created == null) {
            return false;
          }
          resourceId = created.ResourceId;
          return true;
        }
        catch (HttpRequestException ex) {
          DevLogger.LogError(ex);
          return false;
        }
        catch (InvalidOperationException ex) {
          DevLogger.LogError(ex);
          return false;
        }
      }
    }

    /// <summary>
    /// Replaces an existing image/object resource using a multipart targeted PATCH.
    /// </summary>
    public bool TryReplaceResource(string resourceId, string contentType, byte[] content) {
      if (_ReadOnly || content == null || content.Length == 0) {
        return false;
      }
      try {
        ResourceIdentity identity = this.DecodeResourceId(resourceId);
        HtmlDocument document = this.LoadPageFresh(identity.PageId);
        HtmlNode node = this.FindResource(document, identity.NativeId);
        if (node == null) {
          return false;
        }
        string id = node.GetAttributeValue("id", string.Empty);
        if (string.IsNullOrWhiteSpace(id)) {
          return false;
        }
        string part = "Resource" + Guid.NewGuid().ToString("N");
        string html;
        if (node.Name.Equals("img", StringComparison.OrdinalIgnoreCase)) {
          html = "<img src=\"name:" + part + "\" alt=\"" + WebUtility.HtmlEncode(node.GetAttributeValue("alt", string.Empty)) + "\" />";
        }
        else {
          html = "<object data-attachment=\"" + WebUtility.HtmlEncode(node.GetAttributeValue("data-attachment", "attachment")) + "\" data=\"name:" + part + "\" type=\"" + WebUtility.HtmlEncode(contentType) + "\" />";
        }
        this.SendMultipartPatch(identity.PageId, new Patch[] { new Patch(id, "replace", string.Empty, html) }, part, contentType, content);
        return true;
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Rejects deletion while the OneNote resource is still referenced by a page element.
    /// OneNote does not expose a detached mutable resource collection.
    /// </summary>
    public bool TryDeleteResource(string resourceId) {
      if (_ReadOnly) {
        return false;
      }
      ResourceIdentity identity = this.DecodeResourceId(resourceId);
      return this.FindResource(this.LoadPage(identity.PageId), identity.NativeId) == null && false;
    }

    /// <summary>
    /// Returns whether a heading owns non-empty direct content.
    /// </summary>
    public bool HasDirectContent(string area) {
      AreaNode node = this.ResolveArea(this.BuildTree(), area);
      if (node.Level == ContentLevel.ContentAggregation) {
        return false;
      }
      if (node.Level == ContentLevel.BeyondContent) {
        throw new InvalidOperationException("Direct content is not applicable to this area.");
      }
      return !string.IsNullOrWhiteSpace(this.GetDirectMarkdown(node));
    }

    /// <summary>
    /// Returns direct content of a concrete OneNote content container.
    /// For a page this is the preamble before the first heading; for a heading it is the
    /// content following that heading before the first subordinate or sibling heading.
    /// Aggregation areas return an empty string.
    /// </summary>
    public string GetDirectContent(string area) {
      AreaNode node = this.ResolveArea(this.BuildTree(), area);
      if (node.Level == ContentLevel.ContentAggregation) {
        return string.Empty;
      }
      if (node.Level == ContentLevel.BeyondContent) {
        throw new InvalidOperationException("Direct content is not applicable to this area.");
      }
      return this.GetDirectMarkdown(node);
    }

    /// <summary>
    /// Returns deterministic Markdown aggregation of the addressed content scope.
    /// </summary>
    public string GetAggregatedContent(string area) {
      AreaNode node = this.ResolveArea(this.BuildTree(), area);
      if (node.Level == ContentLevel.BeyondContent) {
        throw new InvalidOperationException("Aggregated content is not applicable to this area.");
      }
      StringBuilder result = new StringBuilder();
      this.AppendAggregated(node, result);
      return result.ToString().Trim();
    }

    /// <summary>
    /// Deletes a heading subtree, page, section or section group.
    /// </summary>
    public bool TryDelete(string area) {
      if (_ReadOnly) {
        return false;
      }
      try {
        AreaNode node = this.ResolveArea(this.BuildTree(), area);
        if (node.Kind == NodeKind.Heading) {
          return this.DeleteHeading(node);
        }
        string collection;
        if (node.Kind == NodeKind.Page) {
          collection = "pages";
        }
        else if (node.Kind == NodeKind.Section) {
          collection = "sections";
        }
        else if (node.Kind == NodeKind.SectionGroup) {
          collection = "sectionGroups";
        }
        else {
          return false;
        }
        string deleteUrl = _GraphBaseUrl + "/sites/" + Uri.EscapeDataString(_SiteId) + "/onenote/" + collection + "/" + Uri.EscapeDataString(node.NativeId);
        using (HttpResponseMessage response = this.SendGraphRequest(HttpMethod.Delete, deleteUrl, null)) {
          return response.IsSuccessStatusCode;
        }
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Renames a heading or page. Section/container rename is conservatively rejected
    /// until its Graph endpoint semantics are verified for the deployed tenant.
    /// </summary>
    public bool TryRename(string area, string newName, out KnowledgeResourceIdChange[] resourceIdChanges) {
      resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();
      if (_ReadOnly || string.IsNullOrWhiteSpace(newName)) {
        return false;
      }
      try {
        AreaNode node = this.ResolveArea(this.BuildTree(), area);
        if (node.Kind == NodeKind.Page) {
          this.SendPatch(node.NativeId, new Patch[] { new Patch("title", "replace", string.Empty, WebUtility.HtmlEncode(newName.Trim())) });
          return true;
        }
        if (node.Kind != NodeKind.Heading) {
          return false;
        }
        HtmlDocument document = this.LoadPageFresh(node.PageId);
        HtmlNode heading = this.FindHeading(node, document);
        if (heading == null) {
          return false;
        }
        string id = heading.GetAttributeValue("id", string.Empty);
        string html = "<h" + node.HeadingLevel + ">" + WebUtility.HtmlEncode(newName.Trim()) + "</h" + node.HeadingLevel + ">";
        this.SendPatch(node.PageId, new Patch[] { new Patch(id, "replace", string.Empty, html) });
        return true;
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Creates content children as pages or headings. Structural creation is deliberately
    /// conservative because OneNote section-group creation support differs by endpoint.
    /// </summary>
    public bool TryAddSubArea(string area, string name, KnowledgeAreaKind kind) {
      if (_ReadOnly || string.IsNullOrWhiteSpace(name)) {
        return false;
      }
      try {
        AreaNode parent = this.ResolveArea(this.BuildTree(), area);
        if ((parent.Kind == NodeKind.Page || parent.Kind == NodeKind.Heading) && kind == KnowledgeAreaKind.Content) {
          int level;
          if (parent.Kind == NodeKind.Page) {
            level = 1;
          }
          else {
            level = parent.HeadingLevel + 1;
          }
          if (level > 6) {
            return false;
          }
          HtmlDocument document = this.LoadPageFresh(this.RequirePageId(parent));
          Target target = this.GetInsertionTarget(parent, document);
          string html = "<h" + level + ">" + WebUtility.HtmlEncode(name.Trim()) + "</h" + level + ">";
          this.SendPatch(this.RequirePageId(parent), new Patch[] { new Patch(target.Id, "insert", target.Position, html) });
          return true;
        }
        if (parent.Kind == NodeKind.Section && kind == KnowledgeAreaKind.Content) {
          return this.CreatePage(parent, name.Trim());
        }
        return false;
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Appends Markdown as a targeted fragment after the addressed page/heading scope.
    /// Existing page content outside the insertion point is not roundtripped.
    /// </summary>
    public bool TryAppendContent(string area, string content) {
      if (_ReadOnly) {
        return false;
      }
      if (string.IsNullOrWhiteSpace(content)) {
        return true;
      }
      try {
        AreaNode node = this.ResolveArea(this.BuildTree(), area);
        if (node.Kind != NodeKind.Page && node.Kind != NodeKind.Heading) {
          return false;
        }
        if (content.IndexOf(_ResourcePrefix, StringComparison.Ordinal) >= 0) {
          return false;
        }
        HtmlDocument document = this.LoadPageFresh(this.RequirePageId(node));
        Target target = this.GetInsertionTarget(node, document);
        string html = Markdown.ToHtml(content, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        this.SendPatch(this.RequirePageId(node), new Patch[] { new Patch(target.Id, "insert", target.Position, html) });
        return true;
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Clears the addressed heading subtree while preserving its heading.
    /// Page truncation is rejected because replacing every unrelated top-level OneNote
    /// element would violate the provider's non-destructive mutation rule.
    /// </summary>
    public bool TryTruncate(string area) {
      if (_ReadOnly) {
        return false;
      }
      AreaNode node = this.ResolveArea(this.BuildTree(), area);
      if (node.Kind != NodeKind.Heading) {
        return false;
      }
      return this.ReplaceHeadingBody(node, string.Empty);
    }

    /// <summary>
    /// Replaces only the addressed heading subtree, preserving unrelated page content.
    /// </summary>
    public bool TryReplace(string area, string newContent) {
      if (_ReadOnly) {
        return false;
      }
      AreaNode node = this.ResolveArea(this.BuildTree(), area);
      if (node.Kind != NodeKind.Heading || newContent.IndexOf(_ResourcePrefix, StringComparison.Ordinal) >= 0) {
        return false;
      }
      return this.ReplaceHeadingBody(node, newContent);
    }

    /// <summary>
    /// Moves a heading subtree within the same page using one targeted Graph PATCH.
    /// Cross-page moves are conservatively rejected because binary resources require a
    /// multipart recreation and resource-ID change mapping before source deletion.
    /// </summary>
    public bool TryMoveContent(string contentAreaToMove, string newParentArea, out KnowledgeResourceIdChange[] resourceIdChanges) {
      resourceIdChanges = Array.Empty<KnowledgeResourceIdChange>();
      if (_ReadOnly) {
        return false;
      }
      try {
        AreaNode root = this.BuildTree();
        AreaNode source = this.ResolveArea(root, contentAreaToMove);
        AreaNode target = this.ResolveArea(root, newParentArea);
        if (source.Kind != NodeKind.Heading || (target.Kind != NodeKind.Page && target.Kind != NodeKind.Heading)) {
          return false;
        }
        if (!source.PageId.Equals(this.RequirePageId(target), StringComparison.Ordinal)) {
          return false;
        }
        HtmlDocument document = this.LoadPageFresh(source.PageId);
        HtmlNode sourceHeading = this.FindHeading(source, document);
        if (sourceHeading == null) {
          return false;
        }
        HtmlNode[] subtree = this.GetSubtree(sourceHeading, source.HeadingLevel);
        int targetLevel;
        if (target.Kind == NodeKind.Page) {
          targetLevel = 1;
        }
        else {
          targetLevel = target.HeadingLevel + 1;
        }
        string movedHtml = this.Rebase(subtree, source.HeadingLevel, targetLevel);
        Target insertion = this.GetInsertionTarget(target, document);
        List<Patch> patches = new List<Patch>();
        patches.Add(new Patch(insertion.Id, "insert", insertion.Position, movedHtml));
        foreach (HtmlNode element in subtree) {
          string id = element.GetAttributeValue("id", string.Empty);
          if (!string.IsNullOrWhiteSpace(id)) {
            patches.Add(new Patch(id, "replace", string.Empty, "<p></p>"));
          }
        }
        this.SendPatch(source.PageId, patches.ToArray());
        return true;
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Releases the authentication strategy's HTTP client.
    /// </summary>
    public void Dispose() {
      if (_Disposed) {
        return;
      }
      _HttpClient.Dispose();
      _Disposed = true;
    }

    /// <summary>
    /// Builds the logical tree from fresh Graph data.
    /// </summary>
    private AreaNode BuildTree() {
      this.EnsureNotDisposed();
      lock (_SyncRoot) {
        if (_CachedTree != null) {
          DevLogger.LogTrace(0, 99999, "OneNote repository tree cache hit.");
          return _CachedTree;
        }

        DevLogger.LogTrace(0, 99999, "OneNote repository tree cache miss. Loading notebook metadata from Microsoft Graph.");
        NotebookInfo[] notebooks = this.LoadNotebooks();
        if (!string.IsNullOrWhiteSpace(_NotebookName)) {
          NotebookInfo configuredNotebook = this.GetConfiguredNotebook(notebooks);
          _CachedTree = new AreaNode(NodeKind.Root, "/", "/", configuredNotebook.Id, string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
          return _CachedTree;
        }

        AreaNode root = new AreaNode(NodeKind.Root, "/", "/", string.Empty, string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
        foreach (NotebookInfo notebook in notebooks) {
          this.AddChild(root, NodeKind.Notebook, notebook.Name, notebook.Id, string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
        }
        root.ChildrenLoaded = true;
        _CachedTree = root;
        return _CachedTree;
      }
    }

    /// <summary>
    /// Loads sections and section groups of a notebook.
    /// </summary>
    private void LoadNotebook(AreaNode parent, NotebookInfo notebook) {
      foreach (JObject section in this.GetValues(this.GetJson(this.SiteOneNoteUrl("/notebooks/" + Uri.EscapeDataString(notebook.Id) + "/sections")))) {
        this.AddChild(parent, NodeKind.Section, this.Display(section, "displayName", "Unnamed Section"), this.Required(section, "id"), string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
      }
      foreach (JObject group in this.GetValues(this.GetJson(this.SiteOneNoteUrl("/notebooks/" + Uri.EscapeDataString(notebook.Id) + "/sectionGroups")))) {
        this.AddChild(parent, NodeKind.SectionGroup, this.Display(group, "displayName", "Unnamed Group"), this.Required(group, "id"), string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
      }
    }

    /// <summary>
    /// Loads direct sections and section groups of an existing section-group node.
    /// </summary>
    private void LoadSectionGroupChildren(AreaNode group) {
      foreach (JObject section in this.GetValues(this.GetJson(this.SiteOneNoteUrl("/sectionGroups/" + Uri.EscapeDataString(group.NativeId) + "/sections")))) {
        this.AddChild(group, NodeKind.Section, this.Display(section, "displayName", "Unnamed Section"), this.Required(section, "id"), string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
      }
      foreach (JObject child in this.GetValues(this.GetJson(this.SiteOneNoteUrl("/sectionGroups/" + Uri.EscapeDataString(group.NativeId) + "/sectionGroups")))) {
        this.AddChild(group, NodeKind.SectionGroup, this.Display(child, "displayName", "Unnamed Group"), this.Required(child, "id"), string.Empty, string.Empty, 0, ContentLevel.ContentAggregation);
      }
    }

    /// <summary>
    /// Loads page metadata of one section without downloading page HTML.
    /// </summary>
    private void LoadSectionChildren(AreaNode section) {
      foreach (JObject page in this.GetValues(this.GetJson(this.SiteOneNoteUrl("/sections/" + Uri.EscapeDataString(section.NativeId) + "/pages")))) {
        string pageId = this.Required(page, "id");
        this.AddChild(section, NodeKind.Page, this.Display(page, "title", "Untitled"), pageId, pageId, string.Empty, 0, ContentLevel.ContentContainer);
      }
    }

    /// <summary>
    /// Projects H1-H6 into hierarchical content areas.
    /// </summary>
    private void LoadHeadings(AreaNode page, HtmlDocument document) {
      HtmlNodeCollection headings = document.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6");
      if (headings == null) {
        return;
      }
      AreaNode[] parents = new AreaNode[7];
      parents[0] = page;
      foreach (HtmlNode heading in headings) {
        int level = this.HeadingLevel(heading);
        int parentLevel = level - 1;
        while (parentLevel > 0 && parents[parentLevel] == null) {
          parentLevel--;
        }
        AreaNode parent;
        if (parents[parentLevel] == null) {
          parent = page;
        }
        else {
          parent = parents[parentLevel];
        }
        string name = WebUtility.HtmlDecode(heading.InnerText).Trim();
        if (string.IsNullOrWhiteSpace(name)) {
          name = "Untitled";
        }
        AreaNode child = this.AddChild(parent, NodeKind.Heading, name, heading.GetAttributeValue("id", string.Empty), page.NativeId, this.Fingerprint(heading), level, ContentLevel.ContentContainer);
        parents[level] = child;
        for (int index = level + 1; index <= 6; index++) {
          parents[index] = null;
        }
      }
    }

    /// <summary>
    /// Returns the direct Markdown content owned by a concrete OneNote content container.
    /// </summary>
    private string GetDirectMarkdown(AreaNode node) {
      if (node.Kind == NodeKind.Page) {
        return this.GetPageDirectMarkdown(node);
      }
      if (node.Kind == NodeKind.Heading) {
        return this.GetHeadingDirectMarkdown(node);
      }
      return string.Empty;
    }

    /// <summary>
    /// Returns the OneNote page preamble before its first projected H1-H6 heading.
    /// The page itself is a content container, analogous to a Markdown document.
    /// </summary>
    private string GetPageDirectMarkdown(AreaNode node) {
      HtmlDocument document = this.LoadPage(node.PageId);
      HtmlNode body = document.DocumentNode.SelectSingleNode("//body");
      if (body == null) {
        return string.Empty;
      }

      StringBuilder html = new StringBuilder();
      foreach (HtmlNode child in body.ChildNodes) {
        if (this.IsHeading(child)) {
          break;
        }
        html.Append(child.OuterHtml);
      }

      return this.HtmlToKnowledgeMarkdown(node.PageId, html.ToString());
    }

    /// <summary>
    /// Returns only nodes directly belonging to a heading before its first child heading.
    /// </summary>
    private string GetHeadingDirectMarkdown(AreaNode node) {
      HtmlDocument document = this.LoadPage(node.PageId);
      HtmlNode heading = this.FindHeading(node, document);
      if (heading == null) {
        throw new InvalidOperationException("The OneNote heading could not be resolved.");
      }
      StringBuilder html = new StringBuilder();
      HtmlNode current = heading.NextSibling;
      while (current != null && !this.IsHeading(current)) {
        html.Append(current.OuterHtml);
        current = current.NextSibling;
      }
      return this.HtmlToKnowledgeMarkdown(node.PageId, html.ToString());
    }

    /// <summary>
    /// Converts OneNote HTML to canonical Markdown and provider-neutral resource references.
    /// </summary>
    private string HtmlToKnowledgeMarkdown(string pageId, string html) {
      HtmlDocument document = new HtmlDocument();
      document.LoadHtml(html);

      // OneNote frequently emits presentation/layout HTML that is technically valid, but
      // produces extremely noisy Markdown. Normalize provider-specific markup before the
      // generic HTML-to-Markdown conversion. The authoritative page HTML remains untouched.
      this.NormalizeOneNoteHtmlForMarkdown(document);

      Dictionary<string, string> replacements = new Dictionary<string, string>();
      HtmlNodeCollection resources = document.DocumentNode.SelectNodes("//img|//object");
      if (resources != null) {
        int index = 0;
        foreach (HtmlNode resource in resources.ToArray()) {
          string nativeId = this.ExtractNativeResourceId(resource);
          if (string.IsNullOrWhiteSpace(nativeId)) {
            continue;
          }
          index++;
          string marker = "ZZZRESOURCE" + index.ToString("0000") + "ZZZ";
          string alt = resource.GetAttributeValue("alt", resource.GetAttributeValue("data-attachment", "resource"));
          replacements[marker] = "![" + alt.Replace("]", "\\]") + "](" + _ResourcePrefix + this.CreateResourceId(pageId, nativeId) + ")";
          resource.ParentNode.ReplaceChild(document.CreateTextNode(marker), resource);
        }
      }
      string markdown = _HtmlToMarkdown.Convert(document.DocumentNode.InnerHtml);
      foreach (KeyValuePair<string, string> replacement in replacements) {
        markdown = markdown.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
      }

      // ReverseMarkdown can preserve BR elements in some OneNote fragments, especially
      // when they originate from nested layout markup. At this point the semantic HTML
      // conversion is complete, so replacing only residual BR tags is safe and cannot
      // alter the source DOM or remove surrounding content.
      // ReverseMarkdown deliberately emits HTML BR elements inside Markdown table cells
      // because a physical line break would terminate the current table row. Any BR that
      // survives the DOM normalization therefore has to remain inside the current cell.
      markdown = Regex.Replace(
        markdown,
        @"<br\s*/?>",
        " ",
        RegexOptions.IgnoreCase
      );

      return markdown.Trim();
    }

    /// <summary>
    /// Normalizes OneNote-specific presentation HTML before it is projected to Markdown.
    /// This method operates only on the temporary read projection and never modifies the
    /// authoritative OneNote page DOM used for targeted mutations.
    /// </summary>
    private void NormalizeOneNoteHtmlForMarkdown(HtmlDocument document) {
      if (document == null) {
        throw new ArgumentNullException(nameof(document));
      }

      this.RemoveOneNoteProjectionAttributes(document);
      this.UnwrapSingleCellLayoutTables(document);
      this.NormalizeOneNoteLinks(document);
      this.NormalizeHtmlLists(document);
      this.NormalizeHtmlBreaks(document);
      this.RemoveEmptyPresentationNodes(document);
    }


    /// <summary>
    /// Converts OneNote hyperlinks into normal Markdown links and translates links to already
    /// known repository pages or headings into provider-neutral knowledge-area references.
    /// Resolution is deliberately cache-only: rendering content must never trigger repository-wide
    /// Graph discovery merely because a page contains cross references.
    /// </summary>
    private void NormalizeOneNoteLinks(HtmlDocument document) {
      HtmlNodeCollection links = document.DocumentNode.SelectNodes("//a");
      if (links == null) {
        return;
      }

      foreach (HtmlNode link in links.ToArray()) {
        string href = WebUtility.HtmlDecode(link.GetAttributeValue("href", string.Empty)).Trim();
        string label = WebUtility.HtmlDecode(link.InnerText).Trim();
        if (string.IsNullOrWhiteSpace(label)) {
          label = href;
        }

        string target = href;
        if (href.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) {
          string resolvedArea = this.TryResolveOneNoteLinkToKnownArea(href);
          if (!string.IsNullOrWhiteSpace(resolvedArea)) {
            target = _AreaPrefix + resolvedArea;
          }
        }

        string markdown = "[" + this.EscapeMarkdownLinkText(label) + "](" + this.EscapeMarkdownLinkTarget(target) + ")";
        HtmlNode replacement = document.CreateTextNode(markdown);
        link.ParentNode.ReplaceChild(replacement, link);
      }
    }

    /// <summary>
    /// Resolves a native OneNote URL against the repository tree that is already materialized.
    /// No Graph request is issued from this method. Page-id is authoritative; object-id is used
    /// as an optional refinement when the target page headings are already known.
    /// </summary>
    private string TryResolveOneNoteLinkToKnownArea(string oneNoteUrl) {
      string pageId = this.ExtractOneNoteQueryIdentifier(oneNoteUrl, "page-id");
      if (string.IsNullOrWhiteSpace(pageId)) {
        return string.Empty;
      }

      AreaNode page = this.FindLoadedNodeByPageId(this.BuildTree(), pageId);
      if (page == null) {
        return string.Empty;
      }

      string objectId = this.ExtractOneNoteQueryIdentifier(oneNoteUrl, "object-id");
      if (!string.IsNullOrWhiteSpace(objectId) && page.ChildrenLoaded) {
        AreaNode heading = this.FindLoadedHeadingByObjectId(page, objectId);
        if (heading != null) {
          return heading.Path;
        }
      }

      return page.Path;
    }

    /// <summary>
    /// Extracts a OneNote identifier from the URL fragment/query payload.
    /// </summary>
    private string ExtractOneNoteQueryIdentifier(string oneNoteUrl, string parameterName) {
      Match match = Regex.Match(
        oneNoteUrl,
        "(?:[?&#]|^)" + Regex.Escape(parameterName) + "=\\{?(?<value>[A-Fa-f0-9-]+)\\}?",
        RegexOptions.IgnoreCase
      );
      if (!match.Success) {
        return string.Empty;
      }
      return match.Groups["value"].Value.Trim();
    }

    /// <summary>
    /// Searches only nodes whose children have already been materialized. This preserves the
    /// lazy-loading guarantee and prevents link conversion from turning into a Graph crawler.
    /// </summary>
    private AreaNode FindLoadedNodeByPageId(AreaNode node, string pageId) {
      if (node.Kind == NodeKind.Page && node.PageId.Equals(pageId, StringComparison.OrdinalIgnoreCase)) {
        return node;
      }
      if (!node.ChildrenLoaded) {
        return null;
      }
      foreach (AreaNode child in node.Children) {
        AreaNode match = this.FindLoadedNodeByPageId(child, pageId);
        if (match != null) {
          return match;
        }
      }
      return null;
    }

    /// <summary>
    /// Searches already projected headings for a OneNote object identifier.
    /// </summary>
    private AreaNode FindLoadedHeadingByObjectId(AreaNode node, string objectId) {
      foreach (AreaNode child in node.Children) {
        if (child.Kind == NodeKind.Heading && child.NativeId.IndexOf(objectId, StringComparison.OrdinalIgnoreCase) >= 0) {
          return child;
        }
        if (child.ChildrenLoaded) {
          AreaNode match = this.FindLoadedHeadingByObjectId(child, objectId);
          if (match != null) {
            return match;
          }
        }
      }
      return null;
    }

    /// <summary>
    /// Converts UL and OL elements to explicit Markdown list text before ReverseMarkdown sees
    /// them. This avoids raw HTML leakage from OneNote list fragments embedded in layout nodes.
    /// </summary>
    private void NormalizeHtmlLists(HtmlDocument document) {
      HtmlNodeCollection lists = document.DocumentNode.SelectNodes("//ul|//ol");
      if (lists == null) {
        return;
      }

      HtmlNode[] orderedLists = lists.ToArray().OrderByDescending((node) => this.GetHtmlDepth(node)).ToArray();
      foreach (HtmlNode list in orderedLists) {
        if (list.ParentNode == null) {
          continue;
        }

        bool numbered = list.Name.Equals("ol", StringComparison.OrdinalIgnoreCase);
        StringBuilder markdown = new StringBuilder();
        HtmlNodeCollection items = list.SelectNodes("./li");
        if (items != null) {
          int number = 1;
          foreach (HtmlNode item in items) {
            string text = WebUtility.HtmlDecode(item.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(text)) {
              continue;
            }
            if (numbered) {
              markdown.Append(number.ToString()).Append(". ");
              number++;
            }
            else {
              markdown.Append("- ");
            }
            markdown.AppendLine(text);
          }
        }

        list.ParentNode.ReplaceChild(document.CreateTextNode("\n" + markdown.ToString() + "\n"), list);
      }
    }

    /// <summary>
    /// Converts HTML BR elements to textual line breaks before generic conversion.
    /// </summary>
    private void NormalizeHtmlBreaks(HtmlDocument document) {
      HtmlNodeCollection breaks = document.DocumentNode.SelectNodes("//br");
      if (breaks == null) {
        return;
      }
      foreach (HtmlNode lineBreak in breaks.ToArray()) {
        if (lineBreak.ParentNode != null) {
          lineBreak.ParentNode.ReplaceChild(document.CreateTextNode("\n"), lineBreak);
        }
      }
    }

    /// <summary>
    /// Returns the DOM depth of a node so nested lists can be normalized inside-out.
    /// </summary>
    private int GetHtmlDepth(HtmlNode node) {
      int depth = 0;
      HtmlNode current = node.ParentNode;
      while (current != null) {
        depth++;
        current = current.ParentNode;
      }
      return depth;
    }

    /// <summary>
    /// Escapes Markdown-significant characters inside a link label.
    /// </summary>
    private string EscapeMarkdownLinkText(string value) {
      return value.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
    }

    /// <summary>
    /// Escapes a Markdown link target without changing provider-neutral URI schemes.
    /// </summary>
    private string EscapeMarkdownLinkTarget(string value) {
      return value.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");
    }

    /// <summary>
    /// Removes OneNote-generated identifiers and presentation metadata that have no meaning
    /// in the provider-neutral Markdown representation. Semantic attributes such as href,
    /// src, alt, title and attachment metadata are deliberately preserved.
    /// </summary>
    private void RemoveOneNoteProjectionAttributes(HtmlDocument document) {
      HtmlNodeCollection nodes = document.DocumentNode.SelectNodes("//*");
      if (nodes == null) {
        return;
      }

      foreach (HtmlNode node in nodes.ToArray()) {
        string[] removableAttributes = node.Attributes
          .Where((attribute) => this.IsProjectionOnlyAttribute(attribute.Name))
          .Select((attribute) => attribute.Name)
          .ToArray();

        foreach (string attributeName in removableAttributes) {
          node.Attributes.Remove(attributeName);
        }
      }
    }

    /// <summary>
    /// Determines whether an HTML attribute belongs only to OneNote's visual/editor
    /// representation and can therefore be discarded from the Markdown projection.
    /// </summary>
    private bool IsProjectionOnlyAttribute(string attributeName) {
      if (string.IsNullOrWhiteSpace(attributeName)) {
        return false;
      }

      if (string.Equals(attributeName, "id", StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
      if (string.Equals(attributeName, "style", StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
      if (string.Equals(attributeName, "class", StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
      if (attributeName.StartsWith("data-tag", StringComparison.OrdinalIgnoreCase)) {
        return true;
      }

      return false;
    }

    /// <summary>
    /// Unwraps tables that contain exactly one logical cell. OneNote commonly uses such
    /// tables as layout containers. Exposing them as Markdown tables creates artificial
    /// pipe syntax and can force lists contained in the cell to remain as raw HTML.
    /// Genuine multi-cell tables are preserved for normal Markdown table conversion.
    /// </summary>
    private void UnwrapSingleCellLayoutTables(HtmlDocument document) {
      HtmlNodeCollection tables = document.DocumentNode.SelectNodes("//table");
      if (tables == null) {
        return;
      }

      foreach (HtmlNode table in tables.ToArray()) {
        HtmlNodeCollection cells = table.SelectNodes(".//td|.//th");
        if (cells == null || cells.Count != 1) {
          continue;
        }

        HtmlNode cell = cells[0];
        HtmlNode parent = table.ParentNode;
        if (parent == null) {
          continue;
        }

        foreach (HtmlNode child in cell.ChildNodes.ToArray()) {
          parent.InsertBefore(child, table);
        }
        parent.RemoveChild(table);
      }
    }

    /// <summary>
    /// Removes empty presentation-only elements that otherwise create superfluous Markdown
    /// whitespace after OneNote layout containers have been normalized.
    /// </summary>
    private void RemoveEmptyPresentationNodes(HtmlDocument document) {
      HtmlNodeCollection nodes = document.DocumentNode.SelectNodes("//span|//div|//p");
      if (nodes == null) {
        return;
      }

      foreach (HtmlNode node in nodes.ToArray().Reverse()) {
        if (node.ParentNode == null) {
          continue;
        }
        if (node.ChildNodes.Any((child) => child.NodeType == HtmlNodeType.Element)) {
          continue;
        }
        if (!string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(node.InnerText))) {
          continue;
        }

        node.ParentNode.RemoveChild(node);
      }
    }

    /// <summary>
    /// Appends a complete hierarchical Markdown representation.
    /// </summary>
    private void AppendAggregated(AreaNode node, StringBuilder result) {
      if (node.Kind == NodeKind.Page) {
        string direct = this.GetPageDirectMarkdown(node);
        if (!string.IsNullOrWhiteSpace(direct)) {
          result.AppendLine(direct);
          result.AppendLine();
        }
      }
      else if (node.Kind == NodeKind.Heading) {
        result.Append(new string('#', node.HeadingLevel));
        result.Append(' ');
        result.AppendLine(node.Name);
        result.AppendLine();
        string direct = this.GetHeadingDirectMarkdown(node);
        if (!string.IsNullOrWhiteSpace(direct)) {
          result.AppendLine(direct);
          result.AppendLine();
        }
      }
      this.EnsureAreaChildrenLoaded(node);
      foreach (AreaNode child in node.Children) {
        this.AppendAggregated(child, result);
      }
    }

    /// <summary>
    /// Replaces the body and descendants of one heading without touching unrelated siblings.
    /// </summary>
    private bool ReplaceHeadingBody(AreaNode node, string markdown) {
      try {
        HtmlDocument document = this.LoadPageFresh(node.PageId);
        HtmlNode heading = this.FindHeading(node, document);
        if (heading == null) {
          return false;
        }
        HtmlNode[] subtree = this.GetSubtree(heading, node.HeadingLevel);
        List<Patch> patches = new List<Patch>();
        for (int index = 1; index < subtree.Length; index++) {
          string id = subtree[index].GetAttributeValue("id", string.Empty);
          if (!string.IsNullOrWhiteSpace(id)) {
            patches.Add(new Patch(id, "replace", string.Empty, "<p></p>"));
          }
        }
        if (!string.IsNullOrWhiteSpace(markdown)) {
          string headingId = heading.GetAttributeValue("id", string.Empty);
          string html = Markdown.ToHtml(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
          patches.Add(new Patch(headingId, "insert", "after", html));
        }
        if (patches.Count > 0) {
          this.SendPatch(node.PageId, patches.ToArray());
        }
        return true;
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (InvalidOperationException ex) {
        DevLogger.LogError(ex);
        return false;
      }
    }

    /// <summary>
    /// Deletes one heading subtree using targeted replacements.
    /// </summary>
    private bool DeleteHeading(AreaNode node) {
      HtmlDocument document = this.LoadPageFresh(node.PageId);
      HtmlNode heading = this.FindHeading(node, document);
      if (heading == null) {
        return false;
      }
      List<Patch> patches = new List<Patch>();
      foreach (HtmlNode element in this.GetSubtree(heading, node.HeadingLevel)) {
        string id = element.GetAttributeValue("id", string.Empty);
        if (!string.IsNullOrWhiteSpace(id)) {
          patches.Add(new Patch(id, "replace", string.Empty, "<p></p>"));
        }
      }
      if (patches.Count == 0) {
        return false;
      }
      this.SendPatch(node.PageId, patches.ToArray());
      return true;
    }

    /// <summary>
    /// Creates a new page with one initial H1 content area.
    /// </summary>
    private bool CreatePage(AreaNode section, string name) {
      string html = "<!DOCTYPE html><html><head><title>" + WebUtility.HtmlEncode(name) + "</title></head><body><h1>" + WebUtility.HtmlEncode(name) + "</h1></body></html>";
      byte[] pageBytes = Encoding.UTF8.GetBytes(html);
      string createUrl = this.SiteOneNoteUrl("/sections/" + Uri.EscapeDataString(section.NativeId) + "/pages");
      using (HttpResponseMessage response = this.SendGraphRequest(HttpMethod.Post, createUrl, () => {
        ByteArrayContent retryContent = new ByteArrayContent(pageBytes);
        retryContent.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        return retryContent;
      })) {
        this.EnsureSuccess(response, "OneNote page creation failed.");
      }
      return true;
    }

    /// <summary>
    /// Returns the insertion boundary after the addressed logical subtree.
    /// </summary>
    private Target GetInsertionTarget(AreaNode node, HtmlDocument document) {
      if (node.Kind == NodeKind.Page) {
        HtmlNode body = document.DocumentNode.SelectSingleNode("//body");
        if (body == null) {
          throw new InvalidOperationException("The OneNote page contains no body.");
        }
        HtmlNode last = body.ChildNodes.LastOrDefault((item) => item.NodeType == HtmlNodeType.Element && !string.IsNullOrWhiteSpace(item.GetAttributeValue("id", string.Empty)));
        if (last == null) {
          return new Target("body", "after");
        }
        return new Target(last.GetAttributeValue("id", string.Empty), "after");
      }
      HtmlNode heading = this.FindHeading(node, document);
      if (heading == null) {
        throw new InvalidOperationException("The OneNote heading could not be resolved.");
      }
      HtmlNode[] subtree = this.GetSubtree(heading, node.HeadingLevel);
      for (int index = subtree.Length - 1; index >= 0; index--) {
        string id = subtree[index].GetAttributeValue("id", string.Empty);
        if (!string.IsNullOrWhiteSpace(id)) {
          return new Target(id, "after");
        }
      }
      throw new InvalidOperationException("No generated OneNote ID exists at the insertion boundary.");
    }

    /// <summary>
    /// Returns all sibling elements belonging to a heading subtree.
    /// </summary>
    private HtmlNode[] GetSubtree(HtmlNode heading, int level) {
      List<HtmlNode> result = new List<HtmlNode>();
      result.Add(heading);
      HtmlNode current = heading.NextSibling;
      while (current != null) {
        if (this.IsHeading(current) && this.HeadingLevel(current) <= level) {
          break;
        }
        if (current.NodeType == HtmlNodeType.Element) {
          result.Add(current);
        }
        current = current.NextSibling;
      }
      return result.ToArray();
    }

    /// <summary>
    /// Rebases heading levels in a moved subtree and removes stale generated IDs.
    /// </summary>
    private string Rebase(HtmlNode[] nodes, int sourceLevel, int targetLevel) {
      StringBuilder result = new StringBuilder();
      foreach (HtmlNode source in nodes) {
        HtmlNode clone = source.CloneNode(true);
        if (this.IsHeading(clone)) {
          int level = targetLevel + this.HeadingLevel(clone) - sourceLevel;
          if (level < 1 || level > 6) {
            throw new InvalidOperationException("The move would exceed H1-H6.");
          }
          clone.Name = "h" + level;
        }
        clone.Attributes.Remove("id");
        result.Append(clone.OuterHtml);
      }
      return result.ToString();
    }

    /// <summary>
    /// Loads current OneNote HTML including generated element IDs.
    /// </summary>
    private HtmlDocument LoadPage(string pageId) {
      if (_PageCache.TryGetValue(pageId, out HtmlDocument cached)) {
        DevLogger.LogTrace(0, 99999, "OneNote page cache hit: " + pageId);
        return cached;
      }

      return this.LoadPageFresh(pageId);
    }

    /// <summary>
    /// Loads the current OneNote HTML from Graph and replaces the cached page snapshot.
    /// This method is used before mutations because Graph-generated element IDs can change
    /// after every page update.
    /// </summary>
    private HtmlDocument LoadPageFresh(string pageId) {
      DevLogger.LogTrace(0, 99999, "OneNote page cache miss/fresh load: " + pageId);
      string html = this.GetString(this.SiteOneNoteUrl("/pages/" + Uri.EscapeDataString(pageId) + "/content?includeIDs=true"));
      HtmlDocument document = new HtmlDocument();
      document.LoadHtml(html);
      _PageCache[pageId] = document;
      return document;
    }

    /// <summary>
    /// Resolves a heading after a fresh page read. Generated IDs are intentionally not trusted across writes.
    /// </summary>
    private HtmlNode FindHeading(AreaNode node, HtmlDocument document) {
      HtmlNodeCollection headings = document.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6");
      if (headings == null) {
        return null;
      }
      foreach (HtmlNode heading in headings) {
        if (this.HeadingLevel(heading) == node.HeadingLevel && this.Fingerprint(heading).Equals(node.Fingerprint, StringComparison.Ordinal)) {
          return heading;
        }
      }
      return null;
    }

    /// <summary>
    /// Creates a private heading fingerprint used only to reacquire current generated IDs.
    /// </summary>
    private string Fingerprint(HtmlNode heading) {
      string text = Regex.Replace(WebUtility.HtmlDecode(heading.InnerText).Trim(), @"\s+", " ");
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(this.HeadingLevel(heading) + "|" + text));
      return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Sends OneNote JSON patch commands.
    /// </summary>
    private void SendPatch(string pageId, Patch[] patches) {
      JArray array = new JArray();
      foreach (Patch patch in patches) {
        array.Add(patch.ToJson());
      }
      string json = array.ToString(Formatting.None);
      string url = this.SiteOneNoteUrl("/pages/" + Uri.EscapeDataString(pageId) + "/content");
      using (HttpResponseMessage response = this.SendGraphRequest(new HttpMethod("PATCH"), url, () => new StringContent(json, Encoding.UTF8, "application/json"))) {
        this.EnsureSuccess(response, "OneNote page PATCH failed.");
      }
    }

    /// <summary>
    /// Sends a multipart OneNote patch containing one binary resource.
    /// </summary>
    private void SendMultipartPatch(string pageId, Patch[] patches, string partName, string contentType, byte[] bytes) {
      JArray array = new JArray();
      foreach (Patch patch in patches) {
        array.Add(patch.ToJson());
      }

      string json = array.ToString(Formatting.None);
      string url = this.SiteOneNoteUrl("/pages/" + Uri.EscapeDataString(pageId) + "/content");
      using (HttpResponseMessage response = this.SendGraphRequest(new HttpMethod("PATCH"), url, () => {
        MultipartFormDataContent multipart = new MultipartFormDataContent("KnowledgeBoundary" + Guid.NewGuid().ToString("N"));
        multipart.Add(new StringContent(json, Encoding.UTF8, "application/json"), "Commands");
        ByteArrayContent binary = new ByteArrayContent(bytes);
        binary.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(binary, partName);
        return multipart;
      })) {
        this.EnsureSuccess(response, "OneNote multipart PATCH failed.");
      }
    }

    /// <summary>
    /// Resolves the SharePoint site ID from the configured URL.
    /// </summary>
    private string ResolveSiteId(string sourceUrl) {
      Uri uri = new Uri(sourceUrl);
      string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length < 2 || !segments[0].Equals("sites", StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException("A SharePoint /sites/... URL is required.");
      }
      JObject site = this.GetJson(_GraphBaseUrl + "/sites/" + uri.Host + ":/sites/" + segments[1]);
      return this.Required(site, "id");
    }

    /// <summary>
    /// Loads all accessible notebooks.
    /// </summary>
    private NotebookInfo[] LoadNotebooks() {
      if (_CachedNotebooks != null) {
        return _CachedNotebooks;
      }

      List<NotebookInfo> result = new List<NotebookInfo>();
      foreach (JObject notebook in this.GetValues(this.GetJson(this.SiteOneNoteUrl("/notebooks")))) {
        result.Add(new NotebookInfo(this.Required(notebook, "id"), this.Display(notebook, "displayName", "Unnamed Notebook")));
      }
      _CachedNotebooks = result.ToArray();
      return _CachedNotebooks;
    }

    /// <summary>
    /// Resolves the optional notebook exactly and rejects ambiguity.
    /// </summary>
    private NotebookInfo GetConfiguredNotebook(NotebookInfo[] notebooks) {
      NotebookInfo[] matches = notebooks.Where((item) => item.Name.Equals(_NotebookName, StringComparison.Ordinal)).ToArray();
      if (matches.Length != 1) {
        throw new InvalidOperationException("Notebook '" + _NotebookName + "' must resolve exactly once.");
      }
      return matches[0];
    }

    /// <summary>
    /// Adds a collision-safe logical child without exposing its native ID.
    /// </summary>
    private AreaNode AddChild(AreaNode parent, NodeKind kind, string name, string nativeId, string pageId, string fingerprint, int headingLevel, ContentLevel level) {
      string segmentValue;
      if (string.IsNullOrWhiteSpace(name)) {
        segmentValue = "_";
      }
      else {
        segmentValue = name.Trim();
      }
      string segment = Uri.EscapeDataString(segmentValue);
      string path;
      if (parent.Path == "/") {
        path = "/" + segment;
      }
      else {
        path = parent.Path + "/" + segment;
      }
      if (parent.Children.Any((item) => item.Path.Equals(path, StringComparison.Ordinal))) {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(nativeId + "|" + fingerprint));
        path += "~" + Convert.ToHexString(hash).Substring(0, 8).ToLowerInvariant();
      }
      AreaNode node = new AreaNode(kind, path, name, nativeId, pageId, fingerprint, headingLevel, level);
      parent.Children.Add(node);
      return node;
    }

    /// <summary>
    /// Resolves a logical area path.
    /// </summary>
    private AreaNode ResolveArea(AreaNode root, string area) {
      if (string.IsNullOrWhiteSpace(area) || !area.StartsWith("/", StringComparison.Ordinal)) {
        throw new ArgumentException("Area paths must be absolute.", nameof(area));
      }

      string normalized;
      if (area.Length > 1) {
        normalized = area.TrimEnd('/');
      }
      else {
        normalized = area;
      }
      if (normalized == "/") {
        return root;
      }

      string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
      AreaNode current = root;
      string currentPath = string.Empty;
      foreach (string segment in segments) {
        this.EnsureAreaChildrenLoaded(current);
        currentPath += "/" + segment;
        AreaNode child = current.Children.FirstOrDefault((item) => item.Path.Equals(currentPath, StringComparison.Ordinal));
        if (child == null) {
          throw new InvalidOperationException("Area '" + normalized + "' does not exist.");
        }
        current = child;
      }
      return current;
    }

    /// <summary>
    /// Lazily materializes page headings. Structural metadata is loaded independently from
    /// page HTML so ordinary navigation does not prefetch every OneNote page body.
    /// </summary>
    private void EnsureAreaChildrenLoaded(AreaNode node) {
      if (node.ChildrenLoaded) {
        return;
      }

      lock (_SyncRoot) {
        if (node.ChildrenLoaded) {
          return;
        }

        if (node.Kind == NodeKind.Root) {
          if (!string.IsNullOrWhiteSpace(node.NativeId)) {
            DevLogger.LogTrace(0, 99999, "OneNote notebook metadata cache miss for configured repository root.");
            this.LoadNotebook(node, new NotebookInfo(node.NativeId, _NotebookName));
          }
        }
        else if (node.Kind == NodeKind.Notebook) {
          DevLogger.LogTrace(0, 99999, "OneNote notebook metadata cache miss: " + node.Path);
          this.LoadNotebook(node, new NotebookInfo(node.NativeId, node.Name));
        }
        else if (node.Kind == NodeKind.SectionGroup) {
          DevLogger.LogTrace(0, 99999, "OneNote section-group metadata cache miss: " + node.Path);
          this.LoadSectionGroupChildren(node);
        }
        else if (node.Kind == NodeKind.Section) {
          DevLogger.LogTrace(0, 99999, "OneNote section page-metadata cache miss: " + node.Path);
          this.LoadSectionChildren(node);
        }
        else if (node.Kind == NodeKind.Page) {
          DevLogger.LogTrace(0, 99999, "OneNote heading projection cache miss: " + node.Path);
          this.LoadHeadings(node, this.LoadPage(node.PageId));
        }

        node.ChildrenLoaded = true;
      }
    }

    /// <summary>
    /// Adds descendants in pre-order and lazily expands page heading projections only when
    /// recursive enumeration actually requires them.
    /// </summary>
    private void AddNodes(AreaNode parent, List<AreaNode> result) {
      this.EnsureAreaChildrenLoaded(parent);
      foreach (AreaNode child in parent.Children) {
        result.Add(child);
        this.AddNodes(child, result);
      }
    }

    /// <summary>
    /// Adds descendant paths in pre-order and lazily expands page heading projections only
    /// when recursive enumeration actually requires them.
    /// </summary>
    private void AddPaths(AreaNode parent, List<string> result) {
      this.EnsureAreaChildrenLoaded(parent);
      foreach (AreaNode child in parent.Children) {
        result.Add(child.Path);
        this.AddPaths(child, result);
      }
    }

    /// <summary>
    /// Returns the page ID for page/resource-capable heading areas.
    /// </summary>
    private string RequirePageId(AreaNode node) {
      if (node.Kind == NodeKind.Page) {
        return node.NativeId;
      }
      if (node.Kind == NodeKind.Heading) {
        return node.PageId;
      }
      throw new InvalidOperationException("The area is not page-backed.");
    }

    /// <summary>
    /// Finds a resource node by native OneNote resource ID.
    /// </summary>
    private HtmlNode FindResource(HtmlDocument document, string nativeId) {
      HtmlNodeCollection nodes = document.DocumentNode.SelectNodes("//img|//object");
      if (nodes == null) {
        return null;
      }
      return nodes.FirstOrDefault((node) => this.ExtractNativeResourceId(node).Equals(nativeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Extracts the native resource ID from a OneNote resource URL.
    /// </summary>
    private string ExtractNativeResourceId(HtmlNode node) {
      string source = node.GetAttributeValue("data-fullres-src", string.Empty);
      if (string.IsNullOrWhiteSpace(source)) {
        source = node.GetAttributeValue("src", node.GetAttributeValue("data", string.Empty));
      }
      Uri uri;
      if (!Uri.TryCreate(source, UriKind.Absolute, out uri)) {
        return string.Empty;
      }
      Match match = Regex.Match(uri.AbsolutePath, @"/onenote/resources/(?<id>[^/]+)/(?:\$value|content)", RegexOptions.IgnoreCase);
      if (match.Success) {
        return Uri.UnescapeDataString(match.Groups["id"].Value);
      }
      return string.Empty;
    }

    /// <summary>
    /// Creates an opaque, reversible provider-owned resource identifier.
    /// </summary>
    private string CreateResourceId(string pageId, string nativeId) {
      JObject value = new JObject();
      value["p"] = pageId;
      value["r"] = nativeId;
      string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString(Formatting.None))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
      return "1." + base64;
    }

    /// <summary>
    /// Decodes a provider-owned resource identifier.
    /// </summary>
    private ResourceIdentity DecodeResourceId(string resourceId) {
      if (string.IsNullOrWhiteSpace(resourceId) || !resourceId.StartsWith("1.", StringComparison.Ordinal)) {
        throw new InvalidOperationException("Invalid OneNote resource identifier.");
      }
      string value = resourceId.Substring(2).Replace('-', '+').Replace('_', '/');
      while (value.Length % 4 != 0) {
        value += "=";
      }
      JObject json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
      string pageId = json.Value<string>("p");
      string nativeId = json.Value<string>("r");
      if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(nativeId)) {
        throw new InvalidOperationException("Incomplete OneNote resource identifier.");
      }
      return new ResourceIdentity(pageId, nativeId);
    }

    /// <summary>
    /// Returns whether an HTML element is H1-H6.
    /// </summary>
    private bool IsHeading(HtmlNode node) {
      return node != null && Regex.IsMatch(node.Name, "^h[1-6]$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Returns an H1-H6 numeric level.
    /// </summary>
    private int HeadingLevel(HtmlNode node) {
      if (!this.IsHeading(node)) {
        throw new InvalidOperationException("The HTML node is not a heading.");
      }
      return int.Parse(node.Name.Substring(1));
    }

    /// <summary>
    /// Returns a site-scoped OneNote Graph URL.
    /// </summary>
    private string SiteOneNoteUrl(string suffix) {
      return _GraphBaseUrl + "/sites/" + Uri.EscapeDataString(_SiteId) + "/onenote" + suffix;
    }

    /// <summary>
    /// Sends one Microsoft Graph request through the central repository governor.
    /// The governor serializes requests, spaces them locally, retries transient failures,
    /// respects Retry-After when supplied and applies exponential backoff otherwise.
    /// </summary>
    private HttpResponseMessage SendGraphRequest(HttpMethod method, string url, Func<HttpContent> contentFactory) {
      this.EnsureNotDisposed();
      if (_ReadOnly && method != HttpMethod.Get && method != HttpMethod.Head) {
        throw new InvalidOperationException("The OneNote repository is read-only. Graph mutation requests are blocked centrally.");
      }

      lock (_GraphRequestSyncRoot) {
        int retry = 0;
        while (true) {
          this.WaitForGraphRequestSlot();
          using (HttpRequestMessage request = new HttpRequestMessage(method, url)) {
            if (contentFactory != null) {
              request.Content = contentFactory();
            }

            DevLogger.LogTrace(0, 99999, "OneNote Graph request: " + method.Method + " " + url);
            _LastGraphRequestUtc = DateTime.UtcNow;
            HttpResponseMessage response = _HttpClient.SendAsync(request).GetAwaiter().GetResult();

            // HTTP 429 is deliberately never retried inside the provider. A caller such as
            // BackgroundFetchingKnowledgeRepositoryCacheWrapper can apply a repository-wide
            // throttle without keeping the Graph provider itself blocked.
            if (response.StatusCode == (HttpStatusCode)429) {
              DevLogger.LogTrace(0, 99999, "OneNote Graph returned HTTP 429 Too Many Requests. The provider returns the throttling response without an internal retry.");
              return response;
            }

            if (!this.IsTransientGraphFailure(response.StatusCode) || retry >= _MaximumGraphRetryCount) {
              if (response.IsSuccessStatusCode && method != HttpMethod.Get && method != HttpMethod.Head) {
                this.InvalidateRepositoryCache();
              }
              return response;
            }

            int delayMilliseconds = this.GetRetryDelayMilliseconds(response, retry);
            DevLogger.LogTrace(0, 99999, "OneNote Graph transient HTTP " + (int)response.StatusCode + ". Retry " + (retry + 1) + "/" + _MaximumGraphRetryCount + " after " + delayMilliseconds + " ms.");
            response.Dispose();
            Thread.Sleep(delayMilliseconds);
            retry++;
          }
        }
      }
    }

    /// <summary>
    /// Enforces a conservative minimum interval between Graph requests.
    /// This protects Graph independently from repository caching and consumer behavior.
    /// </summary>
    private void WaitForGraphRequestSlot() {
      if (_LastGraphRequestUtc == DateTime.MinValue) {
        return;
      }

      TimeSpan elapsed = DateTime.UtcNow - _LastGraphRequestUtc;
      int remaining = _MinimumGraphRequestIntervalMilliseconds - (int)elapsed.TotalMilliseconds;
      if (remaining > 0) {
        DevLogger.LogTrace(0, 99999, "OneNote Graph local governor delay: " + remaining + " ms.");
        Thread.Sleep(remaining);
      }
    }

    /// <summary>
    /// Returns whether Graph may recover from the response when retried later.
    /// </summary>
    private bool IsTransientGraphFailure(HttpStatusCode statusCode) {
      return statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout ||
        statusCode == HttpStatusCode.InternalServerError;
    }

    /// <summary>
    /// Resolves Retry-After or calculates bounded exponential backoff for retryable server failures.
    /// HTTP 429 is intentionally returned to the caller and is not handled by this retry path.
    /// </summary>
    private int GetRetryDelayMilliseconds(HttpResponseMessage response, int retry) {
      if (response.Headers.RetryAfter != null) {
        if (response.Headers.RetryAfter.Delta.HasValue) {
          double milliseconds = response.Headers.RetryAfter.Delta.Value.TotalMilliseconds;
          return (int)Math.Min(Math.Max(milliseconds, _InitialBackoffMilliseconds), _MaximumBackoffMilliseconds);
        }
        if (response.Headers.RetryAfter.Date.HasValue) {
          double milliseconds = (response.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow).TotalMilliseconds;
          if (milliseconds > 0) {
            return (int)Math.Min(Math.Max(milliseconds, _InitialBackoffMilliseconds), _MaximumBackoffMilliseconds);
          }
        }
      }

      long exponential = (long)_InitialBackoffMilliseconds * (1L << Math.Min(retry, 10));
      return (int)Math.Min(exponential, _MaximumBackoffMilliseconds);
    }

    /// <summary>
    /// Invalidates all snapshots after a successful mutation.
    /// Reads never invalidate caches.
    /// </summary>
    private void InvalidateRepositoryCache() {
      _CachedTree = null;
      _CachedNotebooks = null;
      _PageCache.Clear();
      DevLogger.LogTrace(0, 99999, "OneNote repository caches invalidated after Graph mutation.");
    }

    /// <summary>
    /// Loads JSON from Graph.
    /// </summary>
    private JObject GetJson(string url) {
      return JObject.Parse(this.GetString(url));
    }

    /// <summary>
    /// Loads text from Graph.
    /// </summary>
    private string GetString(string url) {
      using (HttpResponseMessage response = this.SendGraphRequest(HttpMethod.Get, url, null)) {
        this.EnsureSuccess(response, "Microsoft Graph GET failed.");
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
      }
    }

    /// <summary>
    /// Returns Graph collection values.
    /// </summary>
    private JObject[] GetValues(JObject response) {
      JArray values = response["value"] as JArray;
      if (values == null) {
        return Array.Empty<JObject>();
      }
      return values.OfType<JObject>().ToArray();
    }

    /// <summary>
    /// Returns a required JSON string.
    /// </summary>
    private string Required(JObject value, string propertyName) {
      string result = value.Value<string>(propertyName);
      if (string.IsNullOrWhiteSpace(result)) {
        throw new InvalidOperationException("Graph response misses '" + propertyName + "'.");
      }
      return result;
    }

    /// <summary>
    /// Returns a display JSON string or fallback.
    /// </summary>
    private string Display(JObject value, string propertyName, string fallback) {
      string result = value.Value<string>(propertyName);
      if (string.IsNullOrWhiteSpace(result)) {
        return fallback;
      }
      return result;
    }

    /// <summary>
    /// Converts failed Graph responses to detailed exceptions.
    /// </summary>
    private void EnsureSuccess(HttpResponseMessage response, string message) {
      if (response.IsSuccessStatusCode) {
        return;
      }
      string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
      throw new HttpRequestException(
        message + " HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". " + body,
        null,
        response.StatusCode
      );
    }

    /// <summary>
    /// Guards disposed instances.
    /// </summary>
    private void EnsureNotDisposed() {
      if (_Disposed) {
        throw new ObjectDisposedException(nameof(OneNoteKnowledgeRepositoryProxy));
      }
    }

    private enum NodeKind { Root, Notebook, SectionGroup, Section, Page, Heading }

    private sealed class AreaNode {
      private readonly NodeKind _Kind;
      private readonly string _Path;
      private readonly string _Name;
      private readonly string _NativeId;
      private readonly string _PageId;
      private readonly string _Fingerprint;
      private readonly int _HeadingLevel;
      private readonly ContentLevel _Level;
      private readonly List<AreaNode> _Children;
      private bool _ChildrenLoaded;

      /// <summary>Creates an internal logical area.</summary>
      public AreaNode(NodeKind kind, string path, string name, string nativeId, string pageId, string fingerprint, int headingLevel, ContentLevel level) {
        _Kind = kind; _Path = path; _Name = name; _NativeId = nativeId; _PageId = pageId; _Fingerprint = fingerprint; _HeadingLevel = headingLevel; _Level = level; _Children = new List<AreaNode>();
      }
      public NodeKind Kind { get { return _Kind; } }
      public string Path { get { return _Path; } }
      public string Name { get { return _Name; } }
      public string NativeId { get { return _NativeId; } }
      public string PageId { get { return _PageId; } }
      public string Fingerprint { get { return _Fingerprint; } }
      public int HeadingLevel { get { return _HeadingLevel; } }
      public ContentLevel Level { get { return _Level; } }
      public List<AreaNode> Children { get { return _Children; } }
      public bool ChildrenLoaded { get { return _ChildrenLoaded; } set { _ChildrenLoaded = value; } }
    }

    private sealed class NotebookInfo {
      private readonly string _Id;
      private readonly string _Name;
      /// <summary>Creates notebook metadata.</summary>
      public NotebookInfo(string id, string name) { _Id = id; _Name = name; }
      public string Id { get { return _Id; } }
      public string Name { get { return _Name; } }
    }

    private sealed class ResourceIdentity {
      private readonly string _PageId;
      private readonly string _NativeId;
      /// <summary>Creates a private resource identity.</summary>
      public ResourceIdentity(string pageId, string nativeId) { _PageId = pageId; _NativeId = nativeId; }
      public string PageId { get { return _PageId; } }
      public string NativeId { get { return _NativeId; } }
    }

    private sealed class Target {
      private readonly string _Id;
      private readonly string _Position;
      /// <summary>Creates an insertion target.</summary>
      public Target(string id, string position) { _Id = id; _Position = position; }
      public string Id { get { return _Id; } }
      public string Position { get { return _Position; } }
    }

    private sealed class Patch {
      private readonly string _Target;
      private readonly string _Action;
      private readonly string _Position;
      private readonly string _Content;
      /// <summary>Creates a OneNote patch command.</summary>
      public Patch(string target, string action, string position, string content) { _Target = target; _Action = action; _Position = position; _Content = content; }
      /// <summary>Converts the command to Graph JSON.</summary>
      public JObject ToJson() {
        JObject result = new JObject();
        result["target"] = _Target;
        result["action"] = _Action;
        if (!string.IsNullOrWhiteSpace(_Position)) {
          result["position"] = _Position;
        }
        result["content"] = _Content;
        return result;
      }
    }
  }
}
