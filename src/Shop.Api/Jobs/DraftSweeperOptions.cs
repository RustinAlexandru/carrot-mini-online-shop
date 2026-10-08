using Microsoft.Extensions.Options;

namespace Shop.Api.Jobs;

/// <summary>Configuration section DraftSweeper; override from the environment as DraftSweeper__Interval, DraftSweeper__DraftTtl, DraftSweeper__Enabled.</summary>
public sealed class DraftSweeperOptions
{
    public const string SectionName = "DraftSweeper";

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan DraftTtl { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class DraftSweeperOptionsValidator : IValidateOptions<DraftSweeperOptions>
{
    public ValidateOptionsResult Validate(string? name, DraftSweeperOptions options)
    {
        var failures = new List<string>();
        if (options.Interval <= TimeSpan.Zero) failures.Add("DraftSweeper:Interval must be positive.");
        if (options.DraftTtl <= TimeSpan.Zero) failures.Add("DraftSweeper:DraftTtl must be positive.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
