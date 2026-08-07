namespace Highway.Abstractions;

/// <summary>
/// HTTP-style status codes used by Highway services.
/// A service that returns normally gets 200. A service that throws gets 500.
/// These are the same semantics everyone already knows from HTTP.
/// </summary>
public static class StatusCodes
{
    // 2xx Success
    public const int Status200OK = 200;
    public const int Status201Created = 201;
    public const int Status202Accepted = 202;
    public const int Status204NoContent = 204;

    // 4xx Client Errors
    public const int Status400BadRequest = 400;
    public const int Status401Unauthorized = 401;
    public const int Status403Forbidden = 403;
    public const int Status404NotFound = 404;
    public const int Status405MethodNotAllowed = 405;
    public const int Status408RequestTimeout = 408;
    public const int Status409Conflict = 409;
    public const int Status412PreconditionFailed = 412;
    public const int Status413PayloadTooLarge = 413;
    public const int Status422UnprocessableEntity = 422;
    public const int Status429TooManyRequests = 429;

    // 5xx Server Errors
    public const int Status500InternalServerError = 500;
    public const int Status501NotImplemented = 501;
    public const int Status502BadGateway = 502;
    public const int Status503ServiceUnavailable = 503;
    public const int Status504GatewayTimeout = 504;
}
