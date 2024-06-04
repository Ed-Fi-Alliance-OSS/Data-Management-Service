// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// The Backend PostgreSQL service extensions to be registered to a Frontend DI container
/// </summary>
public static class PostgresqlBackendServiceExtensions
{
    /// <summary>
    /// The Postgresql backend service configuration
    /// </summary>
    /// <param name="connectionString">The PostgreSQL database connection string</param>
    public static IServiceCollection AddPostgresqlBackend(
        this IServiceCollection services,
        string connectionString
    )
    {
        services.AddSingleton((sp) => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IDocumentStoreRepository, PostgresqlDocumentStoreRepository>();
        services.AddSingleton<ISqlAction, SqlAction>();
        services.AddSingleton<IGetDocumentById, GetDocumentById>();
        services.AddSingleton<IGetDocumentByResourceName, GetDocumentByResourceName>();
        services.AddSingleton<IUpdateDocumentById, UpdateDocumentById>();
        services.AddSingleton<IUpsertDocument, UpsertDocument>();
        return services;
    }
}
