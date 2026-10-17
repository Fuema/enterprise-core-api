using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace AngularCrudApi.Infrastructure;

/// <summary>
/// Runs DataAnnotations on the request body and returns a 400 ValidationProblem whose
/// <c>errors</c> keys are camelCase property names ("firstName", "email"...). The React
/// form maps each key onto the matching field automatically.
///
/// Minimal APIs do NOT execute DataAnnotations by themselves; attach this filter with
/// <c>.WithValidation&lt;TRequest&gt;()</c> on every endpoint that accepts a body.
/// </summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A request body is required.");
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return Results.ValidationProblem(ValidationErrors.From(results));
        }

        return await next(context);
    }
}

public static class ValidationErrors
{
    public static string Key(string memberName) => JsonNamingPolicy.CamelCase.ConvertName(memberName);

    public static Dictionary<string, string[]> From(IEnumerable<ValidationResult> results) =>
        results
            .SelectMany(result => (result.MemberNames.Any() ? result.MemberNames : [string.Empty])
                .Select(member => (Key: Key(member), Message: result.ErrorMessage ?? "The value is invalid.")))
            .GroupBy(error => error.Key, error => error.Message)
            .ToDictionary(group => group.Key, group => group.ToArray());

    /// <summary>A single field error, e.g. Field("email", "This email is already in use").</summary>
    public static Dictionary<string, string[]> Field(string key, string message) => new() { [key] = [message] };
}

public static class ValidationEndpointExtensions
{
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder) where TRequest : class =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>().ProducesValidationProblem();
}
