// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend;

public static class RelationalRelationshipAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddRelationalRelationshipAuthorizationServices(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RelationalEdOrgAuthorizationElementResolutionCache>();
        services.TryAddSingleton<RelationalEdOrgAuthorizationSubjectSelector>();

        return services;
    }
}
