using Microsoft.Extensions.Options;

namespace Shop.Api.Jobs;

/// <summary>Configuration section DraftSweeper; override from the environment as DraftSweeper__Interval, DraftSweeper__DraftTtl, DraftSweeper__Enabled.</summary>
public sealed class DraftSweeperOptions
{
    public const string SectionName = "DraftSweeper";

    /// <summary>PeriodicTimer accepts whole milliseconds from 1 up to uint.MaxValue - 1 (about 49.7 days).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Keeps now - DraftTtl well inside DateTimeOffset and SQL datetimeoffset; 100 years is far beyond any real use.</summary>
    public static readonly TimeSpan MaxDraftTtl = TimeSpan.FromDays(36500);

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan DraftTtl { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class DraftSweeperOptionsValidator : IValidateOptions<DraftSweeperOptions>
{
    public ValidateOptionsResult Validate(string? name, DraftSweeperOptions options)
    {
        var failures = new List<string>();
        if (options.Interval < DraftSweeperOptions.MinInterval || options.Interval > DraftSweeperOptions.MaxInterval)
            failures.Add($"DraftSweeper:Interval must be between {DraftSweeperOptions.MinInterval} and {DraftSweeperOptions.MaxInterval} (the PeriodicTimer range), but was {options.Interval}.");
        if (options.DraftTtl <= TimeSpan.Zero || options.DraftTtl > DraftSweeperOptions.MaxDraftTtl)
            failures.Add($"DraftSweeper:DraftTtl must be positive and at most {DraftSweeperOptions.MaxDraftTtl}, but was {options.DraftTtl}.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
