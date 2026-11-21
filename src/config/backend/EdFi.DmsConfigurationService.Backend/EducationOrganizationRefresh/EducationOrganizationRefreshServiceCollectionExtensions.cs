// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>Registration of the education-organization refresh (DMS-1441 spec §6.3, §6.10).</summary>
public static class EducationOrganizationRefreshServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="EducationOrganizationRefreshOptions"/> from the <c>AppSettings</c> section and validates it at
    /// startup, and registers the refresh's single job error codes. Calling it more than once is harmless.
    /// </summary>
    public static IServiceCollection AddEducationOrganizationRefresh(
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
                IValidateOptions<EducationOrganizationRefreshOptions>,
                EducationOrganizationRefreshOptionsValidator
            >()
        );
        services
            .AddOptions<EducationOrganizationRefreshOptions>()
            .Bind(configuration.GetSection(EducationOrganizationRefreshOptions.SectionName))
            .ValidateOnStart();

        foreach ((string code, string message) in EducationOrganizationRefreshJobErrorCodes.All)
        {
            services.AddJobErrorCode(code, message);
        }

        return services;
    }

    /// <summary>Marks the registration as done, so a second call adds nothing.</summary>
    private sealed class RegistrationMarker;
}
