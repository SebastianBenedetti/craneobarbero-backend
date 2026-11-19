namespace CraneoBarbero.Api.Common.Errors;

public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string ValidationError = "validation_error";
    public const string InvalidId = "invalid_id";
    public const string Unauthorized = "unauthorized";
    public const string TokenExpired = "token_expired";
    public const string AccountInactive = "account_inactive";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string Conflict = "conflict";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string TooManyRequests = "too_many_requests";
    public const string InternalError = "internal_error";
    public const string InvalidCredentials = "invalid_credentials";
    public const string EmailInUse = "email_in_use";
    public const string InvalidToken = "invalid_token";
    public const string EmailAlreadyVerified = "email_already_verified";
    public const string InvalidCurrentPassword = "invalid_current_password";
    public const string SamePassword = "same_password";
    public const string HasFutureAppointments = "has_future_appointments";
    public const string BranchInactive = "branch_inactive";
    public const string HolidayExists = "holiday_exists";
    public const string SlotUnavailable = "slot_unavailable";
    public const string ClientOverlap = "client_overlap";
    public const string BarberUnavailable = "barber_unavailable";
    public const string CutUnavailable = "cut_unavailable";
    public const string ClientUnavailable = "client_unavailable";
    public const string EmailNotVerified = "email_not_verified";
}

public static class ErrorMessages
{
    public const string BadRequest = "La solicitud no es válida.";
    public const string ValidationError = "Los datos enviados no son válidos.";
    public const string InvalidId = "El identificador debe ser un número entero positivo.";
    public const string InvalidValue = "El valor no es válido.";
    public const string InvalidJson = "El cuerpo de la solicitud no es un JSON válido.";
    public const string MissingBody = "Falta el cuerpo de la solicitud.";
    public const string Unauthorized = "Tenés que iniciar sesión.";
    public const string TokenExpired = "Tu sesión venció. Iniciá sesión de nuevo.";
    public const string AccountInactive = "Tu cuenta está desactivada.";
    public const string Forbidden = "No tenés permiso para realizar esta acción.";
    public const string NotFound = "No se encontró el recurso solicitado.";
    public const string MethodNotAllowed = "Método no permitido.";
    public const string Conflict = "La operación entra en conflicto con el estado actual.";
    public const string PayloadTooLarge = "La solicitud es demasiado grande.";
    public const string UnsupportedMediaType = "El tipo de contenido no es compatible.";
    public const string TooManyRequests = "Demasiadas solicitudes. Probá de nuevo en unos minutos.";
    public const string InternalError = "Ocurrió un error inesperado. Probá de nuevo más tarde.";
}
