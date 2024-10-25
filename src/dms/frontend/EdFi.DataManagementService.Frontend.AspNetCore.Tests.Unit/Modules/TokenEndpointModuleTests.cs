// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.OAuthService;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class TokenEndpointModuleTests
{
    [TestFixture]
    public class When_Posting_Successfully_To_The_Token_Endpoint
    {
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        [SetUp]
        public void SetUp()
        {
            // Arrange
            var oAuthManager = A.Fake<IOAuthManager>();
            var appSettings = A.Fake<IOptions<Configuration.AppSettings>>();
            var json =
                """{"status_code":200, "body":{"token":"fake_access_token","token_type":"bearer","expires_in":300}}""";
            JsonNode _fake_responseJson = JsonNode.Parse(json)!;
            var _fake_response_200 = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_fake_responseJson.ToString(), Encoding.UTF8, "application/json")
            };

            A.CallTo(() => appSettings.Value.AuthenticationService).Returns("/test/oauth/token");

            A.CallTo(
                    () => oAuthManager.GetAccessTokenAsync(A<HttpContext>.Ignored, A<string>.Ignored)
                )
                .Returns(_fake_response_200);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
            });

            using var client = factory.CreateClient();
            var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
            var clientId = "CSClient1";
            var clientSecret = "test123@Puiu";
            var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            proxyRequest.Headers.Add("Authorization", $"Basic {encodedCredentials}");

            // Act
            proxyRequest!.Content = new StringContent("""{"grant_type"="client_credentials"}""", Encoding.UTF8, "application/json");
            _response = client.SendAsync(proxyRequest).GetAwaiter().GetResult();
            var content = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _jsonContent = JsonNode.Parse(content) ?? throw new Exception("JSON parsing failed");
        }

        [TearDownAttribute]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_OK()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_the_body_contains_a_bear_token_with_expiry()
        {
            _jsonContent?["access_token"]?.ToString().Should().Be("fake_access_token");
            _jsonContent?["expires_in"]?.Should().Be(300);
            _jsonContent?["token_type"]?.ToString().Should().Be("bearer");
        }
    }

    [Test]
    public async Task Returns_400_when_the_authentication_headers_missing()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });

        using var client = factory.CreateClient();
        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");

        // Act
        proxyRequest!.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/json");
        var response = client.SendAsync(proxyRequest).GetAwaiter().GetResult();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

    }

    [Test]
    public async Task Returns_503_when_the_upstream_service_is_unavailable()
    {
        // TODO(): Fake a 500 from an upstream IdP server.
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();
        var json =
            """{"status_code":500}""";
        JsonNode _descriptorsJson = JsonNode.Parse(json)!;

        A.CallTo(
                () => contentProvider.LoadJsonContent(A<string>.Ignored, A<string>.Ignored, A<string>.Ignored)
            )
            .Returns(_descriptorsJson);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => contentProvider);
                }
            );
        });
        using var client = factory.CreateClient();
        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
        var clientId = "CSClient1";
        var clientSecret = "test123@Puiu";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        proxyRequest.Headers.Add("Authorization", $"Basic {encodedCredentials}");
        proxyRequest!.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/json");

        // Act
        var response = client.SendAsync(proxyRequest).GetAwaiter().GetResult();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
