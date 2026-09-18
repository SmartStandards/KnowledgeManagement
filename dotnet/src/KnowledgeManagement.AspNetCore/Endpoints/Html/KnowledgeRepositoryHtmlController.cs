using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeManagement.SmartStandards.Endpoints.Html {

  /// <summary>
  /// Exposes one <see cref="IKnowledgeRepository"/> as a small human-readable HTML site.
  ///
  /// The controller is intentionally read-only and provider-neutral:
  ///
  /// - structural and aggregation areas are rendered as navigation pages,
  /// - navigation descends until the first concrete content container is reached,
  /// - the first content container represents the human-visible document boundary,
  /// - subordinate content containers are treated as Markdown headings inside that document
  ///   and are not exposed as separate navigation pages,
  /// - document Markdown is rendered to safe HTML,
  /// - provider-neutral knowledge-resource references are translated to the raw resource
  ///   endpoint of KnowledgeRepositoryController",
  /// - image references are consequently rendered as normal HTML image elements,
  /// - every page contains a breadcrumb navigation bar.
  ///
  /// This deliberately differs from the provider-neutral repository tree. The HTML facade
  /// preserves the original human document cut instead of exposing every heading-level
  /// content container as an independently navigable page.
  ///
  /// No provider-specific path, resource identity or storage detail is decoded by this
  /// controller.
  /// </summary>
  [ApiController]
  [ApiExplorerSettings(IgnoreApi = true)]
  [Route("api/knowledge")]
  public class KnowledgeRepositoryHtmlController : ControllerBase {

    private const string _HtmlContentType = "text/html; charset=utf-8";
    private const string _KnowledgeResourceReferencePrefix = "knowledge-resource:";

    private static readonly Regex _KnowledgeResourceReferenceRegex = new Regex(
      @"knowledge-resource:(?<id>[A-Za-z0-9._~-]+)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _HeadingRegex = new Regex(
      @"^(?<level>#{1,6})\s+(?<text>.+)$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _UnorderedListRegex = new Regex(
      @"^\s*[-*+]\s+(?<text>.+)$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _OrderedListRegex = new Regex(
      @"^\s*\d+\.\s+(?<text>.+)$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _TableSeparatorRegex = new Regex(
      @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex _InlineTokenRegex = new Regex(
      @"(?<image>!\[(?<imageAlt>[^\]]*)\]\((?<imageUrl>[^)\s]+)(?:\s+""[^""]*"")?\))|(?<link>\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)(?:\s+""[^""]*"")?\))|(?<code>`(?<codeText>[^`]+)`)|(?<bold>\*\*(?<boldText>.+?)\*\*)|(?<italic>\*(?<italicText>[^*]+)\*)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private readonly IKnowledgeRepository _KnowledgeRepository;

    /// <summary>
    /// Creates the human-readable HTML facade.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The provider-neutral repository exposed through the HTML browser.
    /// </param>
    public KnowledgeRepositoryHtmlController(
      IKnowledgeRepository knowledgeRepository
    ) {
      if (knowledgeRepository == null) {
        throw new ArgumentNullException(
          nameof(knowledgeRepository)
        );
      }

      _KnowledgeRepository = knowledgeRepository;
    }

    /// <summary>
    /// Returns the human-readable repository root.
    /// </summary>
    [HttpGet(Name = KnowledgeRepositoryHttpRouteNames._HtmlRoot)]
    public IActionResult GetRoot() {
      return this.GetAreaInternal(
        "/"
      );
    }

    /// <summary>
    /// Returns one human-readable logical knowledge area.
    /// </summary>
    /// <param name="area">
    /// The catch-all logical area path relative to the HTML controller route.
    /// </param>
    [HttpGet("{**area}", Name = KnowledgeRepositoryHttpRouteNames._HtmlArea)]
    public IActionResult GetArea(string area) {
      return this.GetAreaInternal(
        this.ToRepositoryArea(
          area
        )
      );
    }

    /// <summary>
    /// Builds one complete HTML page for the requested logical knowledge area.
    /// </summary>
    private IActionResult GetAreaInternal(
      string repositoryArea
    ) {
      ContentLevel contentLevel;
      bool supportsSubAreas;
      bool canBeRenamed;
      bool canBeDeleted;
      bool canAddSubAreas;
      bool canAppendContent;
      bool canTruncate;
      bool supportsResources;

      if (!this.TryGetAreaCapabilities(
            repositoryArea,
            out contentLevel,
            out supportsSubAreas,
            out canBeRenamed,
            out canBeDeleted,
            out canAddSubAreas,
            out canAppendContent,
            out canTruncate,
            out supportsResources
          )) {
        return this.NotFound(
          "Knowledge area not found: "
          + repositoryArea
        );
      }

      string title =
        this.GetAreaDisplayName(
          repositoryArea
        );

      if (contentLevel == ContentLevel.ContentContainer) {
        string documentArea =
          this.GetDocumentRootArea(
            repositoryArea
          );

        if (!string.Equals(
              documentArea,
              repositoryArea,
              StringComparison.Ordinal
            )) {
          return this.Redirect(
            this.BuildHtmlAreaRequestPath(
              documentArea
            )
          );
        }
      }

      string[] childAreas =
        Array.Empty<string>();

      // Human navigation intentionally stops at the first concrete content container.
      // Child content containers below it represent headings inside the same Markdown
      // document and are therefore rendered as part of the article instead of links.
      if (contentLevel != ContentLevel.ContentContainer &&
          supportsSubAreas) {
        childAreas =
          _KnowledgeRepository.GetAreas(
            false,
            repositoryArea
          );
      }

      string markdown =
        string.Empty;

      // Structural and aggregation nodes are navigation pages only. This prevents the
      // human facade from aggregating large parent scopes and leads the reader down to
      // concrete article/document boundaries instead.
      if (contentLevel == ContentLevel.ContentContainer) {
        markdown =
          _KnowledgeRepository.GetAggregatedContent(
            repositoryArea
          );

        markdown =
          this.ResolveKnowledgeResourceReferences(
            markdown
          );
      }

      string articleHtml =
        this.RenderMarkdown(
          markdown
        );

      string page =
        this.BuildHtmlDocument(
          repositoryArea,
          title,
          childAreas,
          articleHtml
        );

      return this.Content(
        page,
        _HtmlContentType,
        Encoding.UTF8
      );
    }

    /// <summary>
    /// Builds the complete browser document including breadcrumb navigation, child-area
    /// navigation and rendered content.
    /// </summary>
    private string BuildHtmlDocument(
      string repositoryArea,
      string title,
      string[] childAreas,
      string articleHtml
    ) {
      StringBuilder builder =
        new StringBuilder();

      builder.Append("<!doctype html>");
      builder.Append("<html lang=\"en\">");
      builder.Append("<head>");
      builder.Append("<meta charset=\"utf-8\">");
      builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
      builder.Append("<title>");
      builder.Append(
        WebUtility.HtmlEncode(
          title
        )
      );
      builder.Append("</title>");
      builder.Append("<style>");
      builder.Append(this.GetPageCss());
      builder.Append("</style>");
      builder.Append("</head>");
      builder.Append("<body>");
      builder.Append("<main class=\"page\">");

      builder.Append(
        this.BuildBreadcrumbHtml(
          repositoryArea
        )
      );

      builder.Append("<header class=\"page-header\">");
      builder.Append("<h1>");
      builder.Append(
        WebUtility.HtmlEncode(
          title
        )
      );
      builder.Append("</h1>");
      builder.Append("</header>");

      if (childAreas.Length > 0) {
        builder.Append(
          this.BuildChildNavigationHtml(
            childAreas
          )
        );
      }

      if (!string.IsNullOrWhiteSpace(
            articleHtml
          )) {
        builder.Append("<article class=\"content\">");
        builder.Append(
          articleHtml
        );
        builder.Append("</article>");
      }

      builder.Append("</main>");
      builder.Append("</body>");
      builder.Append("</html>");

      return builder.ToString();
    }

    /// <summary>
    /// Builds one breadcrumb trail from repository root to the current logical area.
    /// </summary>
    private string BuildBreadcrumbHtml(
      string repositoryArea
    ) {
      StringBuilder builder =
        new StringBuilder();

      builder.Append("<nav class=\"breadcrumbs\" aria-label=\"Breadcrumb\">");

      string rootName =
        this.GetAreaDisplayName(
          "/"
        );

      builder.Append("<a href=\"");
      builder.Append(
        WebUtility.HtmlEncode(
          this.BuildHtmlAreaRequestPath(
            "/"
          )
        )
      );
      builder.Append("\">");
      builder.Append(
        WebUtility.HtmlEncode(
          rootName
        )
      );
      builder.Append("</a>");

      if (repositoryArea != "/") {
        string[] segments =
          repositoryArea.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries
          );

        string currentPath =
          string.Empty;

        foreach (string segment in segments) {
          currentPath +=
            "/"
            + segment;

          builder.Append("<span class=\"breadcrumb-separator\">/</span>");

          string displayName =
            this.GetAreaDisplayName(
              currentPath
            );

          if (string.Equals(
                currentPath,
                repositoryArea,
                StringComparison.Ordinal
              )) {
            builder.Append("<span aria-current=\"page\">");
            builder.Append(
              WebUtility.HtmlEncode(
                displayName
              )
            );
            builder.Append("</span>");
          }
          else {
            builder.Append("<a href=\"");
            builder.Append(
              WebUtility.HtmlEncode(
                this.BuildHtmlAreaRequestPath(
                  currentPath
                )
              )
            );
            builder.Append("\">");
            builder.Append(
              WebUtility.HtmlEncode(
                displayName
              )
            );
            builder.Append("</a>");
          }
        }
      }

      builder.Append("</nav>");

      return builder.ToString();
    }

    /// <summary>
    /// Builds clickable direct-child navigation for one logical area.
    /// </summary>
    private string BuildChildNavigationHtml(
      string[] childAreas
    ) {
      StringBuilder builder =
        new StringBuilder();

      builder.Append("<nav class=\"children\" aria-label=\"Sub areas\">");
      builder.Append("<h2>Sub areas</h2>");
      builder.Append("<ul>");

      foreach (string childArea in childAreas) {
        string displayName =
          this.GetAreaDisplayName(
            childArea
          );

        builder.Append("<li><a href=\"");
        builder.Append(
          WebUtility.HtmlEncode(
            this.BuildHtmlAreaRequestPath(
              childArea
            )
          )
        );
        builder.Append("\">");
        builder.Append(
          WebUtility.HtmlEncode(
            displayName
          )
        );
        builder.Append("</a></li>");
      }

      builder.Append("</ul>");
      builder.Append("</nav>");

      return builder.ToString();
    }

    /// <summary>
    /// Replaces provider-neutral resource references with absolute URLs of the raw REST
    /// resource endpoint. Markdown image syntax therefore becomes directly embeddable HTML
    /// during the subsequent Markdown rendering step.
    /// </summary>
    private string ResolveKnowledgeResourceReferences(
      string markdown
    ) {
      if (string.IsNullOrEmpty(markdown)) {
        return markdown;
      }

      return _KnowledgeResourceReferenceRegex.Replace(
        markdown,
        (Match match) => {
          string resourceId =
            match.Groups["id"].Value;

          return this.BuildAbsoluteResourceUrl(
            resourceId
          );
        }
      );
    }

    /// <summary>
    /// Builds an absolute URL to the raw resource endpoint exposed by
    /// <see cref="KnowledgeRepositoryController"/>.
    /// </summary>
    private string BuildAbsoluteResourceUrl(
      string resourceId
    ) {
      string url = this.Url.RouteUrl(
        KnowledgeRepositoryHttpRouteNames._RawResource,
        new {
          resourceId = resourceId
        },
        this.Request.Scheme,
        this.Request.Host.Value
      );

      if (string.IsNullOrWhiteSpace(url)) {
        throw new InvalidOperationException(
          "The ASP.NET Core route for the knowledge resource endpoint could not be resolved."
        );
      }

      return url;
    }

    /// <summary>
    /// Renders the Markdown subset commonly produced by knowledge repositories into safe
    /// server-side HTML without adding another runtime package dependency.
    ///
    /// Supported block constructs include headings, paragraphs, fenced code, blockquotes,
    /// ordered and unordered lists, horizontal rules and ordinary pipe tables. Supported
    /// inline constructs include images, links, inline code, bold and italic text.
    /// </summary>
    private string RenderMarkdown(
      string markdown
    ) {
      if (string.IsNullOrWhiteSpace(markdown)) {
        return string.Empty;
      }

      string normalized =
        markdown.Replace(
          "\r\n",
          "\n",
          StringComparison.Ordinal
        ).Replace(
          '\r',
          '\n'
        );

      string[] lines =
        normalized.Split('\n');

      StringBuilder builder =
        new StringBuilder();

      bool inCodeBlock =
        false;

      bool inUnorderedList =
        false;

      bool inOrderedList =
        false;

      StringBuilder paragraph =
        new StringBuilder();

      for (int index = 0;
           index < lines.Length;
           index++) {

        string line =
          lines[index];

        if (line.StartsWith(
              "```",
              StringComparison.Ordinal
            )) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          this.CloseLists(
            builder,
            ref inUnorderedList,
            ref inOrderedList
          );

          if (!inCodeBlock) {
            inCodeBlock =
              true;

            builder.Append("<pre><code>");
          }
          else {
            inCodeBlock =
              false;

            builder.Append("</code></pre>");
          }

          continue;
        }

        if (inCodeBlock) {
          builder.Append(
            WebUtility.HtmlEncode(
              line
            )
          );
          builder.Append('\n');
          continue;
        }

        if (string.IsNullOrWhiteSpace(
              line
            )) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          this.CloseLists(
            builder,
            ref inUnorderedList,
            ref inOrderedList
          );

          continue;
        }

        if (this.IsHorizontalRule(
              line
            )) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          this.CloseLists(
            builder,
            ref inUnorderedList,
            ref inOrderedList
          );

          builder.Append("<hr>");
          continue;
        }

        Match headingMatch =
          _HeadingRegex.Match(
            line
          );

        if (headingMatch.Success) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          this.CloseLists(
            builder,
            ref inUnorderedList,
            ref inOrderedList
          );

          int headingLevel =
            headingMatch.Groups["level"].Value.Length;

          builder.Append("<h");
          builder.Append(
            headingLevel
          );
          builder.Append('>');
          builder.Append(
            this.RenderInlineMarkdown(
              headingMatch.Groups["text"].Value
            )
          );
          builder.Append("</h");
          builder.Append(
            headingLevel
          );
          builder.Append('>');

          continue;
        }

        if (line.StartsWith(
              ">",
              StringComparison.Ordinal
            )) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          this.CloseLists(
            builder,
            ref inUnorderedList,
            ref inOrderedList
          );

          string quote =
            line.Substring(
              1
            ).TrimStart();

          builder.Append("<blockquote>");
          builder.Append(
            this.RenderInlineMarkdown(
              quote
            )
          );
          builder.Append("</blockquote>");

          continue;
        }

        Match unorderedMatch =
          _UnorderedListRegex.Match(
            line
          );

        if (unorderedMatch.Success) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          if (inOrderedList) {
            builder.Append("</ol>");
            inOrderedList =
              false;
          }

          if (!inUnorderedList) {
            builder.Append("<ul>");
            inUnorderedList =
              true;
          }

          builder.Append("<li>");
          builder.Append(
            this.RenderInlineMarkdown(
              unorderedMatch.Groups["text"].Value
            )
          );
          builder.Append("</li>");

          continue;
        }

        Match orderedMatch =
          _OrderedListRegex.Match(
            line
          );

        if (orderedMatch.Success) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          if (inUnorderedList) {
            builder.Append("</ul>");
            inUnorderedList =
              false;
          }

          if (!inOrderedList) {
            builder.Append("<ol>");
            inOrderedList =
              true;
          }

          builder.Append("<li>");
          builder.Append(
            this.RenderInlineMarkdown(
              orderedMatch.Groups["text"].Value
            )
          );
          builder.Append("</li>");

          continue;
        }

        if (index + 1 < lines.Length &&
            line.Contains(
              "|",
              StringComparison.Ordinal
            ) &&
            _TableSeparatorRegex.IsMatch(
              lines[index + 1]
            )) {
          this.FlushParagraph(
            builder,
            paragraph
          );

          this.CloseLists(
            builder,
            ref inUnorderedList,
            ref inOrderedList
          );

          index =
            this.RenderMarkdownTable(
              builder,
              lines,
              index
            );

          continue;
        }

        if (paragraph.Length > 0) {
          paragraph.Append(' ');
        }

        paragraph.Append(
          line.Trim()
        );
      }

      this.FlushParagraph(
        builder,
        paragraph
      );

      this.CloseLists(
        builder,
        ref inUnorderedList,
        ref inOrderedList
      );

      if (inCodeBlock) {
        builder.Append("</code></pre>");
      }

      return builder.ToString();
    }

    /// <summary>
    /// Renders one simple pipe-table and returns the index of the last consumed line.
    /// </summary>
    private int RenderMarkdownTable(
      StringBuilder builder,
      string[] lines,
      int headerIndex
    ) {
      string[] headers =
        this.SplitTableRow(
          lines[headerIndex]
        );

      builder.Append("<div class=\"table-scroll\"><table><thead><tr>");

      foreach (string header in headers) {
        builder.Append("<th>");
        builder.Append(
          this.RenderInlineMarkdown(
            header.Trim()
          )
        );
        builder.Append("</th>");
      }

      builder.Append("</tr></thead><tbody>");

      int index =
        headerIndex + 2;

      while (index < lines.Length &&
             !string.IsNullOrWhiteSpace(
               lines[index]
             ) &&
             lines[index].Contains(
               "|",
               StringComparison.Ordinal
             )) {
        string[] cells =
          this.SplitTableRow(
            lines[index]
          );

        builder.Append("<tr>");

        for (int cellIndex = 0;
             cellIndex < headers.Length;
             cellIndex++) {
          string cell =
            string.Empty;

          if (cellIndex < cells.Length) {
            cell =
              cells[cellIndex];
          }

          builder.Append("<td>");
          builder.Append(
            this.RenderInlineMarkdown(
              cell.Trim()
            )
          );
          builder.Append("</td>");
        }

        builder.Append("</tr>");
        index++;
      }

      builder.Append("</tbody></table></div>");

      return index - 1;
    }

    /// <summary>
    /// Splits one Markdown pipe-table row while tolerating optional leading and trailing
    /// pipe characters.
    /// </summary>
    private string[] SplitTableRow(
      string line
    ) {
      string normalized =
        line.Trim();

      if (normalized.StartsWith(
            "|",
            StringComparison.Ordinal
          )) {
        normalized =
          normalized.Substring(
            1
          );
      }

      if (normalized.EndsWith(
            "|",
            StringComparison.Ordinal
          )) {
        normalized =
          normalized.Substring(
            0,
            normalized.Length - 1
          );
      }

      return normalized.Split(
        '|'
      );
    }

    /// <summary>
    /// Renders the supported inline Markdown constructs while HTML-encoding all ordinary
    /// text and attributes.
    /// </summary>
    private string RenderInlineMarkdown(
      string text
    ) {
      if (string.IsNullOrEmpty(text)) {
        return string.Empty;
      }

      StringBuilder builder =
        new StringBuilder();

      int position =
        0;

      MatchCollection matches =
        _InlineTokenRegex.Matches(
          text
        );

      foreach (Match match in matches) {
        if (match.Index > position) {
          builder.Append(
            WebUtility.HtmlEncode(
              text.Substring(
                position,
                match.Index - position
              )
            )
          );
        }

        if (match.Groups["image"].Success) {
          string imageUrl =
            this.NormalizeUrl(
              match.Groups["imageUrl"].Value,
              true
            );

          builder.Append("<img src=\"");
          builder.Append(
            WebUtility.HtmlEncode(
              imageUrl
            )
          );
          builder.Append("\" alt=\"");
          builder.Append(
            WebUtility.HtmlEncode(
              match.Groups["imageAlt"].Value
            )
          );
          builder.Append("\" loading=\"lazy\">");
        }
        else if (match.Groups["link"].Success) {
          string linkUrl =
            this.NormalizeUrl(
              match.Groups["linkUrl"].Value,
              false
            );

          builder.Append("<a href=\"");
          builder.Append(
            WebUtility.HtmlEncode(
              linkUrl
            )
          );
          builder.Append("\">");
          builder.Append(
            WebUtility.HtmlEncode(
              match.Groups["linkText"].Value
            )
          );
          builder.Append("</a>");
        }
        else if (match.Groups["code"].Success) {
          builder.Append("<code>");
          builder.Append(
            WebUtility.HtmlEncode(
              match.Groups["codeText"].Value
            )
          );
          builder.Append("</code>");
        }
        else if (match.Groups["bold"].Success) {
          builder.Append("<strong>");
          builder.Append(
            WebUtility.HtmlEncode(
              match.Groups["boldText"].Value
            )
          );
          builder.Append("</strong>");
        }
        else if (match.Groups["italic"].Success) {
          builder.Append("<em>");
          builder.Append(
            WebUtility.HtmlEncode(
              match.Groups["italicText"].Value
            )
          );
          builder.Append("</em>");
        }

        position =
          match.Index
          + match.Length;
      }

      if (position < text.Length) {
        builder.Append(
          WebUtility.HtmlEncode(
            text.Substring(
              position
            )
          )
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Restricts rendered Markdown URLs to ordinary browser-safe schemes.
    /// </summary>
    private string NormalizeUrl(
      string url,
      bool image
    ) {
      string trimmed =
        url.Trim();

      if (trimmed.StartsWith(
            "/",
            StringComparison.Ordinal
          ) ||
          trimmed.StartsWith(
            "#",
            StringComparison.Ordinal
          )) {
        return trimmed;
      }

      if (!Uri.TryCreate(
            trimmed,
            UriKind.Absolute,
            out Uri absoluteUri
          )) {
        return trimmed;
      }

      if (string.Equals(
            absoluteUri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase
          ) ||
          string.Equals(
            absoluteUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase
          )) {
        return trimmed;
      }

      if (!image &&
          string.Equals(
            absoluteUri.Scheme,
            Uri.UriSchemeMailto,
            StringComparison.OrdinalIgnoreCase
          )) {
        return trimmed;
      }

      return "#";
    }

    /// <summary>
    /// Flushes accumulated paragraph text as one HTML paragraph.
    /// </summary>
    private void FlushParagraph(
      StringBuilder builder,
      StringBuilder paragraph
    ) {
      if (paragraph.Length == 0) {
        return;
      }

      builder.Append("<p>");
      builder.Append(
        this.RenderInlineMarkdown(
          paragraph.ToString()
        )
      );
      builder.Append("</p>");

      paragraph.Clear();
    }

    /// <summary>
    /// Closes any currently open list block.
    /// </summary>
    private void CloseLists(
      StringBuilder builder,
      ref bool inUnorderedList,
      ref bool inOrderedList
    ) {
      if (inUnorderedList) {
        builder.Append("</ul>");
        inUnorderedList =
          false;
      }

      if (inOrderedList) {
        builder.Append("</ol>");
        inOrderedList =
          false;
      }
    }

    /// <summary>
    /// Determines whether one Markdown line is a horizontal rule.
    /// </summary>
    private bool IsHorizontalRule(
      string line
    ) {
      string trimmed =
        line.Trim();

      return string.Equals(
               trimmed,
               "---",
               StringComparison.Ordinal
             ) ||
             string.Equals(
               trimmed,
               "***",
               StringComparison.Ordinal
             ) ||
             string.Equals(
               trimmed,
               "___",
               StringComparison.Ordinal
             );
    }

    /// <summary>
    /// Resolves the human-visible document root for one content-container area.
    ///
    /// The first content container below a non-container parent is treated as the original
    /// document boundary. Consecutive content-container descendants are interpreted as
    /// headings or sections belonging to that same document.
    /// </summary>
    private string GetDocumentRootArea(
      string repositoryArea
    ) {
      string currentArea =
        repositoryArea;

      while (!string.Equals(
        currentArea,
        "/",
        StringComparison.Ordinal
      )) {
        string parentArea =
          this.GetParentArea(
            currentArea
          );

        ContentLevel parentContentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;
        bool supportsResources;

        bool parentExists =
          this.TryGetAreaCapabilities(
            parentArea,
            out parentContentLevel,
            out supportsSubAreas,
            out canBeRenamed,
            out canBeDeleted,
            out canAddSubAreas,
            out canAppendContent,
            out canTruncate,
            out supportsResources
          );

        if (!parentExists ||
            parentContentLevel != ContentLevel.ContentContainer) {
          return currentArea;
        }

        currentArea =
          parentArea;
      }

      return currentArea;
    }

    /// <summary>
    /// Returns the logical parent path for one repository area.
    /// </summary>
    private string GetParentArea(
      string repositoryArea
    ) {
      if (string.IsNullOrWhiteSpace(
            repositoryArea
          ) ||
          string.Equals(
            repositoryArea,
            "/",
            StringComparison.Ordinal
          )) {
        return "/";
      }

      string normalized =
        repositoryArea.TrimEnd('/');

      int separatorIndex =
        normalized.LastIndexOf('/');

      if (separatorIndex <= 0) {
        return "/";
      }

      return normalized.Substring(
        0,
        separatorIndex
      );
    }

    /// <summary>
    /// Returns the direct provider-neutral display name for one logical area.
    /// </summary>
    private string GetAreaDisplayName(
      string repositoryArea
    ) {
      try {
        return _KnowledgeRepository.GetAreaName(
          repositoryArea
        );
      }
      catch (InvalidOperationException) {
        if (repositoryArea == "/") {
          return "Knowledge";
        }

        string[] segments =
          repositoryArea.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries
          );

        if (segments.Length == 0) {
          return "Knowledge";
        }

        return segments[
          segments.Length - 1
        ];
      }
    }

    /// <summary>
    /// Builds the HTML-controller request path for one logical repository area.
    /// </summary>
    private string BuildHtmlAreaRequestPath(
      string repositoryArea
    ) {
      string routeName;
      object routeValues;

      if (string.Equals(
            repositoryArea,
            "/",
            StringComparison.Ordinal
          )) {
        routeName = KnowledgeRepositoryHttpRouteNames._HtmlRoot;
        routeValues = new { };
      }
      else {
        routeName = KnowledgeRepositoryHttpRouteNames._HtmlArea;
        routeValues = new {
          area = repositoryArea.TrimStart('/')
        };
      }

      string url = this.Url.RouteUrl(
        routeName,
        routeValues
      );

      if (string.IsNullOrWhiteSpace(url)) {
        throw new InvalidOperationException(
          "The ASP.NET Core route for the human-readable knowledge endpoint could not be resolved."
        );
      }

      return url;
    }

    /// <summary>
    /// Converts the controller catch-all route value into one canonical repository area.
    /// </summary>
    private string ToRepositoryArea(
      string area
    ) {
      if (string.IsNullOrWhiteSpace(area)) {
        return "/";
      }

      string normalized =
        area.Replace(
          '\\',
          '/'
        ).Trim('/');

      if (string.IsNullOrEmpty(normalized)) {
        return "/";
      }

      return "/"
        + normalized;
    }

    /// <summary>
    /// Resolves area capabilities and converts provider-specific missing-area exceptions
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
      contentLevel =
        ContentLevel.BeyondContent;

      supportsSubAreas =
        false;

      canBeRenamed =
        false;

      canBeDeleted =
        false;

      canAddSubAreas =
        false;

      canAppendContent =
        false;

      canTruncate =
        false;

      supportsResources =
        false;

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

    /// <summary>
    /// Returns the intentionally self-contained stylesheet used by the HTML browser.
    /// </summary>
    private string GetPageCss() {
      return @"
html {
  background: #f7f8fa;
  color: #20242a;
  font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", sans-serif;
  line-height: 1.6;
}
body {
  margin: 0;
}
.page {
  box-sizing: border-box;
  max-width: 1100px;
  margin: 0 auto;
  padding: 28px 24px 72px 24px;
}
.breadcrumbs {
  margin-bottom: 22px;
  color: #5c6673;
  font-size: 14px;
}
.breadcrumbs a {
  color: #315f9f;
  text-decoration: none;
}
.breadcrumbs a:hover {
  text-decoration: underline;
}
.breadcrumb-separator {
  padding: 0 8px;
  color: #9aa2ad;
}
.page-header {
  border-bottom: 1px solid #d9dee5;
  margin-bottom: 24px;
}
.page-header h1 {
  margin: 0 0 14px 0;
  font-size: 30px;
  line-height: 1.25;
}
.children {
  background: #ffffff;
  border: 1px solid #dde2e8;
  border-radius: 8px;
  margin-bottom: 28px;
  padding: 16px 20px;
}
.children h2 {
  margin: 0 0 8px 0;
  font-size: 18px;
}
.children ul {
  margin: 0;
  padding-left: 22px;
}
.children a {
  color: #315f9f;
  text-decoration: none;
}
.children a:hover {
  text-decoration: underline;
}
.content {
  background: #ffffff;
  border: 1px solid #dde2e8;
  border-radius: 8px;
  padding: 24px 28px;
  overflow-wrap: anywhere;
}
.content h1,
.content h2,
.content h3,
.content h4,
.content h5,
.content h6 {
  line-height: 1.3;
  margin-top: 1.5em;
}
.content img {
  display: block;
  max-width: 100%;
  height: auto;
  margin: 20px auto;
}
.content pre {
  overflow-x: auto;
  padding: 16px;
  background: #f3f5f7;
  border-radius: 6px;
}
.content code {
  font-family: Consolas, ""Courier New"", monospace;
}
.content blockquote {
  margin-left: 0;
  padding-left: 16px;
  border-left: 4px solid #cbd3dd;
  color: #56606d;
}
.content a {
  color: #315f9f;
}
.table-scroll {
  overflow-x: auto;
}
.content table {
  width: 100%;
  border-collapse: collapse;
  margin: 20px 0;
}
.content th,
.content td {
  border: 1px solid #d9dee5;
  padding: 8px 10px;
  text-align: left;
  vertical-align: top;
}
.content th {
  background: #f3f5f7;
}
";

    }

  }

}
