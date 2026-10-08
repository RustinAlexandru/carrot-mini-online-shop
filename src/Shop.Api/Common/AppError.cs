namespace Shop.Api.Common;

/// <summary>Transport-independent business failure. Domain and services return it; only <see cref="ApiProblems"/> maps it to HTTP.</summary>
public abstract record AppError
{
    private AppError()
    {
    }

    public sealed record NotFound(string Message) : AppError;

    public sealed record Conflict(string Message) : AppError;

    public sealed record Validation(IReadOnlyDictionary<string, string[]> Errors) : AppError
    {
        public static Validation Single(string field, string message)
            => new(new Dictionary<string, string[]> { [field] = [message] });
    }
}

/// <summary>A value or an <see cref="AppError"/>.</summary>
public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly AppError? _error;

    private Result(T? value, AppError? error)
    {
        _value = value;
        _error = error;
    }

    public bool IsSuccess => _error is null;
    public T Value => _error is null ? _value! : throw new InvalidOperationException("Result is a failure.");
    public AppError Error => _error ?? throw new InvalidOperationException("Result is a success.");

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(AppError error) => new(default, error);

    public static implicit operator Result<T>(AppError error) => Fail(error);
    public static implicit operator Result<T>(T value) => Ok(value);
}
