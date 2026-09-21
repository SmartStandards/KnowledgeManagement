using KnowledgeManagement.SmartStandards.Endpoints.Html;
using Logging.SmartStandards;
using Logging.SmartStandards.CopyForKnowledgeManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeManagement.SmartStandards.Endpoints.Raw {

  /// <summary>
  /// Exposes one <see cref="IKnowledgeRepository"/> through a deliberately small,
  /// REST-like HTTP surface optimized for both human callers and simple AI agents.
  /// 
  /// The controller is intentionally self-describing from its entry URL:
  /// 
  /// - GET on a BeyondContent area returns a self-describing Markdown navigation response
  ///   containing logical child area paths and fully qualified absolute URLs.
  /// - GET on a ContentAggregation area returns direct navigation only. Callers descend
  ///   explicitly until a concrete content container is reached.
  /// - GET on a ContentContainer area returns the complete aggregated Markdown content
  ///   exposed through that container.
  /// - POST appends Markdown content through <see cref="IKnowledgeRepository.TryAppendContent(string, string)"/>.
  /// - DELETE truncates the addressed content area through
  ///   <see cref="IKnowledgeRepository.TryTruncate(string)"/>.
  /// 
  /// More advanced repository operations such as rename, physical deletion, replacement
  /// and content movement are intentionally not exposed by this controller.
  ///
  /// By default, read requests prefer any existing local cache entry when the directly
  /// consumed repository exposes <see cref="IKnowledgeRepositoryCacheControl"/>. Missing
  /// values are still loaded from the authoritative source and cached. This keeps the RAW
  /// facade fast while preserving complete lazy population of previously unseen data.
  /// </summary>
  [ApiController]
  [Route("api/knowledge/raw")]
  [EndpointGroupName("KnowledgeRepository-RAW")]
  public class KnowledgeRepositoryRawController : ControllerBase {

    private const string _MarkdownContentType = "text/markdown; charset=utf-8";
    private const string _RouteEscapeMarker = "~";
    private const string _RouteEscapedTilde = "~7E";
    private const string _RouteEscapedPercent = "~25";
    private const string _KnowledgeResourceReferencePrefix = "knowledge-resource:";

    private static readonly Regex _KnowledgeReferenceRegex = new Regex(
      "(?<scheme>knowledge-area|knowledge-resource):(?<target>[^\\s\\)\\]\\>\\\"']+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private readonly IKnowledgeRepository _KnowledgeRepository;
    private readonly bool _DisableCacheRefresh;

    /// <summary>
    /// Creates the controller using the single knowledge repository supplied through
    /// constructor dependency injection.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The repository implementation exposed through this HTTP endpoint.
    /// </param>
    /// <param name="disableCacheRefresh">
    /// When true and the directly consumed repository implements
    /// <see cref="IKnowledgeRepositoryCacheControl"/>, existing cache entries are always
    /// preferred regardless of age. Missing values are still loaded from the authoritative
    /// source and cached normally. The default is true because the RAW facade is optimized
    /// for low-latency read access and should avoid unnecessary source refreshes.
    /// </param>
    public KnowledgeRepositoryRawController(
      IKnowledgeRepository knowledgeRepository,
      bool disableCacheRefresh = true
    ) {
      if (knowledgeRepository == null) {
        throw new ArgumentNullException(
          nameof(knowledgeRepository)
        );
      }

      _KnowledgeRepository =
        knowledgeRepository;

      _DisableCacheRefresh =
        disableCacheRefresh;
    }

    /// <summary>
    /// Handles GET requests for the repository root.
    /// 
    /// The root request is forwarded to the same area-resolution logic used for arbitrary
    /// nested paths.
    /// </summary>
    /// <returns>
    /// A self-describing navigation listing for BeyondContent areas or aggregated
    /// Markdown for content-capable areas.
    /// </returns>
    [HttpGet(Name = KnowledgeRepositoryHttpRouteNames._RawRoot)]
    public IActionResult GetRoot() {
      return this.ExecuteReadWithCachePolicy(
        () => this.GetAreaInternal(
          "/"
        )
      );
    }

    /// <summary>
    /// Handles GET requests for any nested logical knowledge area.
    /// 
    /// BeyondContent areas return a compact Markdown navigation document containing
    /// logical direct child paths and fully qualified direct child URLs.
    /// 
    /// ContentAggregation areas return direct navigation. ContentContainer areas return
    /// <see cref="IKnowledgeRepository.GetAggregatedContent(string)"/> as Markdown.
    /// </summary>
    /// <param name="area">The catch-all logical area path relative to the controller route.</param>
    /// <returns>The navigation listing or aggregated Markdown content.</returns>
    [HttpGet("{**area}", Name = KnowledgeRepositoryHttpRouteNames._RawArea)]
    public IActionResult GetArea(string area) {
      string repositoryArea =
        this.ToRepositoryArea(
          area
        );

      return this.ExecuteReadWithCachePolicy(
        () => this.GetAreaInternal(
          repositoryArea
        )
      );
    }

    /// <summary>
    /// Returns one opaque knowledge resource as raw HTTP content.
    ///
    /// Resource identifiers remain fully opaque. The controller forwards the identifier
    /// directly to <see cref="IKnowledgeRepository.GetResourceContent(string)"/> and never
    /// attempts to decode provider-specific identity information.
    /// </summary>
    /// <param name="resourceId">The opaque repository resource identifier.</param>
    /// <returns>The binary resource content or HTTP 404 when the resource does not exist.</returns>
    [HttpGet("resources/{resourceId}", Name = KnowledgeRepositoryHttpRouteNames._RawResource)]
    public IActionResult GetResource(string resourceId) {
      return this.ExecuteReadWithCachePolicy(
        () => this.GetResourceInternal(
          resourceId
        )
      );
    }

    /// <summary>
    /// Returns one opaque resource while the selected cache read policy is active.
    /// </summary>
    private IActionResult GetResourceInternal(
      string resourceId
    ) {
      if (string.IsNullOrWhiteSpace(resourceId)) {
        return this.NotFound();
      }

      byte[] content;

      try {
        content = _KnowledgeRepository.GetResourceContent(
          resourceId
        );
      }
      catch (InvalidOperationException) {
        return this.NotFound(
          "Knowledge resource not found."
        );
      }

      string contentType = this.DetectContentType(
        content
      );

      this.Response.Headers["Content-Disposition"] = "inline";

      return this.File(
        content,
        contentType
      );
    }

    /// <summary>
    /// Appends Markdown content to the repository root when the root is content-capable.
    /// 
    /// The request body is passed directly to
    /// <see cref="IKnowledgeRepository.TryAppendContent(string, string)"/>.
    /// </summary>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpPost]
    [Consumes("text/markdown", "text/plain")]
    public IActionResult PostRoot() {
      return this.PostAreaInternal("/");
    }

    /// <summary>
    /// Appends Markdown content to one nested logical area.
    /// 
    /// On a ContentContainer, direct text may be appended and structured content is merged
    /// hierarchically.
    /// 
    /// On a ContentAggregation, the repository contract requires the payload to contain
    /// subordinate structure because the aggregation itself does not own direct content.
    /// 
    /// POST is rejected for BeyondContent areas or areas without append capability.
    /// </summary>
    /// <param name="area">The catch-all logical area path relative to the controller route.</param>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpPost("{**area}")]
    [Consumes("text/markdown", "text/plain")]
    public IActionResult PostArea(string area) {
      return this.PostAreaInternal(this.ToRepositoryArea(area));
    }

    /// <summary>
    /// Truncates the repository root when the root is content-capable and truncation is
    /// supported.
    /// </summary>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpDelete]
    public IActionResult DeleteRoot() {
      return this.DeleteAreaInternal("/");
    }

    /// <summary>
    /// Truncates one nested logical content area.
    /// 
    /// The addressed area itself is preserved. Its direct content and/or subordinate
    /// content tree are removed according to the repository contract.
    /// 
    /// DELETE is rejected for BeyondContent areas or areas without truncate capability.
    /// </summary>
    /// <param name="area">The catch-all logical area path relative to the controller route.</param>
    /// <returns>204 on success or an appropriate error response.</returns>
    [HttpDelete("{**area}")]
    public IActionResult DeleteArea(string area) {
      return this.DeleteAreaInternal(this.ToRepositoryArea(area));
    }

    /// <summary>
    /// Executes one RAW read under prefer-existing cache semantics when cache refresh has
    /// been disabled for this controller instance and the directly consumed repository
    /// exposes the optional local cache-control capability.
    ///
    /// Missing values are still loaded and cached by the repository cache wrapper.
    /// </summary>
    private IActionResult ExecuteReadWithCachePolicy(
      Func<IActionResult> action
    ) {
      if (!_DisableCacheRefresh) {
        return action();
      }

      IKnowledgeRepositoryCacheControl cacheControl =
        _KnowledgeRepository as IKnowledgeRepositoryCacheControl;

      if (cacheControl == null) {
        return action();
      }

      using (IDisposable scope = cacheControl.BeginPreferExistingScope()) {
        return action();
      }
    }

    /// <summary>
    /// Performs the polymorphic GET behavior defined by the area's content level.
    /// </summary>
    private IActionResult GetAreaInternal(string repositoryArea) {
      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: " + repositoryArea
        );
      }

      if (contentLevel == ContentLevel.BeyondContent) {
        string navigation = this.BuildNavigationResponse(
          repositoryArea,
          supportsSubAreas
        );

        return this.Content(
          navigation,
          _MarkdownContentType,
          Encoding.UTF8
        );
      }

      if (contentLevel == ContentLevel.ContentAggregation) {
        string response = this.BuildContentAggregationResponse(
          repositoryArea,
          supportsSubAreas
        );

        return this.Content(
          response,
          _MarkdownContentType,
          Encoding.UTF8
        );
      }

      string content = _KnowledgeRepository.GetAggregatedContent(
        repositoryArea
      );

      content = this.ResolveKnowledgeReferences(
        content
      );

      return this.Content(
        content,
        _MarkdownContentType,
        Encoding.UTF8
      );
    }

    /// <summary>
    /// Performs one append operation after validating that the target exists, is part of
    /// the content model and reports append capability.
    /// </summary>
    private IActionResult PostAreaInternal(string repositoryArea) {
      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: " + repositoryArea
        );
      }

      if (contentLevel == ContentLevel.BeyondContent) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "POST is not available for navigation-only knowledge areas."
        );
      }

      if (!canAppendContent) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "This knowledge area does not allow content append operations."
        );
      }

      string content;

      try {
        using StreamReader reader = new StreamReader(
          this.Request.Body,
          Encoding.UTF8,
          true,
          4096,
          true
        );

        content = reader.ReadToEnd();
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);

        return this.BadRequest(
          "The request body could not be read."
        );
      }

      bool success = _KnowledgeRepository.TryAppendContent(
        repositoryArea,
        content
      );

      if (!success) {
        return this.Conflict(
          "The content could not be appended. The supplied structure may be invalid for this knowledge area or the repository state may have changed."
        );
      }

      return this.NoContent();
    }

    /// <summary>
    /// Performs one truncate operation after validating that the target exists, is part
    /// of the content model and reports truncate capability.
    /// </summary>
    private IActionResult DeleteAreaInternal(string repositoryArea) {
      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out ContentLevel contentLevel,
            out bool supportsSubAreas,
            out bool canBeRenamed,
            out bool canBeDeleted,
            out bool canAddSubAreas,
            out bool canAppendContent,
            out bool canTruncate,
            out bool supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: " + repositoryArea
        );
      }

      if (contentLevel == ContentLevel.BeyondContent) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "DELETE is not available for navigation-only knowledge areas."
        );
      }

      if (!canTruncate) {
        return this.StatusCode(
          StatusCodes.Status405MethodNotAllowed,
          "This knowledge area does not allow truncate operations."
        );
      }

      bool success = _KnowledgeRepository.TryTruncate(
        repositoryArea
      );

      if (!success) {
        return this.Conflict(
          "The knowledge area could not be truncated. The repository state may have changed or the operation could not be completed atomically."
        );
      }

      return this.NoContent();
    }

    /// <summary>
    /// Builds a compact self-describing navigation response for a BeyondContent area.
    /// 
    /// The response deliberately contains fully qualified URLs rather than only logical
    /// path fragments so a simple AI agent can discover the next accessible locations
    /// from a single entry URL without additional API documentation.
    /// 
    /// Only direct child areas are listed. A caller can follow any URL and repeat GET to
    /// continue traversing the repository.
    /// </summary>
    private string BuildNavigationResponse(
      string repositoryArea,
      bool supportsSubAreas
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append("Knowledge area: `");
      builder.Append(repositoryArea);
      builder.Append('`');
      builder.Append(Environment.NewLine);
      builder.Append(Environment.NewLine);

      if (!supportsSubAreas) {
        builder.Append("This knowledge area contains no directly accessible sub-areas.");
        return builder.ToString();
      }

      string[] childAreas =
        this.GetDirectAreas(
          repositoryArea
        );

      if (childAreas.Length == 0) {
        builder.Append("This knowledge area contains no directly accessible sub-areas.");
        return builder.ToString();
      }

      builder.Append("The following directly accessible sub-area URLs are available:");
      builder.Append(Environment.NewLine);
      builder.Append(Environment.NewLine);

      foreach (string childArea in childAreas) {
        builder.Append("- `");
        builder.Append(childArea);
        builder.Append("` -> <");
        builder.Append(this.BuildAbsoluteAreaUrl(childArea));
        builder.Append('>');
        builder.Append(Environment.NewLine);
      }

      builder.Append(Environment.NewLine);
      builder.Append("Use HTTP GET on any URL above to continue browsing or to retrieve its Markdown content.");

      return builder.ToString();
    }

    /// <summary>
    /// Returns only direct child areas for interactive/raw navigation.
    ///
    /// The raw facade intentionally never performs recursive area enumeration. Clients
    /// discover larger repositories by following the returned direct-child URLs one level
    /// at a time.
    /// </summary>
    private string[] GetDirectAreas(
      string repositoryArea
    ) {
      return _KnowledgeRepository.GetAreas(
        false,
        repositoryArea
      );
    }

    /// <summary>
    /// Builds the self-describing response for a ContentAggregation area.
    /// 
    /// Only direct child URLs are rendered. The raw facade deliberately does not request
    /// aggregated content for an aggregation area because doing so may require a complete
    /// provider subtree. Callers continue one level at a time until a content container is
    /// reached.
    /// </summary>
    private string BuildContentAggregationResponse(
      string repositoryArea,
      bool supportsSubAreas
    ) {
      StringBuilder builder = new StringBuilder();

      string navigation = this.BuildNavigationResponse(
        repositoryArea,
        supportsSubAreas
      );

      if (!string.IsNullOrWhiteSpace(navigation)) {
        builder.Append(navigation.TrimEnd('\r', '\n'));
      }

      // ContentAggregation areas are navigation scopes in the raw HTTP facade. Calling
      // GetAggregatedContent here can force an arbitrarily large provider subtree to be
      // materialized, especially when an AggregatedKnowledgeRepository is mounted at a
      // high level. Clients should follow one of the direct child URLs and retrieve
      // concrete ContentContainer Markdown on demand.
      return builder.ToString();
    }

    /// <summary>
    /// Resolves provider-neutral knowledge references at the final RAW Markdown boundary.
    ///
    /// The RAW endpoint does not have an HTML URL-normalization stage. Therefore every
    /// provider-neutral knowledge scheme must be converted to a normal HTTP URL before the
    /// Markdown leaves this controller. A downstream Markdown renderer never receives
    /// knowledge-area: or knowledge-resource: and consequently cannot collapse such links
    /// to the current page or to "#".
    ///
    /// Ordinary links such as http, https and mailto are not matched and remain unchanged.
    /// </summary>
    private string ResolveKnowledgeReferences(
      string content
    ) {
      if (string.IsNullOrEmpty(
            content
          )) {
        return content;
      }

      return _KnowledgeReferenceRegex.Replace(
        content,
        (Match match) => {
          string scheme =
            match.Groups["scheme"].Value;

          string target =
            match.Groups["target"].Value;

          if (string.Equals(
                scheme,
                "knowledge-area",
                StringComparison.OrdinalIgnoreCase
              )) {
            string repositoryArea =
              this.DecodeKnowledgeAreaReference(
                target
              );

            return this.BuildAbsoluteAreaUrl(
              repositoryArea
            );
          }

          // Resource IDs are opaque repository identifiers. Do not URI-decode or otherwise
          // reinterpret them before handing them back to the repository endpoint.
          return this.BuildAbsoluteResourceUrl(
            target
          );
        }
      );
    }

    /// <summary>
    /// Decodes one provider-neutral knowledge-area URI target exactly once into the logical
    /// repository area expected by <see cref="IKnowledgeRepository"/>.
    ///
    /// Existing percent encoding is therefore not encoded a second time. HTTP route
    /// transport encoding remains centralized in <see cref="BuildAbsoluteAreaUrl(string)"/>.
    /// No provider-specific interpretation is performed.
    /// </summary>
    private string DecodeKnowledgeAreaReference(
      string encodedArea
    ) {
      if (string.IsNullOrWhiteSpace(
            encodedArea
          ) ||
          string.Equals(
            encodedArea,
            "/",
            StringComparison.Ordinal
          )) {
        return "/";
      }

      string decodedArea;

      try {
        decodedArea =
          Uri.UnescapeDataString(
            encodedArea
          );
      }
      catch (UriFormatException ex) {
        DevLogger.LogError(
          ex
        );

        // Preserve malformed escape sequences literally. BuildAbsoluteAreaUrl will still
        // apply the RAW endpoint's provider-neutral transport encoding.
        decodedArea =
          encodedArea;
      }

      if (string.IsNullOrWhiteSpace(
            decodedArea
          ) ||
          string.Equals(
            decodedArea,
            "/",
            StringComparison.Ordinal
          )) {
        return "/";
      }

      decodedArea =
        decodedArea
        .Replace(
          '\\',
          '/'
        )
        .Trim();

      if (!decodedArea.StartsWith(
            "/",
            StringComparison.Ordinal
          )) {
        decodedArea =
          "/"
          + decodedArea;
      }

      return decodedArea;
    }

    /// <summary>
    /// Builds a fully qualified resource URL for one opaque repository resource identifier.
    /// The current request scheme is retained so HTTPS remains HTTPS behind a correctly
    /// configured forwarded-header pipeline while local HTTP development continues to work.
    /// </summary>
    private string BuildAbsoluteResourceUrl(string resourceId) {
      string routePath =
        this.Url.RouteUrl(
          KnowledgeRepositoryHttpRouteNames._RawResource,
          new {
            resourceId = resourceId
          }
        );

      if (string.IsNullOrWhiteSpace(
            routePath
          )) {
        throw new InvalidOperationException(
          "The ASP.NET Core route for the knowledge resource endpoint could not be resolved."
        );
      }

      string pathBase =
        this.Request.PathBase.Value;

      if (string.IsNullOrEmpty(
            pathBase
          )) {
        pathBase =
          string.Empty;
      }

      if (routePath.StartsWith(
            pathBase,
            StringComparison.OrdinalIgnoreCase
          )) {
        pathBase =
          string.Empty;
      }

      return this.Request.Scheme
        + "://"
        + this.Request.Host.Value
        + pathBase
        + routePath;
    }

    /// <summary>
    /// Detects a safe HTTP content type from common binary signatures.
    ///
    /// Knowledge resources may originate from arbitrary providers and the resource-content
    /// API deliberately addresses them only by opaque identifier. Signature detection keeps
    /// the raw endpoint provider-neutral while allowing browsers and AI consumers to handle
    /// common images directly.
    /// </summary>
    private string DetectContentType(byte[] content) {
      if (content == null ||
          content.Length == 0) {
        return "application/octet-stream";
      }

      if (content.Length >= 8 &&
          content[0] == 0x89 &&
          content[1] == 0x50 &&
          content[2] == 0x4E &&
          content[3] == 0x47 &&
          content[4] == 0x0D &&
          content[5] == 0x0A &&
          content[6] == 0x1A &&
          content[7] == 0x0A) {
        return "image/png";
      }

      if (content.Length >= 3 &&
          content[0] == 0xFF &&
          content[1] == 0xD8 &&
          content[2] == 0xFF) {
        return "image/jpeg";
      }

      if (content.Length >= 6 &&
          content[0] == 0x47 &&
          content[1] == 0x49 &&
          content[2] == 0x46 &&
          content[3] == 0x38 &&
          (content[4] == 0x37 || content[4] == 0x39) &&
          content[5] == 0x61) {
        return "image/gif";
      }

      if (content.Length >= 12 &&
          content[0] == 0x52 &&
          content[1] == 0x49 &&
          content[2] == 0x46 &&
          content[3] == 0x46 &&
          content[8] == 0x57 &&
          content[9] == 0x45 &&
          content[10] == 0x42 &&
          content[11] == 0x50) {
        return "image/webp";
      }

      if (content.Length >= 4 &&
          content[0] == 0x25 &&
          content[1] == 0x50 &&
          content[2] == 0x44 &&
          content[3] == 0x46) {
        return "application/pdf";
      }

      string textPrefix = Encoding.UTF8.GetString(
        content,
        0,
        Math.Min(
          content.Length,
          512
        )
      ).TrimStart();

      if (textPrefix.StartsWith(
            "<svg",
            StringComparison.OrdinalIgnoreCase
          ) ||
          textPrefix.StartsWith(
            "<?xml",
            StringComparison.OrdinalIgnoreCase
          ) &&
          textPrefix.IndexOf(
            "<svg",
            StringComparison.OrdinalIgnoreCase
          ) >= 0) {
        return "image/svg+xml";
      }

      return "application/octet-stream";
    }

    /// <summary>
    /// Builds a fully qualified absolute HTTP URL for one logical repository area.
    /// 
    /// Each logical path segment is URI-escaped individually so area hierarchy remains
    /// visible while special characters inside a segment remain safe.
    /// </summary>
    private string BuildAbsoluteAreaUrl(string repositoryArea) {
      string routeName;
      object routeValues;

      if (string.Equals(
            repositoryArea,
            "/",
            StringComparison.Ordinal
          )) {
        routeName =
          KnowledgeRepositoryHttpRouteNames._RawRoot;

        routeValues =
          new {
          };
      }
      else {
        routeName =
          KnowledgeRepositoryHttpRouteNames._RawArea;

        routeValues =
          new {
            area = EncodeAreaForRoute(
              repositoryArea
            )
          };
      }

      string routePath =
        this.Url.RouteUrl(
          routeName,
          routeValues
        );

      if (string.IsNullOrWhiteSpace(
            routePath
          )) {
        throw new InvalidOperationException(
          "The ASP.NET Core route for the knowledge area endpoint could not be resolved."
        );
      }

      string pathBase =
        this.Request.PathBase.Value;

      if (string.IsNullOrEmpty(
            pathBase
          )) {
        pathBase =
          string.Empty;
      }

      if (routePath.StartsWith(
            pathBase,
            StringComparison.OrdinalIgnoreCase
          )) {
        pathBase =
          string.Empty;
      }

      return this.Request.Scheme
        + "://"
        + this.Request.Host.Value
        + pathBase
        + routePath;
    }

    /// <summary>
    /// Converts the catch-all controller route value into one canonical repository area
    /// path.
    /// </summary>
    private string ToRepositoryArea(string area) {
      if (string.IsNullOrWhiteSpace(area)) {
        return "/";
      }

      string normalized = area.Replace('\\', '/').Trim('/');

      if (string.IsNullOrEmpty(normalized)) {
        return "/";
      }

      string[] parts = normalized.Split(
        '/',
        StringSplitOptions.RemoveEmptyEntries
      );

      for (int index = 0; index < parts.Length; index++) {
        parts[index] = DecodeAreaSegmentFromRoute(
          parts[index]
        );
      }

      return "/" + string.Join("/", parts);
    }

    /// <summary>
    /// Encodes one complete logical area for public URL transport without producing IIS
    /// double-escape sequences for logical percent characters.
    /// </summary>
    private static string EncodeAreaForRoute(
      string area
    ) {
      string[] parts = area
        .TrimStart('/')
        .Split(
          '/',
          StringSplitOptions.RemoveEmptyEntries
        );

      for (int index = 0; index < parts.Length; index++) {
        parts[index] = parts[index]
          .Replace(
            _RouteEscapeMarker,
            _RouteEscapedTilde,
            StringComparison.Ordinal
          )
          .Replace(
            "%",
            _RouteEscapedPercent,
            StringComparison.Ordinal
          );
      }

      return string.Join(
        "/",
        parts
      );
    }

    /// <summary>
    /// Restores one logical area segment from the route-safe transport representation.
    /// </summary>
    private static string DecodeAreaSegmentFromRoute(
      string segment
    ) {
      return segment
        .Replace(
          _RouteEscapedPercent,
          "%",
          StringComparison.Ordinal
        )
        .Replace(
          _RouteEscapedTilde,
          _RouteEscapeMarker,
          StringComparison.Ordinal
        );
    }

    /// <summary>
    /// Resolves area capabilities and converts a provider-specific missing-area exception
    /// into a false result suitable for HTTP 404 handling.
    /// </summary>
    private bool TryGetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate,
      out bool supportsResources
    ) {
      contentLevel = ContentLevel.BeyondContent;
      supportsSubAreas = false;
      canBeRenamed = false;
      canBeDeleted = false;
      canAddSubAreas = false;
      canAppendContent = false;
      canTruncate = false;
      supportsResources = false;

      try {
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
  }
}
