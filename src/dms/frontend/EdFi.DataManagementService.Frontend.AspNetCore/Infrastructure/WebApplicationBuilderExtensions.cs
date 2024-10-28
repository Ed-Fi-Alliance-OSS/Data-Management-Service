// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Backend.OpenSearch;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using static EdFi.DataManagementService.Core.DmsCoreServiceExtensions;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationBuilderExtensions
{
    public static void AddServices(this WebApplicationBuilder webAppBuilder)
    {
        var logger = ConfigureLogging();

        webAppBuilder
            .Services.AddDmsDefaultConfiguration(
                logger,
                webAppBuilder.Configuration.GetSection("CircuitBreaker")
            )
            .AddTransient<IContentProvider, ContentProvider>()
            .AddTransient<IVersionProvider, VersionProvider>()
            .AddTransient<IAssemblyProvider, AssemblyProvider>()
            .AddTransient<IOAuthManager, OAuthManager>()
            .Configure<DatabaseOptions>(webAppBuilder.Configuration.GetSection("DatabaseOptions"))
            .Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"))
            .Configure<CoreAppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"))
            .AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>()
            .Configure<ConnectionStrings>(webAppBuilder.Configuration.GetSection("ConnectionStrings"))
            .AddSingleton<IValidateOptions<ConnectionStrings>, ConnectionStringsValidator>()
            .AddSingleton<IValidateOptions<IdentitySettings>, IdentitySettingsValidator>();

        if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
        {
            logger.Information("Injecting rate limiting");
            ConfigureRateLimit(webAppBuilder);
        }

        ConfigureDatastore(webAppBuilder, logger);
        ConfigureQueryHandler(webAppBuilder, logger);

        webAppBuilder.Services.AddSingleton(
            new DbHealthCheck(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:DatabaseConnection").Value
                    ?? string.Empty,
                webAppBuilder.Configuration.GetSection("AppSettings:Datastore").Value ?? string.Empty,
                webAppBuilder.Services.BuildServiceProvider().GetRequiredService<ILogger<DbHealthCheck>>()
            )
        );

        webAppBuilder
            .Services.AddHealthChecks()
            .AddCheck<ApplicationHealthCheck>("ApplicationHealthCheck")
            .AddCheck<DbHealthCheck>("DbHealthCheck");

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

        // For Security(Keycloak)
        IConfiguration config = webAppBuilder.Configuration;
        var settings = config.GetSection("IdentitySettings");
        var identitySettings = ReadSettings();
        webAppBuilder.Services.Configure<IdentitySettings>(settings);
        webAppBuilder.Services.AddHttpClient();

        var metadataAddress = $"{identitySettings.Authority}/.well-known/openid-configuration";

        webAppBuilder
            .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.MetadataAddress = metadataAddress;
                    options.Authority = identitySettings.Authority;
                    options.Audience = identitySettings.Audience;
                    options.RequireHttpsMetadata = identitySettings.RequireHttpsMetadata;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateAudience = true,
                        ValidateIssuer = false,
                        RoleClaimType = identitySettings.RoleClaimType,
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnAuthenticationFailed = context =>
                        {
                            Console.WriteLine($"Authentication failed: {context.Exception.Message}");
                            return Task.CompletedTask;
                        },
                    };
                }
            );
        webAppBuilder.Services.AddAuthorization(options =>
            options.AddPolicy(
               SecurityConstants.ServicePolicy,
                policy => policy.RequireClaim(ClaimTypes.Role, identitySettings.ServiceRole)
            )
        );

        IdentitySettings ReadSettings()
        {
            return new IdentitySettings
            {
                EnforceAuthorization = config.GetValue<bool>("IdentitySettings:EnforceAuthorization"),
                Authority = config.GetValue<string>("IdentitySettings:Authority")!,
                RequireHttpsMetadata = config.GetValue<bool>("IdentitySettings:RequireHttpsMetadata"),
                Audience = config.GetValue<string>("IdentitySettings:Audience")!,
                RoleClaimType = config.GetValue<string>("IdentitySettings:RoleClaimType")!,
                ServiceRole = config.GetValue<string>("IdentitySettings:ServiceRole")!,
            };
        }
    }

    private static void ConfigureDatastore(WebApplicationBuilder webAppBuilder, Serilog.ILogger logger)
    {
        if (
            string.Equals(
                webAppBuilder.Configuration.GetSection("AppSettings:Datastore").Value,
                "postgresql",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            logger.Information("Injecting PostgreSQL as the primary backend datastore");
            webAppBuilder.Services.AddPostgresqlDatastore(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:DatabaseConnection").Value
                    ?? string.Empty
            );
            webAppBuilder.Services.AddSingleton<IDatabaseDeploy, Backend.Postgresql.Deploy.DatabaseDeploy>();
        }
        else
        {
            logger.Information("Injecting MSSQL as the primary backend datastore");
            webAppBuilder.Services.AddSingleton<IDatabaseDeploy, Backend.Mssql.Deploy.DatabaseDeploy>();
        }
    }

    private static void ConfigureQueryHandler(WebApplicationBuilder webAppBuilder, Serilog.ILogger logger)
    {
        if (
            string.Equals(
                webAppBuilder.Configuration.GetSection("AppSettings:QueryHandler").Value,
                "postgresql",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            logger.Information("Injecting PostgreSQL as the backend query handler");
            webAppBuilder.Services.AddPostgresqlQueryHandler();
        }
        else
        {
            logger.Information("Injecting OpenSearch as the backend query handler");
            webAppBuilder.Services.AddOpenSearchQueryHandler(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:OpenSearchUrl").Value
                    ?? string.Empty
            );
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
                        Window = TimeSpan.FromSeconds(rateLimitOptions.Window),
                    }
                )
            );
        });
    }
}
