// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Api.Configuration;
using Serilog;

namespace EdFi.DataManagementService.Api.Infrastructure
{
    public static class WebApplicationBuilderExtensions
    {
        public static void AddServices(this WebApplicationBuilder webAppBuilder)
        {
            webAppBuilder.Services.Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"));

            webAppBuilder.Services.AddSingleton<LogAppSettingsService>();
            if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
            {
                ConfigureRateLimit(webAppBuilder);
            }
            ConfigureLogging();

            void ConfigureLogging()
            {
                var logger = new LoggerConfiguration()
                            .ReadFrom.Configuration(new ConfigurationBuilder()
                            .AddJsonFile("Serilog_Configuration.json").Build())
                            .Enrich.FromLogContext()
                            .CreateLogger();
                webAppBuilder.Logging.ClearProviders();
                webAppBuilder.Logging.AddSerilog(logger);
            }
        }

        private static void ConfigureRateLimit(WebApplicationBuilder webAppBuilder)
        {
            webAppBuilder.Services.Configure<RateLimitOptions>(webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit));
            var rateLimitOptions = new RateLimitOptions();
            webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Bind(rateLimitOptions);

            webAppBuilder.Services.AddRateLimiter(limiterOptions =>
            {
                limiterOptions.RejectionStatusCode = (int) HttpStatusCode.TooManyRequests;
                limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: httpContext.Request.Headers.Host.ToString(),
                        factory: partition => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = rateLimitOptions.PermitLimit,
                            QueueLimit = rateLimitOptions.QueueLimit,
                            Window = TimeSpan.FromSeconds(rateLimitOptions.Window)
                        }));
            });

        }
    }
}
