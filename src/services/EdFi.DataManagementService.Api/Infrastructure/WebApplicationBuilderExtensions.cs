// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Core;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Backend.Deploy;
using Microsoft.CodeAnalysis.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace EdFi.DataManagementService.Api.Infrastructure;

public static class WebApplicationBuilderExtensions
{
    public static void AddServices(this WebApplicationBuilder webAppBuilder)
    {
        webAppBuilder.Services.AddSingleton<IApiSchemaProvider, ApiSchemaFileLoader>();
        webAppBuilder.Services.AddSingleton<ICoreFacade, CoreFacade>();
        webAppBuilder.Services.AddSingleton<IDocumentStoreRepository, SuccessDocumentStoreRepository>();
        webAppBuilder.Services.AddTransient<IDocumentValidator, DocumentValidator>();
        webAppBuilder.Services.AddTransient<ISchemaValidator, SchemaValidator>();
        webAppBuilder.Services.AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>();
        webAppBuilder.Services.AddTransient<IContentProvider, ContentProvider>();
        webAppBuilder.Services.AddTransient<IVersionProvider, VersionProvider>();
        webAppBuilder.Services.AddTransient<IDataModelProvider, DataModelProvider>();
        webAppBuilder.Services.AddTransient<IAssemblyProvider, AssemblyProvider>();

        webAppBuilder.Services.Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"));
        webAppBuilder.Services.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();

        webAppBuilder.Services.Configure<ConnectionStrings>(webAppBuilder.Configuration.GetSection("ConnectionStrings"));
        webAppBuilder.Services.AddSingleton<IValidateOptions<ConnectionStrings>, ConnectionStringsValidator>();

        if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
        {
            ConfigureRateLimit(webAppBuilder);
        }
        ConfigureLogging();
        ConfigureDatabase();

        void ConfigureLogging()
        {
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(webAppBuilder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            webAppBuilder.Logging.ClearProviders();
            webAppBuilder.Logging.AddSerilog(logger);
        }

        void ConfigureDatabase()
        {
            if (string.Equals(webAppBuilder.Configuration.GetSection("AppSettings:DatabaseEngine").Value, "postgresql", StringComparison.OrdinalIgnoreCase))
            {
                webAppBuilder.Services.AddSingleton<IDatabaseDeploy, DataManagementService.Backend.Postgresql.Deploy.DatabaseDeploy>();
            }
            else
            {
                webAppBuilder.Services.AddSingleton<IDatabaseDeploy, DataManagementService.Backend.Mssql.Deploy.DatabaseDeploy>();
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
