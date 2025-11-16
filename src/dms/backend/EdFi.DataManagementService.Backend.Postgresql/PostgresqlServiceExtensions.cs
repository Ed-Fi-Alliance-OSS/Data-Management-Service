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
        services.AddSingleton(sp =>
        {
            NpgsqlDataSourceBuilder builder = new(connectionString);
            var csb = builder.ConnectionStringBuilder;

            // Skip RESET/DISCARD when returning pooled connections, we manage session state explicitly.
            csb.NoResetOnClose = true;

            // Make PostgreSQL monitoring output more readable
            if (string.IsNullOrWhiteSpace(csb.ApplicationName))
            {
                csb.ApplicationName = "EdFi.DMS";
            }

            // Let Npgsql handle plan caching automatically
            csb.AutoPrepareMinUsages = 3;
            csb.MaxAutoPrepare = 256;

            return builder.Build();
        });
        services.AddSingleton<IDocumentStoreRepository, PostgresqlDocumentStoreRepository>();
        services.AddSingleton<IAuthorizationRepository, PostgresqlAuthorizationRepository>();
        services.AddSingleton<IGetDocumentById, GetDocumentById>();
        services.AddSingleton<IQueryDocument, QueryDocument>();
        services.AddSingleton<IUpdateDocumentById, UpdateDocumentById>();
        services.AddSingleton<IUpsertDocument, UpsertDocument>();
        services.AddSingleton<IDeleteDocumentById, DeleteDocumentById>();
        services.AddSingleton<ISqlAction, SqlAction>();
        services.AddSingleton<IBatchUnitOfWorkFactory, PostgresqlBatchUnitOfWorkFactory>();
        return services;
    }

    /// <summary>
    /// The Postgresql backend query handler configuration
    /// This can only be used with PostgreSQL also as the backend datastore
    /// </summary>
    public static IServiceCollection AddPostgresqlQueryHandler(this IServiceCollection services)
    {
        services.AddSingleton<IQueryHandler, PostgresqlDocumentStoreRepository>();
        return services;
    }
}
