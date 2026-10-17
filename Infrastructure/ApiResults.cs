namespace AngularCrudApi.Infrastructure;

/// <summary>ProblemDetails shortcuts shared by every resource.</summary>
public static class ApiResults
{
    public static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    /// <summary>409: the request is valid but the current data forbids it (e.g. delete a row still in use).</summary>
    public static IResult Conflict(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    /// <summary>400 ValidationProblem with a single field error (key = camelCase field name).</summary>
    public static IResult FieldError(string field, string message) =>
        Results.ValidationProblem(ValidationErrors.Field(field, message));

    /// <summary>"1 state" / "3 states".</summary>
    public static string Plural(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
