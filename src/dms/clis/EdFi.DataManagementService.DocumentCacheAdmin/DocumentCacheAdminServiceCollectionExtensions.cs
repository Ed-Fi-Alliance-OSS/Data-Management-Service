// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentCacheAdminRuntimeServices(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogger logger,
        DocumentCacheTargetKey invocationTarget
    )
    {
        services.AddDocumentCacheRuntimeServices(
            configuration,
            logger,
            invocationTarget,
            DocumentCacheRuntimeTargetSelection.InvocationTarget
        );
        services.AddCdcDownstreamPublicationHistory(configuration);
        services.AddSingleton<IDocumentCacheAdminTargetResolver, DocumentCacheAdminTargetResolver>();
        services.AddSingleton<
            IDocumentCacheAdminMutatingCommandDispatcher,
            DocumentCacheAdminMutatingCommandDispatcher
        >();
        services.TryAddSingleton<IDocumentCacheAdminCliTelemetry, DocumentCacheAdminCliTelemetry>();
        return services;
    }
}
