using SmeDemo.Auth.Contracts;

namespace SmeDemo.Auth.Services;

/// <summary>
/// Request-shape validation. Business decisions (is the password right, is the
/// challenge still alive) stay in <see cref="AuthFlowService"/>.
/// </summary>
public static class RequestValidation
{
    private const int MaxUsernameLength = 128;
    private const int MaxPasswordLength = 256;

    public static IReadOnlyDictionary<string, string[]>? Validate(LoginRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            errors["username"] = ["The username is required."];
        }
        else if (request.Username.Length > MaxUsernameLength)
        {
            errors["username"] = [$"The username must be at most {MaxUsernameLength} characters."];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors["password"] = ["The password is required."];
        }
        else if (request.Password.Length > MaxPasswordLength)
        {
            errors["password"] = [$"The password must be at most {MaxPasswordLength} characters."];
        }

        return errors.Count == 0 ? null : errors;
    }

    public static IReadOnlyDictionary<string, string[]>? Validate(VerifyRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.Code is not { Length: 6 } code || !code.All(char.IsAsciiDigit))
        {
            errors["code"] = ["The code must be 6 digits."];
        }

        return errors.Count == 0 ? null : errors;
    }
}
