using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeManagement.SmartStandards.Endpoints.Joplin {

  /// <summary>
  /// Provides ASP.NET Core registration helpers for the profile-scoped Joplin knowledge
  /// repository WebDAV endpoint and its persistent synchronization-state stores.
  /// </summary>
  public static class JoplinKnowledgeRepositoryWebDavExtensions {

    /// <summary>
    /// Registers the Joplin WebDAV protocol middleware.
    ///
    /// The effective synchronization target is exposed below:
    ///
    /// <c>{endpointPath}/{profileId}/</c>
    ///
    /// The additional profile segment is combined with the HTTP Basic Authentication
    /// username to derive the deterministic synchronization identifier used by the
    /// configured <see cref="IJoplinSyncStateStoreFactory"/>.
    ///
    /// This call should be placed before Swagger and endpoint-routing/controller
    /// middleware. The middleware short-circuits only requests below the supplied path and
    /// leaves every other request unchanged.
    /// </summary>
    /// <param name="app">The ASP.NET Core application builder.</param>
    /// <param name="endpointPath">
    /// The application-relative path exposed to Joplin before the profile segment, for
    /// example <c>/api/knowledge/joplin</c>.
    /// </param>
    /// <returns>The original application builder.</returns>
    public static IApplicationBuilder UseJoplinKnowledgeRepositoryWebDav(
      this IApplicationBuilder app,
      string endpointPath = "/api/knowledge/joplin"
    ) {
      if (app == null) {
        throw new ArgumentNullException(
          nameof(app)
        );
      }

      if (string.IsNullOrWhiteSpace(endpointPath)) {
        throw new ArgumentException(
          "A Joplin WebDAV endpoint path is required.",
          nameof(endpointPath)
        );
      }

      app.UseMiddleware<JoplinKnowledgeRepositoryWebDavMiddleware>(
        endpointPath
      );

      return app;
    }

    /// <summary>
    /// Registers a persistent file-system-backed Joplin synchronization-state store
    /// factory.
    ///
    /// One direct child directory is created on demand for each deterministic
    /// synchronization identifier. The directory name is exactly the SHA-256 synchronization
    /// identifier produced from username plus profile URL segment by the middleware.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="rootDirectory">
    /// The common parent directory used for all Joplin synchronization-state scopes.
    /// </param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddFileBasedJoplinSyncStateStores(
      this IServiceCollection services,
      string rootDirectory
    ) {
      if (services == null) {
        throw new ArgumentNullException(
          nameof(services)
        );
      }

      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentException(
          "A Joplin synchronization-state root directory is required.",
          nameof(rootDirectory)
        );
      }

      FileBasedJoplinSyncStateStoreFactory factory =
        new FileBasedJoplinSyncStateStoreFactory(
          rootDirectory
        );

      services.AddSingleton<IJoplinSyncStateStoreFactory>(
        factory
      );

      return services;
    }
  }
}
