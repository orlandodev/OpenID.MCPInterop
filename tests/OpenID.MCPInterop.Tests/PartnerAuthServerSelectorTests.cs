using OpenID.MCPInterop.Client.Partner;
using Xunit;

namespace OpenID.MCPInterop.Tests;

/// <summary>
/// Covers the silent-fallback regression: previously, an Authority not
/// matching anything the target server's PRM advertised fell back to the
/// first advertised server with no diagnostic at all.
/// </summary>
public sealed class PartnerAuthServerSelectorTests
{
    [Fact]
    public void Select_ExactMatch_ReturnsMatchingServerWithoutFallback()
    {
        var servers = new[] { new Uri("https://dev-example.us.auth0.com/"), new Uri("https://other-as.test/") };
        var fallbackMessages = new List<string>();

        var selected = PartnerAuthServerSelector.Select(servers, "https://dev-example.us.auth0.com/", fallbackMessages.Add);

        Assert.Equal(new Uri("https://dev-example.us.auth0.com/"), selected);
        Assert.Empty(fallbackMessages);
    }

    [Theory]
    [InlineData("https://dev-example.us.auth0.com", "https://dev-example.us.auth0.com/")]
    [InlineData("https://dev-example.us.auth0.com/", "https://dev-example.us.auth0.com")]
    [InlineData("https://DEV-EXAMPLE.us.auth0.com/", "https://dev-example.us.auth0.com/")]
    public void Select_MatchDiffersByTrailingSlashOrCase_StillMatchesWithoutFallback(string advertised, string configured)
    {
        var servers = new[] { new Uri(advertised) };
        var fallbackMessages = new List<string>();

        var selected = PartnerAuthServerSelector.Select(servers, configured, fallbackMessages.Add);

        Assert.Equal(new Uri(advertised), selected);
        Assert.Empty(fallbackMessages);
    }

    [Fact]
    public void Select_NoMatchingServer_FallsBackToFirstAndReportsFallback()
    {
        var servers = new[] { new Uri("https://unexpected-as.test/") };
        var fallbackMessages = new List<string>();

        var selected = PartnerAuthServerSelector.Select(servers, "https://dev-example.us.auth0.com/", fallbackMessages.Add);

        Assert.Equal(new Uri("https://unexpected-as.test/"), selected);
        var message = Assert.Single(fallbackMessages);
        Assert.Contains("dev-example.us.auth0.com", message);
        Assert.Contains("unexpected-as.test", message);
    }

    [Fact]
    public void Select_NoServersAdvertised_ReturnsNullAndReportsFallback()
    {
        var servers = Array.Empty<Uri>();
        var fallbackMessages = new List<string>();

        var selected = PartnerAuthServerSelector.Select(servers, "https://dev-example.us.auth0.com/", fallbackMessages.Add);

        Assert.Null(selected);
        var message = Assert.Single(fallbackMessages);
        Assert.Contains("none were advertised at all", message);
    }
}
