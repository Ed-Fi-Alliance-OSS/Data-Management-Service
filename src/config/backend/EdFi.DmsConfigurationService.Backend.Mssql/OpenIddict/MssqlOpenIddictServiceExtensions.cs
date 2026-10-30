// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Configuration;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict
{
    public static class MssqlOpenIddictServiceExtensions
    {
        public static IServiceCollection AddMssqlOpenIddictStores(
            this IServiceCollection services,
            IConfiguration configuration,
            string authority
        )
        {
            // The OpenIddict stores open their connections from DatabaseOptions too.
            services.AddMssqlSessionLanguagePin();

            // Add identity options
            services.AddOpenIddictIdentityOptions(configuration);
            services.AddSigningKeyServices();
            services.AddSingleton<IOpenIddictDataRepository, OpenIddictDataRepository>();
            services.AddSingleton<IIdentityProviderRepository, OpenIddictClientRepository>();
            services.AddSingleton<IOpenIddictTokenRepository, OpenIddictTokenRepository>();
            services.AddSingleton<OpenIddictTokenManager>();
            services.AddSingleton<ITokenManager, OpenIddictTokenManager>();
            services.AddSingleton<ITokenRevocationManager, OpenIddictTokenManager>();
            services.AddSingleton<ITokenIntrospectionManager, OpenIddictTokenManager>();
            services.AddSingleton<IClientSecretHasher, ClientSecretHasher>();
            services.TryAddSingleton<ClientSecretPlaceholderHash>();

            // Add OpenIddict-compatible services
            services.AddScoped<IOpenIdConnectConfigurationProvider, OpenIdConnectConfigurationProvider>();

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
                    Issuer = issuer ?? string.Empty,
                    Audience = audience ?? string.Empty,
                    ExpirationMinutes = 30,
                },
                configuration
            );

            return services;
        }
    }
}
