// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Introspection;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class TokenInfoModuleTests
{
    private readonly ITokenInfoProvider _tokenInfoProvider = A.Fake<ITokenInfoProvider>();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
                    // Use the new test authentication extension that mimics production setup
                    collection.AddTestAuthentication();

                    var identitySettings = ctx
                        .Configuration.GetSection("IdentitySettings")
                        .Get<IdentitySettings>()!;
                    collection.AddAuthorization(options =>
                    {
                        options.AddPolicy(
                            SecurityConstants.ServicePolicy,
                            policy =>
                                policy.RequireClaim(
                                    identitySettings.RoleClaimType,
                                    identitySettings.ConfigServiceRole
                                )
                        );
                        AuthorizationScopePolicies.Add(options);
                    });
                    collection.AddTransient((_) => _tokenInfoProvider);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class SuccessTests : TokenInfoModuleTests
    {
        private TokenInfoResponse _mockTokenInfoResponse = null!;

        [SetUp]
        public void SetUp()
        {
            _mockTokenInfoResponse = new TokenInfoResponse
            {
                Active = true,
                ClientId = "test-client",
                NamespacePrefixes = new List<string> { "uri://ed-fi.org" },
                EducationOrganizations = new List<TokenInfoEducationOrganization>
                {
                    new()
                    {
                        EducationOrganizationId = 255901,
                        NameOfInstitution = "Test School",
                        Type = "edfi.School",
                        LocalEducationAgencyId = 255950,
                    },
                },
                AssignedProfiles = Array.Empty<string>(),
                ClaimSet = new TokenInfoClaimSet { Name = "SIS Vendor" },
                Resources = new List<TokenInfoResource>
                {
                    new() { Resource = "/ed-fi/students", Operations = new List<string> { "Create", "Read" } },
                },
                Services = new List<TokenInfoService>
                {
                    new() { Service = "identity", Operations = new List<string> { "Read" } },
                },
            };
        }

        [Test]
        public async Task Should_return_token_info_for_valid_json_request()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _tokenInfoProvider.GetTokenInfoAsync(A<string>.That.Matches(t => t == "valid-token")))
                .Returns(_mockTokenInfoResponse);

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": "valid-token"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TokenInfoResponse>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            result.Should().NotBeNull();
            result!.Active.Should().BeTrue();
            result.ClientId.Should().Be("test-client");
            result.NamespacePrefixes.Should().ContainSingle().Which.Should().Be("uri://ed-fi.org");
            result.EducationOrganizations.Should().ContainSingle();
            result.Resources.Should().ContainSingle();
            result.Services.Should().ContainSingle();
        }

        [Test]
        public async Task Should_return_token_info_for_valid_form_request()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _tokenInfoProvider.GetTokenInfoAsync(A<string>.That.Matches(t => t == "valid-token")))
                .Returns(_mockTokenInfoResponse);

            var formContent = new FormUrlEncodedContent(
                new Dictionary<string, string> { { "token", "valid-token" } }
            );

            // Act
            var response = await client.PostAsync("/oauth/token_info", formContent);

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TokenInfoResponse>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            result.Should().NotBeNull();
            result!.Active.Should().BeTrue();
            result.ClientId.Should().Be("test-client");
        }

        [Test]
        public async Task Should_return_inactive_token_info_for_expired_token()
        {
            // Arrange
            using var client = SetUpClient();
            var expiredTokenInfo = new TokenInfoResponse
            {
                Active = false,
                ClientId = "test-client",
                NamespacePrefixes = new List<string> { "uri://ed-fi.org" },
                EducationOrganizations = new List<TokenInfoEducationOrganization>(),
                AssignedProfiles = Array.Empty<string>(),
                ClaimSet = new TokenInfoClaimSet { Name = "SIS Vendor" },
                Resources = new List<TokenInfoResource>(),
                Services = new List<TokenInfoService>(),
            };

            A.CallTo(() => _tokenInfoProvider.GetTokenInfoAsync(A<string>.That.Matches(t => t == "expired-token")))
                .Returns(expiredTokenInfo);

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": "expired-token"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TokenInfoResponse>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            result.Should().NotBeNull();
            result!.Active.Should().BeFalse();
        }
    }

    [TestFixture]
    public class ValidationTests : TokenInfoModuleTests
    {
        [Test]
        public async Task Should_return_bad_request_for_missing_token_in_json_request()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_bad_request_for_empty_token_in_json_request()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": ""
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_bad_request_for_null_token_in_json_request()
        {
            // Arrange
            using var client = SetUpClient();

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": null
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_bad_request_for_missing_token_in_form_request()
        {
            // Arrange
            using var client = SetUpClient();

            var formContent = new FormUrlEncodedContent(new Dictionary<string, string>());

            // Act
            var response = await client.PostAsync("/oauth/token_info", formContent);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_bad_request_for_empty_token_in_form_request()
        {
            // Arrange
            using var client = SetUpClient();

            var formContent = new FormUrlEncodedContent(
                new Dictionary<string, string> { { "token", "" } }
            );

            // Act
            var response = await client.PostAsync("/oauth/token_info", formContent);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [TestFixture]
    public class NotFoundTests : TokenInfoModuleTests
    {
        [Test]
        public async Task Should_return_not_found_for_invalid_token_in_json_request()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _tokenInfoProvider.GetTokenInfoAsync(A<string>._)).Returns<TokenInfoResponse?>(null);

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": "invalid-token"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task Should_return_not_found_for_invalid_token_in_form_request()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _tokenInfoProvider.GetTokenInfoAsync(A<string>._)).Returns<TokenInfoResponse?>(null);

            var formContent = new FormUrlEncodedContent(
                new Dictionary<string, string> { { "token", "invalid-token" } }
            );

            // Act
            var response = await client.PostAsync("/oauth/token_info", formContent);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task Should_return_not_found_for_malformed_token()
        {
            // Arrange
            using var client = SetUpClient();
            A.CallTo(() => _tokenInfoProvider.GetTokenInfoAsync(A<string>._)).Returns<TokenInfoResponse?>(null);

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": "not-a-valid-jwt-token"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class AuthorizationTests : TokenInfoModuleTests
    {
        [Test]
        public async Task Should_require_authorization()
        {
            // Arrange
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                // Don't add test authentication - should fail without auth
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.PostAsync(
                "/oauth/token_info",
                new StringContent(
                    """
                    {
                        "token": "some-token"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
