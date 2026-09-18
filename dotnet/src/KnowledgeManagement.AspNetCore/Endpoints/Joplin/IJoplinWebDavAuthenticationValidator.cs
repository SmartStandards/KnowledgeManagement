using Microsoft.AspNetCore.Http;

namespace KnowledgeManagement.SmartStandards.Endpoints.Joplin {

    /// <summary>
    /// Provides application-defined validation for credentials supplied by a Joplin WebDAV
    /// client through HTTP Basic Authentication.
    ///
    /// The WebDAV middleware handles the HTTP authentication protocol itself. Implementations
    /// of this interface decide only whether the supplied credentials may access the requested
    /// Joplin synchronization scope. They may use an existing user store, configuration,
    /// database, Active Directory integration or any other application-specific mechanism.
    ///
    /// Passwords are supplied only for the duration of the current request and are never
    /// persisted by the WebDAV infrastructure.
    /// </summary>
    public interface IJoplinWebDavAuthenticationValidator {

      /// <summary>
      /// Validates one username/password pair for one deterministic Joplin synchronization
      /// scope.
      /// </summary>
      /// <param name="userName">The username supplied by the client.</param>
      /// <param name="password">The password supplied by the client.</param>
      /// <param name="syncId">
      /// The deterministic synchronization identifier derived from the supplied username and
      /// the additional profile URL segment. The same identifier is used to select the
      /// persistent synchronization-state directory.
      /// </param>
      /// <param name="context">
      /// The current ASP.NET Core HTTP context. This allows implementations to inspect
      /// additional request information when required.
      /// </param>
      /// <returns>
      /// <c>true</c> when access to this concrete Joplin synchronization scope is permitted;
      /// otherwise <c>false</c>.
      /// </returns>
      bool ValidateCredentials(
        string userName,
        string password,
        string syncId,
        HttpContext context
      );
    }


  public class DelegateBasedJoplinWebDavAuthenticationValidator : IJoplinWebDavAuthenticationValidator {

    private readonly Func<string, string, string, HttpContext, bool> _validationDelegate;

    public DelegateBasedJoplinWebDavAuthenticationValidator(
      Func<string, string, string, HttpContext, bool> validationDelegate
    ) {
      _validationDelegate = validationDelegate ?? throw new ArgumentNullException(nameof(validationDelegate));
    }

    public bool ValidateCredentials(
      string userName,
      string password,
      string syncId,
      HttpContext context
    ) {
      return _validationDelegate(userName, password, syncId, context);
    }

  }

}
