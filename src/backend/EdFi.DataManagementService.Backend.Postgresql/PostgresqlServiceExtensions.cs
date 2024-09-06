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
public static class PostgresqlServiceExtensions
{
    /// <summary>
    /// The Postgresql backend datastore configuration
    /// </summary>
    /// <param name="connectionString">The PostgreSQL database connection string</param>
    public static IServiceCollection AddPostgresqlDatastore(
        this IServiceCollection services,
        string connectionString
    )
    {
        services.AddSingleton((sp) => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IDocumentStoreRepository, PostgresqlDocumentStoreRepository>();
        services.AddSingleton<IGetDocumentById, GetDocumentById>();
        services.AddSingleton<IQueryDocument, QueryDocument>();
        services.AddSingleton<IUpdateDocumentById, UpdateDocumentById>();
        services.AddSingleton<IUpsertDocument, UpsertDocument>();
        services.AddSingleton<IDeleteDocumentById, DeleteDocumentById>();
        services.AddSingleton<ISqlAction, SqlAction>();
        return services;
    }

    /// <summary>
    /// The Postgresql backend query handler configuration
    /// This can only be used with PostgreSQL also as the backend datastore
    /// </summary>
    public static IServiceCollection AddPostgresqlQueryHandler(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IQueryHandler, PostgresqlDocumentStoreRepository>();
        return services;
    }
}
