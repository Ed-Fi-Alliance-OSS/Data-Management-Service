// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
    public class TestStartup(IConfiguration configuration)
    {
        public IConfiguration? Configuration { get; } = configuration;

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTransient<Model.TokenRequest.Validator>();
            services.AddTransient<Model.RegisterRequest.Validator>();
            services.AddTransient<IClientRepository, ClientRepository>();
            services.AddTransient<ITokenManager, FakeTokenManager>();
            services.AddTransient<IEndpointModule, IdentityModule>();
            services.AddTransient<IEndpointModule, HealthModule>();
        }

        public static void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseMiddleware<RequestLoggingMiddleware>();
            app.UseEndpoints(endpoints =>
            {
                var healthCheck = new HealthModule();
                healthCheck.MapEndpoints(endpoints);

                // Initialize and map endpoints from IdentityModule
                var identityModule = new IdentityModule();
                identityModule.MapEndpoints(endpoints);
            });
        }
    }
}
