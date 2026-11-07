using MediatR;
using SmeDemo.Application.Contracts;

namespace SmeDemo.Application.Features.Me;

public sealed record GetMeQuery(string Id, string Org, string Name) : IRequest<MeResponse>;

public sealed class GetMeHandler : IRequestHandler<GetMeQuery, MeResponse>
{
    public Task<MeResponse> Handle(GetMeQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new MeResponse(request.Id, request.Org, request.Name));
    }
}
