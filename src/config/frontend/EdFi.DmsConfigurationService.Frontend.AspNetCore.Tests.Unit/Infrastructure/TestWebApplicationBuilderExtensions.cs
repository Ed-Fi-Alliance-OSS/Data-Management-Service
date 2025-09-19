// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public static class TestWebApplicationBuilderExtensions
{
    /// <summary>
    /// Configures test authentication that replaces the production JWT Bearer authentication setup
    /// with a test authentication handler. This should be called in ConfigureTestServices to
    /// properly override the production authentication configuration.
    /// </summary>
    public static IServiceCollection AddTestAuthentication(this IServiceCollection services)
    {
        // Remove existing authentication services
        services.RemoveAll<IAuthenticationService>();
        services.RemoveAll<IAuthenticationSchemeProvider>();

        // Clear any existing authentication schemes configuration
        var authSchemeDescriptors = services
            .Where(d =>
                d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<AuthenticationOptions>)
                || d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<JwtBearerOptions>)
            )
            .ToList();

        foreach (var descriptor in authSchemeDescriptors)
        {
            services.Remove(descriptor);
        }

        // Add test authentication with Bearer scheme for production compatibility
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                JwtBearerDefaults.AuthenticationScheme,
                options => { }
            );

        return services;
    }
}
