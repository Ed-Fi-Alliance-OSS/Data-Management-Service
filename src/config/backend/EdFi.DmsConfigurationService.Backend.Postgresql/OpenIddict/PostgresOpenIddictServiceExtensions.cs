// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Data;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Configuration;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    public static class PostgresOpenIddictServiceExtensions
    {
        public static IServiceCollection AddPostgresOpenIddictStores(this IServiceCollection services, IConfiguration configuration, string authority)
        {
            // Add identity options
            services.AddOpenIddictIdentityOptions(configuration);
            services.AddSingleton<IClientRepository, PostgresOpenIddictClientRepository>();
            services.AddSingleton<ITokenManager, PostgresTokenManager>();
            services.AddSingleton<IClientSecretHasher, ClientSecretHasher>();

            // Add enhanced OpenIddict-compatible services
            services.AddScoped<IOpenIdConnectConfigurationProvider, OpenIdConnectConfigurationProvider>();
            services.AddScoped<IEnhancedTokenValidator, EnhancedTokenValidator>();

            // Add minimal OpenIddict validation support
            var issuer = configuration["IdentitySettings:Authority"];
            var audience = configuration["IdentitySettings:Audience"];

            services
                .AddOpenIddict()
                .AddValidation(options =>
                {
                    options.SetIssuer(issuer ?? string.Empty);
                    options.UseSystemNetHttp();
                    options.UseAspNetCore();
                });

            // Register JWT authentication with default settings (keeping existing implementation)
            services.AddJwtAuthentication(
                new JwtSettings
                {
                    Issuer = issuer ?? String.Empty,
                    Audience = audience ?? String.Empty,
                    ExpirationMinutes = 30,
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
            // Add identity options
            services.AddOpenIddictIdentityOptions(configuration);

            services.AddSingleton<IClientRepository, PostgresOpenIddictClientRepository>();
            services.AddSingleton<ITokenManager, PostgresTokenManager>();
            services.AddSingleton<IClientSecretHasher, ClientSecretHasher>();

            // Add enhanced OpenIddict-compatible services
            services.AddScoped<IOpenIdConnectConfigurationProvider, OpenIdConnectConfigurationProvider>();
            services.AddScoped<IEnhancedTokenValidator, EnhancedTokenValidator>();

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
