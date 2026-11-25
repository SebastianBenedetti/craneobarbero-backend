using System.Text.Json;
using CraneoBarbero.Api.Common.Errors;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CraneoBarbero.Api.Common.Validation;

public sealed class SnakeCaseEnumModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Metadata.ModelType) ?? context.Metadata.ModelType;
        return type.IsEnum && !context.Metadata.IsComplexType ? new SnakeCaseEnumModelBinder(type) : null;
    }
}

public sealed class SnakeCaseEnumModelBinder(Type enumType) : IModelBinder
{
    private readonly Dictionary<string, object> _values = Enum.GetValues(enumType)
        .Cast<object>()
        .ToDictionary(value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!), value => value, StringComparer.OrdinalIgnoreCase);

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (result == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);
        var text = result.FirstValue?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        if (_values.TryGetValue(text, out var value) ||
            (!text.Any(char.IsDigit) && Enum.TryParse(enumType, text, ignoreCase: true, out value)))
        {
            bindingContext.Result = ModelBindingResult.Success(value);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, ErrorMessages.InvalidValue);
        return Task.CompletedTask;
    }
}
