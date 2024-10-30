// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests.Middleware;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using EdFi.DmsConfigurationService.Backend.Keycloak;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
    public class TestStartup
    {
        public IConfiguration? Configuration { get; }

        public TestStartup(IConfiguration configuration)
        {
            this.Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            // Add your minimal API services here
            //services.AddEndpointsApiExplorer();
            //services.AddSingleton<ITokenManager, FakeTokenManager>();

            // Configure Kestrel to allow synchronous IO if needed
            services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            // Register services specifically for minimal APIs
            services.AddEndpointsApiExplorer();  // Adds OpenAPI/Swagger support if needed
            services.AddSingleton<Model.TokenRequest.Validator>();
            services.AddSingleton<Model.RegisterRequest.Validator>();
            services.AddSingleton<IClientRepository, ClientRepository>();
            services.AddSingleton<ITokenManager, FakeTokenManager>();
            services.AddSingleton<IEndpointModule, IdentityModule>();
            services.AddSingleton<IEndpointModule, HealthModule>();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            // Get the FakeTokenManager instance from the DI container.
            /*var fakeTokenManager = app.ApplicationServices.GetRequiredService<ITokenManager>() as FakeTokenManager;
            if (fakeTokenManager == null)
            {
                throw new InvalidOperationException("FakeTokenManager instance could not be resolved.");
            } */
            // Use the middleware and pass the FakeTokenManager instance to it.
            //app.UseMiddleware<ProviderStateMiddleware>(fakeTokenManager);

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
