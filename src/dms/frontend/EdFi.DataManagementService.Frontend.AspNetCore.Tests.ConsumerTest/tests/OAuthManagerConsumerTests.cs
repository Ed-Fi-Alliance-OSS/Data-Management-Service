// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using NUnit.Framework;
using PactNet;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using FakeItEasy;
using System.Net;
using Newtonsoft.Json.Linq;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.ContractTest;

[TestFixture]
public class OAuthManagerConsumerTests : PactTestBase
{
    private const string UpstreamUri = "/connect/token";
    private const string ValidAuthHeader = "Basic valid-auth-header";
    private const string InvalidAuthHeader = "invalid-header";

    //[Test]
    public async Task ItHandlesUnauthorizedResponseFromProvider()
    {
        // Arrange the expected interaction
        pact!
            .UponReceiving("a request for an access token with invalid credentials")
                .Given("OAuth Provider is up and running")
                .WithRequest(HttpMethod.Post, "/oauth/token")
                .WithHeader("Authorization", InvalidAuthHeader)
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithBody("grant_type=client_credentials", "application/x-www-form-urlencoded")  // Specify content type here
        .WillRespond()
                .WithStatus(HttpStatusCode.Unauthorized)
                .WithHeader("Content-Type", "application/problem+json")
                .WithJsonBody(new
                {
                    error = "unauthorized",
                    error_description = "Invalid credentials"
                });

        await pact.VerifyAsync(async ctx =>
        {
            // Act: Make the actual call to the OAuthManager
            var response = await oAuthManager!.GetAccessTokenAsync(
                fakeHttpClientWrapper!,
                "Basic invalid_credentials",
                $"{_mockProviderServiceBaseUri}/oauth/token",
                new TraceId("trace-id-example")
            );
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }
}


        /* pact.UponReceiving("A request for an access token with a valid credentials")
            .WithRequest(HttpMethod.Post, "/connect/token")
                .WithJsonBody(new
                {
                    clientid = "CSClient1",
                    clientsecret = "test123@Puiu"
                })
                .WithHeader("Content-Type", "application/json")
            .WillRespond()
                .WithStatus(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithJsonBody(new
                {
                    access_token = "input123token",
                    expires_in = 900,
                    token_type = "bearer"
                });

        await pact.VerifyAsync(async ctx =>
        {
            var client = new HttpClient();

            // Act
            var requestBody = new { clientid = "CSClient1", clientsecret = "test123@Puiu" };
            var response = await client.PostAsJsonAsync($"{ctx.MockServerUri}connect/token", requestBody);
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().NotBeNull();
            content.Should().Contain("input123token");
            content.Should().Contain("bearer");
        }); */

