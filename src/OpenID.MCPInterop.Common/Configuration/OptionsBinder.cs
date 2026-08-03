using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace OpenID.MCPInterop.Common.Configuration;

/// <summary>
/// Binds a configuration section into a strongly-typed options POCO
/// (annotated with <see cref="RequiredAttribute"/> on whatever fields are
/// mandatory) and validates it. Every missing/invalid field in a section is reported
/// together in one exception, not one at a time as each is first accessed.
/// </summary>
public static class OptionsBinder
{
    public static T BindAndValidate<T>(IConfiguration configuration, string sectionName) where T : new()
    {
        var options = configuration.GetSection(sectionName).Get<T>() ?? new T();

        var validationContext = new ValidationContext(options);
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, validationContext, validationResults, validateAllProperties: true))
        {
            var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
            throw new InvalidOperationException($"Missing or invalid configuration in section '{sectionName}': {errors}");
        }

        return options;
    }
}
