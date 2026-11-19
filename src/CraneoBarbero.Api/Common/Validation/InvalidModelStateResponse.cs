using CraneoBarbero.Api.Common.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CraneoBarbero.Api.Common.Validation;

public static class InvalidModelStateResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var routeParameters = context.ActionDescriptor.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Path)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var invalidEntries = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .ToList();

        if (invalidEntries.Count > 0 && invalidEntries.All(entry => routeParameters.Contains(entry.Key)))
        {
            return new BadRequestObjectResult(new ApiErrorResponse(ErrorMessages.InvalidId, ErrorCodes.InvalidId));
        }

        var errors = invalidEntries.ToDictionary(
            entry => ErrorKey.ToCamelCase(entry.Key),
            entry => entry.Value!.Errors
                .Select(error => Describe(entry.Key, error))
                .Distinct()
                .ToArray());

        return new BadRequestObjectResult(new ApiErrorResponse(ErrorMessages.ValidationError, ErrorCodes.ValidationError, errors));
    }

    private static string Describe(string key, ModelError error)
    {
        if (key.StartsWith('$') || error.Exception is System.Text.Json.JsonException)
        {
            return ErrorMessages.InvalidJson;
        }

        return string.IsNullOrWhiteSpace(error.ErrorMessage) ? ErrorMessages.InvalidValue : error.ErrorMessage;
    }
}
