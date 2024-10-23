// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EdFi.DmsConfigurationService.Backend;
using FakeItEasy;
using Microsoft.AspNetCore.Http;
using PactNet;
using static System.String;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests.Middleware
{
    public class ProviderStateMiddleware
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly RequestDelegate _next;
        private readonly IDictionary<string, Func<IDictionary<string, object>, Task>>? _providerStates;
        //private readonly FakeTokenManger _fakeTokenManager;
        private readonly FakeTokenManager _fakeTokenManager;

        public ProviderStateMiddleware(RequestDelegate next, FakeTokenManager fakeTokenManager)
        //public ProviderStateMiddleware(RequestDelegate next, FakeTokenManager fakeTokenManager)
        {
            _next = next;
            _fakeTokenManager = fakeTokenManager;
            _providerStates = new Dictionary<string, Func<IDictionary<string, object>, Task>>
            {
                ["A request for an access token with invalid credentials that throws an error from Keycloak"] = SetShouldThrowExceptionToTrue
            };
        }
        private async Task SetShouldThrowExceptionToTrue(IDictionary<string, object> parameters)
        {
            //// Set ShouldThrowException to true
            ////this._fakeTokenManager.ShouldThrowException = true;
            //_fakeTokenManager.SetShouldThrowExceptionToTrue();

            // If you don't have anything to await, you can still just return Task.CompletedTask
            await Task.CompletedTask; // This is effectively a no-op.
        }
        //PACT EXAMPLE
        /* private async Task CreateAddress(IDictionary<string, object> parameters)
        {
            JsonElement id = (JsonElement)parameters["id"];

            AddressDto address = new AddressDto
            {
                Id = id.GetString()!,
                AddressType = "delivery",
                Street = "Main street",
                Number = 123,
                City = "Sun City",
                ZipCode = 90210,
                State = "Yukon",
                Country = "United States"
            };

            await _addresses.AddAddressAsync(address);
        } */

        public async Task InvokeAsync(HttpContext context)
        {
            if (!(context.Request.Path.Value?.StartsWith("/provider-states") ?? false))
            {
                await this._next.Invoke(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;

            if (context.Request.Method == HttpMethod.Post.ToString())
            {
                string jsonRequestBody;

                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                {
                    jsonRequestBody = await reader.ReadToEndAsync();
                }

                try
                {
                    ProviderState? providerState = JsonSerializer.Deserialize<ProviderState>(jsonRequestBody, Options);

                    // Ensure _providerStates is not null and providerState.State is not null or empty
                    if (this._providerStates != null && !string.IsNullOrEmpty(providerState?.State))
                    {
                        if (this._providerStates.TryGetValue(providerState.State, out var action) && action != null)
                        {
                            // Ensure that providerState.Params is not null
                            if (providerState.Params != null)
                            {
                                await action.Invoke(providerState.Params);
                            }
                            else
                            {
                                // Handle the case where Params is null
                                context.Response.StatusCode = StatusCodes.Status400BadRequest; // or another appropriate status
                                await context.Response.WriteAsync("Provider state parameters are null.");
                            }
                        }
                        else
                        {
                            // Handle the case where the provider state is not found
                            context.Response.StatusCode = StatusCodes.Status404NotFound; // or another appropriate status
                            await context.Response.WriteAsync($"Provider state '{providerState.State}' not found.");
                        }
                    }
                }
                catch (Exception e)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync("Failed to deserialize JSON provider state body:");
                    await context.Response.WriteAsync(jsonRequestBody);
                    await context.Response.WriteAsync(string.Empty);
                    await context.Response.WriteAsync(e.ToString());
                }
            }
        }
    }
}
