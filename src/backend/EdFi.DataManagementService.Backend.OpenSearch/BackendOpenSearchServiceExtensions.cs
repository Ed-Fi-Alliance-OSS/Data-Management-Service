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
public static class BackendOpenSearchServiceExtensions
{
    /// <summary>
    /// The OpenSearch backend service configuration
    /// </summary>
    /// <param name="connectionUri">The OpenSearch connection Uri</param>
    public static IServiceCollection AddOpenSearchBackend(
        this IServiceCollection services,
        Uri connectionUri
    )
    {
        services.AddSingleton((sp) => new OpenSearchClient(new ConnectionSettings(connectionUri)));
        services.AddSingleton<IQueryHandler, OpenSearchQueryHandlerRepository>();
        services.AddSingleton<IGetDocumentById, GetDocumentById>();
        services.AddSingleton<IQueryDocument, QueryDocument>();
        services.AddSingleton<IUpdateDocumentById, UpdateDocumentById>();
        services.AddSingleton<IUpsertDocument, UpsertDocument>();
        services.AddSingleton<IDeleteDocumentById, DeleteDocumentById>();
        return services;
    }
}
