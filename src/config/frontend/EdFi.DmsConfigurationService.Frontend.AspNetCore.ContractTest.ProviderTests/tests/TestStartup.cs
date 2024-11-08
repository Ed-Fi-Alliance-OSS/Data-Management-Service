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
using Microsoft.Extensions.Options;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
    public class TestStartup(IConfiguration configuration)
    {
        public IConfiguration? Configuration { get; } = configuration;

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<Model.TokenRequest.Validator>();
            services.AddSingleton<Model.RegisterRequest.Validator>();
            services.AddSingleton<IClientRepository, ClientRepository>();
            services.AddSingleton<ITokenManager, FakeTokenManager>();
            services.AddSingleton<IEndpointModule, IdentityModule>();
            services.AddSingleton<IEndpointModule, HealthModule>();
            services.AddSingleton<IClientRepository, FakeClientRepository>();
            services.AddTransient<IValidateOptions<IdentitySettings>, IdentitySettingsValidator>();
        }

        public static void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            // Get the FakeTokenManager instance from the DI container.
            var fakeTokenManager = app.ApplicationServices.GetRequiredService<ITokenManager>() as FakeTokenManager;
            var fakeClientRepository = app.ApplicationServices.GetRequiredService<IClientRepository>() as FakeClientRepository;
            if (fakeTokenManager == null)
            {
                throw new InvalidOperationException("FakeTokenManager instance could not be resolved.");
            }
            // Use the middleware and pass the FakeTokenManager instance to it.
            app.UseMiddleware<ProviderStateMiddleware>(fakeTokenManager);
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
