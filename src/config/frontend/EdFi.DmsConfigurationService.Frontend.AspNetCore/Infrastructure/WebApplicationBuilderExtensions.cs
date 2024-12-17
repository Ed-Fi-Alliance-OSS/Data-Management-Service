// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Security.Claims;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Postgresql;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationBuilderExtensions
{
    public static void AddServices(this WebApplicationBuilder webApplicationBuilder)
    {
        var logger = ConfigureLogging();
        webApplicationBuilder.Services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

        // For FluentValidation
        var executingAssembly = Assembly.GetExecutingAssembly();

        webApplicationBuilder
            .Services.AddExceptionHandler<GlobalExceptionHandler>()
            .AddValidatorsFromAssembly(executingAssembly)
            .AddValidatorsFromAssembly(
                Assembly.Load("Edfi.DmsConfigurationService.DataModel"),
                ServiceLifetime.Transient
            )
            .AddFluentValidationAutoValidation();

        ValidatorOptions.Global.DisplayNameResolver = (type, memberInfo, expression) =>
            memberInfo
                ?.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()
                ?.GetName();

        // Read Database configuration from appSettings
        webApplicationBuilder
            .Services.Configure<AppSettings>(webApplicationBuilder.Configuration.GetSection("AppSettings"))
            .AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>()
            .Configure<DatabaseOptions>(webApplicationBuilder.Configuration.GetSection("ConnectionStrings"))
            .AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        ;
        ConfigureDatastore(webApplicationBuilder, logger);
        ConfigureIdentityProvider(webApplicationBuilder, logger);

        webApplicationBuilder.Services.AddHttpClient();

        webApplicationBuilder.Services.AddTransient<IApplicationRepository, ApplicationRepository>();
        webApplicationBuilder.Services.AddTransient<IVendorRepository, VendorRepository>();
        webApplicationBuilder.Services.AddTransient<IClaimSetRepository, ClaimSetRepository>();

        Serilog.ILogger ConfigureLogging()
        {
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(webApplicationBuilder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            webApplicationBuilder.Logging.ClearProviders();
            webApplicationBuilder.Logging.AddSerilog(logger);

            return logger;
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

    private static void ConfigureIdentityProvider(
        WebApplicationBuilder webApplicationBuilder,
        Serilog.ILogger logger
    )
    {
        IConfiguration config = webApplicationBuilder.Configuration;
        var identitySettings = config.GetSection("IdentitySettings").Get<IdentitySettings>();
        if (identitySettings == null)
        {
            logger.Error("Error reading IdentitySettings");
            throw new InvalidOperationException("Unable to read IdentitySettings from appsettings");
        }
        webApplicationBuilder
            .Services.Configure<IdentitySettings>(config.GetSection("IdentitySettings"))
            .AddSingleton<IValidateOptions<IdentitySettings>, IdentitySettingsValidator>();

        webApplicationBuilder
            .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.MetadataAddress =
                        $"{identitySettings.Authority}/.well-known/openid-configuration";
                    options.Authority = identitySettings.Authority;
                    options.Audience = identitySettings.Audience;
                    options.RequireHttpsMetadata = identitySettings.RequireHttpsMetadata;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateAudience = true,
                        ValidAudience = identitySettings.Audience,
                        ValidateIssuer = true,
                        ValidIssuer = identitySettings.Authority,
                        ValidateLifetime = true,
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
        webApplicationBuilder.Services.AddAuthorization(options =>
            options.AddPolicy(
                SecurityConstants.ServicePolicy,
                policy => policy.RequireClaim(ClaimTypes.Role, identitySettings.ConfigServiceRole)
            )
        );

        if (
            string.Equals(
                webApplicationBuilder.Configuration.GetSection("AppSettings:IdentityProvider").Value,
                "keycloak",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var keycloakSettings = config.GetSection("KeycloakSettings").Get<KeycloakSettings>();
            if (keycloakSettings == null)
            {
                logger.Error("Error reading KeycloakSettings");
                throw new InvalidOperationException("Unable to read KeycloakSettings from appsettings");
            }
            webApplicationBuilder
                .Services.Configure<KeycloakSettings>(config.GetSection("KeycloakSettings"))
                .AddSingleton<IValidateOptions<KeycloakSettings>, KeycloakSettingsValidator>();

            webApplicationBuilder.Services.AddKeycloakServices(
                keycloakSettings.Url,
                keycloakSettings.Realm,
                identitySettings.ClientId,
                identitySettings.ClientSecret,
                identitySettings.RoleClaimType,
                identitySettings.Scope
            );
        }
    }
}
