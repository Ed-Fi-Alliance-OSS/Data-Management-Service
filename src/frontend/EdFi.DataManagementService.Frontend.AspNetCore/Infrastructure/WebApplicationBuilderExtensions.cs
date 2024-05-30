// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.Extensions.Options;
using Serilog;
using static EdFi.DataManagementService.Core.DmsCoreServiceExtensions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationBuilderExtensions
{
    public static void AddServices(this WebApplicationBuilder webAppBuilder)
    {
        webAppBuilder
            .Services.AddDmsDefaultConfiguration()
            .AddPostgresqlBackend(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:DatabaseConnection").Value
                    ?? string.Empty
            )
            .AddTransient<IContentProvider, ContentProvider>()
            .AddTransient<IVersionProvider, VersionProvider>()
            .AddTransient<IAssemblyProvider, AssemblyProvider>()
            .Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"))
            .AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>()
            .Configure<ConnectionStrings>(webAppBuilder.Configuration.GetSection("ConnectionStrings"))
            .AddSingleton<IValidateOptions<ConnectionStrings>, ConnectionStringsValidator>();

        var logger = ConfigureLogging();

        if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
        {
            logger.Information("Injecting rate limiting");
            ConfigureRateLimit(webAppBuilder);
        }
        ConfigureDatabase(logger);

        Serilog.ILogger ConfigureLogging()
        {
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(webAppBuilder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            webAppBuilder.Logging.ClearProviders();
            webAppBuilder.Logging.AddSerilog(logger);

            return logger;
        }

        void ConfigureDatabase(Serilog.ILogger logger)
        {
            if (
                string.Equals(
                    webAppBuilder.Configuration.GetSection("AppSettings:DatabaseEngine").Value,
                    "postgresql",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                logger.Information("Injecting PostgreSQL as the primary backend datastore");
                webAppBuilder.Services.AddSingleton<
                    IDatabaseDeploy,
                    Backend.Postgresql.Deploy.DatabaseDeploy
                >();
            }
            else
            {
                logger.Information("Injecting MSSQL as the primary backend datastore");
                webAppBuilder.Services.AddSingleton<IDatabaseDeploy, Backend.Mssql.Deploy.DatabaseDeploy>();
            }
        }
    }

    private static void ConfigureRateLimit(WebApplicationBuilder webAppBuilder)
    {
        webAppBuilder.Services.Configure<RateLimitOptions>(
            webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit)
        );
        var rateLimitOptions = new RateLimitOptions();
        webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Bind(rateLimitOptions);

        webAppBuilder.Services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;
            limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Request.Headers.Host.ToString(),
                    factory: partition => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitOptions.PermitLimit,
                        QueueLimit = rateLimitOptions.QueueLimit,
                        Window = TimeSpan.FromSeconds(rateLimitOptions.Window)
                    }
                )
            );
        });
    }
}
