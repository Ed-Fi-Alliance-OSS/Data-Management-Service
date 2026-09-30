// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions
{
    /// <summary>
    /// Extension methods for registering OpenIddict-related services.
    /// </summary>
    public static class OpenIddictServiceCollectionExtensions
    {
        /// <summary>
        /// Adds OpenIddict identity options to the service collection.
        /// </summary>
        public static IServiceCollection AddOpenIddictIdentityOptions(
            this IServiceCollection services,
            IConfiguration configuration
        )
        {
            // Configure IdentityOptions from appsettings
            services.Configure<IdentityOptions>(options =>
            {
                options.Audience =
                    configuration.GetValue<string>("IdentitySettings:Audience") ?? string.Empty;
                options.Authority =
                    configuration.GetValue<string>("IdentitySettings:Authority") ?? string.Empty;
                options.TokenExpirationMinutes = configuration.GetValue<int>(
                    "IdentitySettings:TokenExpirationMinutes",
                    30
                );
                options.UseCertificates = configuration.GetValue<bool>(
                    "IdentitySettings:UseCertificates",
                    false
                );
                options.UseDevelopmentCertificates = configuration.GetValue<bool>(
                    "IdentitySettings:UseDevelopmentCertificates",
                    false
                );
                options.DevCertificatePath =
                    configuration.GetValue<string>("IdentitySettings:DevCertificatePath") ?? "devcert.pfx";
                options.DevCertificatePassword =
                    configuration.GetValue<string>("IdentitySettings:DevCertificatePassword") ?? "password";
                options.CertificatePath =
                    configuration.GetValue<string>("IdentitySettings:CertificatePath") ?? string.Empty;
                options.CertificatePassword =
                    configuration.GetValue<string>("IdentitySettings:CertificatePassword") ?? string.Empty;
                options.EncryptionKey =
                    configuration.GetValue<string>("IdentitySettings:EncryptionKey") ?? string.Empty;
                options.KeyFormatCacheSize = configuration.GetValue<int>(
                    "IdentitySettings:KeyFormatCacheSize",
                    100
                );
                options.TokenCleanupEnabled = configuration.GetValue<bool>(
                    "IdentitySettings:TokenCleanupEnabled",
                    true
                );
                options.TokenCleanupIntervalMinutes = configuration.GetValue<int>(
                    "IdentitySettings:TokenCleanupIntervalMinutes",
                    30
                );
                options.BearerTokenPerClientLimit = configuration.GetValue<int>(
                    "IdentitySettings:BearerTokenPerClientLimit",
                    5
                );
                options.SigningKeyRefreshIntervalSeconds = configuration.GetValue<int>(
                    "IdentitySettings:SigningKeyRefreshIntervalSeconds",
                    300
                );
                options.SigningKeyMaxStalenessSeconds = configuration.GetValue<int>(
                    "IdentitySettings:SigningKeyMaxStalenessSeconds",
                    3600
                );
                options.SigningKeyUnknownKeyRefreshCooldownSeconds = configuration.GetValue<int>(
                    "IdentitySettings:SigningKeyUnknownKeyRefreshCooldownSeconds",
                    30
                );
                options.SigningKeyLoadTimeoutSeconds = configuration.GetValue<int>(
                    "IdentitySettings:SigningKeyLoadTimeoutSeconds",
                    10
                );
            });

            // Invalid signing-key settings stop the host at startup instead of surfacing on the
            // first authenticated request.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<IdentityOptions>, SigningKeyOptionsValidator>()
            );
            services.AddOptions<IdentityOptions>().ValidateOnStart();

            return services;
        }

        /// <summary>
        /// Registers the signing-key services (spec §4.3, step 2.1): the development-certificate store, both key
        /// sources, the source selected by <see cref="IdentityOptions.UseCertificates"/>, the one snapshot provider,
        /// its refresh service, and <see cref="TimeProvider"/>; and (step 2.3) the configuration manager and the shared
        /// bearer events. Only the self-contained store registrations call this, so nothing here exists in Keycloak
        /// mode. Every registration is a try-add, so registering twice still yields one of each. The bearer schemes
        /// take the manager and the events through <see cref="SigningKeyJwtBearerOptionsExtensions.UseSigningKeySnapshot"/>:
        /// the default <c>Bearer</c> scheme since step 3.1 and <c>DmsJwtBearer</c> since step 3.2.
        /// </summary>
        public static IServiceCollection AddSigningKeyServices(this IServiceCollection services)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<DevelopmentCertificateStore>();
            services.TryAddSingleton<DatabaseSigningKeySource>();
            services.TryAddSingleton<CertificateSigningKeySource>();

            // The same switch the token manager reads, so validation and issuance use the same key store.
            services.TryAddSingleton<ISigningKeySource>(serviceProvider =>
                serviceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.UseCertificates
                    ? serviceProvider.GetRequiredService<CertificateSigningKeySource>()
                    : serviceProvider.GetRequiredService<DatabaseSigningKeySource>()
            );

            services.TryAddSingleton<ISigningKeySnapshotProvider, SigningKeySnapshotProvider>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, SigningKeyRefreshService>()
            );
            services.TryAddSingleton<SigningKeyConfigurationManager>();
            services.TryAddSingleton<SigningKeyBearerEvents>();

            return services;
        }
    }
}
