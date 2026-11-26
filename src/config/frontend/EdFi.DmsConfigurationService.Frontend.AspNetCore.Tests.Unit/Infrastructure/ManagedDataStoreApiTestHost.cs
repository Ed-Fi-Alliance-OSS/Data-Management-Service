// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Uses production routes with isolated test dependencies and optional path-base simulation: the
/// <c>X-Test-PathBase</c> header becomes the request's path base, and a request path under it is routed without it,
/// as UsePathBase does, so a returned relative URL can be requested again.
/// </summary>
public static class ManagedDataStoreApiTestHost
{
    public static WebApplicationFactory<Program> Create(
        Action<IServiceCollection> configure,
        string identityProvider = "keycloak",
        string provider = "postgresql",
        string? connectionString = null,
        bool multitenant = false
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:IdentityProvider", identityProvider);
            builder.UseSetting("AppSettings:Datastore", provider);
            builder.UseSetting("AppSettings:MultiTenancy", multitenant.ToString());
            builder.UseSetting("AppSettings:DeployDatabaseOnStartup", "false");
            builder.UseSetting("AppSettings:EnableDataStoreManagement", "false");
            builder.UseSetting("DmsDataStoreProvisioning:Enabled", "false");
            builder.UseSetting("JobSettings:WorkerEnabled", "false");
            builder.UseSetting("JobSettings:SchedulerEnabled", "false");
            builder.UseSetting("JobSettings:RetentionEnabled", "false");
            if (connectionString is not null)
            {
                builder.UseSetting("DatabaseSettings:DatabaseConnection", connectionString);
            }
            builder.ConfigureServices(services =>
            {
                services.AddTestAuthentication();
                services.AddSingleton<IStartupFilter, ManagedRoutes>();
                configure(services);
            });
        });

    private sealed class ManagedRoutes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, continuation) =>
                    {
                        string pathBase = context.Request.Headers["X-Test-PathBase"].ToString();
                        context.Request.PathBase = pathBase;
                        if (
                            pathBase.Length > 1
                            && context.Request.Path.StartsWithSegments(
                                pathBase.TrimEnd('/'),
                                out PathString remaining
                            )
                        )
                        {
                            context.Request.Path = remaining;
                        }
                        await continuation();
                    }
                );
                next(app);
            };
    }
}
