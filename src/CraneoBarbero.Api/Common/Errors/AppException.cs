namespace CraneoBarbero.Api.Common.Errors;

public abstract class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public virtual IReadOnlyDictionary<string, string[]>? Errors => null;
}

public sealed class BadRequestException(string message, string code = ErrorCodes.BadRequest)
    : AppException(StatusCodes.Status400BadRequest, code, message);

public sealed class UnauthorizedException(string message, string code = ErrorCodes.Unauthorized)
    : AppException(StatusCodes.Status401Unauthorized, code, message);

public sealed class ForbiddenException(string message = ErrorMessages.Forbidden, string code = ErrorCodes.Forbidden)
    : AppException(StatusCodes.Status403Forbidden, code, message);

public sealed class NotFoundException(string message = ErrorMessages.NotFound, string code = ErrorCodes.NotFound)
    : AppException(StatusCodes.Status404NotFound, code, message);

public sealed class ConflictException(string message, string code = ErrorCodes.Conflict)
    : AppException(StatusCodes.Status409Conflict, code, message);

public sealed class TooManyRequestsException(string message = ErrorMessages.TooManyRequests, string code = ErrorCodes.TooManyRequests)
    : AppException(StatusCodes.Status429TooManyRequests, code, message);

public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : AppException(StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, ErrorMessages.ValidationError)
{
    public override IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
