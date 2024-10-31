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
using EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests.Middleware;

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
            // Get the FakeTokenManager instance from the DI container.
            var fakeTokenManager = app.ApplicationServices.GetRequiredService<ITokenManager>() as FakeTokenManager;
            if (fakeTokenManager == null)
            {
                throw new InvalidOperationException("FakeTokenManager instance could not be resolved.");
            }
            // Use the middleware and pass the FakeTokenManager instance to it.
            app.UseMiddleware<ProviderStateMiddleware>(fakeTokenManager);
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
