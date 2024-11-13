// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using NUnit.Framework;
using PactNet;
using PactNet.Matchers;
using System.Text.Json;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core;
using Microsoft.Extensions.Logging;
using System.Net;
using EdFi.DataManagementService.Core.External.Model;
using System.Text;

namespace EdFi.DataManagementService.Frontend.AspNetCore.ContractTest;

public class OAuthManagerTests
{

    public IPactBuilderV3? pact;
    public IHttpClientWrapper? fakeHttpClientWrapper;
    public OAuthManager? oAuthManager;
    public string mockProviderServiceBaseUri = "http://localhost:9222"; // Mock service URI
    private string pactDir = Path.Join("..", "..", "..", "pacts");

    [SetUp]
    public void SetUp()
    {
        fakeHttpClientWrapper = A.Fake<IHttpClientWrapper>();
        var logger = A.Fake<ILogger<OAuthManager>>();
        oAuthManager = new OAuthManager(logger);

        // Initialize the Pact provider configuration for PactNet 5
        pact = Pact.V3("OAuthConsumer", "Keycloak").WithHttpInteractions();
    }

    [Test]
    public async Task GetAccessToken_ShouldReturnUnauthorizedForInvalidCredentials()
    {
        // Arrange the expected interaction
        const string AuthHeader = "Basic invalid_credentials";
        const string TokenPath = "/realms/edfi/protocol/openid-connect/token";
        const string RequestBody = "grant_type=client_credentials";

        pact!
            .UponReceiving("a token request with invalid credentials")
                .WithRequest(HttpMethod.Post, TokenPath)
                .WithHeader("Authorization", AuthHeader)
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithBody(RequestBody, "application/x-www-form-urlencoded")
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
            // Act: Make the request directly to the Pact mock server URI
            var response = await oAuthManager!.GetAccessTokenAsync(
                fakeHttpClientWrapper!,
                AuthHeader,
                $"{ctx.MockServerUri}{TokenPath}",
                new TraceId("trace-id-example")
            );

            // Assert: Verify the response is Unauthorized
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(await response.Content.ReadAsStringAsync(), Contains.Substring("unauthorized"));
        });
    }
}
