namespace AuroraDbManager.Api.Application.Monitoring;

/// <summary>
/// The id of the API request the current code is running for, if it is running for one. Set by
/// the request pipeline; read where work is handed to the background, so the logs of a job can be
/// traced back to the request that created it.
/// </summary>
public static class RequestCorrelation
{
    /// <summary>The name the id has in log scopes.</summary>
    public const string LogProperty = "CorrelationId";

    private static readonly AsyncLocal<string?> CurrentId = new();

    public static string? Current
    {
        get => CurrentId.Value;
        set => CurrentId.Value = value;
    }
}
