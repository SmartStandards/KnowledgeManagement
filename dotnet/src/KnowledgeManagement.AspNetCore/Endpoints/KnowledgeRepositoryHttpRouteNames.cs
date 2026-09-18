
namespace KnowledgeManagement.SmartStandards.Endpoints {

  /// <summary>
  /// Defines stable ASP.NET Core route names used exclusively for reverse URL generation
  /// between the knowledge HTTP facades.
  ///
  /// Route names deliberately contain no physical URL path. Controller route templates may
  /// therefore be changed or externally prefixed without changing the code that creates
  /// self-links, resource links or breadcrumb links.
  /// </summary>
  internal static class KnowledgeRepositoryHttpRouteNames {

    internal const string _RawRoot = "KnowledgeRepository.Raw.Root";

    internal const string _RawArea = "KnowledgeRepository.Raw.Area";

    internal const string _RawResource = "KnowledgeRepository.Raw.Resource";

    internal const string _HtmlRoot = "KnowledgeRepository.Html.Root";

    internal const string _HtmlArea = "KnowledgeRepository.Html.Area";

  }

}
