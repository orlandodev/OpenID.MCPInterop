using System.Collections.Concurrent;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Single in-memory session shared across requests. This project is a
/// personal, single-operator demo UI (not a multi-user app), so one shared
/// connection/log is enough - it plays the same role as Client's
/// Console.WriteLine calls and pendingAuthorizations dictionary, just kept
/// alive across requests instead of a single process run.
/// </summary>
public sealed class PartnerSessionState
{
    private readonly List<string> _log = [];
    private readonly List<(string Name, string? Description, string? Schema)> _tools = [];
    private readonly object _connectLock = new();
    private CancellationTokenSource? _connectCts;

    public ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> PendingCallbacks { get; } = new();

    public PartnerConnectionStatus Status { get; private set; } = PartnerConnectionStatus.NotConnected;

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToArray();
            }
        }
    }

    public IReadOnlyList<(string Name, string? Description, string? Schema)> Tools
    {
        get
        {
            lock (_tools)
            {
                return _tools.ToArray();
            }
        }
    }

    public McpClient? Client { get; private set; }

    public void AppendLog(string message)
    {
        lock (_log)
        {
            _log.Add($"{DateTimeOffset.Now:T} {message}");
        }
    }

    public void SetStatus(PartnerConnectionStatus status) => Status = status;

    /// <summary>
    /// Records a successful connection, disposing any previous McpClient first -
    /// otherwise a second call (e.g. two racing connection attempts both
    /// succeeding) leaks the earlier HttpClientTransport/HttpClient.
    /// </summary>
    public async ValueTask SetConnectedAsync(McpClient client, IEnumerable<(string Name, string? Description, string? Schema)> tools)
    {
        var previousClient = Client;
        Client = client;
        lock (_tools)
        {
            _tools.Clear();
            _tools.AddRange(tools);
        }

        Status = PartnerConnectionStatus.Connected;

        await DisposalHelper.DisposeIfNotNullAsync(previousClient);
    }

    /// <summary>
    /// Atomically checks and transitions to Connecting, so two near-simultaneous
    /// /connect requests can't both start a connection attempt (see review).
    /// Also cancels/discards any prior attempt, so a stuck one doesn't require
    /// restarting this process - just a fresh /connect.
    /// </summary>
    /// <returns>false if a connection attempt is already in progress or connected, in which case <paramref name="cancellationToken"/> is not meaningful.</returns>
    public bool TryBeginConnecting(out CancellationToken cancellationToken)
    {
        lock (_connectLock)
        {
            if (Status is PartnerConnectionStatus.Connecting or PartnerConnectionStatus.Connected)
            {
                cancellationToken = default;
                return false;
            }

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            Status = PartnerConnectionStatus.Connecting;
            cancellationToken = _connectCts.Token;
            return true;
        }
    }

    /// <summary>Lets the UI abandon a stuck Connecting attempt without restarting the process - see TryBeginConnecting.</summary>
    public void CancelConnecting()
    {
        lock (_connectLock)
        {
            _connectCts?.Cancel();
        }
    }
}
