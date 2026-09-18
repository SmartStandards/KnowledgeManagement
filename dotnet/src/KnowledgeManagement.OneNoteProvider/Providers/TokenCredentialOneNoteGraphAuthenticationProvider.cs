using Azure.Core;
using System.Net.Http.Headers;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Adapts any Azure TokenCredential to OneNote Graph authentication.
  /// </summary>
  public sealed class TokenCredentialOneNoteGraphAuthenticationProvider : IOneNoteGraphAuthenticationProvider {

    private readonly TokenCredential _Credential;
    private readonly string[] _ReadOnlyScopes;
    private readonly string[] _ReadWriteScopes;

    /// <summary>
    /// Creates the authentication strategy.
    /// </summary>
    public TokenCredentialOneNoteGraphAuthenticationProvider(
      TokenCredential credential,
      string[] readOnlyScopes,
      string[] readWriteScopes) {

      if (credential == null) {
        throw new ArgumentNullException(nameof(credential));
      }
      if (readOnlyScopes == null || readOnlyScopes.Length == 0) {
        throw new ArgumentException("Read-only scopes are required.", nameof(readOnlyScopes));
      }
      if (readWriteScopes == null || readWriteScopes.Length == 0) {
        throw new ArgumentException("Read/write scopes are required.", nameof(readWriteScopes));
      }

      _Credential = credential;
      _ReadOnlyScopes = (string[])readOnlyScopes.Clone();
      _ReadWriteScopes = (string[])readWriteScopes.Clone();
    }

    /// <summary>
    /// Creates an authenticated Graph HTTP client.
    /// </summary>
    public HttpClient CreateHttpClient(bool readOnly) {
      string[] scopes;
      if (readOnly) {
        scopes = _ReadOnlyScopes;
      } else {
        scopes = _ReadWriteScopes;
      }

      AccessToken token = _Credential.GetToken(new TokenRequestContext(scopes), CancellationToken.None);
      HttpClient client = new HttpClient();
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
      return client;
    }
  }
}
