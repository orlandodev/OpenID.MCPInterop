using OpenID.MCPInterop.Client;
using Xunit;

namespace OpenID.MCPInterop.Tests;

/// <summary>
/// Covers the silent-fallback regression (an Authority not matching anything
/// the target server's PRM advertised used to fall back to the first
/// advertised server with no diagnostic at all) and the corroboration
/// feature: Select always logs what the PRM advertised, so "the client read
/// the AS from the metadata" shows up in the Log rather than just being inferred.
/// </summary>
public sealed class AuthServerSelectorTests
{
    [Fact]
    public void Select_ExactMatch_ReturnsMatchingServerAndLogsSelection()
    {
        var servers = new[] { new Uri("https://dev-example.us.auth0.com/"), new Uri("https://other-as.test/") };
        var logMessages = new List<string>();

        var selected = AuthServerSelector.Select(servers, "https://dev-example.us.auth0.com/", logMessages.Add);

        Assert.Equal(new Uri("https://dev-example.us.auth0.com/"), selected);
        Assert.Contains(logMessages, m => m.Contains("listed 2 authorization server"));
        Assert.Contains(logMessages, m => m.Contains("Selected") && m.Contains("dev-example.us.auth0.com"));
        Assert.DoesNotContain(logMessages, m => m.Contains("falling back") || m.Contains("No authorization server"));
    }

    [Theory]
    [InlineData("https://dev-example.us.auth0.com", "https://dev-example.us.auth0.com/")]
    [InlineData("https://dev-example.us.auth0.com/", "https://dev-example.us.auth0.com")]
    [InlineData("https://DEV-EXAMPLE.us.auth0.com/", "https://dev-example.us.auth0.com/")]
    public void Select_MatchDiffersByTrailingSlashOrCase_StillMatchesWithoutFallback(string advertised, string configured)
    {
        var servers = new[] { new Uri(advertised) };
        var logMessages = new List<string>();

        var selected = AuthServerSelector.Select(servers, configured, logMessages.Add);

        Assert.Equal(new Uri(advertised), selected);
        Assert.DoesNotContain(logMessages, m => m.Contains("falling back") || m.Contains("No authorization server"));
    }

    [Fact]
    public void Select_NoMatchingServer_FallsBackToFirstAndReportsFallback()
    {
        var servers = new[] { new Uri("https://unexpected-as.test/") };
        var logMessages = new List<string>();

        var selected = AuthServerSelector.Select(servers, "https://dev-example.us.auth0.com/", logMessages.Add);

        Assert.Equal(new Uri("https://unexpected-as.test/"), selected);
        Assert.Contains(logMessages, m => m.Contains("dev-example.us.auth0.com") && m.Contains("unexpected-as.test"));
    }

    [Fact]
    public void Select_NoServersAdvertised_ReturnsNullAndReportsFallback()
    {
        var servers = Array.Empty<Uri>();
        var logMessages = new List<string>();

        var selected = AuthServerSelector.Select(servers, "https://dev-example.us.auth0.com/", logMessages.Add);

        Assert.Null(selected);
        Assert.Contains(logMessages, m => m.Contains("none were advertised at all"));
    }
}
