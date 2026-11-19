using System.Text.Json.Serialization;

namespace CraneoBarbero.Api.Common.Errors;

public sealed record ApiErrorResponse(
    string Error,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Errors = null);
