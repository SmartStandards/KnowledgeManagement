using System;
using Microsoft.AspNetCore.Http;

namespace KnowledgeManagement.SmartStandards.Endpoints.Html {

  /// <summary>Configuration for the HTML facade. Register one instance in DI.</summary>
  public sealed class KnowledgeRepositoryHtmlOptions {

    /// <summary>Null/empty disables persistent caching. Use an absolute private directory outside wwwroot.</summary>
    public string CacheDirectory { get; set; }

    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Distinct identity for the logical repository/configuration. Change when mounts or credentials change.</summary>
    public string CacheNamespace { get; set; } = "knowledge-html-v1";

    /// <summary>Optional additional tenant/visibility identity. User claims and Authorization remain part of every key.</summary>
    public Func<HttpContext, string> CacheScope { get; set; }

    /// <summary>Evaluated for each page and again before every edit/refresh POST. Null denies both actions.</summary>
    public Func<HttpContext, bool> CanEdit { get; set; }

    /// <summary>Optional invalidation hook for a provider's own cache, called on explicit refresh.</summary>
    public Action<HttpContext> RefreshRepository { get; set; }

  }

}
