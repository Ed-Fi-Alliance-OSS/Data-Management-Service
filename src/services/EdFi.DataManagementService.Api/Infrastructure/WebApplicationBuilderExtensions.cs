// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.RateLimiting;
using EdFi.DataManagementService.Api.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace EdFi.DataManagementService.Api.Infrastructure
{
    public static class WebApplicationBuilderExtensions
    {
        public static void AddServices(this WebApplicationBuilder webAppBuilder)
        {
            webAppBuilder.Services.Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"));
            webAppBuilder.Services.AddLogging(builder => builder.AddConsole());
            webAppBuilder.Services.AddSingleton<LogAppSettingsService>();
            webAppBuilder.Services.Configure<RateLimitOptions>(webAppBuilder.Configuration.GetSection("RateLimit"));
            ConfigureRateLimit(webAppBuilder);
        }

        private static void ConfigureRateLimit(WebApplicationBuilder webAppBuilder)
        {
            var rateLimitOptions = new RateLimitOptions();
            webAppBuilder.Configuration.GetSection("RateLimit").Bind(rateLimitOptions);

            webAppBuilder.Services.AddRateLimiter(limiterOptions =>
            {
                limiterOptions.OnRejected = (context, cancellationToken) =>
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return new ValueTask();
                };
                limiterOptions.AddFixedWindowLimiter("fixed", options =>
                {
                    options.PermitLimit = rateLimitOptions.PermitLimit;
                    options.Window = TimeSpan.FromSeconds(rateLimitOptions.Window);
                    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    options.QueueLimit = rateLimitOptions.QueueLimit;
                });
            });
        }
    }
}
