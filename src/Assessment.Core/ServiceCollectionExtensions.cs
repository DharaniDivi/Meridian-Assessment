using Assessment.Core.Configuration;
using Assessment.Core.Http;
using Assessment.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Assessment.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAssessmentCore(this IServiceCollection services)
    {
        services.AddHttpClient<AssessmentHttpClient>();

        services.AddScoped<KeyAcquisitionService>();
        services.AddScoped<Layer1Service>();
        services.AddScoped<Layer2Service>();
        services.AddScoped<Layer3Service>();
        services.AddScoped<Layer4Service>();
        services.AddScoped<SubmissionService>();

        return services;
    }

    public static IServiceCollection AddAssessmentCore(
        this IServiceCollection services,
        Action<AssessmentOptions> configure)
    {
        services.Configure(configure);
        return services.AddAssessmentCore();
    }
}
