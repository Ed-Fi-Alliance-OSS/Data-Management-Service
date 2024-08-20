// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;

namespace EdFi.DataManagementService.Backend.OpenSearch;

/// <summary>
/// The Backend OpenSearch service extensions to be registered to a Frontend DI container
/// </summary>
public static class OpenSearchServiceExtensions
{
    /// <summary>
    /// The OpenSearch backend query handler configuration
    /// </summary>
    /// <param name="connectionUrl">The OpenSearch connection URL</param>
    public static IServiceCollection AddOpenSearchQueryHandler(
        this IServiceCollection services,
        string connectionUrl
    )
    {
        services.AddSingleton<IOpenSearchClient>(
            (sp) => new OpenSearchClient(new ConnectionSettings(new Uri(connectionUrl)))
        );
        services.AddSingleton<IQueryHandler, OpenSearchQueryHandlerRepository>();
        return services;
    }
}
