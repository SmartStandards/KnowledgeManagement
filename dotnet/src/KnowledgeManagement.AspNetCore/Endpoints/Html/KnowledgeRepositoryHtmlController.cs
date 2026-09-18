using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace KnowledgeManagement.SmartStandards.Endpoints.Html {

  /// <summary>
  /// Provider-neutral HTML facade matching KornSW KnowledgeRepo 0.1.2.
  /// Host middleware owns read authentication/authorization, including raw resources.
  /// The optional CanEdit hook controls editing and refresh, on both GET and POST.
  /// Cache contains protected repository values only, never HTML, permissions or CSRF tokens.
  /// </summary>
  [ApiController]
  [ApiExplorerSettings(IgnoreApi = true)]
  [Route("api/knowledge")]
  public class KnowledgeRepositoryHtmlController : ControllerBase {
    private const string _HtmlContentType = "text/html; charset=utf-8";
    private static readonly Regex _KnowledgeResourceReferenceRegex = new Regex(@"knowledge-resource:(?<id>[A-Za-z0-9._~-]+)", RegexOptions.Compiled);
    private static readonly Regex _HeadingRegex = new Regex(@"^ {0,3}(?<level>#{1,6})[ \t]+(?<text>.+?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex _UnorderedListRegex = new Regex(@"^\s*[-*+]\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex _OrderedListRegex = new Regex(@"^\s*\d+\.\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex _TableSeparatorRegex = new Regex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex _InlineTokenRegex = new Regex(
      @"(?<image>!\[(?<imageAlt>[^\]]*)\]\((?<imageUrl>[^)\s]+)(?:\s+""[^""]*"")?\))|(?<link>\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)(?:\s+""[^""]*"")?\))|(?<code>`(?<codeText>[^`]+)`)|(?<bold>\*\*(?<boldText>.+?)\*\*)|(?<italic>\*(?<italicText>[^*]+)\*)", RegexOptions.Compiled);
    private static readonly object[] _CacheLocks = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();
    private readonly IKnowledgeRepository _KnowledgeRepository;
    private readonly KnowledgeRepositoryHtmlOptions _Options;
    private readonly IAntiforgery _Antiforgery;
    private readonly IDataProtector _Protector;
    private readonly ILogger<KnowledgeRepositoryHtmlController> _Logger;
    private readonly Dictionary<string, object> _Memo = new Dictionary<string, object>(StringComparer.Ordinal);
    private string _CacheEpoch;

    public KnowledgeRepositoryHtmlController(IKnowledgeRepository knowledgeRepository,
      KnowledgeRepositoryHtmlOptions options = null, IAntiforgery antiforgery = null,
      IDataProtectionProvider dataProtection = null, ILogger<KnowledgeRepositoryHtmlController> logger = null) {
      _KnowledgeRepository = knowledgeRepository ?? throw new ArgumentNullException(nameof(knowledgeRepository));
      _Options = options ?? new KnowledgeRepositoryHtmlOptions();
      _Antiforgery = antiforgery;
      _Logger = logger;
      if (!string.IsNullOrWhiteSpace(_Options.CacheDirectory) && !Path.IsPathFullyQualified(_Options.CacheDirectory)) {
        throw new ArgumentException("CacheDirectory must be an absolute private path.");
      }
      if (!string.IsNullOrWhiteSpace(_Options.CacheDirectory) && _Options.CacheLifetime > TimeSpan.Zero) {
        if (dataProtection == null)
          throw new InvalidOperationException("Register AddDataProtection() to enable the repository cache.");
        _Protector = dataProtection.CreateProtector("KnowledgeRepository.Html.Cache.v1", _Options.CacheNamespace);
      }
    }

    [HttpGet(Name = KnowledgeRepositoryHttpRouteNames._HtmlRoot)]
    public IActionResult GetRoot() { return GetAreaInternal("/"); }

    [HttpGet("{**area}", Name = KnowledgeRepositoryHttpRouteNames._HtmlArea)]
    public IActionResult GetArea(string area) {
      try { return GetAreaInternal(ToRepositoryArea(area)); }
      catch (ArgumentException) { return BadRequest("Ungültiger Bereich."); }
    }

    [HttpGet("_search", Name = KnowledgeRepositoryHttpRouteNames._HtmlSearch)]
    public IActionResult Search([FromQuery] string q = "") {
      NoStore();
      q = (q ?? "").Trim();
      if (q.Length > 200)
        return BadRequest(new { fault = "Suchbegriff ist zu lang." });
      try {
        var matches = q.Length == 0 ? Array.Empty<string>() : Read("search", q, () => _KnowledgeRepository.GetAreasByKeyword(q, "/"));
        var results = new List<object>();
        foreach (string area in matches.Take(100)) {
          string document = DocumentRoot(area);
          string url = BuildHtmlAreaRequestPath(document);
          if (document != area) {
            var view = Document(document);
            if (view.Anchors.TryGetValue(area, out string anchor))
              url += "#" + anchor;
          }
          string text = Regex.Replace(Read("direct", area, () => _KnowledgeRepository.GetDirectContent(area)) ?? "", @"\s+", " ");
          int position = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
          int start = Math.Max(0, position - 70);
          string snippet = (start > 0 ? "…" : "") + text.Substring(start, Math.Min(220, text.Length - start));
          if (text.Length > start + 220)
            snippet += "…";
          results.Add(new { title = Name(area), path = area, url, snippet });
        }
        return new JsonResult(new { results, more = matches.Length > 100, warnings = Array.Empty<object>() });
      }
      catch (Exception ex) when (IsRepositoryError(ex)) { return RepositoryError(ex); }
    }

    [HttpPost("_refresh", Name = KnowledgeRepositoryHttpRouteNames._HtmlRefresh)]
    [RequestSizeLimit(16777216)]
    public async Task<IActionResult> Refresh([FromForm] string area = "/") {
      NoStore();
      var denied = await ValidateMutation();
      if (denied != null)
        return denied;
      try {
        area = ToRepositoryArea(area);
        InvalidateCache();
        _Options.RefreshRepository?.Invoke(HttpContext);
        return new JsonResult(new { url = BuildHtmlAreaRequestPath(area) });
      }
      catch (ArgumentException) { return BadRequest(new { fault = "Ungültiger Bereich." }); }
      catch (Exception ex) when (IsRepositoryError(ex)) { return RepositoryError(ex); }
    }

    [HttpPost("_edit", Name = KnowledgeRepositoryHttpRouteNames._HtmlEdit)]
    [RequestSizeLimit(16777216)]
    [RequestFormLimits(ValueLengthLimit = 16777216)]
    public async Task<IActionResult> Edit([FromForm] string area = "/", [FromForm] string operation = "",
      [FromForm] string content = "", [FromForm] string name = "", [FromForm] int kind = 1) {
      NoStore();
      var denied = await ValidateMutation();
      if (denied != null)
        return denied;
      try {
        area = ToRepositoryArea(area);
        if (operation != "TryReplace" && operation != "TryAppendContent" && operation != "TryAddSubArea") {
          return BadRequest(new { fault = "Unbekannte Aktion." });
        }
        if (operation == "TryAddSubArea" && (string.IsNullOrWhiteSpace(name) || name.Length > 512 || (kind != 0 && kind != 1))) {
          return BadRequest(new { fault = "Ungültiger Name oder Bereichstyp." });
        }
        // Check cache invalidation before writing and again after: old concurrent loaders
        // must never publish their snapshot into the post-write generation.
        InvalidateCache();
        bool success;
        try {
          success = operation == "TryReplace" ? _KnowledgeRepository.TryReplace(area, content ?? "")
            : operation == "TryAppendContent" ? _KnowledgeRepository.TryAppendContent(area, content ?? "")
            : _KnowledgeRepository.TryAddSubArea(area, name.Trim(), (KnowledgeAreaKind)kind);
        }
        finally { InvalidateCache(); }
        if (!success)
          return Conflict(new { fault = "Die Quelle unterstützt diese Änderung nicht oder lehnt sie ab." });
        return new JsonResult(new { url = BuildHtmlAreaRequestPath(area) });
      }
      catch (ArgumentException) { return BadRequest(new { fault = "Ungültige Eingabe." }); }
      catch (Exception ex) when (IsRepositoryError(ex)) { return RepositoryError(ex); }
    }

    private async Task<IActionResult> ValidateMutation() {
      if (_Options.CanEdit?.Invoke(HttpContext) != true)
        return StatusCode(403, new { fault = "Zugriff nicht erlaubt." });
      if (_Antiforgery == null)
        throw new InvalidOperationException("Register AddAntiforgery() before enabling CanEdit.");
      try { await _Antiforgery.ValidateRequestAsync(HttpContext); }
      catch (AntiforgeryValidationException) { return BadRequest(new { fault = "Sitzung abgelaufen. Bitte Seite neu laden." }); }
      return null;
    }

    private void NoStore() {
      Response.Headers["Cache-Control"] = "no-store";
      Response.Headers["X-Content-Type-Options"] = "nosniff";
      Response.Headers["Referrer-Policy"] = "same-origin";
    }
    private static bool IsRepositoryError(Exception ex) {
      return ex is IOException || ex is InvalidOperationException || ex is System.Net.Http.HttpRequestException
        || ex is UnauthorizedAccessException || ex is TimeoutException;
    }
    private IActionResult RepositoryError(Exception ex) {
      _Logger?.LogWarning(ex, "Knowledge repository operation failed");
      return StatusCode(503, new { fault = "Die Wissensquelle ist momentan nicht verfügbar oder lehnt die Operation ab." });
    }
    private static string H(string value) { return WebUtility.HtmlEncode(value ?? ""); }
    private static string Hash(string value) {
      using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
    }
    private string Name(string area) { return Read("name", area, () => _KnowledgeRepository.GetAreaName(area)); }
    private string[] Areas(string area, bool recurse) { return Read(recurse ? "descendants" : "children", area, () => _KnowledgeRepository.GetAreas(recurse, area)); }
    private ContentLevel Level(string area) {
      return Read("level", area, () => {
        _KnowledgeRepository.GetAreaCapabilities(area, out ContentLevel level, out _, out _, out _, out _, out _, out _, out _);
        return level;
      });
    }
    private static string Parent(string area) {
      int i = area.TrimEnd('/').LastIndexOf('/');
      return i <= 0 ? "/" : area.Substring(0, i);
    }
    private static string ToRepositoryArea(string area) {
      if (string.IsNullOrWhiteSpace(area))
        return "/";
      if (area.Any(c => char.IsControl(c) || c == '\\'))
        throw new ArgumentException("Invalid area");
      var parts = area.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Any(p => p == "." || p == ".."))
        throw new ArgumentException("Invalid area");
      return "/" + string.Join("/", parts);
    }
    private string DocumentRoot(string area) {
      var chain = new List<string>();
      for (string p = area; p != "/"; p = Parent(p))
        chain.Add(p);
      chain.Add("/");
      chain.Reverse();
      foreach (string candidate in chain) { if (Level(candidate) == ContentLevel.ContentContainer) return candidate; }
      return area;
    }
    private string Route(string name, object values = null) {
      return Url.RouteUrl(name, values) ?? throw new InvalidOperationException("Knowledge HTTP route not registered: " + name);
    }
    private string BuildHtmlAreaRequestPath(string area) {
      return area == "/" ? Route(KnowledgeRepositoryHttpRouteNames._HtmlRoot)
        : Route(KnowledgeRepositoryHttpRouteNames._HtmlArea, new { area = area.TrimStart('/') });
    }
    private string ResolveKnowledgeResourceReferences(string markdown) {
      return _KnowledgeResourceReferenceRegex.Replace(markdown ?? "", m => Route(KnowledgeRepositoryHttpRouteNames._RawResource, new { resourceId = m.Groups["id"].Value }));
    }

    private sealed class Heading {
      public string Id; public string Label; public int Level;
    }
    private sealed class DocumentView {
      public string Markdown; public string Html;
      public List<Heading> Outline = new List<Heading>();
      public Dictionary<string, string> Anchors = new Dictionary<string, string>(StringComparer.Ordinal);
    }
    private DocumentView Document(string area) {
      string memoKey = "view:" + area;
      if (_Memo.TryGetValue(memoKey, out object cached))
        return (DocumentView)cached;
      var view = new DocumentView { Markdown = Read("aggregate", area, () => _KnowledgeRepository.GetAggregatedContent(area)) ?? "" };
      string[] children = Areas(area, true);
      string[] names = children.Select(p => Plain(RenderInlineMarkdown(Name(p)))).ToArray();
      int cursor = 0;
      var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
      view.Html = Regex.Replace(RenderMarkdown(ResolveKnowledgeResourceReferences(view.Markdown)), @"<h([1-6])>(.*?)</h\1>", match => {
        string label = Plain(match.Groups[2].Value);
        string logical = null;
        for (int i = cursor; i < children.Length; i++) {
          if (names[i] == label) { logical = children[i]; cursor = i + 1; break; }
        }
        occurrences.TryGetValue(label, out int count);
        occurrences[label] = ++count;
        string id = "section-" + Hash(logical ?? area + "\0" + label + "\0" + count).Substring(0, 16);
        if (logical != null)
          view.Anchors[logical] = id;
        view.Outline.Add(new Heading { Id = id, Label = label, Level = int.Parse(match.Groups[1].Value) });
        return "<h" + match.Groups[1].Value + " id=\"" + id + "\">" + match.Groups[2].Value + "</h" + match.Groups[1].Value + ">";
      }, RegexOptions.Singleline);
      _Memo[memoKey] = view;
      return view;
    }
    private static string Plain(string html) { return WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "")).Trim(); }

    private IActionResult GetAreaInternal(string area) {
      NoStore();
      try {
        string document = DocumentRoot(area);
        if (document != area) {
          var view = Document(document);
          return Redirect(BuildHtmlAreaRequestPath(document) + (view.Anchors.TryGetValue(area, out string anchor) ? "#" + anchor : ""));
        }
        bool content = Level(area) == ContentLevel.ContentContainer;
        var article = content ? Document(area) : new DocumentView { Markdown = "", Html = "" };
        string[] children = Areas(content && area != "/" ? Parent(area) : area, false);
        if (content && area == "/")
          children = Array.Empty<string>();
        bool canEdit = _Options.CanEdit?.Invoke(HttpContext) == true;
        if (canEdit && _Antiforgery == null)
          throw new InvalidOperationException("Register AddAntiforgery() before enabling CanEdit.");
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' https: data:; style-src 'nonce-" + nonce + "'; script-src 'nonce-" + nonce + "'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'self'";
        return Content(BuildPage(area, Name(area), children, article, content, canEdit, nonce), _HtmlContentType, Encoding.UTF8);
      }
      catch (InvalidOperationException ex) {
        _Logger?.LogWarning(ex, "Knowledge area cannot be resolved");
        return NotFound("Wissensbereich nicht gefunden oder nicht verfügbar.");
      }
      catch (Exception ex) when (IsRepositoryError(ex)) { return RepositoryError(ex); }
    }

    private string BuildPage(string area, string title, string[] children, DocumentView view, bool content, bool canEdit, string nonce) {
      var b = new StringBuilder();
      b.Append("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
        .Append(H(title)).Append(" · Wissen</title><style nonce=\"").Append(H(nonce)).Append("\">").Append(GetPageCss()).Append("</style></head><body>");
      b.Append("<header class=\"site-header\"><a class=\"brand\" href=\"").Append(H(BuildHtmlAreaRequestPath("/"))).Append("\">Wissen</a><div class=\"header-actions\">");
      b.Append("<form id=\"wiki-search\" role=\"search\" action=\"").Append(H(Route(KnowledgeRepositoryHttpRouteNames._HtmlSearch))).Append("\"><input type=\"search\" name=\"q\" maxlength=\"200\" aria-label=\"Wissen durchsuchen\" placeholder=\"Wissen durchsuchen …\" required><button type=\"submit\" class=\"quiet\">Suchen</button></form>");
      AntiforgeryTokenSet tokens = canEdit ? _Antiforgery.GetAndStoreTokens(HttpContext) : null;
      string hidden = canEdit ? "<input type=\"hidden\" name=\"" + H(tokens.FormFieldName) + "\" value=\"" + H(tokens.RequestToken) + "\"><input type=\"hidden\" name=\"area\" value=\"" + H(area) + "\">" : "";
      if (canEdit) {
        b.Append("<form data-mutation action=\"").Append(H(Route(KnowledgeRepositoryHttpRouteNames._HtmlRefresh))).Append("\" method=\"post\">").Append(hidden)
          .Append("<button type=\"submit\" class=\"quiet\" title=\"Quellen neu laden\">Aktualisieren</button></form><button type=\"button\" class=\"quiet\" data-dialog=\"edit-dialog\">Bearbeiten</button>");
      }
      b.Append("</div></header><nav class=\"breadcrumbs\" aria-label=\"Breadcrumb\"><ol>");
      var crumbs = new List<string>();
      for (string p = area; p != "/"; p = Parent(p))
        crumbs.Add(p);
      crumbs.Add("/");
      crumbs.Reverse();
      foreach (string crumb in crumbs) {
        string label = crumb == "/" ? "Wissen" : Name(crumb);
        b.Append("<li>").Append(crumb == area ? "<span aria-current=\"page\">" + H(label) + "</span>" : "<a href=\"" + H(BuildHtmlAreaRequestPath(crumb)) + "\">" + H(label) + "</a>").Append("</li>");
      }
      b.Append("</ol></nav><div class=\"layout\"><div class=\"sidebar\"><nav class=\"area-nav\" aria-label=\"Wissensbereiche\"><h2>Bereiche</h2>");
      foreach (string child in children)
        b.Append("<a").Append(child == area ? " aria-current=\"page\"" : "").Append(" href=\"").Append(H(BuildHtmlAreaRequestPath(child))).Append("\">").Append(H(Name(child))).Append("</a>");
      b.Append("</nav><aside class=\"outline\" aria-label=\"Dokumentgliederung\">");
      if (view.Outline.Count > 0) {
        b.Append("<h2>Auf dieser Seite</h2><nav>");
        foreach (var heading in view.Outline)
          b.Append("<a class=\"outline-level-").Append(heading.Level).Append("\" href=\"#").Append(heading.Id).Append("\">").Append(H(heading.Label)).Append("</a>");
        b.Append("</nav>");
      }
      b.Append("</aside></div><main><h1>").Append(H(title)).Append("</h1>");
      if (content)
        b.Append("<article id=\"document-content\">").Append(view.Html).Append("</article>");
      else {
        b.Append("<ul class=\"document-list\">");
        foreach (string child in children)
          b.Append("<li><a href=\"").Append(H(BuildHtmlAreaRequestPath(child))).Append("\">").Append(H(Name(child))).Append("</a></li>");
        b.Append("</ul>");
        if (children.Length == 0)
          b.Append("<p class=\"muted\">Hier sind noch keine Inhalte vorhanden.</p>");
      }
      b.Append("</main></div>").Append(DialogStart("search-dialog", "Wissen durchsuchen")).Append("<p id=\"search-status\" role=\"status\"></p><div id=\"search-results\"></div></dialog>");
      b.Append(DialogStart("error-dialog", "Aktualisieren")).Append("<p id=\"action-error\" role=\"alert\"></p></dialog>");
      if (canEdit) {
        string action = H(Route(KnowledgeRepositoryHttpRouteNames._HtmlEdit));
        b.Append(DialogStart("edit-dialog", "Inhalt bearbeiten")).Append("<p id=\"edit-error\" role=\"alert\"></p><form data-mutation method=\"post\" action=\"").Append(action).Append("\">").Append(hidden)
          .Append("<label for=\"edit-content\">Markdown</label><textarea id=\"edit-content\" name=\"content\" rows=\"16\">").Append(H(view.Markdown)).Append("</textarea><div class=\"form-actions\"><button name=\"operation\" value=\"TryReplace\">Inhalt ersetzen</button><button name=\"operation\" value=\"TryAppendContent\">Ergänzung zusammenführen</button></div></form>")
          .Append("<form data-mutation class=\"new-area\" method=\"post\" action=\"").Append(action).Append("\">").Append(hidden)
          .Append("<input name=\"name\" aria-label=\"Name des Unterbereichs\" placeholder=\"Neuer Unterbereich\" required><select name=\"kind\" aria-label=\"Art des Unterbereichs\"><option value=\"1\">Inhalt</option><option value=\"0\">Struktur</option></select><button name=\"operation\" value=\"TryAddSubArea\">Anlegen</button></form></dialog>");
      }
      b.Append("<script nonce=\"").Append(H(nonce)).Append("\">").Append(GetPageScript()).Append("</script></body></html>");
      return b.ToString();
    }
    private static string DialogStart(string id, string title) {
      return "<dialog id=\"" + id + "\" aria-labelledby=\"" + id + "-title\"><div class=\"dialog-head\"><h2 id=\"" + id + "-title\">" + H(title) + "</h2><button type=\"button\" class=\"quiet close-dialog\" aria-label=\"Schließen\">×</button></div>";
    }

    private bool DiskCache => _Protector != null && _Options.CacheLifetime > TimeSpan.Zero;
    private string CacheRoot => Path.Combine(_Options.CacheDirectory, Hash(_Options.CacheNamespace));
    private string Scope() {
      string claims = string.Join("\n", User.Claims.Select(c => c.Type + "=" + c.Value + "@" + c.Issuer).OrderBy(x => x, StringComparer.Ordinal));
      return Hash(Request.Host.Value + "|" + Request.PathBase.Value + "|" + User.Identity?.AuthenticationType + "|" + User.Identity?.IsAuthenticated
        + "|" + claims + "|" + Request.Headers["Authorization"].ToString() + "|" + _Options.CacheScope?.Invoke(HttpContext));
    }
    private sealed class CacheEntry<T> {
      public DateTimeOffset Created { get; set; }
      public T Value { get; set; }
    }
    private T Read<T>(string operation, string argument, Func<T> loader) {
      string local = JsonSerializer.Serialize(new[] { operation, argument });
      if (_Memo.TryGetValue(local, out object memory))
        return (T)memory;
      if (!DiskCache) { T fresh = loader(); _Memo[local] = fresh; return fresh; }
      string file;
      try {
        Directory.CreateDirectory(CacheRoot);
        if (_CacheEpoch == null) {
          string epochFile = Path.Combine(CacheRoot, "epoch");
          _CacheEpoch = System.IO.File.Exists(epochFile) ? System.IO.File.ReadAllText(epochFile) : "0";
        }
        file = Path.Combine(CacheRoot, Hash(_CacheEpoch + "|" + Scope() + "|" + local) + ".cache");
      }
      catch (Exception ex) when (IsCacheError(ex)) { _Logger?.LogWarning(ex, "Knowledge cache unavailable"); T fresh = loader(); _Memo[local] = fresh; return fresh; }
      // In-process stampede protection; atomic files also allow several worker processes.
      lock (_CacheLocks[Convert.ToByte(Path.GetFileName(file).Substring(0, 2), 16)]) {
        if (TryReadCache(file, out T cached)) { _Memo[local] = cached; return cached; }
        T value = loader(); // Never persist failures or turn them into empty repository trees.
        try {
          var entry = new CacheEntry<T> { Created = DateTimeOffset.UtcNow, Value = value };
          AtomicWrite(file, _Protector.Protect(JsonSerializer.SerializeToUtf8Bytes(entry)));
          // Bounded opportunistic cleanup; cache is disposable, never touch sync state.
          if (RandomNumberGenerator.GetInt32(64) == 0) {
            foreach (string old in Directory.EnumerateFiles(CacheRoot, "*.cache").Take(256)) {
              if (System.IO.File.GetLastWriteTimeUtc(old) < DateTime.UtcNow - _Options.CacheLifetime - TimeSpan.FromDays(1))
                System.IO.File.Delete(old);
            }
          }
        }
        catch (Exception ex) when (IsCacheError(ex)) { _Logger?.LogWarning(ex, "Knowledge cache write failed"); }
        _Memo[local] = value;
        return value;
      }
    }
    private bool TryReadCache<T>(string file, out T value) {
      value = default;
      try {
        if (!System.IO.File.Exists(file))
          return false;
        var entry = JsonSerializer.Deserialize<CacheEntry<T>>(_Protector.Unprotect(System.IO.File.ReadAllBytes(file)));
        if (entry == null || entry.Created > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - entry.Created >= _Options.CacheLifetime)
          return false;
        value = entry.Value;
        return true;
      }
      catch (Exception ex) when (IsCacheError(ex)) { return false; }
    }
    private static bool IsCacheError(Exception ex) {
      return ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException || ex is JsonException;
    }
    private static void AtomicWrite(string file, byte[] bytes) {
      string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
      try { System.IO.File.WriteAllBytes(temporary, bytes); System.IO.File.Move(temporary, file, true); }
      finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
    private void InvalidateCache() {
      _Memo.Clear();
      _CacheEpoch = null;
      if (!DiskCache)
        return;
      Directory.CreateDirectory(CacheRoot);
      AtomicWrite(Path.Combine(CacheRoot, "epoch"), Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
    }

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

      bool inCodeBlock = false;
      char fenceCharacter = '`';
      int fenceLength = 0;

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

        Match fence = Regex.Match(line, @"^ {0,3}(`{3,}|~{3,})(.*)$");
        if (fence.Success && (!inCodeBlock ||
            (fence.Groups[1].Value[0] == fenceCharacter && fence.Groups[1].Length >= fenceLength && string.IsNullOrWhiteSpace(fence.Groups[2].Value)))) {
          FlushParagraph(builder, paragraph);
          CloseLists(builder, ref inUnorderedList, ref inOrderedList);
          if (!inCodeBlock) {
            fenceCharacter = fence.Groups[1].Value[0];
            fenceLength = fence.Groups[1].Length;
            inCodeBlock = true;
            builder.Append("<pre><code>");
          }
          else { inCodeBlock = false; builder.Append("</code></pre>"); }
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
      string trimmed = url.Trim();
      if (trimmed.Any(c => char.IsControl(c) || c == '\\'))
        return "#";
      if (Regex.IsMatch(trimmed, @"^[^/?#]*:") && !Regex.IsMatch(trimmed, image ? @"^https?:" : @"^(https?:|mailto:)", RegexOptions.IgnoreCase))
        return "#";

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

    private string GetPageCss() { return @":root{color-scheme:light;--ink:#20372f;--muted:#708079;--line:#e0e7e2;--accent:#216d55}*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:32px}body{margin:0;background:#fafbf9;color:var(--ink);font:16px/1.65 system-ui,sans-serif}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}.site-header{display:flex;align-items:center;justify-content:space-between;gap:24px;padding:16px 32px;background:#fff;border-bottom:1px solid var(--line)}.brand{font-weight:700;font-size:22px;letter-spacing:-.03em}.header-actions{display:flex;gap:16px;align-items:center;font-size:12px}.quiet{border:0;background:none;color:var(--muted);padding:4px 0;cursor:pointer;font:inherit;white-space:nowrap}.quiet:hover{color:var(--accent)}#wiki-search{display:flex;gap:8px;align-items:center}#wiki-search input{width:230px;border:1px solid var(--line);background:#fafbf9;padding:7px 11px;font:13px system-ui}.breadcrumbs{max-width:1560px;margin:22px auto 0;padding:0 32px;font-size:12px;color:var(--muted)}.breadcrumbs ol{display:flex;flex-wrap:wrap;gap:0;list-style:none;padding:0;margin:0}.breadcrumbs li+li:before{content:'/';padding:0 10px;color:#a4b0a9}.breadcrumbs a{color:var(--muted)}.layout{max-width:1560px;display:grid;grid-template-columns:240px minmax(0,1fr);gap:32px;margin:20px auto 48px;padding:0 32px}.area-nav,.outline{font-size:13px;align-self:start;position:sticky;top:24px;max-height:calc(100vh - 48px);overflow:auto}.area-nav h2,.outline h2{margin:10px 0 12px;font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.1em;color:var(--muted)}.area-nav a,.outline a{display:block;padding:6px 10px;border-radius:5px;color:var(--muted);overflow-wrap:anywhere}.area-nav a[aria-current],.outline a[aria-current]{color:var(--accent);background:#edf3ed}.area-nav a:hover,.outline a:hover{color:var(--accent);text-decoration:none;background:#f0f4f0}.outline nav{border-left:1px solid var(--line)}.outline .outline-level-2{padding-left:18px}.outline .outline-level-3{padding-left:28px}.outline .outline-level-4{padding-left:38px}.outline .outline-level-5,.outline .outline-level-6{padding-left:48px}main{min-width:0;background:white;border:1px solid var(--line);border-radius:8px;padding:32px 40px}h1{font-size:30px;line-height:1.25;margin:0 0 28px;letter-spacing:-.03em}article{overflow-wrap:anywhere}article h1,article h2,article h3,article h4,article h5,article h6{scroll-margin-top:28px}article h1{font-size:26px;margin-top:32px}article h2{font-size:23px;margin-top:30px}article h3{font-size:19px;margin-top:24px}article img{max-width:100%;height:auto}pre{overflow:auto;background:#f2f5f1;padding:16px;border-radius:6px}code{font-size:.9em}table{border-collapse:collapse;display:block;overflow:auto;max-width:100%}td,th{padding:8px 12px;border:1px solid var(--line)}blockquote{border-left:3px solid var(--line);margin-left:0;padding-left:20px;color:var(--muted)}.document-list{list-style:none;margin:0;padding:0}.document-list li{border-top:1px solid var(--line)}.document-list a{display:block;padding:14px 0}.muted{color:var(--muted)}.source-status{font-size:12px;color:var(--muted);margin-bottom:20px}.source-status summary{cursor:pointer}.source-status li{margin:8px 0}dialog{width:min(780px,calc(100vw - 32px));max-height:85vh;overflow:auto;border:1px solid var(--line);border-radius:12px;padding:24px 28px;color:var(--ink);box-shadow:0 24px 90px #18332f33}dialog:not([open]){display:none}dialog::backdrop{background:#102c254d}.dialog-head{display:flex;align-items:center;justify-content:space-between;gap:24px;border-bottom:1px solid var(--line);padding-bottom:12px;margin-bottom:20px}.dialog-head h2{font-size:19px;margin:0}.close-dialog{font-size:26px;padding:0 8px}input,select,textarea{border:1px solid #bdcbc2;border-radius:5px;color:var(--ink)}input,select{padding:8px}textarea{display:block;width:100%;padding:12px;font:14px/1.55 ui-monospace,monospace;resize:vertical}dialog button:not(.quiet){background:var(--accent);color:#fff;border:0;border-radius:5px;padding:9px 13px;cursor:pointer;margin:10px 8px 10px 0}dialog label{display:block;font-size:13px;margin-bottom:6px}.new-area{border-top:1px solid var(--line);padding-top:16px;margin-top:14px;display:flex;gap:8px;align-items:center;flex-wrap:wrap}.api-url{display:block;overflow-wrap:anywhere}.notice{background:#eef5ef;padding:10px 14px;border-radius:5px;font-size:14px}.search-result{padding:16px 0;border-bottom:1px solid var(--line)}.search-result>a{font-weight:600;font-size:17px}.search-result small{display:block;color:var(--muted);overflow-wrap:anywhere}.search-result p{margin:6px 0 0;font-size:14px}button:focus-visible,a:focus-visible,input:focus-visible,textarea:focus-visible,select:focus-visible{outline:2px solid var(--accent);outline-offset:3px}@media(max-width:1100px){.layout{grid-template-columns:210px minmax(0,1fr);gap:18px;padding:0 20px}main{padding:24px}.site-header{padding:14px 20px}.breadcrumbs{padding:0 20px}}@media(max-width:800px){.site-header{align-items:flex-start;gap:12px}.header-actions{gap:10px;flex-wrap:wrap;justify-content:flex-end}#wiki-search input{width:170px}.layout{grid-template-columns:minmax(0,1fr)}.area-nav{position:static;display:flex;gap:6px;flex-wrap:wrap;max-height:none}.area-nav h2{width:100%;margin:0}.outline{position:static;grid-row:2;max-height:180px}.outline:empty{display:none}main{grid-row:3}.outline h2{margin-top:0}.outline nav{display:flex;gap:4px;flex-wrap:wrap;border:0}.outline nav a{padding:3px 8px}.breadcrumbs{margin-top:14px}h1{font-size:26px}dialog{padding:18px}}@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}}

.sidebar{align-self:start;position:sticky;top:24px;max-height:calc(100vh - 48px);overflow:auto}.sidebar .area-nav,.sidebar .outline{position:static;max-height:none;overflow:visible}.sidebar .outline{margin-top:28px;border-top:1px solid var(--line);padding-top:10px}.header-actions form{margin:0}@media(max-width:800px){.sidebar{position:static;max-height:none}.sidebar .outline{max-height:220px;overflow:auto}main{grid-row:auto}}
"; }
    private string GetPageScript() { return @"(() => {
  const show = id => { const dialog = document.getElementById(id); if (dialog && !dialog.open) dialog.showModal(); };
  document.querySelectorAll('[data-dialog]').forEach(button => button.addEventListener('click', () => show(button.dataset.dialog)));
  document.querySelectorAll('.close-dialog').forEach(button => button.addEventListener('click', () => button.closest('dialog').close()));
  document.querySelectorAll('dialog').forEach(dialog => dialog.addEventListener('click', event => {
    if (event.target !== dialog) return;
    const r = dialog.getBoundingClientRect();
    if (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom) dialog.close();
  }));
  let controller;
  document.getElementById('wiki-search').addEventListener('submit', async event => {
    event.preventDefault(); const form = event.currentTarget; const query = form.elements.q.value.trim(); if (!query) return;
    if (controller) controller.abort(); const active = new AbortController(); controller = active;
    const status = document.getElementById('search-status'); const results = document.getElementById('search-results');
    results.replaceChildren(); status.textContent = 'Suche läuft …'; show('search-dialog');
    try {
      const response = await fetch(form.action + '?q=' + encodeURIComponent(query), {credentials: 'same-origin', signal: active.signal, headers: {'Accept': 'application/json'}});
      const data = await response.json(); if (!response.ok) throw new Error(data.fault || 'Suche derzeit nicht verfügbar.');
      if (controller !== active) return;
      status.textContent = data.results.length + ' Treffer für „' + query + '“' + (data.more ? ' (erste 100)' : '');
      data.results.forEach(result => {
        const card = document.createElement('div'); card.className = 'search-result'; const link = document.createElement('a');
        link.href = result.url; link.textContent = result.title; link.addEventListener('click', () => document.getElementById('search-dialog').close());
        const path = document.createElement('small'); path.textContent = result.path; const snippet = document.createElement('p'); snippet.textContent = result.snippet;
        card.append(link, path, snippet); results.append(card);
      });
    } catch (error) { if (error.name !== 'AbortError') status.textContent = error.message || 'Suche derzeit nicht verfügbar.'; }
  });
  document.querySelectorAll('form[data-mutation]').forEach(form => form.addEventListener('submit', async event => {
    event.preventDefault();
    if (form.dataset.busy) return;
    const body = new FormData(form); if (event.submitter && event.submitter.name) body.set(event.submitter.name, event.submitter.value);
    const editing = !!form.closest('#edit-dialog');
    const status = document.getElementById(editing ? 'edit-error' : 'action-error'); status.textContent = '';
    form.dataset.busy = 'true'; const buttons = [...form.querySelectorAll('button')]; buttons.forEach(button => button.disabled = true);
    try {
      const response = await fetch(form.action, {method: 'POST', body, credentials: 'same-origin', headers: {'Accept': 'application/json'}});
      const data = await response.json(); if (!response.ok) throw new Error(data.fault || 'Aktion fehlgeschlagen. Bitte Seite neu laden.');
      window.location.assign(data.url);
    } catch (error) { status.textContent = error.message || 'Aktion fehlgeschlagen.'; if (!editing) show('error-dialog'); }
    finally { delete form.dataset.busy; buttons.forEach(button => button.disabled = false); }
  }));
  const outlineLinks = [...document.querySelectorAll('.outline a')];
  if ('IntersectionObserver' in window && outlineLinks.length) {
    const observer = new IntersectionObserver(entries => { for (const entry of entries) if (entry.isIntersecting) {
      outlineLinks.forEach(link => { if (link.hash === '#' + entry.target.id) link.setAttribute('aria-current', 'location'); else link.removeAttribute('aria-current'); });
    } }, {rootMargin: '-8% 0px -70% 0px'});
    document.querySelectorAll('article h1[id],article h2[id],article h3[id],article h4[id],article h5[id],article h6[id]').forEach(h => observer.observe(h));
  }
})();
"; }
  }
}
