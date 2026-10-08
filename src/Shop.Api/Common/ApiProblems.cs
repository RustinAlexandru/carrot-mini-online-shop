
namespace Shop.Api.Common;

public static class ApiProblems
{
    public static IResult Validation(IDictionary<string, string[]> errors)
        => Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);

    public static IResult Unauthorized(string detail)
        => Results.Problem(detail: detail, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");

    /// <summary>The single AppError-to-HTTP mapping: NotFound 404, Conflict 409, Validation 400 with field errors.</summary>
    public static IResult ToResult(AppError error) => error switch
    {
        AppError.NotFound notFound => Results.Problem(detail: notFound.Message, statusCode: StatusCodes.Status404NotFound, title: "Not Found"),
        AppError.Conflict conflict => Results.Problem(detail: conflict.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
        AppError.Validation validation => Results.ValidationProblem(
            validation.Errors.ToDictionary(e => e.Key, e => e.Value), statusCode: StatusCodes.Status400BadRequest),
        _ => throw new InvalidOperationException($"Unmapped error {error.GetType().Name}.")
    };

    public static IResult ToResult<T>(Result<T> result, Func<T, IResult> success)
        => result.IsSuccess ? success(result.Value) : ToResult(result.Error);
}
