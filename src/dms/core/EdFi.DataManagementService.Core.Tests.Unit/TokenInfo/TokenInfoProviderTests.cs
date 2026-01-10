// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.Security;
using EdFi.DataManagementService.Core.TokenInfo;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.TokenInfo;

public class TokenInfoProviderTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Valid_JWT_Token_With_All_Claims : TokenInfoProviderTests
    {
        private TokenInfoProvider? _tokenInfoProvider;
        private TokenInfoResponse? _response;
        private string? _jwtToken;
        private TestHttpMessageHandler? _handler = null;

        [SetUp]
        public async Task Setup()
        {
            // Arrange - Create a valid JWT token
            var tokenHandler = new JwtSecurityTokenHandler();
            // Note: Test key only - never use hardcoded secrets in production
#pragma warning disable S6781 // JWT secret keys should not be disclosed - This is a test key only
            var testSecretKey = "this-is-a-test-secret-key-for-jwt-tokens-12345678";
            var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(testSecretKey));
#pragma warning restore S6781
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new("client_id", "test-client"),
                new("sub", "test-user"),
                new("scope", "EdFiSandbox"),
                new("dmsInstanceIds", "1"),
                new("educationOrganizationIds", "255901,255902"),
                new("namespacePrefixes", "uri://ed-fi.org"),
                new(
                    "http://ed-fi.org/ods/identity/claims/ed-fi/student",
                    "http://ed-fi.org/ods/identity/claims/domains/edFiTypes"
                ),
                new(
                    "http://ed-fi.org/ods/identity/claims/ed-fi/school",
                    "http://ed-fi.org/ods/identity/claims/domains/edFiTypes"
                ),
                new("http://ed-fi.org/identity/claims/services/sis", "true"),
            };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = credentials,
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            _jwtToken = tokenHandler.WriteToken(token);

            // Setup mocks
            var fakeTokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            var fakeConfigContext = new ConfigurationServiceContext("client", "secret", "scope");
            var fakeEdOrgRepo = A.Fake<IEducationOrganizationRepository>();
            var fakeDmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var fakeDmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();

            // Setup authorization metadata response
            var authMetadata = new JsonArray
            {
                new JsonObject
                {
                    ["claimSetName"] = "EdFiSandbox",
                    ["claims"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["claimId"] = 1,
                            ["claimName"] = "http://ed-fi.org/ods/identity/claims/ed-fi/student",
                        },
                        new JsonObject
                        {
                            ["claimId"] = 2,
                            ["claimName"] = "http://ed-fi.org/ods/identity/claims/ed-fi/school",
                        },
                    },
                    ["authorizations"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["claimId"] = 1,
                            ["actions"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["actionName"] = "Read",
                                    ["authorizationStrategies"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["authorizationStrategyName"] = "NoFurtherAuthorizationRequired",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse("https://api.example.com/authorizationMetadata", authMetadata);

            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => fakeTokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            // Setup education organizations
            A.CallTo(() => fakeEdOrgRepo.GetEducationOrganizationsAsync(A<long[]>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<TokenInfoEducationOrganization>>(
                        new List<TokenInfoEducationOrganization>
                        {
                            new()
                            {
                                EducationOrganizationId = 255901,
                                Type = "School",
                                NameOfInstitution = "Test School 1",
                            },
                            new()
                            {
                                EducationOrganizationId = 255902,
                                Type = "School",
                                NameOfInstitution = "Test School 2",
                            },
                        }
                    )
                );

            // Setup API Schema
            var apiSchemaJson = new JsonObject
            {
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectVersion"] = "5.0.0",
                    ["description"] = "Ed-Fi Data Standard",
                    ["projectEndpointName"] = "ed-fi",
                    ["isExtensionProject"] = false,
                    ["abstractResources"] = new JsonObject(),
                    ["caseInsensitiveEndpointNameMapping"] = new JsonObject
                    {
                        ["students"] = "students",
                        ["schools"] = "schools",
                    },
                    ["resourceNameMapping"] = new JsonObject
                    {
                        ["Student"] = "students",
                        ["School"] = "schools",
                    },
                    ["resourceSchemas"] = new JsonObject
                    {
                        ["students"] = new JsonObject
                        {
                            ["resourceName"] = "Student",
                            ["isDescriptor"] = false,
                        },
                        ["schools"] = new JsonObject
                        {
                            ["resourceName"] = "School",
                            ["isDescriptor"] = false,
                        },
                    },
                },
            };

            var apiSchemaNodes = new ApiSchemaDocumentNodes(apiSchemaJson, Array.Empty<JsonNode>());
            A.CallTo(() => fakeApiSchemaProvider.GetApiSchemaNodes()).Returns(apiSchemaNodes);

            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);

            _tokenInfoProvider = new TokenInfoProvider(
                configServiceApiClient,
                fakeTokenHandler,
                fakeConfigContext,
                fakeEdOrgRepo,
                fakeDmsInstanceProvider,
                fakeDmsInstanceSelection,
                fakeApiSchemaProvider,
                NullLogger<TokenInfoProvider>.Instance
            );

            // Act
            _response = await _tokenInfoProvider.GetTokenInfoAsync(_jwtToken);
        }

        [Test]
        public void Should_Return_Active_Token()
        {
            _response.Should().NotBeNull();
            _response!.Active.Should().BeTrue();
        }

        [Test]
        public void Should_Extract_Client_Id()
        {
            _response!.ClientId.Should().Be("test-client");
        }

        [Test]
        public void Should_Have_ClaimSet()
        {
            _response!.ClaimSet.Should().NotBeNull();
            _response!.ClaimSet.Name.Should().Be("EdFiSandbox");
        }

        [Test]
        public void Should_Extract_Namespace_Prefixes()
        {
            _response!.NamespacePrefixes.Should().ContainSingle();
            _response!.NamespacePrefixes.Should().Contain("uri://ed-fi.org");
        }

        [Test]
        public void Should_Extract_Education_Organizations()
        {
            _response!.EducationOrganizations.Should().HaveCount(2);
            _response!.EducationOrganizations[0].EducationOrganizationId.Should().Be(255901);
            _response!.EducationOrganizations[0].Type.Should().Be("School");
            _response!.EducationOrganizations[0].NameOfInstitution.Should().Be("Test School 1");
            _response!.EducationOrganizations[1].EducationOrganizationId.Should().Be(255902);
            _response!.EducationOrganizations[1].Type.Should().Be("School");
            _response!.EducationOrganizations[1].NameOfInstitution.Should().Be("Test School 2");
        }

        [Test]
        public void Should_Extract_Resources_With_Correct_Paths()
        {
            _response!.Resources.Should().HaveCount(2);
            _response!.Resources.Should().Contain(r => r.Resource == "/ed-fi/students");
            _response!.Resources.Should().Contain(r => r.Resource == "/ed-fi/schools");
        }

        [Test]
        public void Should_Extract_Resources_With_Actions()
        {
            var studentResource = _response!.Resources.First(r => r.Resource == "/ed-fi/students");
            studentResource.Operations.Should().ContainSingle();
            studentResource.Operations.Should().Contain("Read");
        }

        [Test]
        public void Should_Extract_Services()
        {
            _response!.Services.Should().NotBeNull();
            _response!.Services.Should().ContainSingle();
            _response!.Services![0].Service.Should().Be("sis");
            _response!.Services![0].Operations.Should().ContainSingle();
            _response!.Services![0].Operations.Should().Contain("true");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Invalid_JWT_Token : TokenInfoProviderTests
    {
        private TokenInfoProvider? _tokenInfoProvider;
        private TokenInfoResponse? _response;

        [SetUp]
        public async Task Setup()
        {
            // Arrange
            var _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);
            var fakeTokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            var fakeConfigContext = new ConfigurationServiceContext("client", "secret", "scope");
            var fakeEdOrgRepo = A.Fake<IEducationOrganizationRepository>();
            var fakeDmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var fakeDmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();

            _tokenInfoProvider = new TokenInfoProvider(
                configServiceApiClient,
                fakeTokenHandler,
                fakeConfigContext,
                fakeEdOrgRepo,
                fakeDmsInstanceProvider,
                fakeDmsInstanceSelection,
                fakeApiSchemaProvider,
                NullLogger<TokenInfoProvider>.Instance
            );

            // Act
            _response = await _tokenInfoProvider.GetTokenInfoAsync("not-a-valid-jwt-token");
        }

        [Test]
        public void Should_Return_Null_For_Invalid_Token()
        {
            _response.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Token_Without_DmsInstanceIds : TokenInfoProviderTests
    {
        private TokenInfoProvider? _tokenInfoProvider;
        private TokenInfoResponse? _response;
        private string? _jwtToken;

        [SetUp]
        public async Task Setup()
        {
            // Arrange - Create a JWT token without dmsInstanceIds
            var tokenHandler = new JwtSecurityTokenHandler();
#pragma warning disable S6781 // JWT secret keys should not be disclosed - This is a test key only
            var key = new SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes("this-is-a-test-secret-key-for-jwt-tokens-12345678")
            );
#pragma warning restore S6781
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim> { new("client_id", "test-client"), new("scope", "EdFiSandbox") };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = credentials,
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            _jwtToken = tokenHandler.WriteToken(token);

            // Setup mocks
            var fakeTokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            var fakeConfigContext = new ConfigurationServiceContext("client", "secret", "scope");
            var fakeEdOrgRepo = A.Fake<IEducationOrganizationRepository>();
            var fakeDmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var fakeDmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();

            var authMetadata = new JsonArray
            {
                new JsonObject
                {
                    ["claimSetName"] = "EdFiSandbox",
                    ["claims"] = new JsonArray(),
                    ["authorizations"] = new JsonArray(),
                },
            };

            var _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse("https://api.example.com/authorizationMetadata", authMetadata);

            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => fakeTokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);

            _tokenInfoProvider = new TokenInfoProvider(
                configServiceApiClient,
                fakeTokenHandler,
                fakeConfigContext,
                fakeEdOrgRepo,
                fakeDmsInstanceProvider,
                fakeDmsInstanceSelection,
                fakeApiSchemaProvider,
                NullLogger<TokenInfoProvider>.Instance
            );

            // Act
            _response = await _tokenInfoProvider.GetTokenInfoAsync(_jwtToken);
        }

        [Test]
        public void Should_Return_Active_Token()
        {
            _response.Should().NotBeNull();
            _response!.Active.Should().BeTrue();
        }

        [Test]
        public void Should_Have_Empty_Education_Organizations()
        {
            _response!.EducationOrganizations.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Config_Service_Returns_Error : TokenInfoProviderTests
    {
        private TokenInfoProvider? _tokenInfoProvider;

        [SetUp]
        public void Setup()
        {
            // Arrange
            var _handler = new TestHttpMessageHandler(HttpStatusCode.InternalServerError, "");

            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            var fakeTokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => fakeTokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var fakeConfigContext = new ConfigurationServiceContext("client", "secret", "scope");
            var fakeEdOrgRepo = A.Fake<IEducationOrganizationRepository>();
            var fakeDmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var fakeDmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();

            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);

            _tokenInfoProvider = new TokenInfoProvider(
                configServiceApiClient,
                fakeTokenHandler,
                fakeConfigContext,
                fakeEdOrgRepo,
                fakeDmsInstanceProvider,
                fakeDmsInstanceSelection,
                fakeApiSchemaProvider,
                NullLogger<TokenInfoProvider>.Instance
            );
        }

        [Test]
        public void Should_Throw_Exception_When_Config_Service_Fails()
        {
            // Arrange - Create a valid JWT token
            var tokenHandler = new JwtSecurityTokenHandler();
#pragma warning disable S6781 // JWT secret keys should not be disclosed - This is a test key only
            var key = new SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes("this-is-a-test-secret-key-for-jwt-tokens-12345678")
            );
#pragma warning restore S6781
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim> { new("client_id", "test-client"), new("scope", "EdFiSandbox") };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = credentials,
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            var jwtToken = tokenHandler.WriteToken(token);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await _tokenInfoProvider!.GetTokenInfoAsync(jwtToken)
            );
        }
    }
}
