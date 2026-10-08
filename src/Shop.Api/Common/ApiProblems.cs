namespace Shop.Api.Common;

public static class ApiProblems
{
    public static IResult Validation(IDictionary<string, string[]> errors)
        => Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);

    public static IResult Unauthorized(string detail)
        => Results.Problem(detail: detail, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
}
