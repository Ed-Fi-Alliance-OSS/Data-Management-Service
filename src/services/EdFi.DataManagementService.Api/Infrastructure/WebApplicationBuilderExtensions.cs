// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Core;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
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

        var issuer = webAppBuilder.Configuration.GetValue<string>("AppSettings:Authentication:Issuer");
        var authority = webAppBuilder.Configuration.GetValue<string>("AppSettings:Authentication:Authority");
        var signingKeyValue = webAppBuilder.Configuration.GetValue<string>(
            "AppSettings:Authentication:SigningKey"
        );
        webAppBuilder
            .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateLifetime = true,
                    ValidateAudience = false,
                    ValidIssuer = issuer,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyValue!))
                };
                options.RequireHttpsMetadata = false;
            });
        webAppBuilder.Services.AddAuthorization();

        webAppBuilder.Services.Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"));

        if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
        {
            ConfigureRateLimit(webAppBuilder);
        }
        ConfigureLogging();

        void ConfigureLogging()
        {
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(webAppBuilder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            webAppBuilder.Logging.ClearProviders();
            webAppBuilder.Logging.AddSerilog(logger);
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
