// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomValidationConsumer;

/// <summary>
/// The implementer's own registration surface, reproducing design.md's "Registration and
/// Composition" sample: an ordinary <see cref="IServiceCollection"/> extension method that a
/// deployment calls once at its composition root.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDistrictValidators(
        this IServiceCollection services,
        Action<ExternalIdentityOptions> configureIdentity
    )
    {
        services.Configure(configureIdentity);

        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, StudentIdentityValidator>()
        );

        return services;
    }
}
