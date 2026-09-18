using Azure.Core;

namespace KnowledgeManagement.SmartStandards.Providers {

  /// <summary>
  /// Exposes a previously acquired access token through TokenCredential.
  /// This mirrors the Graph Explorer mechanism used by the prototype.
  /// </summary>
  public sealed class StaticTokenCredential : TokenCredential {

    private readonly string _Token;
    private readonly DateTimeOffset _ExpiresOn;

    /// <summary>
    /// Creates the credential.
    /// </summary>
    public StaticTokenCredential(string token) {
      if (string.IsNullOrWhiteSpace(token)) {
        throw new ArgumentException("The access token must not be empty.", nameof(token));
      }
      _Token = token;
      _ExpiresOn = DateTimeOffset.UtcNow.AddHours(1);
    }

    /// <summary>
    /// Returns the static token.
    /// </summary>
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) {
      return new AccessToken(_Token, _ExpiresOn);
    }

    /// <summary>
    /// Returns the static token as a completed ValueTask as required by TokenCredential.
    /// </summary>
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) {
      return ValueTask.FromResult(new AccessToken(_Token, _ExpiresOn));
    }
  }
}
