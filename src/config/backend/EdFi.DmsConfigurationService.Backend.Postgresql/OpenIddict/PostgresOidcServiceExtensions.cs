// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Data;

using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
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
            // services.AddSingleton<IClientRepository, OpenIddictClientRepository>()
            services.AddSingleton<ITokenManager, OpenIddictTokenManager>();
            // services AddSingleton(typeof(IOpenIddictTokenStore<>), typeof(OpenIddictDapperTokenStore<>))
            // TODO: services AddSingleton<ITokenManager, OpenIddictTokenManager>
            // Register IDbConnection for Npgsql
            services.AddTransient<IDbConnection>(sp =>
             {
                 var config = sp.GetRequiredService<IConfiguration>();
                 return new NpgsqlConnection(config.GetConnectionString("DatabaseConnection"));
             });
            return services;
        }
    }
}
