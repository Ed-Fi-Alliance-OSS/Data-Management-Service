// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.Configuration;
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
    /// The Postgresql backend datastore configuration with per-request connection string support
    /// </summary>
    public static IServiceCollection AddPostgresqlDatastore(this IServiceCollection services)
    {
        // Register NpgsqlDataSource as scoped with lazy factory that uses per-request connection string
        // Lazy evaluation ensures connection string is only retrieved when NpgsqlDataSource is actually used
        services.AddScoped<NpgsqlDataSource>(sp =>
        {
            var lazyDataSource = sp.GetRequiredService<Lazy<NpgsqlDataSource>>();
            return lazyDataSource.Value;
        });

        services.AddScoped<Lazy<NpgsqlDataSource>>(sp => new Lazy<NpgsqlDataSource>(() =>
        {
            var requestConnectionStringProvider = sp.GetRequiredService<IRequestConnectionStringProvider>();
            string connectionString = requestConnectionStringProvider.GetConnectionString();
            return NpgsqlDataSource.Create(connectionString);
        }));

        // Register all repositories as scoped (they depend on scoped NpgsqlDataSource)
        services.AddScoped<IDocumentStoreRepository, PostgresqlDocumentStoreRepository>();
        services.AddScoped<IAuthorizationRepository, PostgresqlAuthorizationRepository>();
        services.AddScoped<IGetDocumentById, GetDocumentById>();
        services.AddScoped<IQueryDocument, QueryDocument>();
        services.AddScoped<IUpdateDocumentById, UpdateDocumentById>();
        services.AddScoped<IUpsertDocument, UpsertDocument>();
        services.AddScoped<IDeleteDocumentById, DeleteDocumentById>();
        services.AddScoped<ISqlAction, SqlAction>();
        return services;
    }

    /// <summary>
    /// The Postgresql backend query handler configuration
    /// This can only be used with PostgreSQL also as the backend datastore
    /// </summary>
    public static IServiceCollection AddPostgresqlQueryHandler(this IServiceCollection services)
    {
        services.AddScoped<IQueryHandler, PostgresqlDocumentStoreRepository>();
        return services;
    }
}
