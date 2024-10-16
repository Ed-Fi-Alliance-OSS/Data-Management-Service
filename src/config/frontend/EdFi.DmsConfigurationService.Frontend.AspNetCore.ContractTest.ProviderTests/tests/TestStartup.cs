// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using EdFi.DmsConfigurationService.Backend;

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
            services.AddEndpointsApiExplorer();
            services.AddSingleton<ITokenManager, FakeTokenManager>();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                _ = endpoints.MapPost("/connect/token", async (TokenRequest request, ITokenManager tokenManager) =>
                {
                    // Use the tokenManager to get the access token
                    var token = await tokenManager.GetAccessTokenAsync(new List<KeyValuePair<string, string>>
                                {
                                    new KeyValuePair<string, string>("client_id", request.ClientId),
                                    new KeyValuePair<string, string>("client_secret", request.ClientSecret)
                                });
                    {
                        // Assuming the token is in JSON format as defined in the FakeTokenManager
                        return Results.Text(token, "application/json");
                    }
                });
            });
        }

    }
}
