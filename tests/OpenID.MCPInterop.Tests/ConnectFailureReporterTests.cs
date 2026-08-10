using OpenID.MCPInterop.Client.Partner;
using Xunit;

namespace OpenID.MCPInterop.Tests;

/// <summary>
/// Covers the duplicate-logging regression found in review: RunConnectionAsync's
/// own catch already logs and sets Status for any exception it observes
/// directly, so /connect's catch must not re-report the same failure - only
/// a genuinely new TimeoutException (the 30s wait itself elapsing) should.
/// </summary>
public sealed class ConnectFailureReporterTests
{
    [Fact]
    public void Classify_TimeoutException_ReturnsReportTimeout()
    {
        var exception = new TimeoutException();

        var action = ConnectFailureReporter.Classify(exception);

        Assert.Equal(ConnectFailureAction.ReportTimeout, action);
    }

    [Fact]
    public void Classify_OperationCanceledException_ReturnsAlreadyHandled()
    {
        // RunConnectionAsync's own branch already logged this and reset Status -
        // re-setting it to Failed here would undo the Cancel button's point.
        var exception = new OperationCanceledException();

        var action = ConnectFailureReporter.Classify(exception);

        Assert.Equal(ConnectFailureAction.AlreadyHandled, action);
    }

    [Fact]
    public void Classify_GenericException_ReturnsAlreadyHandled()
    {
        // Matches the confirmed regression: this exact exception surfaced as
        // both "Connection failed: X" and "Could not start login: X".
        var exception = new InvalidOperationException("Failed to find .well-known/openid-configuration metadata.");

        var action = ConnectFailureReporter.Classify(exception);

        Assert.Equal(ConnectFailureAction.AlreadyHandled, action);
    }
}
