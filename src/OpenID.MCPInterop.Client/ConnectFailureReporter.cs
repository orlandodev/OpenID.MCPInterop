namespace OpenID.MCPInterop.Client;

/// <summary>
/// Decides what /connect's catch block should log for a failure while awaiting
/// the authorization URL. RunConnectionAsync's own catch already logs and sets
/// Status for anything it throws directly, so only a bare TimeoutException
/// (the 30s wait itself elapsing) is genuinely new here - re-logging the rest
/// duplicated a single failure under two messages.
/// </summary>
internal static class ConnectFailureReporter
{
    public static ConnectFailureAction Classify(Exception exception) =>
        exception is TimeoutException
            ? ConnectFailureAction.ReportTimeout
            : ConnectFailureAction.AlreadyHandled;
}
