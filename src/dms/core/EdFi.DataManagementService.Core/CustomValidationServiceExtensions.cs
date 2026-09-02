// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Registers the startup guard that audits ICustomResourceValidator registrations.
/// </summary>
public static class CustomValidationServiceExtensions
{
    /// <summary>
    /// Registers <see cref="CustomValidatorRegistrationGuard"/> so it runs during startup task
    /// execution and can see every ICustomResourceValidator descriptor a plugin registers, whether
    /// that registration runs before or after this call.
    /// </summary>
    public static IServiceCollection AddCustomValidationGuard(this IServiceCollection services)
    {
        // The guard's other sibling registration guards use AddSingleton<IDmsStartupTask, T>(), which
        // resolves its constructor arguments from the container. That will not work here: the guard
        // needs the live IServiceCollection itself, and IServiceCollection cannot be resolved from DI
        // because it is never registered as a service - see the comment below. So this registers
        // through the factory overload instead, closing over the `services` parameter directly. The
        // closure is the only path by which the guard can see the collection; do not "tidy" this back
        // to the two-generic-argument form used by the sibling guards.
        services.AddSingleton<IDmsStartupTask>(sp => new CustomValidatorRegistrationGuard(
            services,
            sp,
            sp.GetRequiredService<IEffectiveApiSchemaProvider>(),
            sp.GetRequiredService<ILogger<CustomValidatorRegistrationGuard>>()
        ));

        // Deliberately not done: services.AddSingleton<IServiceCollection>(services). IServiceCollection
        // is declared in Microsoft.Extensions.DependencyInjection.Abstractions, a namespace the plugin
        // spine permits a plugin to register into. A guard that resolved IServiceCollection from DI
        // would read whichever registration won the race, not necessarily this one. The closure above
        // cannot be displaced that way, so the collection must reach the guard only through it.

        return services;
    }
}
