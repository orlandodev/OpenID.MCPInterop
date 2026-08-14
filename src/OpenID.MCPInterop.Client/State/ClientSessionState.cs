using System.Collections.Concurrent;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using OpenID.MCPInterop.Client.Helpers;
using OpenID.MCPInterop.Client.Models;

namespace OpenID.MCPInterop.Client.State;

/// <summary>
/// Single in-memory session shared across requests, tracking both the
/// primary leg (CIMD or direct-trust, depending on ClientOptions.UseCimd)
/// and the optional EMA leg independently - the Keycloak scenario needs both
/// live at once. This project is a personal, single-operator demo UI (not a
/// multi-user app), so one shared session is enough.
/// </summary>
public sealed class ClientSessionState
{
    private readonly List<(DateTimeOffset Timestamp, string Message)> _log = [];
    private readonly List<(string Name, string? Description, string? Schema)> _tools = [];
    private readonly List<(string Name, string? Description, string? Schema)> _emaTools = [];
    private readonly object _connectLock = new();
    private readonly object _emaConnectLock = new();
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _emaConnectCts;

    public ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> PendingCallbacks { get; } = new();

    public ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> PendingEmaCallbacks { get; } = new();

    public ClientConnectionStatus Status { get; private set; } = ClientConnectionStatus.NotConnected;

    public ClientConnectionStatus EmaStatus { get; private set; } = ClientConnectionStatus.NotConnected;

    public TokenClaims? LastTokenClaims { get; private set; }

    public TokenClaims? LastEmaTokenClaims { get; private set; }

    /// <summary>Which leg's connection row/tool list the sidebar currently has active - "primary" or "ema".</summary>
    public string SelectedLeg { get; private set; } = "primary";

    public string? SelectedToolName { get; private set; }

    public string? SelectedEmaToolName { get; private set; }

    public ToolInvocationResult? LastResult { get; private set; }

    public ToolInvocationResult? LastEmaResult { get; private set; }

    public IReadOnlyList<(DateTimeOffset Timestamp, string Message)> Log
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

    public IReadOnlyList<(string Name, string? Description, string? Schema)> EmaTools
    {
        get
        {
            lock (_emaTools)
            {
                return _emaTools.ToArray();
            }
        }
    }

    public McpClient? Client { get; private set; }

    public McpClient? EmaClient { get; private set; }

    public void AppendLog(string message)
    {
        lock (_log)
        {
            _log.Add((DateTimeOffset.Now, message));
        }
    }

    public void SetStatus(ClientConnectionStatus status) => Status = status;

    public void SetEmaStatus(ClientConnectionStatus status) => EmaStatus = status;

    public void SetLastTokenClaims(TokenClaims claims) => LastTokenClaims = claims;

    public void SetLastEmaTokenClaims(TokenClaims claims) => LastEmaTokenClaims = claims;

    /// <summary>Selects which leg's row/panel is active in the sidebar - ignored for an unrecognized value.</summary>
    public void SetSelectedLeg(string leg)
    {
        if (leg is "primary" or "ema")
        {
            SelectedLeg = leg;
        }
    }

    /// <summary>Selects the tool shown in the master/detail view for the given leg - by name, so it survives a tool-list refresh after reconnecting.</summary>
    public void SetSelectedTool(string leg, string? toolName)
    {
        if (leg == "ema")
        {
            SelectedEmaToolName = toolName;
        }
        else
        {
            SelectedToolName = toolName;
        }
    }

    /// <summary>Records the most recent tool call's outcome on the given leg, for the "Last invocation" card.</summary>
    public void SetLastResult(string leg, ToolInvocationResult result)
    {
        if (leg == "ema")
        {
            LastEmaResult = result;
        }
        else
        {
            LastResult = result;
        }
    }

    /// <summary>Empties the session log - the console panel's "Clear" action.</summary>
    public void ClearLog()
    {
        lock (_log)
        {
            _log.Clear();
        }
    }

    /// <summary>
    /// Records a successful primary-leg connection, disposing any previous
    /// McpClient first - otherwise a second call (e.g. two racing connection
    /// attempts both succeeding) leaks the earlier HttpClientTransport/HttpClient.
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

        Status = ClientConnectionStatus.Connected;

        await DisposalHelper.DisposeIfNotNullAsync(previousClient);
    }

    /// <summary>Mirrors <see cref="SetConnectedAsync"/> for the EMA leg.</summary>
    public async ValueTask SetEmaConnectedAsync(McpClient client, IEnumerable<(string Name, string? Description, string? Schema)> tools)
    {
        var previousClient = EmaClient;
        EmaClient = client;
        lock (_emaTools)
        {
            _emaTools.Clear();
            _emaTools.AddRange(tools);
        }

        EmaStatus = ClientConnectionStatus.Connected;

        await DisposalHelper.DisposeIfNotNullAsync(previousClient);
    }

    /// <summary>
    /// Atomically checks and transitions to Connecting, so two near-simultaneous
    /// /connect requests can't both start a connection attempt. Also cancels/
    /// discards any prior attempt, so a stuck one doesn't require restarting
    /// this process - just a fresh /connect.
    /// </summary>
    /// <returns>false if a connection attempt is already in progress or connected, in which case <paramref name="cancellationToken"/> is not meaningful.</returns>
    public bool TryBeginConnecting(out CancellationToken cancellationToken)
    {
        lock (_connectLock)
        {
            if (Status is ClientConnectionStatus.Connecting or ClientConnectionStatus.Connected)
            {
                cancellationToken = default;
                return false;
            }

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            Status = ClientConnectionStatus.Connecting;
            cancellationToken = _connectCts.Token;
            return true;
        }
    }

    /// <summary>
    /// Mirrors <see cref="TryBeginConnecting"/> for the EMA leg, additionally
    /// requiring the primary leg to already be Connected - the EMA leg's
    /// hand-rolled login is independent of the primary leg's MCP session but
    /// only makes sense to start once it exists.
    /// </summary>
    public bool TryBeginEmaConnecting(out CancellationToken cancellationToken)
    {
        lock (_emaConnectLock)
        {
            if (Status != ClientConnectionStatus.Connected || EmaStatus is ClientConnectionStatus.Connecting or ClientConnectionStatus.Connected)
            {
                cancellationToken = default;
                return false;
            }

            _emaConnectCts?.Cancel();
            _emaConnectCts?.Dispose();
            _emaConnectCts = new CancellationTokenSource();
            EmaStatus = ClientConnectionStatus.Connecting;
            cancellationToken = _emaConnectCts.Token;
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

    /// <summary>Mirrors <see cref="CancelConnecting"/> for the EMA leg.</summary>
    public void CancelEmaConnecting()
    {
        lock (_emaConnectLock)
        {
            _emaConnectCts?.Cancel();
        }
    }
}
