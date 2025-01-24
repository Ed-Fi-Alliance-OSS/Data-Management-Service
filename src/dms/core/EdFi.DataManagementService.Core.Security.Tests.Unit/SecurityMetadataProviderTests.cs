// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Security.Tests.Unit;

public class SecurityMetadataProviderTests
{
    [TestFixture]
    public class Given_Valid_Request : SecurityMetadataProviderTests
    {
        private SecurityMetadataProvider? _metadataProvider;
        private IList<ClaimSet>? _claims;
        private TestHttpMessageHandler? _handler = null;

        [SetUp]
        public async Task Setup()
        {
            var clientId = "testclient";
            var clientSecret = "secret";
            var scope = "fullaccess";
            string? expectedToken = "valid-token";
            var expectedClaims = new List<ClaimSet>
            {
                new() { Name = "ClaimSet1" },
                new() { Name = "ClaimSet2" },
            };

            var json = JsonSerializer.Serialize(expectedClaims);
            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, json);
            var configServiceHandler = new ConfigurationServiceResponseHandler { InnerHandler = _handler };
            var httpClientFactory = A.Fake<IHttpClientFactory>();

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => httpClientFactory.CreateClient(A<string>.Ignored)).Returns(httpClient);

            ConfigurationServiceApiClient _configServiceApiClient = new(httpClient);
            _configServiceApiClient.Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", expectedToken);

            var configContext = new ConfigurationServiceContext(clientId, clientSecret, scope);

            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(clientId, clientSecret, scope)).Returns(expectedToken);

            _metadataProvider = new SecurityMetadataProvider(
                _configServiceApiClient,
                tokenHandler,
                configContext
            );

            _claims = await _metadataProvider.GetAllClaimSets();
        }

        [Test]
        public void Should_Return_ClaimSet_List()
        {
            _claims.Should().NotBeNull();
            _claims.Should().HaveCount(2);
            _claims![0].Name.Should().Be("ClaimSet1");
        }
    }

    [TestFixture]
    public class Given_Error_Response_From_Api : ConfigurationServiceTokenHandlerTests
    {
        private SecurityMetadataProvider? _metadataProvider;
        private TestHttpMessageHandler? _handler = null;

        [Test]
        public void Should_Throw_BadRequest()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.BadRequest);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _metadataProvider!.GetAllClaimSets()
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void Should_Throw_Unauthorized()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.Unauthorized);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _metadataProvider!.GetAllClaimSets()
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void Should_Throw_NotFound()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.NotFound);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _metadataProvider!.GetAllClaimSets()
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void Should_Throw_Forbidden()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.Forbidden);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _metadataProvider!.GetAllClaimSets()
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public void Should_Throw_InternalServerError()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.InternalServerError);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _metadataProvider!.GetAllClaimSets()
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.InternalServerError);
        }

        private void SetMetadataProvider(HttpStatusCode statusCode)
        {
            var clientId = "testclient";
            var clientSecret = "secret";
            var scope = "fullaccess";
            string? expectedToken = "valid-token";

            _handler = new TestHttpMessageHandler(statusCode);
            var configServiceHandler = new ConfigurationServiceResponseHandler { InnerHandler = _handler };
            var httpClientFactory = A.Fake<IHttpClientFactory>();

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => httpClientFactory.CreateClient(A<string>.Ignored)).Returns(httpClient);

            ConfigurationServiceApiClient _configServiceApiClient = new(httpClient);
            _configServiceApiClient.Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", expectedToken);

            var configContext = new ConfigurationServiceContext(clientId, clientSecret, scope);

            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(clientId, clientSecret, scope)).Returns(expectedToken);

            _metadataProvider = new SecurityMetadataProvider(
                _configServiceApiClient,
                tokenHandler,
                configContext
            );
        }
    }
}
