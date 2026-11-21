// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>Registration of the education-organization refresh (DMS-1441 spec §6.3, §6.5, §6.6, §6.10).</summary>
public static class EducationOrganizationRefreshServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="EducationOrganizationRefreshOptions"/> from the <c>AppSettings</c> section and validates it at
    /// startup, registers the refresh's single job error codes and its composite code, and registers the refresh job
    /// handler with its target refresher. The projection reader, whose error codes the composite lists, is registered
    /// first. The handler needs the snapshot repository, which the datastore registration provides, and the job
    /// options. Calling it more than once is harmless.
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
        services.AddDmsEducationOrganizationProjectionReader(configuration);

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
        services.AddJobCompositeErrorCode(
            EducationOrganizationRefreshJobErrorCodes.Incomplete,
            EducationOrganizationRefreshJobErrorCodes.IncompleteMessage,
            EducationOrganizationRefreshJobErrorCodes.IncompleteCategories
        );

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<EducationOrganizationTargetRefresher>();
        services.AddJobHandler<
            EducationOrganizationRefreshJobHandler,
            EducationOrganizationRefreshPayload,
            EducationOrganizationRefreshPayloadValidator
        >(EducationOrganizationRefreshJobTypes.Refresh, EducationOrganizationRefreshJobTypes.PayloadVersion);

        return services;
    }

    /// <summary>Marks the registration as done, so a second call adds nothing.</summary>
    private sealed class RegistrationMarker;
}
