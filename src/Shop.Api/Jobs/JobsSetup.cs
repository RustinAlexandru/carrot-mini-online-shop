using Microsoft.Extensions.Options;

namespace Shop.Api.Jobs;

public static class JobsSetup
{
    public static IServiceCollection AddDraftSweeper(this IServiceCollection services)
    {
        services.AddOptions<DraftSweeperOptions>().BindConfiguration(DraftSweeperOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DraftSweeperOptions>, DraftSweeperOptionsValidator>();
        services.AddScoped<DraftSweepRunner>();
        services.AddHostedService<DraftSweeper>();
        return services;
    }
}
