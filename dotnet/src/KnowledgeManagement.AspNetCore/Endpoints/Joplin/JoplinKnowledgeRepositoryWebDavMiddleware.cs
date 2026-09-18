using Logging.SmartStandards.CopyForKnowledgeManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace KnowledgeManagement.SmartStandards.Endpoints.Joplin {

  /// <summary>
  /// Terminates the ASP.NET Core request pipeline for profile-scoped Joplin WebDAV
  /// synchronization endpoints without involving MVC routing, ApiExplorer, Swagger or
  /// formatter-based content negotiation.
  ///
  /// The externally visible endpoint has the following shape:
  ///
  /// <c>{EndpointPath}/{ProfileId}/...</c>
  ///
  /// The persistent synchronization scope is derived deterministically from:
  ///
  /// <c>username + profile-id</c>
  ///
  /// using SHA-256. The resulting synchronization identifier is used both for state-store
  /// lookup and for application-defined authorization.
  ///
  /// The profile identifier is intentionally treated as an opaque URL segment. It may be a
  /// Snowflake44 value as suggested by the application, but the middleware does not depend
  /// on its concrete generation algorithm.
  /// </summary>
  public sealed class JoplinKnowledgeRepositoryWebDavMiddleware {

    private const string _SyncIdentityDomain = "JoplinProfile:v1:";

    private static readonly object _SyncRoot = new object();

    private readonly RequestDelegate _Next;
    private readonly PathString _EndpointPath;

    /// <summary>
    /// Creates the WebDAV protocol middleware.
    /// </summary>
    /// <param name="next">The next ASP.NET Core middleware.</param>
    /// <param name="endpointPath">
    /// The absolute application-relative Joplin endpoint path before the profile segment.
    /// </param>
    public JoplinKnowledgeRepositoryWebDavMiddleware(
      RequestDelegate next,
      string endpointPath
    ) {
      if (next == null) {
        throw new ArgumentNullException(
          nameof(next)
        );
      }

      if (string.IsNullOrWhiteSpace(endpointPath)) {
        throw new ArgumentException(
          "A Joplin WebDAV endpoint path is required.",
          nameof(endpointPath)
        );
      }

      _Next = next;
      _EndpointPath = new PathString(
        this.NormalizeEndpointPath(
          endpointPath
        )
      );
    }

    /// <summary>
    /// Handles requests below the configured Joplin WebDAV endpoint.
    ///
    /// One additional URL segment below the configured endpoint identifies the Joplin
    /// profile. HTTP Basic Authentication supplies the username. Together both values form
    /// the deterministic synchronization identity.
    ///
    /// The concrete state store is created only after credentials and profile scope have
    /// been validated. Unauthorized requests therefore cannot manufacture state
    /// directories.
    /// </summary>
    public Task Invoke(
      HttpContext context,
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStoreFactory syncStateStoreFactory,
      IServiceProvider serviceProvider
    ) {
      if (context == null) {
        throw new ArgumentNullException(
          nameof(context)
        );
      }

      if (knowledgeRepository == null) {
        throw new ArgumentNullException(
          nameof(knowledgeRepository)
        );
      }

      if (syncStateStoreFactory == null) {
        throw new ArgumentNullException(
          nameof(syncStateStoreFactory)
        );
      }

      if (!context.Request.Path.StartsWithSegments(
            _EndpointPath,
            out PathString remainingPath
          )) {
        return _Next(
          context
        );
      }

      string profileId;
      PathString profileRemainingPath;

      if (!this.TryExtractProfileId(
            remainingPath,
            out profileId,
            out profileRemainingPath
          )) {
        context.Response.StatusCode =
          StatusCodes.Status404NotFound;

        return Task.CompletedTask;
      }

      string userName;
      string password;

      if (!this.TryReadBasicCredentials(
            context,
            out userName,
            out password
          )) {
        this.WriteAuthenticationChallenge(
          context
        );

        return Task.CompletedTask;
      }

      string syncId = this.CreateSyncId(
        userName,
        profileId
      );

      IJoplinWebDavAuthenticationValidator authenticationValidator =
        serviceProvider.GetService<IJoplinWebDavAuthenticationValidator>();

      if (authenticationValidator != null) {
        bool valid = authenticationValidator.ValidateCredentials(
          userName,
          password,
          syncId,
          context
        );

        if (!valid) {
          this.WriteAuthenticationChallenge(
            context
          );

          return Task.CompletedTask;
        }
      }

      IJoplinSyncStateStore syncStateStore =
        syncStateStoreFactory.GetOrCreate(
          syncId
        );

      this.EnableSynchronousIoForWebDav(
        context
      );

      string profileBasePath =
        _EndpointPath.Value
        + "/"
        + Uri.EscapeDataString(
          profileId
        );

      lock (_SyncRoot) {
        Task handlingTask = this.HandleRequest(
          context,
          profileRemainingPath,
          profileBasePath,
          knowledgeRepository,
          syncStateStore
        );

        handlingTask.GetAwaiter().GetResult();

        return Task.CompletedTask;
      }
    }

    /// <summary>
    /// Dispatches one raw HTTP/WebDAV request to one profile-scoped protocol handler.
    /// </summary>
    private Task HandleRequest(
      HttpContext context,
      PathString remainingPath,
      string profileBasePath,
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStore syncStateStore
    ) {
      JoplinKnowledgeRepositoryWebDavHandler handler =
        new JoplinKnowledgeRepositoryWebDavHandler(
          knowledgeRepository,
          syncStateStore,
          profileBasePath
        );

      ControllerActionDescriptor actionDescriptor =
        new ControllerActionDescriptor();

      ActionContext actionContext =
        new ActionContext(
          context,
          new RouteData(),
          actionDescriptor
        );

      ControllerContext controllerContext =
        new ControllerContext(
          actionContext
        );

      handler.ControllerContext =
        controllerContext;

      string relativePath = this.GetRelativePath(
        remainingPath
      );

      string method =
        context.Request.Method;

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin WebDAV ASP dispatch: "
        + method
        + " "
        + context.Request.Path.Value
      );

      IActionResult result = this.Dispatch(
        handler,
        method,
        relativePath
      );

      if (result == null) {
        context.Response.Headers["Allow"] =
          "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, MOVE";

        context.Response.StatusCode =
          StatusCodes.Status405MethodNotAllowed;

        return Task.CompletedTask;
      }

      return result.ExecuteResultAsync(
        actionContext
      );
    }

    /// <summary>
    /// Maps raw method strings to the WebDAV protocol handler.
    /// </summary>
    private IActionResult Dispatch(
      JoplinKnowledgeRepositoryWebDavHandler handler,
      string method,
      string relativePath
    ) {
      bool root =
        string.IsNullOrEmpty(
          relativePath
        );

      if (string.Equals(
            method,
            "OPTIONS",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return handler.OptionsRoot();
        }

        return handler.OptionsPath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "PROPFIND",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return handler.PropFindRoot();
        }

        return handler.PropFindPath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "GET",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return handler.GetRoot();
        }

        return handler.GetPath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "HEAD",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return handler.HeadRoot();
        }

        return handler.HeadPath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "PUT",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.PutPath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "DELETE",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.DeletePath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "MKCOL",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.MkColPath(
          relativePath
        );
      }

      if (string.Equals(
            method,
            "MOVE",
            StringComparison.OrdinalIgnoreCase
          )) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.MovePath(
          relativePath
        );
      }

      return null;
    }

    /// <summary>
    /// Extracts the first path segment below the configured endpoint as the opaque Joplin
    /// profile identifier.
    /// </summary>
    private bool TryExtractProfileId(
      PathString remainingPath,
      out string profileId,
      out PathString profileRemainingPath
    ) {
      profileId = string.Empty;
      profileRemainingPath =
        new PathString("/");

      string value =
        remainingPath.Value;

      if (string.IsNullOrWhiteSpace(value) ||
          string.Equals(
            value,
            "/",
            StringComparison.Ordinal
          )) {
        return false;
      }

      string trimmed =
        value.TrimStart('/');

      int separatorIndex =
        trimmed.IndexOf('/');

      string encodedProfileId;

      if (separatorIndex < 0) {
        encodedProfileId =
          trimmed;

        profileRemainingPath =
          new PathString("/");
      }
      else {
        encodedProfileId =
          trimmed.Substring(
            0,
            separatorIndex
          );

        string remainder =
          trimmed.Substring(
            separatorIndex
          );

        if (string.IsNullOrEmpty(remainder)) {
          remainder = "/";
        }

        profileRemainingPath =
          new PathString(
            remainder
          );
      }

      if (string.IsNullOrWhiteSpace(
            encodedProfileId
          )) {
        return false;
      }

      try {
        profileId =
          Uri.UnescapeDataString(
            encodedProfileId
          );
      }
      catch (UriFormatException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      if (string.IsNullOrWhiteSpace(
            profileId
          )) {
        return false;
      }

      if (profileId.Contains(
            "/",
            StringComparison.Ordinal
          ) ||
          profileId.Contains(
            "\\",
            StringComparison.Ordinal
          )) {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Reads one HTTP Basic Authentication credential pair without performing any
    /// application-specific validation.
    /// </summary>
    private bool TryReadBasicCredentials(
      HttpContext context,
      out string userName,
      out string password
    ) {
      userName = string.Empty;
      password = string.Empty;

      string authorization =
        context.Request.Headers.Authorization.ToString();

      if (string.IsNullOrWhiteSpace(
            authorization
          ) ||
          !authorization.StartsWith(
            "Basic ",
            StringComparison.OrdinalIgnoreCase
          )) {
        return false;
      }

      string encodedCredentials =
        authorization.Substring(
          "Basic ".Length
        ).Trim();

      byte[] credentialBytes;

      try {
        credentialBytes =
          Convert.FromBase64String(
            encodedCredentials
          );
      }
      catch (FormatException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      string credentials =
        Encoding.UTF8.GetString(
          credentialBytes
        );

      int separatorIndex =
        credentials.IndexOf(
          ':'
        );

      if (separatorIndex < 0) {
        return false;
      }

      userName =
        credentials.Substring(
          0,
          separatorIndex
        );

      password =
        credentials.Substring(
          separatorIndex + 1
        );

      if (string.IsNullOrWhiteSpace(
            userName
          )) {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Creates the deterministic synchronization identifier shared by state-store lookup
    /// and the application-defined authorization hook.
    ///
    /// Username and profile ID are length-prefixed before hashing so the identity cannot
    /// become ambiguous through delimiter collisions.
    /// </summary>
    private string CreateSyncId(
      string userName,
      string profileId
    ) {
      string identity =
        _SyncIdentityDomain
        + userName.Length.ToString(
          System.Globalization.CultureInfo.InvariantCulture
        )
        + ":"
        + userName
        + ":"
        + profileId.Length.ToString(
          System.Globalization.CultureInfo.InvariantCulture
        )
        + ":"
        + profileId;

      byte[] hash =
        SHA256.HashData(
          Encoding.UTF8.GetBytes(
            identity
          )
        );

      StringBuilder builder =
        new StringBuilder(
          hash.Length * 2
        );

      foreach (byte current in hash) {
        builder.Append(
          current.ToString(
            "x2",
            System.Globalization.CultureInfo.InvariantCulture
          )
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Writes the standard HTTP Basic Authentication challenge expected by Joplin.
    /// </summary>
    private void WriteAuthenticationChallenge(
      HttpContext context
    ) {
      context.Response.Headers.WWWAuthenticate =
        "Basic realm=\"Joplin Knowledge Repository\", charset=\"UTF-8\"";

      context.Response.StatusCode =
        StatusCodes.Status401Unauthorized;
    }

    /// <summary>
    /// Enables synchronous body access only for this WebDAV protocol endpoint.
    /// </summary>
    private void EnableSynchronousIoForWebDav(
      HttpContext context
    ) {
      IHttpBodyControlFeature bodyControlFeature =
        context.Features.Get<IHttpBodyControlFeature>();

      if (bodyControlFeature != null) {
        bodyControlFeature.AllowSynchronousIO =
          true;
      }
    }

    /// <summary>
    /// Converts the remaining ASP.NET Core path to the handler's catch-all path format.
    /// </summary>
    private string GetRelativePath(
      PathString remainingPath
    ) {
      string value =
        remainingPath.Value;

      if (string.IsNullOrWhiteSpace(value) ||
          string.Equals(
            value,
            "/",
            StringComparison.Ordinal
          )) {
        return string.Empty;
      }

      return value.TrimStart('/');
    }

    /// <summary>
    /// Normalizes the configured endpoint path.
    /// </summary>
    private string NormalizeEndpointPath(
      string endpointPath
    ) {
      string normalized =
        endpointPath.Trim();

      if (!normalized.StartsWith(
            "/",
            StringComparison.Ordinal
          )) {
        normalized =
          "/"
          + normalized;
      }

      if (normalized.Length > 1 &&
          normalized.EndsWith(
            "/",
            StringComparison.Ordinal
          )) {
        normalized =
          normalized.TrimEnd('/');
      }

      return normalized;
    }
  }
}
