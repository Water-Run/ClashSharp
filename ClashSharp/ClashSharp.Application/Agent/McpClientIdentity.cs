namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Identifies the credential verified by transport authentication without retaining its secret.</summary>
/// <param name="CredentialId">Opaque non-secret credential identity rechecked inside mutation admission.</param>
public sealed record McpClientIdentity(Guid CredentialId);
