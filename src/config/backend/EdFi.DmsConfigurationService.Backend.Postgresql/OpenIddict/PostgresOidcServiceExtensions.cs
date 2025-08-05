// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Data;
using EdFi.DmsConfigurationService.Backend.OpenIddict;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    public static class PostgresOidcServiceExtensions
    {
        public static IServiceCollection AddPostgresOpenIddictStores(this IServiceCollection services, string authority)
        {
            services.AddSingleton<IClientRepository, PostgresClientSqlProvider>();
            services.AddSingleton<ITokenManager, PostgresTokenManager>();

            // Register JWT authentication with default settings
            services.AddJwtAuthentication(new JwtSettings
            {
                Issuer = "https://dms-config-service",
                Audience = "dms-api",
                ExpirationHours = 1
            });

            // Register database connection
            services.AddTransient<IDbConnection>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                return new NpgsqlConnection(config.GetConnectionString("DatabaseConnection"));
            });
            return services;
        }

        /// <summary>
        /// Adds PostgreSQL OpenIddict stores with custom JWT settings
        /// </summary>
        public static IServiceCollection AddPostgresOpenIddictStores(
            this IServiceCollection services,
            string authority,
            JwtSettings jwtSettings)
        {
            services.AddSingleton<IClientRepository, PostgresClientSqlProvider>();
            services.AddSingleton<ITokenManager, PostgresTokenManager>();
            services.AddJwtAuthentication(jwtSettings);

            services.AddTransient<IDbConnection>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                return new NpgsqlConnection(config.GetConnectionString("DatabaseConnection"));
            });

            return services;
        }
    }
}
