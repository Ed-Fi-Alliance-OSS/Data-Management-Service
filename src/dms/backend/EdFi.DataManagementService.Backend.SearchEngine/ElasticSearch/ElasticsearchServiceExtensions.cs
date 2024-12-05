// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.SearchEngine.elasticsearch;

/// <summary>
/// The Backend ElasticSearch service extensions to be registered to a Frontend DI container
/// </summary>
public static class ElasticsearchServiceExtensions
{
    /// <summary>
    /// The Elasticsearch backend query handler configuration
    /// </summary>
    /// <param name="connectionUrl">The Elasticsearch connection URL</param>
    public static IServiceCollection AddElasticsearchQueryHandler(
        this IServiceCollection services,
        string connectionUrl
    )
    {
        services.AddSingleton(
            _ =>
            {
                var settings = new ElasticsearchClientSettings(new Uri(connectionUrl));
                return new ElasticsearchClient(settings);
            }
        );
        services.AddSingleton<IQueryHandler, ElasticsearchQueryHandlerRepository>();
        return services;
    }
}
