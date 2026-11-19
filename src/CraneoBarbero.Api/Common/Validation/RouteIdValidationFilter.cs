using CraneoBarbero.Api.Common.Errors;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CraneoBarbero.Api.Common.Validation;

public sealed class RouteIdValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            if (parameter.BindingInfo?.BindingSource != BindingSource.Path)
            {
                continue;
            }

            if (!context.ActionArguments.TryGetValue(parameter.Name, out var value))
            {
                continue;
            }

            var isInvalid = value switch
            {
                int number => number <= 0,
                long number => number <= 0,
                _ => false,
            };

            if (isInvalid)
            {
                throw new BadRequestException(ErrorMessages.InvalidId, ErrorCodes.InvalidId);
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
