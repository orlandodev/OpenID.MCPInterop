using OpenID.MCPInterop.Client.Partner;
using Xunit;

namespace OpenID.MCPInterop.Tests;

/// <summary>
/// Covers the check-then-act race regression: /connect's Status-guard check
/// and the connection-state transition used to be two non-atomic steps, so
/// near-simultaneous /connect requests could both start a connection attempt.
/// See DisposalHelperTests for the other regression here.
/// </summary>
public sealed class PartnerSessionStateTests
{
    [Fact]
    public void TryBeginConnecting_WhenNotConnected_ReturnsTrueAndSetsStatusToConnecting()
    {
        var session = new PartnerSessionState();

        var started = session.TryBeginConnecting(out var cancellationToken);

        Assert.True(started);
        Assert.Equal(PartnerConnectionStatus.Connecting, session.Status);
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void TryBeginConnecting_WhenAlreadyConnecting_ReturnsFalse()
    {
        var session = new PartnerSessionState();
        session.TryBeginConnecting(out _);

        var startedAgain = session.TryBeginConnecting(out _);

        Assert.False(startedAgain);
    }

    [Fact]
    public async Task TryBeginConnecting_CalledConcurrently_OnlyOneCallerWins()
    {
        var session = new PartnerSessionState();
        const int callerCount = 20;
        using var barrier = new Barrier(callerCount);

        var results = await Task.WhenAll(Enumerable.Range(0, callerCount).Select(callerIndex => Task.Run(() =>
        {
            _ = callerIndex;
            barrier.SignalAndWait();
            return session.TryBeginConnecting(out _);
        })));

        Assert.Equal(1, results.Count(started => started));
        Assert.Equal(PartnerConnectionStatus.Connecting, session.Status);
    }

    [Fact]
    public void CancelConnecting_WhileConnecting_CancelsTheReturnedToken()
    {
        var session = new PartnerSessionState();
        session.TryBeginConnecting(out var cancellationToken);

        session.CancelConnecting();

        Assert.True(cancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelConnecting_BeforeAnyConnectionAttempt_DoesNotThrow()
    {
        var session = new PartnerSessionState();

        var exception = Record.Exception(session.CancelConnecting);

        Assert.Null(exception);
    }
}
