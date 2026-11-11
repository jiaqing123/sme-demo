using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;

namespace SmeDemo.Application.Features.Me;

public sealed record GetMeQuery(string Id, string Org, string Name) : IQuery<MeResponse>;

public sealed class GetMeHandler : IQueryHandler<GetMeQuery, MeResponse>
{
    public Task<MeResponse> HandleAsync(GetMeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Task.FromResult(new MeResponse(query.Id, query.Org, query.Name));
    }
}
