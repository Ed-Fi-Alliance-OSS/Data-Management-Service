// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using PactNet;
using System.Net.Http.Json;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest;

public class ConsumerIdentityTest
{
    private IPactBuilderV3 pact;

    [SetUp]
    public void Setup()
    {
        pact = Pact.V3("DMS API Consumer", "DMS Configuration Service API").WithHttpInteractions();
    }

    [Test]
    public async Task Given_a_valid_credentials()
    {
        pact.UponReceiving("A request for an access token with a valid credentials")
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
        });
    }

    [Test]
    public async Task Given_empty_client_credentials()
    {
        pact.UponReceiving("A request for an access token with empty client credentials")
            .WithRequest(HttpMethod.Post, "/connect/token")
            .WithHeader("Content-Type", "application/json")
            .WithJsonBody(new
            {
                clientid = "",
                clientsecret = ""
            })
            .WillRespond()
            .WithStatus(HttpStatusCode.BadRequest)
            .WithHeader("Content-Type", "application/json")
            .WithJsonBody(new
            {
                title = "Validation failed",
                errors = new
                {
                    ClientId = new[] { "'Client Id' must not be empty." },
                    ClientSecret = new[] { "'Client Secret' must not be empty." }
                }
            }
            );
        await pact.VerifyAsync(async ctx =>
        {
            var client = new HttpClient();

            // Act
            var requestBody = new { clientid = "", clientsecret = "" };
            var response = await client.PostAsJsonAsync($"{ctx.MockServerUri}connect/token", requestBody);
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            content.Should().NotBeNull();
            content.Should().Contain("'Client Id' must not be empty.");
        });
    }

    [Test]
    public async Task When_error_from_backend()
    {
        pact.UponReceiving("A request for an access token with invalid credentials that throws an error from Keycloak")
            .WithRequest(HttpMethod.Post, "/connect/token")
            .WithHeader("Content-Type", "application/json")
            .WithJsonBody(new
            {
                clientid = "client123",
                clientsecret = "clientsecret123"
            })
            .WillRespond()
            .WithStatus(HttpStatusCode.Unauthorized)
            .WithHeader("Content-Type", "application/json")
            .WithJsonBody(new
            {
                error = "Error from Keycloak"
            });

        await pact.VerifyAsync(async ctx =>
        {
            var client = new HttpClient();
            // Act
            var requestBody = new { clientid = "client123", clientsecret = "clientsecret123" };
            var response = await client.PostAsJsonAsync($"{ctx.MockServerUri}connect/token", requestBody);
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            content.Should().Contain("Error from Keycloak");
        });
    }
}
