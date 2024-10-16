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
    private readonly IPactBuilderV3 pact;

    public ConsumerIdentityTest()
    {
        pact = Pact.V3("DMS API Consumer", "DMS Configuration Service API").WithHttpInteractions();
    }

    [Test]
    public async Task Given_a_valid_credentials()
    {
        pact.UponReceiving("given a valid credentials")
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
}
