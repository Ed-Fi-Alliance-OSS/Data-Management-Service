// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Registration of the job runtime's provider-neutral services, job handlers, and consumer error codes (spec D-7,
/// D-12, D-13). Every rule is checked when the services are composed, so a bad registration fails startup.
/// </summary>
public static class JobServiceCollectionExtensions
{
    /// <summary>
    /// The registry, the error-code registry, the shared <see cref="JobCommandValidator"/>, the enqueuer, and the
    /// schedule service. Calling it more than once is harmless.
    /// </summary>
    public static IServiceCollection AddJobServices(this IServiceCollection services)
    {
        services.TryAddSingleton(Instance<JobHandlerRegistrations>(services));
        services.TryAddSingleton(Instance<JobErrorCodeRegistrations>(services));
        services.TryAddSingleton<IJobHandlerRegistry, JobHandlerRegistry>();
        services.TryAddSingleton<IJobErrorCodeRegistry, JobErrorCodeRegistry>();
        services.TryAddTransient<JobCommandValidator>();
        services.TryAddTransient<IJobEnqueuer, JobEnqueuer>();
        services.TryAddTransient<IJobScheduleService, JobScheduleService>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <paramref name="jobType"/> and the payload versions it accepts. The
    /// job type must match the key syntax and be registered once; <typeparamref name="TPayload"/> must satisfy the
    /// payload contract; <typeparamref name="TValidator"/> applies the type's own rules.
    /// </summary>
    public static IServiceCollection AddJobHandler<THandler, TPayload, TValidator>(
        this IServiceCollection services,
        string jobType,
        params short[] payloadVersions
    )
        where THandler : class, IJobHandler<TPayload>
        where TPayload : class
        where TValidator : class, IJobPayloadValidator<TPayload>
    {
        services.AddJobServices();
        Instance<JobHandlerRegistrations>(services)
            .Add(JobHandlerRegistration.Create<THandler, TPayload, TValidator>(jobType, payloadVersions));
        services.TryAddTransient<THandler>();
        services.TryAddTransient<TValidator>();
        return services;
    }

    /// <summary>Registers a consumer error code and its fixed public message.</summary>
    public static IServiceCollection AddJobErrorCode(
        this IServiceCollection services,
        string code,
        string message
    )
    {
        services.AddJobServices();
        Instance<JobErrorCodeRegistrations>(services).Add(code, message);
        return services;
    }

    /// <summary>The single instance of <typeparamref name="T"/> registered while the services are composed.</summary>
    private static T Instance<T>(IServiceCollection services)
        where T : class, new()
    {
        if (
            services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(T))?.ImplementationInstance
            is T existing
        )
        {
            return existing;
        }

        T created = new();
        services.AddSingleton(created);
        return created;
    }
}
