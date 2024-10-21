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

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
    public class ProviderStateMiddleware
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IDictionary<string, Action> providerStates;
        private readonly RequestDelegate next;

        // This should be an instance of your FakeTokenManager, which should be passed in.
        private readonly FakeTokenManager _fakeTokenManager;

        public ProviderStateMiddleware(RequestDelegate next, FakeTokenManager fakeTokenManager)
        {
            this.next = next;
            _fakeTokenManager = fakeTokenManager;

            writer.WriteLine("This will appear in the test results.");

            Console.WriteLine($"FakeTokenManager instance Middleware: {_fakeTokenManager.GetHashCode()}");

            this.providerStates = new Dictionary<string, Action>
            {
                {
                    "A request for an access token with invalid credentials that throws an error from Keycloak",
                    _fakeTokenManager.SetShouldThrowExceptionToTrue
                }
            };
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!(context.Request.Path.Value?.StartsWith("/provider-states") ?? false))
            {
                await this.next.Invoke(context);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;

            if (context.Request.Method == HttpMethod.Post.ToString())
            {
                string jsonRequestBody;
                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                {
                    jsonRequestBody = await reader.ReadToEndAsync();
                }

                var providerState = JsonSerializer.Deserialize<ProviderState>(jsonRequestBody, _options);

                //A null or empty provider state key must be handled
                if (!IsNullOrEmpty(providerState?.State))
                {
                    this.providerStates[providerState.State].Invoke();
                }

                await context.Response.WriteAsync(Empty);
            }
        }
    }
}
