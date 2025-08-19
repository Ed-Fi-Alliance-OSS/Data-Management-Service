// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    /// <summary>
    /// Extension methods for registering OpenIddict-related services.
    /// </summary>
    public static class OpenIddictServiceCollectionExtensions
    {
        /// <summary>
        /// Adds OpenIddict identity options to the service collection.
        /// </summary>
        public static IServiceCollection AddOpenIddictIdentityOptions(this IServiceCollection services, IConfiguration configuration)
        {
            // Configure IdentityOptions from appsettings
            services.Configure<IdentityOptions>(options =>
            {
                options.Audience = configuration.GetValue<string>("IdentitySettings:Audience") ?? string.Empty;
                options.Authority = configuration.GetValue<string>("IdentitySettings:Authority") ?? string.Empty;
                options.TokenExpirationMinutes = configuration.GetValue<int>("IdentitySettings:TokenExpirationMinutes", 60);
                options.UseCertificates = configuration.GetValue<bool>("IdentitySettings:UseCertificates", false);
                options.UseDevelopmentCertificates = configuration.GetValue<bool>("IdentitySettings:UseDevelopmentCertificates", false);
                options.DevCertificatePath = configuration.GetValue<string>("IdentitySettings:DevCertificatePath") ?? "devcert.pfx";
                options.DevCertificatePassword = configuration.GetValue<string>("IdentitySettings:DevCertificatePassword") ?? "password";
                options.CertificatePath = configuration.GetValue<string>("IdentitySettings:CertificatePath") ?? string.Empty;
                options.CertificatePassword = configuration.GetValue<string>("IdentitySettings:CertificatePassword") ?? string.Empty;
                options.EncryptionKey = configuration.GetValue<string>("IdentitySettings:EncryptionKey") ?? string.Empty;
                options.KeyFormatCacheSize = configuration.GetValue<int>("IdentitySettings:KeyFormatCacheSize", 100);
            });

            return services;
        }
    }
}
