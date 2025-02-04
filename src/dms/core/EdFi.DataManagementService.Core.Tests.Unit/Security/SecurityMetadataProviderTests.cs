// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

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
            var jsonContent = """
                [
                {
                    "id": 1,
                    "Name": "Test-11",
                    "resourceClaims": [
                     {
                        "name": "Test ResourceClaim",
                        "actions": [
                          {
                            "name": "Create",
                            "enabled": true
                          }
                        ]
                        }
                    ]
                },
                {
                    "id": 2,
                    "Name": "Test-22",
                    "resourceClaims": [
                     {
                        "name": "Test ResourceClaim",
                        "actions": [
                              {
                                "name": "Create",
                                "enabled": true
                              },
                              {
                                "name": "Delete",
                                "enabled": true
                              },
                              {
                                "name": "Update",
                                "enabled": true
                              },
                              {
                                "name": "Read",
                                "enabled": true
                              }],
                        "children": [],
                        "authorizationStrategyOverridesForCRUD": [],
                        "_defaultAuthorizationStrategiesForCRUD": [
                            {
                            "actionId": 1,
                            "actionName": "Create",
                            "authorizationStrategies": [
                                {
                                "authStrategyId": 1,
                                "authStrategyName": "NoFurtherAuthorizationRequired",
                                "isInheritedFromParent": false
                                }
                            ]
                            }
                        ]
                     }]
                   }
                ]
                """;

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, jsonContent);
            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };
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
            _claims![0].Name.Should().Be("Test-11");
            _claims![0].ResourceClaims.Should().NotBeNull();
            _claims![0].ResourceClaims!.Should().HaveCount(1);
            _claims![1].Name.Should().Be("Test-22");
            _claims![1].ResourceClaims.Should().NotBeNull();
            _claims![1].ResourceClaims!.Should().HaveCount(1);
            _claims![1].ResourceClaims![0].Should().NotBeNull();
            _claims![1].ResourceClaims![0].DefaultAuthorizationStrategiesForCRUD.Should().NotBeNull();
            _claims![1].ResourceClaims![0].DefaultAuthorizationStrategiesForCRUD.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_Error_Response_From_Api : ConfigurationServiceTokenHandlerTests
    {
        private SecurityMetadataProvider? _metadataProvider;
        private TestHttpMessageHandler? _handler = null;

        [Test]
        public void Should_Throw_Exception_For_BadRequest()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.BadRequest);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _metadataProvider!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_Unauthorized()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.Unauthorized);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _metadataProvider!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_NotFound()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.NotFound);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _metadataProvider!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_Forbidden()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.Forbidden);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _metadataProvider!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_InternalServerError()
        {
            // Arrange
            SetMetadataProvider(HttpStatusCode.InternalServerError);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _metadataProvider!.GetAllClaimSets());
        }

        private void SetMetadataProvider(HttpStatusCode statusCode)
        {
            var clientId = "testclient";
            var clientSecret = "secret";
            var scope = "fullaccess";
            string? expectedToken = "valid-token";

            _handler = new TestHttpMessageHandler(statusCode);
            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };
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
