// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Data;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    public static class PostgresOidcServiceExtensions
    {
        public static IServiceCollection AddPostgresOpenIddictStores(this IServiceCollection services, IConfiguration configuration, string authority)
        {
            services.AddSingleton<IClientRepository, PostgresOpenIddictClientRepository>();
            services.AddSingleton<ITokenManager, PostgresTokenManager>();

            // Register JWT authentication with default settings
            // Read Issuer and Audience from IdentitySettings in configuration
            var issuer = configuration["IdentitySettings:Authority"];
            var audience = configuration["IdentitySettings:Audience"];
            services.AddJwtAuthentication(
                new JwtSettings
                {
                    Issuer = issuer ?? String.Empty,
                    Audience = audience ?? String.Empty,
                    ExpirationHours = 1,
                },
                configuration
            );

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
            IConfiguration configuration,
            string authority,
            JwtSettings jwtSettings)
        {
            services.AddSingleton<IClientRepository, PostgresOpenIddictClientRepository>();
            services.AddSingleton<ITokenManager, PostgresTokenManager>();
            services.AddJwtAuthentication(jwtSettings, configuration);

            services.AddTransient<IDbConnection>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                return new NpgsqlConnection(config.GetConnectionString("DatabaseConnection"));
            });

            return services;
        }
    }
}
