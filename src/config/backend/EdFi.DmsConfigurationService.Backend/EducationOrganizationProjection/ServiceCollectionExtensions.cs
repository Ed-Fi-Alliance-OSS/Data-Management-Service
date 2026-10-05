// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>Registration of the DMS education-organization projection reader (DMS-1440 spec §5.1).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="DmsEducationOrganizationProjectionSettings"/> at startup, registers the named
    /// <see cref="HttpClient"/> with <see cref="ProjectionHttpClientLogger"/> as its only logger, the Discovery client
    /// and the token provider (one cache each per process), and the projection job error codes. Calling it more than
    /// once is harmless.
    /// </summary>
    public static IServiceCollection AddDmsEducationOrganizationProjectionReader(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RegistrationMarker)))
        {
            return services;
        }
        services.AddSingleton<RegistrationMarker>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<DmsEducationOrganizationProjectionSettings>,
                DmsEducationOrganizationProjectionSettingsValidator
            >()
        );
        services
            .AddOptions<DmsEducationOrganizationProjectionSettings>()
            .Bind(configuration.GetSection(DmsEducationOrganizationProjectionSettings.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ProjectionHttpClientLogger>();

        services
            .AddHttpClient(
                DmsEducationOrganizationProjectionHttpClient.Name,
                DmsEducationOrganizationProjectionHttpClient.Configure
            )
            .ConfigurePrimaryHttpMessageHandler(
                DmsEducationOrganizationProjectionHttpClient.CreatePrimaryHandler
            )
            .RemoveAllLoggers()
            .AddLogger<ProjectionHttpClientLogger>();

        services.TryAddSingleton<IDmsDiscoveryClient, DmsDiscoveryClient>();
        services.TryAddSingleton<IProjectionServiceTokenProvider, ProjectionServiceTokenProvider>();

        foreach ((string code, string message) in EducationOrganizationProjectionJobErrorCodes.All)
        {
            services.AddJobErrorCode(code, message);
        }

        return services;
    }

    /// <summary>Marks the registration as done, so a second call adds nothing.</summary>
    private sealed class RegistrationMarker;
}
