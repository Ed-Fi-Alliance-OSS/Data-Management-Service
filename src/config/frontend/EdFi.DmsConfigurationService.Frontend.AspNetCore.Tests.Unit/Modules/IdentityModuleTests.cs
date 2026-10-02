// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using OpenIddictIdentityOptions = EdFi.DmsConfigurationService.Backend.OpenIddict.Models.IdentityOptions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class RegisterEndpointTests
{
    private static readonly ClientSecretValidationOptions DefaultClientSecretValidationOptions = new()
    {
        MinimumLength = 8,
        MaximumLength = 12,
    };

    private IIdentityProviderRepository? _clientRepository;

    private static RegisterRequest.Validator CreateRegisterRequestValidator() =>
        new(Options.Create(DefaultClientSecretValidationOptions));

    [SetUp]
    public void Setup()
    {
        _clientRepository = A.Fake<IIdentityProviderRepository>();
        A.CallTo(() =>
                _clientRepository.CreateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored
                )
            )
            .Returns(new ClientCreateResult.Success(Guid.NewGuid()));
        var clientList = A.Fake<IEnumerable<string>>();
        A.CallTo(() => _clientRepository.GetAllClientsAsync())
            .Returns(new ClientClientsResult.Success(clientList));
    }

    [Test]
    public async Task Given_valid_client_details()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient1"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient1"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("CSClient1");
    }

    [Test]
    public async Task Given_empty_client_details()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", ""),
            new KeyValuePair<string, string>("clientsecret", ""),
            new KeyValuePair<string, string>("displayname", ""),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();
        content = System.Text.RegularExpressions.Regex.Unescape(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content.Should().Contain("'Client Id' must not be empty.");
        content.Should().Contain("'Client Secret' must not be empty.");
        content.Should().Contain("'Display Name' must not be empty.");
    }

    [Test]
    [TestCase("sM@1l")]
    [TestCase("VeryVeryVeryLongPasswordM@1l")]
    [TestCase("noupperc@s3")]
    [TestCase("NOLOWERC@S3")]
    [TestCase("NoSpecial0908")]
    [TestCase("NoNumberP@ssWord")]
    public async Task Given_invalid_client_secret(string secret)
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient2"),
            new KeyValuePair<string, string>("clientsecret", secret),
            new KeyValuePair<string, string>("displayname", "CSClient2@cs.com"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content
            .Should()
            .Contain(
                "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 8 to 12 characters long."
            );
    }

    [Test]
    public async Task When_provider_has_bad_credentials()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _clientRepository = A.Fake<IIdentityProviderRepository>();

            var error = new IdentityProviderError.Unauthorized("Unauthorized");

            A.CallTo(() => _clientRepository.GetAllClientsAsync())
                .Returns(new ClientClientsResult.FailureIdentityProvider(error));

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient3"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient3"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        content.Should().Contain("The identity provider returned an unexpected response.");
    }

    [Test]
    public async Task When_provider_has_not_real_admin_role()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _clientRepository = A.Fake<IIdentityProviderRepository>();

            var error = new IdentityProviderError.Forbidden("Forbidden.");

            A.CallTo(() => _clientRepository.GetAllClientsAsync())
                .Returns(new ClientClientsResult.FailureIdentityProvider(error));

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient3"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient3"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        content.Should().Contain("The identity provider returned an unexpected response.");
    }

    [Test]
    public async Task When_provider_has_invalid_realm()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _clientRepository = A.Fake<IIdentityProviderRepository>();

            var error = new IdentityProviderError.NotFound(
                """
                { "error":"Realm does not exist","error_description":"For more on this error consult the server log at the debug level."}
                """
            );

            A.CallTo(() => _clientRepository.GetAllClientsAsync())
                .Returns(new ClientClientsResult.FailureIdentityProvider(error));

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient3"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient3"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();
        var actualResponse = JsonNode.Parse(content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:bad-gateway",
              "title": "Bad Gateway",
              "status": 502,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
               "Realm does not exist. For more on this error consult the server log at the debug level."
            ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public async Task Given_client_with_existing_client_id()
    {
        // Arrange
        var clientList = A.Fake<IEnumerable<string>>();
        _clientRepository = A.Fake<IIdentityProviderRepository>();
        clientList = clientList.Append("CSClient2");
        A.CallTo(() => _clientRepository.GetAllClientsAsync())
            .Returns(new ClientClientsResult.Success(clientList));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient2"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient2@cs.com"),
        ]);

        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content
            .Should()
            .Contain("Client with the same Client Id already exists. Please provide different Client Id.");
    }

    [Test]
    public async Task When_allow_registration_is_disabled()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.Configure<IdentitySettings>(opts =>
                    {
                        opts.AllowRegistration = false;
                    });
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient2"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient2@cs.com"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");
        body["title"]!.GetValue<string>().Should().Be("Authorization Failed");
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The request could not be processed. See 'errors' for details.");
        body["status"]!.GetValue<int>().Should().Be(403);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(1);
        body["errors"]![0]!.GetValue<string>().Should().Be("Registration is disabled.");
    }

    [Test]
    public async Task When_provider_is_unreachable()
    {
        //Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _clientRepository = A.Fake<IIdentityProviderRepository>();

            var error = new IdentityProviderError.Unreachable(
                "No connection could be made because the target machine actively refused it."
            );

            A.CallTo(() => _clientRepository.GetAllClientsAsync())
                .Returns(new ClientClientsResult.FailureIdentityProvider(error));

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => CreateRegisterRequestValidator());
                    collection.AddTransient((_) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        //Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("clientid", "CSClient3"),
            new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
            new KeyValuePair<string, string>("displayname", "CSClient3"),
        ]);
        var response = await client.PostAsync("/connect/register", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var actualResponse = JsonNode.Parse(content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:bad-gateway",
              "title": "Bad Gateway",
              "status": 502,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
                "The identity provider returned an unexpected response."
            ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }
}

[TestFixture]
public class TokenEndpointTests
{
    private ITokenManager? _tokenManager;

    [SetUp]
    public void Setup()
    {
        _tokenManager = A.Fake<ITokenManager>();
        string token = """
            {
                "access_token":"input123token",
                "expires_in":900,
                "token_type":"bearer"
            }
            """;
        A.CallTo(() =>
                _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
            )
            .Returns(new TokenResult.Success(token));
    }

