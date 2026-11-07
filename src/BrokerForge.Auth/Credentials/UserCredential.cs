namespace BrokerForge.Auth.Credentials;

public enum CredentialKind { OAuth, Impersonate, Sso }

public enum CredentialStatus { Connected, Expired, Revoked }

/// <summary>
/// One user's grant for one service instance (HLD D-25, D-28): tokens for OAuth and Impersonate, a username and
/// secret for SSO. <see cref="ToString"/> prints kind, status and expiry only, so a logged record leaks nothing.
/// </summary>
public sealed record UserCredential(
    CredentialKind Kind,
    CredentialStatus Status,
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? Username,
    string? Secret,
    DateTimeOffset ConnectedAt)
{
    public override string ToString() => $"UserCredential {{ Kind = {Kind}, Status = {Status}, ExpiresAt = {ExpiresAt:O}, ConnectedAt = {ConnectedAt:O} }}";
}

/// <summary>A stored row exists but cannot be decrypted or parsed (key ring lost, row corrupted): the user must connect again.</summary>
public sealed class CredentialUnreadableException(string message) : Exception(message);
