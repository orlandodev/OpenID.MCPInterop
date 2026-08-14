namespace OpenID.MCPInterop.Client.Models;

/// <summary>What /connect's own catch block should do for a failure observed while awaiting the authorization URL - see ConnectFailureReporter.</summary>
internal enum ConnectFailureAction
{
    /// <summary>RunConnectionAsync's own catch already logged this exception and set Status - nothing more to do.</summary>
    AlreadyHandled,

    /// <summary>The 30s wait itself elapsed with RunConnectionAsync still not having reported anything - genuinely new information, worth logging.</summary>
    ReportTimeout,
}
