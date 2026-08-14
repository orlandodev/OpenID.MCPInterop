using OpenID.MCPInterop.Client;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

/// <summary>
/// Covers the check-then-act race regression: /connect's Status-guard check
/// and the connection-state transition used to be two non-atomic steps, so
/// near-simultaneous /connect requests could both start a connection attempt.
/// The EMA-leg tests below mirror the same coverage for TryBeginEmaConnecting/
/// CancelEmaConnecting, which didn't exist before the EMA leg's redirect-based
/// hand-off gave it the same race surface. See DisposalHelperTests for the
/// other regression here.
/// </summary>
public sealed class ClientSessionStateTests
{
    [Fact]
    public void TryBeginConnecting_WhenNotConnected_ReturnsTrueAndSetsStatusToConnecting()
    {
        var session = new ClientSessionState();

        var started = session.TryBeginConnecting(out var cancellationToken);

        Assert.True(started);
        Assert.Equal(ClientConnectionStatus.Connecting, session.Status);
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void TryBeginConnecting_WhenAlreadyConnecting_ReturnsFalse()
    {
        var session = new ClientSessionState();
        session.TryBeginConnecting(out _);

        var startedAgain = session.TryBeginConnecting(out _);

        Assert.False(startedAgain);
    }

    [Fact]
    public async Task TryBeginConnecting_CalledConcurrently_OnlyOneCallerWins()
    {
        var session = new ClientSessionState();
        const int callerCount = 20;
        using var barrier = new Barrier(callerCount);

        var results = await Task.WhenAll(Enumerable.Range(0, callerCount).Select(callerIndex => Task.Run(() =>
        {
            _ = callerIndex;
            barrier.SignalAndWait();
            return session.TryBeginConnecting(out _);
        })));

        Assert.Equal(1, results.Count(started => started));
        Assert.Equal(ClientConnectionStatus.Connecting, session.Status);
    }

    [Fact]
    public void CancelConnecting_WhileConnecting_CancelsTheReturnedToken()
    {
        var session = new ClientSessionState();
        session.TryBeginConnecting(out var cancellationToken);

        session.CancelConnecting();

        Assert.True(cancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelConnecting_BeforeAnyConnectionAttempt_DoesNotThrow()
    {
        var session = new ClientSessionState();

        var exception = Record.Exception(session.CancelConnecting);

        Assert.Null(exception);
    }

    [Fact]
    public void TryBeginEmaConnecting_PrimaryLegNotConnected_ReturnsFalse()
    {
        var session = new ClientSessionState();

        var started = session.TryBeginEmaConnecting(out var cancellationToken);

        Assert.False(started);
        Assert.Equal(ClientConnectionStatus.NotConnected, session.EmaStatus);
    }

    [Fact]
    public void TryBeginEmaConnecting_PrimaryLegConnected_ReturnsTrueAndSetsEmaStatusToConnecting()
    {
        var session = new ClientSessionState();
        session.SetStatus(ClientConnectionStatus.Connected);

        var started = session.TryBeginEmaConnecting(out var cancellationToken);

        Assert.True(started);
        Assert.Equal(ClientConnectionStatus.Connecting, session.EmaStatus);
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void TryBeginEmaConnecting_WhenAlreadyConnecting_ReturnsFalse()
    {
        var session = new ClientSessionState();
        session.SetStatus(ClientConnectionStatus.Connected);
        session.TryBeginEmaConnecting(out _);

        var startedAgain = session.TryBeginEmaConnecting(out _);

        Assert.False(startedAgain);
    }

    [Fact]
    public async Task TryBeginEmaConnecting_CalledConcurrently_OnlyOneCallerWins()
    {
        var session = new ClientSessionState();
        session.SetStatus(ClientConnectionStatus.Connected);
        const int callerCount = 20;
        using var barrier = new Barrier(callerCount);

        var results = await Task.WhenAll(Enumerable.Range(0, callerCount).Select(callerIndex => Task.Run(() =>
        {
            _ = callerIndex;
            barrier.SignalAndWait();
            return session.TryBeginEmaConnecting(out _);
        })));

        Assert.Equal(1, results.Count(started => started));
        Assert.Equal(ClientConnectionStatus.Connecting, session.EmaStatus);
    }

    [Fact]
    public void CancelEmaConnecting_WhileConnecting_CancelsTheReturnedToken()
    {
        var session = new ClientSessionState();
        session.SetStatus(ClientConnectionStatus.Connected);
        session.TryBeginEmaConnecting(out var cancellationToken);

        session.CancelEmaConnecting();

        Assert.True(cancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelEmaConnecting_BeforeAnyConnectionAttempt_DoesNotThrow()
    {
        var session = new ClientSessionState();

        var exception = Record.Exception(session.CancelEmaConnecting);

        Assert.Null(exception);
    }
}
