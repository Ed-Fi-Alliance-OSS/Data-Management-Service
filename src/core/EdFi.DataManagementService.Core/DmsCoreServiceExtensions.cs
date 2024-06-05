// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// The DMS service extensions to be registered to a Frontend DI container
/// </summary>
public static class DmsCoreServiceExtensions
{
    /// <summary>
    /// The DMS default service configuration
    /// </summary>
    public static IServiceCollection AddDmsDefaultConfiguration(this IServiceCollection services)
    {
        services
            .AddSingleton<IApiSchemaProvider, ApiSchemaFileLoader>()
            .AddSingleton<IApiSchemaSchemaProvider, ApiSchemaSchemaProvider>()
            .AddSingleton<IApiSchemaValidator, ApiSchemaValidator>()
            .AddSingleton<IApiService, ApiService>()
            .AddTransient<IDocumentValidator, DocumentValidator>()
            .AddTransient<IImmutableIdentityValidator, ImmutableIdValidator>()
            .AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>();

        return services;
    }
}
