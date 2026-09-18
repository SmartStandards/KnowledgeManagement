using System.Net.Http;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Creates authenticated Microsoft Graph HTTP clients for the OneNote provider.
  /// This is the public authentication extension point of the provider.
  /// </summary>
  public interface IOneNoteGraphAuthenticationProvider {

    /// <summary>
    /// Creates an authenticated Graph HTTP client.
    /// </summary>
    /// <param name="readOnly">
    /// true when the repository is protected against writes. Implementations that
    /// control OAuth scopes should request read-only scopes in this mode.
    /// </param>
    HttpClient CreateHttpClient(bool readOnly);
  }
}
