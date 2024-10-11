// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql;

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
        return services;
    }
}
