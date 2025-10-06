// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Configuration;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Extensions;

/// <summary>
/// Extension methods for integrating OpenIddict enhancements into the application
/// </summary>
public static class OpenIddictIntegrationExtensions
{
    /// <summary>
    /// Adds enhanced OpenIddict services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEnhancedOpenIddict(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Add OpenIddict validation components
        services
            .AddOpenIddict()
            .AddValidation(options =>
            {
                // Configure validation to use local authorization server
                options.SetIssuer(configuration["Authentication:Authority"] ?? "https://localhost:5126");

                // Register the System.Net.Http integration
                options.UseSystemNetHttp();

                // Configure the validation handler to use introspection
                options
                    .UseIntrospection()
                    .SetClientId("validation-client")
                    .SetClientSecret("validation-secret");

                // Register the ASP.NET Core host
                options.UseAspNetCore();
            });

        // Register enhanced services
        services.AddScoped<IEnhancedTokenValidator, EnhancedTokenValidator>();
        services.AddScoped<IOpenIdConnectConfigurationProvider, OpenIdConnectConfigurationProvider>();

        return services;
    }

    /// <summary>
    /// Configures the application to use enhanced OpenIddict middleware
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseEnhancedOpenIddict(this IApplicationBuilder app)
    {
        // Add OpenIddict error handling middleware
        app.UseOpenIddictErrorHandling();

        return app;
    }

    /// <summary>
    /// Adds enhanced OpenIddict with all default configurations
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddOpenIddictEnhancements(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        return services.AddEnhancedOpenIddict(configuration);
    }

    /// <summary>
    /// Uses enhanced OpenIddict with all default middleware
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseOpenIddictEnhancements(this IApplicationBuilder app)
    {
        return app.UseEnhancedOpenIddict();
    }
}
