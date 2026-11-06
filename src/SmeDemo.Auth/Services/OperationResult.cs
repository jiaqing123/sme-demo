using SmeDemo.Auth.Errors;

namespace SmeDemo.Auth.Services;

public sealed record OperationResult<T>(T? Value, ApiProblem? Problem)
{
    public bool Succeeded => Problem is null;

    public static OperationResult<T> Success(T value) => new(value, null);

    public static OperationResult<T> Failure(ApiProblem problem) => new(default, problem);
}