    [Test]
    public async Task Given_valid_client_credentials()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().NotBeNull();
        content.Should().Contain("input123token");
        content.Should().Contain("bearer");
    }

    [Test]
    public async Task Given_basic_auth_credentials_with_reserved_characters()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        var encodedCredentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("client%3Awith%2Breserved:secret%3Awith%25reserved%2Bchars")
        );
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encodedCredentials
        );

        // Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() =>
                _tokenManager!.GetAccessTokenAsync(
                    A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(credentials =>
                        credentials.Any(pair =>
                            pair.Key == "client_id" && pair.Value == "client:with+reserved"
                        )
                        && credentials.Any(pair =>
                            pair.Key == "client_secret" && pair.Value == "secret:with%reserved+chars"
                        )
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Given_empty_client_credentials()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", ""),
                new KeyValuePair<string, string>("client_secret", ""),
                new KeyValuePair<string, string>("grant_type", ""),
                new KeyValuePair<string, string>("scope", ""),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();
        content = System.Text.RegularExpressions.Regex.Unescape(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content.Should().Contain("'client_id' must not be empty.");
        content.Should().Contain("'client_secret' must not be empty.");
    }

    [Test]
    public async Task When_error_from_backend()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _tokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.FailureUnknown(
                        "No connection could be made because the target machine actively refused it."
                    )
                );

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task When_provider_is_unreacheable()
    {
        //Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _tokenManager = A.Fake<ITokenManager>();

            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.Unreachable(
                            "No connection could be made because the target machine actively refused it."
                        )
                    )
                );

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        //Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var actualResponse = JsonNode.Parse(content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:bad-gateway",
              "title": "Bad Gateway",
              "status": 502,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
                "The identity provider returned an unexpected response."
            ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public async Task When_provider_has_invalid_realm()
    {
        //Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _tokenManager = A.Fake<ITokenManager>();

            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.NotFound(
                            """
                            { "error":"Realm does not exist","error_description":"For more on this error consult the server log at the debug level."}
                            """
                        )
                    )
                );

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        //Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        var actualResponse = JsonNode.Parse(content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:bad-gateway",
              "title": "Bad Gateway",
              "status": 502,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
               "Realm does not exist. For more on this error consult the server log at the debug level."
            ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public async Task When_provider_has_not_realm_admin_role()
    {
        //Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _tokenManager = A.Fake<ITokenManager>();

            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.Unauthorized("Insufficient Permissions")
                    )
                );

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        //Act
        var requestContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("client_id", "CSClient1"),
            new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
        ]);
        var response = await client.PostAsync("/connect/token", requestContent);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task When_provider_has_bad_credetials()
    {
        //Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _tokenManager = A.Fake<ITokenManager>();

            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.Unauthorized(
                            """
                            {"error":"invalid_client","error_description":"Invalid client or Invalid client credentials"}
                            """
                        )
                    )
                );

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => new TokenRequest.Validator());
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        //Act
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        var actualResponse = JsonNode.Parse(content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:security:authentication",
              "title": "Authentication Failed",
              "status": 401,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
               "invalid_client. Invalid client or Invalid client credentials"
            ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [TestFixture]
    public class Given_a_service_owned_authentication_failure
    {
        private ITokenManager _serviceTokenManager = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _serviceTokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    _serviceTokenManager.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.Ignored
                    )
                )
                .Returns(
                    new TokenResult.FailureAuthentication(
                        "invalid_client",
                        "Invalid client or Invalid client credentials"
                    )
                );

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient(_ => new TokenRequest.Validator());
                    collection.AddTransient(_ => _serviceTokenManager);
                });
            });
            _client = _factory.CreateClient();

            var requestContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            ]);
            _response = await _client.PostAsync("/connect/token", requestContent);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_returns_the_authentication_contract_with_the_composed_error()
        {
            JsonNode actualResponse = JsonNode.Parse(_content)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "The request could not be processed. See 'errors' for details.",
                  "type": "urn:ed-fi:api:security:authentication",
                  "title": "Authentication Failed",
                  "status": 401,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": [
                    "invalid_client. Invalid client or Invalid client credentials"
                  ]
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }

    /// <summary>
    /// The token-limit rejection over the real HTTP pipeline. Driving it through
    /// <c>WebApplicationFactory</c> rather than calling the module directly is what proves the
    /// status, the content type and every member of the body survive
    /// <c>GlobalExceptionHandler</c> and <c>FrameworkErrorResponseMiddleware</c> unreshaped.
    /// </summary>
    [TestFixture]
    public class Given_a_client_that_has_reached_its_token_limit
    {
        private ITokenManager _serviceTokenManager = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _serviceTokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    _serviceTokenManager.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.Ignored
                    )
                )
                .Returns(new TokenResult.FailureTokenLimitExceeded(5));

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient(_ => new TokenRequest.Validator());
                    collection.AddTransient(_ => _serviceTokenManager);
                });
            });
            _client = _factory.CreateClient();

            var requestContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            ]);
            _response = await _client.PostAsync("/connect/token", requestContent);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_429() => _response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_carries_a_correlation_id() =>
            JsonNode.Parse(_content)!["correlationId"]!.GetValue<string>().Should().NotBeEmpty();

        [Test]
        public void It_returns_the_tickets_too_many_tokens_contract()
        {
            JsonNode actualResponse = JsonNode.Parse(_content)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "The caller has authenticated too many times in too short of a time period.",
                  "type": "urn:ed-fi:api:security:authentication:too-many-tokens",
                  "title": "Too Many Tokens",
                  "status": 429,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": [
                    "Too many access tokens have been requested (limit is 5). Access tokens should be reused until they expire."
                  ]
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }

    /// <summary>
    /// A grant that could not be serialized against the other grants in flight for the same client.
    /// Driven over the real pipeline for the same reason the token-limit fixture above is: the
    /// point is that a transient condition answers as a retriable 409 rather than as the 500 the
    /// manager's catch-all would otherwise produce.
    /// </summary>
    [TestFixture]
    public class Given_a_grant_that_could_not_be_serialized
    {
        private ITokenManager _serviceTokenManager = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _serviceTokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    _serviceTokenManager.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.Ignored
                    )
                )
                .Returns(new TokenResult.FailureLockTimeout());

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient(_ => new TokenRequest.Validator());
                    collection.AddTransient(_ => _serviceTokenManager);
                });
            });
            _client = _factory.CreateClient();

            var requestContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            ]);
            _response = await _client.PostAsync("/connect/token", requestContent);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_409_rather_than_a_server_error() =>
            _response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_tells_the_caller_to_retry()
        {
            JsonNode actualResponse = JsonNode.Parse(_content)!;
            JsonNode expectedResponse = JsonNode.Parse(
                """
                {
                  "detail": "Unable to process the request due to a concurrent modification. Retry the request.",
                  "type": "urn:ed-fi:api:conflict",
                  "title": "Conflict",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actualResponse["correlationId"]!.GetValue<string>())
            )!;
            JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
        }
    }
}

/// <summary>
/// End-to-end verification that CMS-generated OAuth/OIDC error branches return the Ed-Fi bad-request
/// contract (400 preserved) in place of the OAuth <c>{ error, error_description }</c> shape, while the
/// protocol success responses stay untouched. <c>/connect/revoke</c> is the documented exception
/// (DMS-1327 D-01): every revocation error is in the OAuth format, pinned here and in
/// <c>RevocationRequestContractTests</c>. Non-fixture container; the
/// runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class OAuthEndpointErrorTests
{
    private static WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection>? configureServices)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
    }

    private static void AssertBadRequestContract(
        HttpResponseMessage response,
        string content,
        string expectedDetail
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject body = JsonNode.Parse(content)!.AsObject();
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["detail"]!.GetValue<string>().Should().Be(expectedDetail);
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);

        // The OAuth { error, error_description } shape must be gone from the parsed body.
        body.ContainsKey("error").Should().BeFalse();
        body.ContainsKey("error_description").Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_token_request_with_an_unsupported_grant_type
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(collection =>
            {
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => _tokenManager);
            });
            _client = _factory.CreateClient();

            // Passes validation (all fields present) but uses an unsupported grant type.
            var requestContent = new FormUrlEncodedContent(
                new[]
                {
                    new KeyValuePair<string, string>("client_id", "CSClient1"),
                    new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                    new KeyValuePair<string, string>("grant_type", "password"),
                    new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
                }
            );
            _response = await _client.PostAsync("/connect/token", requestContent);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_ed_fi_bad_request_contract() =>
            AssertBadRequestContract(_response, _content, "The specified grant type is not supported.");
    }

    [TestFixture]
    public class Given_a_token_request_with_a_malformed_form_payload
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(collection =>
            {
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => _tokenManager);
            });
            _client = _factory.CreateClient();

            // multipart/form-data without a boundary makes form reading throw InvalidDataException.
            var requestContent = new StringContent("client_id=x");
            requestContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "multipart/form-data"
            );
            _response = await _client.PostAsync("/connect/token", requestContent);
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_400() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_returns_the_generic_bad_request_contract_with_the_fixed_form_message()
        {
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            _body["title"]!.GetValue<string>().Should().Be("Bad Request");
            _body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The request could not be processed. See 'errors' for details.");
            _body["status"]!.GetValue<int>().Should().Be(400);
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("The request form payload is malformed.");
        }

        [Test]
        public void It_does_not_leak_the_framework_parsing_message() =>
            _content.Should().NotContain("boundary");
    }

    /// <summary>
    /// DMS-1327 D-17 keeps the OAuth exception format on <c>/connect/revoke</c> only: a fault
    /// escaping the token endpoint is still the Ed-Fi internal-server-error contract.
    /// </summary>
    [TestFixture]
    public class Given_a_token_request_whose_token_manager_faults
    {
        private const string Sentinel = "SENTINEL_TOKEN_MANAGER_FAULT_must_not_leak";

        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
                .Throws(new InvalidOperationException(Sentinel));
            _factory = CreateFactory(collection =>
            {
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => _tokenManager);
            });
            _client = _factory.CreateClient();

            _response = await _client.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(
                    new[]
                    {
                        new KeyValuePair<string, string>("client_id", "CSClient1"),
                        new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
                    }
                )
            );
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_ed_fi_internal_server_error_contract()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            JsonObject body = JsonNode.Parse(_content)!.AsObject();
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
            body.ContainsKey("error").Should().BeFalse();
        }

        [Test]
        public void It_does_not_leak_the_exception_text() => _content.Should().NotContain(Sentinel);
    }

    [TestFixture]
    public class Given_an_introspection_request_without_a_token
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(configureServices: null);
            _client = _factory.CreateClient();
            _response = await _client.PostAsync(
                "/connect/introspect",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
            );
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_ed_fi_bad_request_contract() =>
            AssertBadRequestContract(_response, _content, "The token parameter is missing.");
    }

    [TestFixture]
    public class Given_an_introspection_request_with_a_token
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(configureServices: null);
            _client = _factory.CreateClient();
            // An unresolved/opaque token yields the RFC 7662 { active: false } 200 success, unchanged.
            _response = await _client.PostAsync(
                "/connect/introspect",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "opaque-token") })
            );
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_is_not_problem_details() =>
            _response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");

        [Test]
        public void It_reports_the_token_as_inactive() =>
            _body["active"]!.GetValue<bool>().Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_revocation_request_without_a_token
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(collection => collection.AddTransient(_ => _tokenManager));
            _client = _factory.CreateClient();
            _response = await _client.PostAsync(
                "/connect/revoke",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
            );
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        /// <summary>
        /// DMS-1218 had moved this response to the Ed-Fi contract; DMS-1327 D-01 makes every
        /// revocation error an RFC 6749 §5.2 object, so a conforming OAuth client can read it.
        /// </summary>
        [Test]
        public void It_returns_the_oauth_invalid_request_error()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            JsonObject body = JsonNode.Parse(_content)!.AsObject();
            body["error"]!.GetValue<string>().Should().Be("invalid_request");
            body["error_description"]!.GetValue<string>().Should().Be("The token parameter is missing.");
        }
    }

    /// <summary>
    /// Keycloak mode as it stands until DMS-1327 P3.2: no <c>ITokenRevocationManager</c> is
    /// registered (this fixture removes the one the Test host's self-contained configuration
    /// adds). The endpoint's own shape and credential-presence checks (D-03 rows 1–7) still run
    /// before the no-op branch, so a caller with no client credentials is rejected instead of
    /// being answered 200 as it was before DMS-1327.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_token_from_an_unauthenticated_caller
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(collection =>
            {
                collection.AddTransient(_ => _tokenManager);
                collection.RemoveAll<ITokenRevocationManager>();
            });
            _client = _factory.CreateClient();
            _response = await _client.PostAsync(
                "/connect/revoke",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "opaque-token") })
            );
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_400() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_reports_invalid_client_in_the_oauth_format()
        {
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            JsonObject body = JsonNode.Parse(_content)!.AsObject();
            body["error"]!.GetValue<string>().Should().Be("invalid_client");
            body["error_description"]!.GetValue<string>().Should().Be("Client authentication is required.");
        }

        [Test]
        public void It_does_not_send_a_basic_challenge() =>
            _response.Headers.WwwAuthenticate.Should().BeEmpty();
    }
}

/// <summary>
/// Covers the authentication and <c>client_id</c> ownership rules for <c>POST /connect/revoke</c>
/// described in reference/design/configuration-service/CS-AUTH.md. Non-fixture container; the
/// runnable fixtures are the nested <c>Given_…</c> classes.
///
/// The test-double strategy differs from the rest of this file deliberately. Elsewhere
/// <c>IdentityModuleTests</c> fakes <see cref="ITokenManager"/> outright, which is right when the
/// assertion is about the HTTP contract. Here it would defeat the point: a faked manager would
/// have to re-implement the ownership rule to answer, so the tests would assert against the fake
/// rather than the production decision. Registering the real manager over a faked
/// <see cref="IOpenIddictTokenRepository"/> keeps the signature verification and ownership
/// comparison genuine while still letting the repository call be observed. The secret hasher is
/// faked to a blanket "always matches" so these fixtures can focus on authentication and
/// ownership rather than the hashing algorithm, which OpenIddictTokenManagerTests covers directly.
/// </summary>
public class RevocationOwnershipTests
{
    private const string TestIssuer = "https://cms.example.test";
    private const string TestAudience = "ed-fi-cms-tests";
    private const string OwnerClientId = "revoke-owner-client";
    private const string OtherClientId = "revoke-other-client";
    private const string TestClientSecret = "test-secret";

    private static (string KeyId, byte[] PublicKeySpki, RsaSecurityKey SigningKey) CreateSigningKey()
    {
        var rsa = RSA.Create(2048);
        string keyId = Guid.NewGuid().ToString();
        return (keyId, rsa.ExportSubjectPublicKeyInfo(), new RsaSecurityKey(rsa) { KeyId = keyId });
    }

    /// <summary>
    /// Issues a signed JWT for the test issuer/audience. The "kid" header comes from the signing
    /// key so the token manager can resolve the matching public key.
    /// </summary>
    private static string CreateSignedToken(RsaSecurityKey signingKey, string clientId, Guid jti)
    {
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()),
                new Claim("client_id", clientId),
            ],
            notBefore: now.AddMinutes(-5),
            expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static OpenIddictTokenManager CreateTokenManager(
        IOpenIddictTokenRepository tokenRepository,
        IClientSecretHasher secretHasher
    ) =>
        new(
            Options.Create(new OpenIddictIdentityOptions { Authority = TestIssuer, Audience = TestAudience }),
            NullLogger<OpenIddictTokenManager>.Instance,
            secretHasher,
            tokenRepository
        );

    /// <summary>
    /// Registers <paramref name="clientId"/> as a real, approved application so
    /// <c>ValidateClientCredentialsAsync</c> authenticates it. Pair with a secret hasher fake that
    /// returns true from <c>VerifySecretAsync</c>, which is what actually accepts the secret.
    /// </summary>
    private static void RegisterApprovedClient(IOpenIddictTokenRepository tokenRepository, string clientId) =>
        A.CallTo(() => tokenRepository.GetApplicationByClientIdAsync(clientId))
            .Returns(new ApplicationInfo { ClientId = clientId, IsApproved = true });

    private static WebApplicationFactory<Program> CreateFactory(OpenIddictTokenManager tokenManager) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTestAuthentication();
                collection.AddTransient<ITokenManager>(_ => tokenManager);
                collection.AddTransient<ITokenRevocationManager>(_ => tokenManager);
            });
        });

    /// <summary>
    /// Injects a fault into the manager's ownership comparison, which sits between its dependency
    /// boundaries and must not be relabelled as an outage.
    /// </summary>
    private sealed class FaultingOwnershipTokenManager(
        IOpenIddictTokenRepository tokenRepository,
        IClientSecretHasher secretHasher
    )
        : OpenIddictTokenManager(
            Options.Create(new OpenIddictIdentityOptions { Authority = TestIssuer, Audience = TestAudience }),
            NullLogger<OpenIddictTokenManager>.Instance,
            secretHasher,
            tokenRepository
        )
    {
        protected override bool TokenBelongsToCaller(string? tokenClientId, string callerClientId) =>
            throw new InvalidOperationException("ownership comparison fault");
    }

    private const string UnavailableDescription =
        "Token revocation could not be confirmed. Retry the request and confirm the token's state through the provider's validation path.";

    private static void AssertTemporarilyUnavailable(HttpResponseMessage response, JsonObject body)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.WwwAuthenticate.Should().BeEmpty();
        body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
        body["error_description"]!.GetValue<string>().Should().Be(UnavailableDescription);
    }

    /// <summary>
    /// Creates a client that authenticates to /connect/revoke with HTTP Basic client credentials
    /// — the way RFC 7009 §2.1 requires (RFC 6749 §2.3), not a bearer access token.
    /// </summary>
    private static HttpClient CreateClientWithCredentials(
        WebApplicationFactory<Program> factory,
        string clientId,
        string clientSecret
    )
    {
        var client = factory.CreateClient();
        var encodedCredentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")
        );
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encodedCredentials
        );
        return client;
    }

    private static Task<HttpResponseMessage> PostRevocation(HttpClient client, string token) =>
        client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", token) })
        );

    /// <summary>
    /// Parses the response body as the RFC 6749 §5.2 error object. Returns the root object so
    /// the fixtures can assert on <c>error</c> and <c>error_description</c> as the top-level
    /// members a conforming OAuth client reads, rather than matching substrings — a substring
    /// match passes just as happily when the code is buried inside a problem+json
    /// <c>errors</c> array, which is the shape this contract exists to rule out.
    /// </summary>
    private static async Task<JsonObject> ReadOAuthError(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonNode.Parse(body).Should().BeOfType<JsonObject>($"the body should be a JSON object: {body}");
        return (JsonObject)JsonNode.Parse(body)!;
    }

    /// <summary>
    /// Pins the check-ordering documented on RevokeToken: a request with no credentials at all
    /// AND no <c>token</c> field gets 400 (structurally invalid request), not 401 (authentication
    /// failure) — the missing-token check runs before client authentication is even attempted.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_no_token_and_no_credentials
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = _factory.CreateClient(); // No Authorization header, no form fields at all.

            _response = await _client.PostAsync(
                "/connect/revoke",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_400_not_401() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public async Task It_reports_the_missing_token_rather_than_the_missing_credentials()
        {
            JsonObject body = await ReadOAuthError(_response);
            body["error"]!.GetValue<string>().Should().Be("invalid_request");
            body["error_description"]!.GetValue<string>().Should().Be("The token parameter is missing.");
        }
    }

    /// <summary>
    /// RFC 6749 §2.3 permits credentials in the request body for clients that cannot use HTTP
    /// Basic auth. No other fixture in this class exercises that path — the rest all authenticate
    /// via Basic auth.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_credentials_in_the_form_body
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private Guid _jti;

        /// <summary>
        /// Posts to /connect/revoke with client credentials in the form body instead of an
        /// Authorization header. Used only here, so it lives inside this fixture.
        /// </summary>
        private static Task<HttpResponseMessage> PostRevocationWithFormCredentials(
            HttpClient client,
            string token,
            string clientId,
            string clientSecret
        ) =>
            client.PostAsync(
                "/connect/revoke",
                new FormUrlEncodedContent(
                    new[]
                    {
                        new KeyValuePair<string, string>("token", token),
                        new KeyValuePair<string, string>("client_id", clientId),
                        new KeyValuePair<string, string>("client_secret", clientSecret),
                    }
                )
            );

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = _factory.CreateClient(); // No Authorization header — credentials go in the form body.
            _response = await PostRevocationWithFormCredentials(
                _client,
                CreateSignedToken(signingKey, OwnerClientId, _jti),
                OwnerClientId,
                TestClientSecret
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_revokes_the_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_a_revocation_request_for_a_token_the_caller_owns
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private Guid _jti;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(_client, CreateSignedToken(signingKey, OwnerClientId, _jti));
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_revokes_the_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// The casing defect at the HTTP level, where it actually bites. SQL Server's default
    /// collation resolves a mis-cased <c>client_id</c>, so a caller can authenticate under a
    /// spelling that was never stored while holding a token minted from the stored one. The
    /// faked repository stands in for that collation by resolving the mis-cased lookup.
    ///
    /// Carrying the caller's own spelling into the ownership comparison — rather than the
    /// canonical id authentication resolved — makes this the silent no-op the whole change
    /// exists to prevent: the caller is told <c>200 OK</c> while its own token stays live.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_authenticated_with_non_canonical_casing
    {
        private const string NonCanonicalClientId = "REVOKE-Owner-Client";

        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private Guid _jti;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            // The mis-cased lookup resolves to the application registered under the canonical
            // spelling, which is what a case-insensitive collation does.
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(NonCanonicalClientId))
                .Returns(new ApplicationInfo { ClientId = OwnerClientId, IsApproved = true });
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, NonCanonicalClientId, TestClientSecret);

            // The token carries the canonical client_id, because that is what minting stamps on
            // it no matter which spelling the client authenticated with.
            _response = await PostRevocation(_client, CreateSignedToken(signingKey, OwnerClientId, _jti));
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_revokes_the_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Guards the RFC 7009 §2.1 requirement itself: with no client credentials presented at all,
    /// the caller is rejected before the target token is even looked at. The caller did not
    /// attempt Basic authentication, so the rejection is 400, not 401 (DMS-1327 D-03 row 7).
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_no_client_credentials
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = _factory.CreateClient(); // No Authorization header and no client_id/secret form fields.

            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_400() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_answers_in_the_oauth_error_format() =>
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        [Test]
        public void It_reports_error_as_a_top_level_member() =>
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");

        [Test]
        public void It_reports_error_description_as_a_top_level_member() =>
            _body["error_description"]!.GetValue<string>().Should().Be("Client authentication is required.");

        /// <summary>
        /// RFC 6749 §5.2 conditions the challenge on the client having attempted to authenticate
        /// through the Authorization header. This caller sent no such header, so offering it a
        /// Basic challenge would invite a scheme it did not choose.
        /// </summary>
        [Test]
        public void It_does_not_send_a_basic_challenge() =>
            _response.Headers.WwwAuthenticate.Should().BeEmpty();

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// A client_id with no matching application (or, equivalently, a wrong secret) fails
    /// authentication and is reported as such — unlike the ownership/target-token failures below,
    /// this is not masked as <c>200 OK</c>, since RFC 7009's "always 200" guarantee is about
    /// whether a token is valid or owned, not whether the caller authenticated.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_invalid_client_credentials
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            // No application is registered for this client_id, so the lookup fails regardless of
            // what the secret hasher would say. Explicit, because FakeItEasy's default dummy
            // resolver constructs a real (empty) ApplicationInfo — with IsApproved defaulting to
            // true — for an unconfigured call, rather than returning null.
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync("unregistered-client"))
                .Returns((ApplicationInfo?)null);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, "unregistered-client", TestClientSecret);

            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        /// <summary>
        /// An OAuth client reads <c>error</c> off the root of the response body, so the
        /// endpoint answers in the OAuth error format rather than the Management API's
        /// <c>application/problem+json</c> contract, which would flatten the code into a
        /// sentence inside an <c>errors</c> array where no such client will look for it.
        /// </summary>
        [Test]
        public void It_answers_in_the_oauth_error_format() =>
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        [Test]
        public void It_reports_error_as_a_top_level_member() =>
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");

        [Test]
        public void It_reports_error_description_as_a_top_level_member() =>
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("Invalid client or Invalid client credentials");

        /// <summary>
        /// RFC 6749 §5.2 requires a challenge matching the scheme the client used, and this
        /// caller authenticated through the Authorization header.
        /// </summary>
        [Test]
        public void It_sends_a_basic_challenge() =>
            _response.Headers.WwwAuthenticate.Should().ContainSingle(header => header.Scheme == "Basic");

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// A registered, approved client_id with the wrong secret must fail authentication the same
    /// way an unregistered client_id does — the failure is in the secret comparison rather than
    /// the application lookup, but the caller-facing outcome is identical.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_wrong_client_secret
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            // Explicitly false for clarity, though FakeItEasy would default an unconfigured
            // bool-returning call to false anyway.
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(false);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, "wrong-secret");

            _response = await PostRevocation(_client, "irrelevant-token");
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// A client_id/secret pair that authenticates correctly but belongs to an unapproved
    /// application must still be rejected — approval is checked as part of authentication, not
    /// treated as a separate authorization step.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_from_an_unapproved_client
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Returns(new ApplicationInfo { ClientId = OwnerClientId, IsApproved = false });
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);

            _response = await PostRevocation(_client, "irrelevant-token");
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_revocation_request_for_a_token_owned_by_another_client
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OtherClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));

            // The target token belongs to OwnerClientId; the caller authenticates as OtherClientId.
            _client = CreateClientWithCredentials(_factory, OtherClientId, TestClientSecret);
            _response = await PostRevocation(
                _client,
                CreateSignedToken(signingKey, OwnerClientId, Guid.NewGuid())
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        // The mismatch is deliberately indistinguishable from "token not found".
        [Test]
        public void It_still_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_does_not_revoke_the_other_clients_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// The ownership check is only meaningful if the target token's signature is verified before
    /// its <c>client_id</c> claim is trusted, so this forges a token naming the caller while
    /// embedding another client's <c>jti</c>.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_forged_token_naming_the_caller
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, _) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );

            // Signed with a key the service does not hold, but its "kid" names the real key so
            // the rejection comes from the signature check itself.
            var (_, _, attackerKey) = CreateSigningKey();
            attackerKey.KeyId = keyId;

            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(
                _client,
                CreateSignedToken(attackerKey, OwnerClientId, Guid.NewGuid())
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_still_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_does_not_revoke_the_embedded_jti() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_revocation_request_with_a_malformed_token
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            // A healthy key set is registered so the malformed token is judged against it; with
            // no active key at all the manager reports an outage (503) instead, by design.
            var (keyId, publicKeySpki, _) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(_client, "not-even-a-jwt");
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_still_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_does_not_revoke_anything() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// DMS-1327 D-13: a database failure during client authentication is an operational outcome,
    /// answered 503 in the OAuth error format — not 200 (which would mask it) and not 401 (the
    /// caller may well be valid). No challenge is sent: authentication did not fail.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_when_the_application_lookup_fails
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Throws(new InvalidOperationException("database unavailable"));

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_answers_503_in_the_oauth_error_format() =>
            AssertTemporarilyUnavailable(_response, _body);

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_revocation_request_when_the_secret_hasher_fails
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._))
                .Throws(new InvalidOperationException("hasher unavailable"));

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_answers_503_in_the_oauth_error_format() =>
            AssertTemporarilyUnavailable(_response, _body);

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_revocation_request_when_the_token_update_fails
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);
            var jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(jti, A<Guid>._))
                .Throws(new InvalidOperationException("database unavailable"));

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(_client, CreateSignedToken(signingKey, OwnerClientId, jti));
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_answers_503_in_the_oauth_error_format() =>
            AssertTemporarilyUnavailable(_response, _body);
    }

    /// <summary>
    /// The UPDATE reached the store and the connection then dropped before the result came back.
    /// The caller gets 503 and a description that says "could not be confirmed"; deliberately, no
    /// assertion here claims the token is revoked or still live (DMS-1327 D-13.3).
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_whose_update_commits_but_the_response_is_lost
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;
        private bool _updateReachedTheStore;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);
            var jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(jti, A<Guid>._))
                .Invokes(() => _updateReachedTheStore = true)
                .Throws(new TimeoutException("the connection dropped while reading the result"));

            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(_client, CreateSignedToken(signingKey, OwnerClientId, jti));
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_had_issued_the_update() => _updateReachedTheStore.Should().BeTrue();

        [Test]
        public void It_answers_503_without_claiming_an_outcome() =>
            AssertTemporarilyUnavailable(_response, _body);
    }

    /// <summary>
    /// A programming fault inside the manager is not an outage: it reaches the global exception
    /// handler as a 500 rather than being relabelled 503 or swallowed into 200 (DMS-1327 D-13.1,
    /// Q-04), and the route's marker makes that 500 an OAuth <c>server_error</c> (D-17).
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_when_the_ownership_comparison_faults
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync())
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(new FaultingOwnershipTokenManager(_tokenRepository, _secretHasher));
            _client = CreateClientWithCredentials(_factory, OwnerClientId, TestClientSecret);
            _response = await PostRevocation(
                _client,
                CreateSignedToken(signingKey, OwnerClientId, Guid.NewGuid())
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_answers_500() => _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public async Task It_answers_server_error_in_the_oauth_format()
        {
            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            JsonObject body = await ReadOAuthError(_response);
            body["error"]!.GetValue<string>().Should().Be("server_error");
            body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The revocation request could not be processed.");
        }

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    private const int RealHasherIterations = 1000;
    private const string RealHasherSecret = "SECRET-CALLER-SENTINEL-http-real-hasher";

    private static ClientSecretHasher CreateRealHasher(int iterations, ILogger<ClientSecretHasher> logger) =>
        new(
            logger,
            Options.Create(new OpenIddictIdentityOptions { ClientSecretHashingIterations = iterations })
        );

    /// <summary>Registers an approved application whose stored secret is a real hash of <see cref="RealHasherSecret"/>.</summary>
    private static async Task RegisterClientWithRealHash(
        IOpenIddictTokenRepository tokenRepository,
        string clientId
    )
    {
        string storedHash = await CreateRealHasher(
                RealHasherIterations,
                NullLogger<ClientSecretHasher>.Instance
            )
            .HashSecretAsync(RealHasherSecret);
        A.CallTo(() => tokenRepository.GetApplicationByClientIdAsync(clientId))
            .Returns(
                new ApplicationInfo
                {
                    ClientId = clientId,
                    ClientSecret = storedHash,
                    IsApproved = true,
                }
            );
    }

    /// <summary>
    /// DMS-1327 P2.1 correction, through the HTTP pipeline with the real, registered hasher type.
    /// Its lenient verification answers false for any failure, which made a verification that
    /// could not run look like wrong credentials (401). Revocation now uses the hasher's
    /// failure-preserving path, so the same failure is an operational 503, nothing is revoked, and
    /// the hasher logs nothing about the failure. The zero iteration count is a deterministic
    /// stand-in for a hashing failure; production startup validation rejects it.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_when_the_real_hasher_cannot_verify
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly ILogger<ClientSecretHasher> _hasherLogger = A.Fake<ILogger<ClientSecretHasher>>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        private static string AllHasherLogText(ILogger<ClientSecretHasher> logger) =>
            string.Join(
                "\n",
                Fake.GetCalls(logger)
                    .Where(call => call.Method.Name == nameof(ILogger.Log))
                    .SelectMany(call =>
                        new[] { call.Arguments[2]?.ToString(), (call.Arguments[3] as Exception)?.ToString() }
                    )
            );

        [SetUp]
        public async Task Setup()
        {
            await RegisterClientWithRealHash(_tokenRepository, OwnerClientId);

            _factory = CreateFactory(
                CreateTokenManager(_tokenRepository, CreateRealHasher(0, _hasherLogger))
            );
            _client = CreateClientWithCredentials(_factory, OwnerClientId, RealHasherSecret);
            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_answers_503_in_the_oauth_error_format() =>
            AssertTemporarilyUnavailable(_response, _body);

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_has_the_hasher_log_no_warning_and_no_exception() =>
            Fake.GetCalls(_hasherLogger)
                .Where(call => call.Method.Name == nameof(ILogger.Log))
                .Should()
                .NotContain(call =>
                    call.GetArgument<LogLevel>(0) >= LogLevel.Warning || call.Arguments[3] != null
                );

        [Test]
        public void It_keeps_the_presented_secret_out_of_the_hasher_log() =>
            AllHasherLogText(_hasherLogger).Should().NotContain(RealHasherSecret);
    }

    /// <summary>
    /// A real generated hash missing its last decoded byte. Through the HTTP pipeline this is an
    /// operational 503: no key is loaded, nothing is revoked, and neither the secret nor the stored
    /// value reaches the hasher's log.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_when_the_stored_hash_is_truncated
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly ILogger<ClientSecretHasher> _hasherLogger = A.Fake<ILogger<ClientSecretHasher>>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;
        private string _truncatedHash = null!;

        [SetUp]
        public async Task Setup()
        {
            string storedHash = await CreateRealHasher(
                    RealHasherIterations,
                    NullLogger<ClientSecretHasher>.Instance
                )
                .HashSecretAsync(RealHasherSecret);
            _truncatedHash = Convert.ToBase64String(Convert.FromBase64String(storedHash)[..^1]);
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Returns(
                    new ApplicationInfo
                    {
                        ClientId = OwnerClientId,
                        ClientSecret = _truncatedHash,
                        IsApproved = true,
                    }
                );

            _factory = CreateFactory(
                CreateTokenManager(_tokenRepository, CreateRealHasher(RealHasherIterations, _hasherLogger))
            );
            _client = CreateClientWithCredentials(_factory, OwnerClientId, RealHasherSecret);
            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_answers_503_in_the_oauth_error_format() =>
            AssertTemporarilyUnavailable(_response, _body);

        [Test]
        public void It_does_not_load_verification_keys() =>
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync()).MustNotHaveHappened();

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();

        [Test]
        public void It_keeps_the_hasher_log_free_of_warnings_exceptions_and_secrets()
        {
            var calls = Fake.GetCalls(_hasherLogger)
                .Where(call => call.Method.Name == nameof(ILogger.Log))
                .ToList();
            calls
                .Should()
                .NotContain(call =>
                    call.GetArgument<LogLevel>(0) >= LogLevel.Warning || call.Arguments[3] != null
                );
            string text = string.Join("\n", calls.Select(call => call.Arguments[2]?.ToString()));
            text.Should().NotContain(RealHasherSecret).And.NotContain(_truncatedHash);
        }
    }

    /// <summary>A genuine mismatch checked by the real hasher remains an authentication failure.</summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_wrong_secret_checked_by_the_real_hasher
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            await RegisterClientWithRealHash(_tokenRepository, OwnerClientId);

            _factory = CreateFactory(
                CreateTokenManager(
                    _tokenRepository,
                    CreateRealHasher(RealHasherIterations, NullLogger<ClientSecretHasher>.Instance)
                )
            );
            _client = CreateClientWithCredentials(_factory, OwnerClientId, "not-the-secret");
            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await ReadOAuthError(_response);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_reports_invalid_client() =>
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");

        [Test]
        public void It_sends_a_basic_challenge() =>
            _response.Headers.WwwAuthenticate.Should().ContainSingle(header => header.Scheme == "Basic");

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }
}

public class IdentityProviderErrorParsingTests
{
    [TestFixture]
    public class Given_a_provider_payload_with_an_error_description_but_no_error_field
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() =>
                    _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.Unreachable(
                            """{ "error_description": "Realm does not exist." }"""
                        )
                    )
                );

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient(_ => new TokenRequest.Validator());
                    collection.AddTransient(_ => _tokenManager);
                });
            });
            _client = _factory.CreateClient();

            var requestContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            ]);
            _response = await _client.PostAsync("/connect/token", requestContent);
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_502() => _response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        [Test]
        public void It_returns_the_fixed_fallback_message_instead_of_the_partial_payload() =>
            _body["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The identity provider returned an unexpected response.");

        [Test]
        public void It_does_not_leak_the_partial_error_description() =>
            _content.Should().NotContain("Realm does not exist");
    }
}

/// <summary>
/// Guards against any ASP.NET Core authorization requirement leaking onto <c>/connect/revoke</c>'s
/// sibling endpoints, which must stay anonymous at the framework level — <c>/connect/token</c>
/// especially, since it is where a client gets its first credential-backed response. The existing
/// tests for those routes never install an authentication scheme, so they would not notice. These
/// install the harness's test authentication, present no credentials, and assert the responses are
/// never 401. Non-fixture container; the runnable fixture is the nested <c>Given_…</c> class.
/// </summary>
public class TokenEndpointAnonymityTests
{
    [TestFixture]
    public class Given_an_unauthenticated_request_to_the_sibling_token_endpoints
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _registerResponse = null!;
        private HttpResponseMessage _tokenResponse = null!;
        private HttpResponseMessage _introspectResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection => collection.AddTestAuthentication());
            });

            // No Authorization header and no X-Test-Scope, so the harness authenticates nobody.
            _client = _factory.CreateClient();

            _registerResponse = await _client.PostAsync(
                "/connect/register",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
            );
            _tokenResponse = await _client.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
            );
            _introspectResponse = await _client.PostAsync(
                "/connect/introspect",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_does_not_require_authentication_for_register() =>
            _registerResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        [Test]
        public void It_does_not_require_authentication_for_token() =>
            _tokenResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        [Test]
        public void It_does_not_require_authentication_for_introspect() =>
            _introspectResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// DMS-1327 P2.3: the <c>/connect/revoke</c> request contract — the D-03 precedence, the strict
/// D-04 Basic decoding, the D-01 OAuth error bodies and the D-17 exception format — through the
/// real HTTP pipeline. The manager is a recorder: these fixtures are about what the endpoint
/// decides before and after it, and every rejection asserts the manager was never called.
/// <c>RevocationOwnershipTests</c> covers the real manager behind the same endpoint. Non-fixture
/// container; the runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class RevocationRequestContractTests
{
    private const string ClientId = "contract-client";
    private const string ClientSecret = "contract-secret";
    private const string Token = "SECRET-TOKEN-SENTINEL-contract";

    /// <summary>Planted in caller input that is rejected, to prove it never reaches a log.</summary>
    private const string Sentinel = "SECRET-REVOKE-SENTINEL";

    private const string Challenge = "Basic realm=\"EdFi.DmsConfigurationService\"";
    private const string DuplicateDescription = "A request parameter or header was included more than once.";
    private const string MixedDescription = "Only one client authentication mechanism may be used.";
    private const string MissingTokenDescription = "The token parameter is missing.";
    private const string AuthenticationRequiredDescription = "Client authentication is required.";
    private const string InvalidCredentialsDescription = "Invalid client or Invalid client credentials";

    private static readonly string _validBasic = BasicHeader($"{ClientId}:{ClientSecret}");

    private static string BasicHeader(string decoded) => BasicHeader(Encoding.UTF8.GetBytes(decoded));

    private static string BasicHeader(byte[] decoded) => $"Basic {Convert.ToBase64String(decoded)}";

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));

    private static (string, string)[] WithFormCredentials(params (string, string)[] fields) =>
        [("client_id", ClientId), ("client_secret", ClientSecret), .. fields];

    /// <summary>Records every request the endpoint hands over and answers with a configured outcome.</summary>
    internal sealed class RecordingRevocationManager(Func<TokenRevocationResult> respond)
        : ITokenRevocationManager
    {
        public List<TokenRevocationRequest> Requests { get; } = [];

        public Task<TokenRevocationResult> RevokeTokenAsync(
            TokenRevocationRequest request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(respond());
        }
    }

    /// <summary>
    /// Arranges a host with a recording manager and a capturing log provider, sends the request
    /// the fixture builds, and keeps the response. A fixture overrides <see cref="Respond"/> to
    /// choose the manager's outcome, or <see cref="RegisterManager"/> for the Keycloak shape.
    /// </summary>
    public abstract class RevocationRequestFixture
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        private protected RecordingRevocationManager Manager { get; private set; } = null!;
        private protected RevocationLogCapture Logs { get; private set; } = null!;
        protected HttpResponseMessage Response { get; private set; } = null!;
        protected string Content { get; private set; } = null!;

        protected virtual TokenRevocationResult Respond() => new TokenRevocationResult.Completed();

        protected virtual bool RegisterManager => true;

        protected abstract HttpContent? Body { get; }

        protected virtual IEnumerable<string> AuthorizationValues => [];

        /// <summary>
        /// Sends the request through <c>TestServer.SendAsync</c> instead of <see cref="HttpClient"/>,
        /// which can rewrite whitespace in the Authorization value; the handler then sees exactly
        /// the bytes the fixture wrote.
        /// </summary>
        protected virtual bool SendRawRequest => false;

        [SetUp]
        public async Task Setup()
        {
            Manager = new RecordingRevocationManager(Respond);
            Logs = new RevocationLogCapture();
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddSingleton<ILoggerProvider>(Logs);
                    collection.RemoveAll<ITokenRevocationManager>();
                    if (RegisterManager)
                    {
                        collection.AddSingleton<ITokenRevocationManager>(Manager);
                    }
                });
            });
            _client = _factory.CreateClient();

            string[] authorization = [.. AuthorizationValues];
            if (SendRawRequest)
            {
                await SendRawAsync(authorization);
                return;
            }

            using HttpRequestMessage request = new(HttpMethod.Post, "/connect/revoke") { Content = Body };
            if (authorization.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization).Should().BeTrue();
            }

            Response = await _client.SendAsync(request);
            Content = await Response.Content.ReadAsStringAsync();
        }

        private async Task SendRawAsync(string[] authorization)
        {
            HttpContent? body = Body;
            byte[] payload = body is null ? [] : await body.ReadAsByteArrayAsync();
            string? contentType = body?.Headers.ContentType?.ToString();

            HttpContext context = await _factory.Server.SendAsync(raw =>
            {
                raw.Request.Method = HttpMethods.Post;
                raw.Request.Path = "/connect/revoke";
                raw.Request.ContentType = contentType;
                raw.Request.ContentLength = payload.Length;
                raw.Request.Body = new MemoryStream(payload);
                if (authorization.Length > 0)
                {
                    raw.Request.Headers.Authorization = new StringValues(authorization);
                }
            });

            Content = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode)
            {
                Content = new StringContent(Content),
            };
            Response.Content.Headers.ContentType = context.Response.ContentType is { } responseType
                ? System.Net.Http.Headers.MediaTypeHeaderValue.Parse(responseType)
                : null;
            foreach ((string name, StringValues values) in context.Response.Headers)
            {
                Response.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        [TearDown]
        public void TearDown()
        {
            Response?.Dispose();
            _client?.Dispose();
            _factory?.Dispose();
            Logs?.Dispose();
        }

        protected void AssertOAuthError(HttpStatusCode status, string error, string description)
        {
            Response.StatusCode.Should().Be(status, Content);
            Response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            JsonObject body = JsonNode.Parse(Content)!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be(error);
            body["error_description"]!.GetValue<string>().Should().Be(description);
        }

        protected void AssertChallenge() =>
            Response
                .Headers.GetValues("WWW-Authenticate")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(Challenge);

        protected void AssertNoChallenge() => Response.Headers.WwwAuthenticate.Should().BeEmpty();

        protected void AssertManagerNotCalled() => Manager.Requests.Should().BeEmpty();

        protected void AssertNotLogged(params string[] values)
        {
            string captured = Logs.AllCapturedText();
            foreach (string value in values)
            {
                captured.Should().NotContain(value);
            }
        }
    }

    // ----- D-03 row 1: no form content type -----

    public static IEnumerable<TestFixtureData> NonFormBodies()
    {
        yield return new TestFixtureData(
            new Func<HttpContent?>(() =>
                new StringContent($"{{\"token\":\"{Token}\"}}", Encoding.UTF8, "application/json")
            )
        ).SetArgDisplayNames("a JSON body");
        yield return new TestFixtureData(new Func<HttpContent?>(() => null)).SetArgDisplayNames("no body");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(NonFormBodies))]
    public class Given_a_revocation_request_without_a_form_body(Func<HttpContent?> body)
        : RevocationRequestFixture
    {
        protected override HttpContent? Body => body();

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_invalid_request() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "The request body must be application/x-www-form-urlencoded."
            );

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();
    }

    // ----- D-03 row 2 / D-17: malformed form, through the exception handler -----

    /// <summary>
    /// <c>multipart/form-data</c> without a boundary makes <c>ReadFormAsync</c> throw
    /// <see cref="InvalidDataException"/>. The handler selects the OAuth writer from the route's
    /// marker, read off <c>IExceptionHandlerFeature.Endpoint</c> (A-05).
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_malformed_form_payload : RevocationRequestFixture
    {
        protected override HttpContent? Body
        {
            get
            {
                StringContent content = new($"token={Sentinel}");
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "multipart/form-data"
                );
                return content;
            }
        }

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_invalid_request_in_the_oauth_format() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "The request form payload is malformed."
            );

        [Test]
        public void It_does_not_leak_the_framework_parsing_message() =>
            Content.Should().NotContain("boundary");

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_is_not_logged_as_a_failed_request() =>
            Logs
                .Records.Should()
                .NotContain(record => record.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id);
    }

    // ----- D-03 row 3: duplicated parameters or Authorization header -----

    public static IEnumerable<TestFixtureData> DuplicatedInputs()
    {
        yield return new TestFixtureData(
            WithFormCredentials(("token", Token), ("token", Sentinel)),
            Array.Empty<string>()
        ).SetArgDisplayNames("token");
        yield return new TestFixtureData(
            WithFormCredentials(
                ("token", Token),
                ("token_type_hint", "access_token"),
                ("token_type_hint", Sentinel)
            ),
            Array.Empty<string>()
        ).SetArgDisplayNames("token_type_hint");
        // One copy is the valid client id: duplicates are rejected, never resolved by picking one.
        yield return new TestFixtureData(
            new[]
            {
                ("token", Token),
                ("client_id", ClientId),
                ("client_id", Sentinel),
                ("client_secret", ClientSecret),
            },
            Array.Empty<string>()
        ).SetArgDisplayNames("client_id");
        yield return new TestFixtureData(
            new[]
            {
                ("token", Token),
                ("client_id", ClientId),
                ("client_secret", ClientSecret),
                ("client_secret", Sentinel),
            },
            Array.Empty<string>()
        ).SetArgDisplayNames("client_secret");
        yield return new TestFixtureData(
            new[] { ("token", Token) },
            new[] { _validBasic, BasicHeader($"{Sentinel}:{ClientSecret}") }
        ).SetArgDisplayNames("Authorization (two valid Basic values)");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(DuplicatedInputs))]
    public class Given_a_revocation_request_with_a_duplicated_input(
        (string, string)[] fields,
        string[] authorization
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(fields);

        protected override IEnumerable<string> AuthorizationValues => authorization;

        [Test]
        public void It_answers_invalid_request() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", DuplicateDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_does_not_log_the_duplicated_values() => AssertNotLogged(Sentinel, Token);
    }

    // ----- D-03 row 4: mixed mechanisms, decided on form key presence -----

    public static IEnumerable<TestFixtureData> MixedMechanisms()
    {
        yield return new TestFixtureData(
            _validBasic,
            new[] { ("token", Token), ("client_id", "") }
        ).SetArgDisplayNames("valid Basic and an empty form client_id");
        yield return new TestFixtureData(
            _validBasic,
            new[] { ("token", Token), ("client_secret", "x") }
        ).SetArgDisplayNames("valid Basic and a form client_secret only");
        yield return new TestFixtureData(
            $"Basic {Sentinel}",
            new[] { ("token", Token), ("client_secret", "x") }
        ).SetArgDisplayNames("malformed Basic and a form client_secret");
        yield return new TestFixtureData(
            _validBasic,
            WithFormCredentials(("token", Token))
        ).SetArgDisplayNames("valid Basic and valid form credentials");
        yield return new TestFixtureData(_validBasic, new[] { ("client_id", ClientId) }).SetArgDisplayNames(
            "valid Basic, form credentials and no token"
        );
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(MixedMechanisms))]
    public class Given_a_revocation_request_mixing_authentication_mechanisms(
        string authorization,
        (string, string)[] fields
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(fields);

        protected override IEnumerable<string> AuthorizationValues => [authorization];

        [Test]
        public void It_answers_invalid_request() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", MixedDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_does_not_log_the_header() => AssertNotLogged(Sentinel);
    }

    // ----- D-03 row 5: missing or empty token, ahead of every credential check -----

    public static IEnumerable<TestFixtureData> MissingTokens()
    {
        yield return new TestFixtureData(
            new[] { _validBasic },
            Array.Empty<(string, string)>()
        ).SetArgDisplayNames("valid Basic and no token");
        yield return new TestFixtureData(new[] { _validBasic }, new[] { ("token", "") }).SetArgDisplayNames(
            "valid Basic and an empty token"
        );
        yield return new TestFixtureData(Array.Empty<string>(), WithFormCredentials()).SetArgDisplayNames(
            "valid form credentials and no token"
        );
        yield return new TestFixtureData(
            Array.Empty<string>(),
            Array.Empty<(string, string)>()
        ).SetArgDisplayNames("no credentials and no token");
        yield return new TestFixtureData(
            new[] { $"Basic {Sentinel}" },
            Array.Empty<(string, string)>()
        ).SetArgDisplayNames("malformed Basic and no token");
        yield return new TestFixtureData(
            Array.Empty<string>(),
            new[] { ("client_id", ClientId) }
        ).SetArgDisplayNames("a form client_id only and no token");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(MissingTokens))]
    public class Given_a_revocation_request_without_a_token(string[] authorization, (string, string)[] fields)
        : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(fields);

        protected override IEnumerable<string> AuthorizationValues => authorization;

        [Test]
        public void It_answers_invalid_request() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", MissingTokenDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();
    }

    // ----- D-03 row 6 / D-04: malformed Basic credentials -----

    public static IEnumerable<TestFixtureData> MalformedBasicValues()
    {
        string sentinelPair = $"{ClientId}:{Sentinel}";
        string validBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sentinelPair));
        yield return new TestFixtureData("Basic", "base64").SetArgDisplayNames(
            "a Basic scheme with no value"
        );
        yield return new TestFixtureData("Basic ", "base64").SetArgDisplayNames(
            "a Basic scheme and an empty value"
        );
        yield return new TestFixtureData(
            $"Basic {validBase64[..4]} {validBase64[4..]}",
            "base64"
        ).SetArgDisplayNames("embedded whitespace");
        yield return new TestFixtureData("Basic YWJj-2Rl", "base64").SetArgDisplayNames(
            "the URL-safe '-' character"
        );
        yield return new TestFixtureData("Basic YWJj_2Rl", "base64").SetArgDisplayNames(
            "the URL-safe '_' character"
        );
        yield return new TestFixtureData("Basic YWJjZA", "base64").SetArgDisplayNames(
            "a length that is not a multiple of four"
        );
        yield return new TestFixtureData("Basic YQ=a", "base64").SetArgDisplayNames("padding before the end");
        yield return new TestFixtureData("Basic Y===", "base64").SetArgDisplayNames(
            "three padding characters"
        );
        yield return new TestFixtureData(
            BasicHeader([0xFF, (byte)':', (byte)'a']),
            "utf8"
        ).SetArgDisplayNames("bytes that are not UTF-8");
        yield return new TestFixtureData(BasicHeader(Sentinel), "separator").SetArgDisplayNames("no colon");
        yield return new TestFixtureData(BasicHeader($":{Sentinel}"), "empty").SetArgDisplayNames(
            "an empty client id"
        );
        yield return new TestFixtureData(BasicHeader($"{Sentinel}:"), "empty").SetArgDisplayNames(
            "an empty secret"
        );
        yield return new TestFixtureData(
            BasicHeader($"{ClientId}:{Sentinel}%"),
            "form-decoding"
        ).SetArgDisplayNames("a lone '%'");
        yield return new TestFixtureData(
            BasicHeader($"{ClientId}:{Sentinel}%2"),
            "form-decoding"
        ).SetArgDisplayNames("'%' with one hex digit");
        yield return new TestFixtureData(
            BasicHeader($"{ClientId}:%GG{Sentinel}"),
            "form-decoding"
        ).SetArgDisplayNames("'%' with non-hex digits");
        yield return new TestFixtureData(
            BasicHeader($"cli%ent:{Sentinel}"),
            "form-decoding"
        ).SetArgDisplayNames("a '%' in the client id");
        yield return new TestFixtureData(
            BasicHeader($"{ClientId}:{Sentinel}%FF"),
            "form-decoding"
        ).SetArgDisplayNames("%FF after percent decoding");
        yield return new TestFixtureData(
            BasicHeader($"{ClientId}:{Sentinel}%C3"),
            "form-decoding"
        ).SetArgDisplayNames("a lone %C3 lead byte");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(MalformedBasicValues))]
    public class Given_a_revocation_request_with_malformed_basic_credentials(
        string authorization,
        string stage
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [authorization];

        [Test]
        public void It_answers_401_invalid_client() =>
            AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_sends_the_basic_challenge() => AssertChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_logs_only_the_fixed_stage_name() =>
            Logs
                .Records.Should()
                .ContainSingle(record =>
                    record.Message == $"Revocation Basic credentials were malformed at the {stage} stage"
                );

        [Test]
        public void It_does_not_log_the_credentials() =>
            AssertNotLogged(
                Sentinel,
                Token,
                authorization.Length > "Basic ".Length ? authorization["Basic ".Length..] : Sentinel
            );
    }

    // ----- D-03 "Basic attempted" / D-04 stage 0: an invalid separator after the scheme -----

    /// <summary>
    /// A tab (alone, or followed by a space) after <c>Basic</c> is not the RFC 7235 <c>1*SP</c>
    /// separator, but the scheme token is still <c>Basic</c>: the request attempted Basic and is
    /// malformed, so it must never fall back to the form rules.
    /// </summary>
    public static IEnumerable<TestFixtureData> InvalidBasicSeparators()
    {
        string sentinelBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{Sentinel}"));
        yield return new TestFixtureData($"Basic\t{sentinelBase64}", sentinelBase64).SetArgDisplayNames(
            "a tab"
        );
        yield return new TestFixtureData($"Basic\t {sentinelBase64}", sentinelBase64).SetArgDisplayNames(
            "a tab then a space"
        );
        yield return new TestFixtureData($"basic\t{sentinelBase64}", sentinelBase64).SetArgDisplayNames(
            "a lower-case scheme and a tab"
        );
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(InvalidBasicSeparators))]
    public class Given_a_raw_revocation_request_with_an_invalid_separator_after_basic(
        string authorization,
        string credentials
    ) : RevocationRequestFixture
    {
        protected override bool SendRawRequest => true;

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [authorization];

        [Test]
        public void It_answers_401_invalid_client() =>
            AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_sends_the_basic_challenge() => AssertChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_logs_only_the_fixed_stage_name() =>
            Logs
                .Records.Should()
                .ContainSingle(record =>
                    record.Message
                    == "Revocation Basic credentials were malformed at the scheme-separator stage"
                );

        [Test]
        public void It_does_not_log_the_credentials() => AssertNotLogged(Sentinel, Token, credentials);
    }

    public static IEnumerable<TestFixtureData> InvalidBasicSeparatorsWithFormCredentialKeys()
    {
        string sentinelBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{Sentinel}"));
        yield return new TestFixtureData(
            $"Basic\t{sentinelBase64}",
            sentinelBase64,
            WithFormCredentials(("token", Token))
        ).SetArgDisplayNames("a tab and valid form credentials");
        yield return new TestFixtureData(
            $"Basic\t {sentinelBase64}",
            sentinelBase64,
            WithFormCredentials(("token", Token))
        ).SetArgDisplayNames("a tab then a space and valid form credentials");
        yield return new TestFixtureData(
            $"Basic\t{sentinelBase64}",
            sentinelBase64,
            new[] { ("token", Token), ("client_id", ClientId) }
        ).SetArgDisplayNames("a tab and a form client_id only");
        yield return new TestFixtureData(
            $"Basic\t {sentinelBase64}",
            sentinelBase64,
            new[] { ("token", Token), ("client_secret", ClientSecret) }
        ).SetArgDisplayNames("a tab then a space and a form client_secret only");
    }

    [TestFixtureSource(
        typeof(RevocationRequestContractTests),
        nameof(InvalidBasicSeparatorsWithFormCredentialKeys)
    )]
    public class Given_a_raw_revocation_request_with_an_invalid_separator_after_basic_and_form_credentials(
        string authorization,
        string credentials,
        (string, string)[] fields
    ) : RevocationRequestFixture
    {
        protected override bool SendRawRequest => true;

        protected override HttpContent? Body => Form(fields);

        protected override IEnumerable<string> AuthorizationValues => [authorization];

        [Test]
        public void It_answers_invalid_request_for_mixed_mechanisms() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", MixedDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_does_not_log_the_credentials() =>
            AssertNotLogged(Sentinel, Token, ClientSecret, credentials);
    }

    /// <summary>
    /// Several spaces after <c>Basic</c> are valid <c>1*SP</c> and still authenticate, sent raw so
    /// the client cannot collapse them first.
    /// </summary>
    [TestFixture]
    public class Given_a_raw_revocation_request_with_several_spaces_after_basic : RevocationRequestFixture
    {
        protected override bool SendRawRequest => true;

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues =>
            [$"Basic   {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"))}"];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_passes_the_credentials_to_the_manager() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenRevocationRequest(ClientId, ClientSecret, Token, TokenTypeHint.None));
    }

    /// <summary>
    /// A scheme token that merely starts with <c>Basic</c> is a different scheme, not a Basic
    /// attempt: it is ignored like any non-Basic header and the form credentials authenticate.
    /// </summary>
    [TestFixture]
    public class Given_a_raw_revocation_request_with_a_longer_scheme_token_and_form_credentials
        : RevocationRequestFixture
    {
        protected override bool SendRawRequest => true;

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        protected override IEnumerable<string> AuthorizationValues => [$"Basicx {Sentinel}"];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_authenticates_with_the_form_credentials_only() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenRevocationRequest(ClientId, ClientSecret, Token, TokenTypeHint.None));
    }

    // ----- D-04: well-formed Basic credentials decode exactly -----

    public static IEnumerable<TestFixtureData> WellFormedBasicValues()
    {
        yield return new TestFixtureData("Basic", "client:secret", "client", "secret").SetArgDisplayNames(
            "plain values"
        );
        yield return new TestFixtureData("basic", "client:secret", "client", "secret").SetArgDisplayNames(
            "a lower-case scheme"
        );
        yield return new TestFixtureData(
            "Basic",
            "my+client:se+cret",
            "my client",
            "se cret"
        ).SetArgDisplayNames("a space encoded as '+'");
        yield return new TestFixtureData(
            "Basic",
            "my%20client:se%20cret",
            "my client",
            "se cret"
        ).SetArgDisplayNames("a space encoded as %20");
        yield return new TestFixtureData("Basic", "client:a%2Bb", "client", "a+b").SetArgDisplayNames(
            "a literal '+' encoded as %2B"
        );
        yield return new TestFixtureData("Basic", "client:a:b", "client", "a:b").SetArgDisplayNames(
            "a colon inside the secret"
        );
        yield return new TestFixtureData("Basic", "cli%3Aent:secret", "cli:ent", "secret").SetArgDisplayNames(
            "an encoded colon in the client id"
        );
        yield return new TestFixtureData("Basic", "client:sécret", "client", "sécret").SetArgDisplayNames(
            "raw UTF-8"
        );
        yield return new TestFixtureData(
            "Basic",
            "client:s%C3%A9cret",
            "client",
            "sécret"
        ).SetArgDisplayNames("percent-encoded UTF-8");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(WellFormedBasicValues))]
    public class Given_a_revocation_request_with_well_formed_basic_credentials(
        string scheme,
        string decoded,
        string expectedClientId,
        string expectedSecret
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues =>
            [$"{scheme} {Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded))}"];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_passes_the_form_decoded_credentials_to_the_manager() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenRevocationRequest(expectedClientId, expectedSecret, Token, TokenTypeHint.None));
    }

    // ----- D-03 row 7: Basic not attempted and form credentials incomplete -----

    public static IEnumerable<TestFixtureData> IncompleteFormCredentials()
    {
        yield return new TestFixtureData(
            Array.Empty<string>(),
            new[] { ("token", Token) }
        ).SetArgDisplayNames("no credentials");
        yield return new TestFixtureData(
            new[] { $"Bearer {Sentinel}" },
            new[] { ("token", Token) }
        ).SetArgDisplayNames("a Bearer header and no form credentials");
        yield return new TestFixtureData(
            Array.Empty<string>(),
            new[] { ("token", Token), ("client_id", ClientId) }
        ).SetArgDisplayNames("a form client_id only");
        yield return new TestFixtureData(
            Array.Empty<string>(),
            new[] { ("token", Token), ("client_secret", Sentinel) }
        ).SetArgDisplayNames("a form client_secret only");
        yield return new TestFixtureData(
            Array.Empty<string>(),
            new[] { ("token", Token), ("client_id", ClientId), ("client_secret", "") }
        ).SetArgDisplayNames("an empty form client_secret");
        yield return new TestFixtureData(
            Array.Empty<string>(),
            new[] { ("token", Token), ("client_id", ""), ("client_secret", Sentinel) }
        ).SetArgDisplayNames("an empty form client_id");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(IncompleteFormCredentials))]
    public class Given_a_revocation_request_with_incomplete_form_credentials(
        string[] authorization,
        (string, string)[] fields
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(fields);

        protected override IEnumerable<string> AuthorizationValues => authorization;

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_client", AuthenticationRequiredDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();
    }

    /// <summary>
    /// A Bearer header is not client authentication for this endpoint (Q-03): it is ignored, and
    /// the form credentials authenticate on their own.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_bearer_header_and_form_credentials
        : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        protected override IEnumerable<string> AuthorizationValues => [$"Bearer {Sentinel}"];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_authenticates_with_the_form_credentials_only() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenRevocationRequest(ClientId, ClientSecret, Token, TokenTypeHint.None));
    }

    // ----- D-03 row 8: the manager rejects the client -----

    [TestFixture]
    public class Given_a_basic_authenticated_revocation_the_manager_rejects : RevocationRequestFixture
    {
        protected override TokenRevocationResult Respond() => new TokenRevocationResult.InvalidClient();

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_401_invalid_client() =>
            AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_sends_the_basic_challenge() => AssertChallenge();
    }

    [TestFixture]
    public class Given_a_form_authenticated_revocation_the_manager_rejects : RevocationRequestFixture
    {
        protected override TokenRevocationResult Respond() => new TokenRevocationResult.InvalidClient();

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();
    }

    // ----- D-03 rows 9–12: the remaining manager outcomes -----

    public static IEnumerable<TestFixtureData> ManagerOutcomes()
    {
        yield return new TestFixtureData(
            new TokenRevocationResult.UnsupportedTokenType(),
            HttpStatusCode.BadRequest,
            "unsupported_token_type",
            "The token type is not supported by the identity provider."
        );
        yield return new TestFixtureData(
            new TokenRevocationResult.InvalidRequest(),
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The identity provider rejected the revocation request."
        );
        yield return new TestFixtureData(
            new TokenRevocationResult.TemporarilyUnavailable("contract-boundary"),
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "Token revocation could not be confirmed. Retry the request and confirm the token's state through the provider's validation path."
        );
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(ManagerOutcomes))]
    public class Given_a_revocation_the_manager_does_not_complete(
        TokenRevocationResult outcome,
        HttpStatusCode status,
        string error,
        string description
    ) : RevocationRequestFixture
    {
        protected override TokenRevocationResult Respond() => outcome;

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_the_mapped_oauth_error() => AssertOAuthError(status, error, description);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();
    }

    [TestFixture]
    public class Given_a_revocation_the_manager_completes : RevocationRequestFixture
    {
        protected override HttpContent? Body => Form(("token", Token), ("token_type_hint", "access_token"));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_answers_with_an_empty_body() => Content.Should().BeEmpty();

        [Test]
        public void It_passes_the_token_and_hint_to_the_manager() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenRevocationRequest(ClientId, ClientSecret, Token, TokenTypeHint.AccessToken));
    }

    // ----- D-06: token_type_hint -----

    public static IEnumerable<TestFixtureData> TokenTypeHints()
    {
        yield return new TestFixtureData("access_token", TokenTypeHint.AccessToken);
        yield return new TestFixtureData("refresh_token", TokenTypeHint.RefreshToken);
        yield return new TestFixtureData("bogus", TokenTypeHint.None);
        yield return new TestFixtureData("ACCESS_TOKEN", TokenTypeHint.None);
        yield return new TestFixtureData("", TokenTypeHint.None);
        yield return new TestFixtureData(null, TokenTypeHint.None);
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(TokenTypeHints))]
    public class Given_a_revocation_request_with_a_token_type_hint(string? hint, TokenTypeHint expected)
        : RevocationRequestFixture
    {
        protected override HttpContent? Body =>
            hint is null ? Form(("token", Token)) : Form(("token", Token), ("token_type_hint", hint));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_passes_the_parsed_hint() =>
            Manager.Requests.Should().ContainSingle().Which.TokenTypeHint.Should().Be(expected);
    }

    // ----- Keycloak mode until P3.2 -----

    /// <summary>
    /// With no revocation manager registered (Keycloak mode until DMS-1327 P3.2), a request that
    /// passes the endpoint's shape and credential-presence checks is still answered 200 without
    /// revoking anything. P3.2 registers the Keycloak manager; P4.1 makes it required.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_no_registered_manager : RevocationRequestFixture
    {
        protected override bool RegisterManager => false;

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_still_answers_200_until_keycloak_revocation_is_registered() =>
            Response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ----- D-03 row 13 / D-17: a programming fault escaping the handler -----

    /// <summary>
    /// A fault inside the endpoint after the manager was called is a 500 <c>server_error</c> in the
    /// OAuth format, logged once as a failed request with the exception attached, and nothing the
    /// caller sent (token, secret, Authorization value) reaches any log field, the attached
    /// exception or its inner chain (D-15 rules 2 and 3).
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_whose_manager_faults : RevocationRequestFixture
    {
        private const string FaultText = "The revocation manager faulted.";

        protected override TokenRevocationResult Respond() => throw new InvalidOperationException(FaultText);

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_server_error_in_the_oauth_format() =>
            AssertOAuthError(
                HttpStatusCode.InternalServerError,
                "server_error",
                "The revocation request could not be processed."
            );

        [Test]
        public void It_does_not_leak_the_exception_text() => Content.Should().NotContain(FaultText);

        [Test]
        public void It_sends_the_trace_id_header() =>
            Response.Headers.GetValues("TraceId").Should().ContainSingle().Which.Should().NotBeEmpty();

        [Test]
        public void It_logs_one_failed_request_with_the_exception_attached() =>
            Logs
                .Records.Should()
                .ContainSingle(record => record.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id)
                .Which.Exception.Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Be(FaultText);

        [Test]
        public void It_does_not_log_the_caller_input() =>
            AssertNotLogged(Token, ClientSecret, _validBasic["Basic ".Length..]);
    }
}

/// <summary>
/// Captures every log record the host writes: category, level, rendered message, every structured
/// state pair, every active scope (through <see cref="ISupportExternalScope"/>), and the attached
/// exception. <see cref="AllCapturedText"/> flattens all of it, including each exception's message,
/// data and string form down the inner chain, so a disclosure assertion cannot miss a field.
/// </summary>
internal sealed class RevocationLogCapture : ILoggerProvider, ISupportExternalScope
{
    public sealed record Record(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        IReadOnlyList<object?> Scopes,
        Exception? Exception
    );

    private readonly System.Collections.Concurrent.ConcurrentQueue<Record> _records = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public IReadOnlyList<Record> Records => [.. _records];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose() { }

    public string AllCapturedText()
    {
        StringBuilder text = new();
        foreach (Record record in _records)
        {
            text.AppendLine($"{record.Category} {record.Level} {record.EventId} {record.Message}");
            AppendPairs(text, record.State);
            foreach (object? scope in record.Scopes)
            {
                text.AppendLine(scope?.ToString());
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    AppendPairs(text, pairs);
                }
            }
            for (
                Exception? exception = record.Exception;
                exception is not null;
                exception = exception.InnerException
            )
            {
                text.AppendLine($"{exception.GetType().FullName} {exception.Message}");
                foreach (System.Collections.DictionaryEntry entry in exception.Data)
                {
                    text.AppendLine($"{entry.Key}={entry.Value}");
                }
                text.AppendLine(exception.ToString());
            }
        }
        return text.ToString();
    }

    private static void AppendPairs(StringBuilder text, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        foreach ((string key, object? value) in pairs)
        {
            text.AppendLine($"{key}={value}");
        }
    }

    private sealed class CapturingLogger(string category, RevocationLogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => capture._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            List<object?> scopes = [];
            capture._scopeProvider.ForEachScope((scope, list) => list.Add(scope), scopes);
            List<KeyValuePair<string, object?>> pairs = state
                is IEnumerable<KeyValuePair<string, object?>> values
                ? [.. values]
                : [];
            capture._records.Enqueue(
                new Record(category, logLevel, eventId, formatter(state, exception), pairs, scopes, exception)
            );
        }
    }
}
