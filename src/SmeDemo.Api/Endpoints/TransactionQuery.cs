namespace SmeDemo.Api.Endpoints;

/// <summary>
/// The query-string surface of the transactions endpoint. <c>[AsParameters]</c>
/// binds each property by name, keeping the handler signature flat.
/// </summary>
public sealed class TransactionQuery
{
    public int? Limit { get; init; }
    public string? Cursor { get; init; }
}
