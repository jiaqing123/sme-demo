namespace SmeDemo.Application.Errors;

/// <summary>
/// A request-shape failure. The API renders it as VALIDATION_ERROR with the
/// per-field dictionary from requirements §7.2.
/// </summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public sealed class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException(string message)
        : base(message)
    {
    }
}

public sealed class ResourceForbiddenException : Exception
{
    public ResourceForbiddenException(string message)
        : base(message)
    {
    }
}

public sealed class UnauthenticatedException : Exception
{
    public UnauthenticatedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A required external store (SQL Server, Table or Blob Storage) could not be
/// reached after retries. The API renders it as UPSTREAM_UNAVAILABLE/503 from
/// requirements §7.2 instead of leaking a driver exception as INTERNAL.
/// </summary>
public sealed class StoreUnavailableException : Exception
{
    public StoreUnavailableException(string message)
        : base(message)
    {
    }

    public StoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
