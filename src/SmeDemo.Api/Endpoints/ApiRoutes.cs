namespace SmeDemo.Api.Endpoints;

internal static class ApiRoutes
{
    public const string Version = "v1";
    public const string Prefix = "/api/" + Version;
    public const string Me = "/me";
    public const string Cards = "/cards";

    // Endpoint names are used to build URLs (see CardArtUrlProvider), so they
    // live next to the route constants they refer to.
    public const string CardArt = "GetCardArt";
}
