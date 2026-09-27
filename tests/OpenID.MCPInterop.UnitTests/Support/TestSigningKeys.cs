using OpenID.MCPInterop.Client.Auth;

namespace OpenID.MCPInterop.UnitTests.Support;

/// <summary>One ClientSigningKey shared across tests that only need *a* key - RSA generation is slow enough to share.</summary>
internal static class TestSigningKeys
{
    private static readonly Lazy<ClientSigningKey> SharedKey = new(() =>
        ClientSigningKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"mcpinterop-test-{Guid.NewGuid():N}", "cimd-signing.pem")));

    public static ClientSigningKey Shared => SharedKey.Value;
}
