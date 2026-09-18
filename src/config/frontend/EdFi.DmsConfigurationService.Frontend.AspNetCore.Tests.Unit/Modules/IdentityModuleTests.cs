// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
/// protocol success responses stay untouched. Non-fixture container; the
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
            // Revocation now requires an authenticated caller, so the test auth handler is
            // installed here; the missing-token 400 contract itself is unchanged.
            _factory = CreateFactory(collection =>
            {
                collection.AddTestAuthentication();
                collection.AddTransient(_ => _tokenManager);
            });
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
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

        [Test]
        public void It_returns_the_ed_fi_bad_request_contract() =>
            AssertBadRequestContract(_response, _content, "The token parameter is missing.");
    }

    /// <summary>
    /// Previously asserted that an anonymous caller got 200 OK. Revocation now requires an
    /// authenticated caller — otherwise the endpoint cannot tell whether the caller owns the
    /// token — so the expected outcome is updated in place to 401.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_token_from_an_unauthenticated_caller
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(collection => collection.AddTransient(_ => _tokenManager));
            _client = _factory.CreateClient();
            _response = await _client.PostAsync(
                "/connect/revoke",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "opaque-token") })
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The other half of Task 1's acceptance criterion: an invalid bearer token is rejected the
    /// same way a missing one is. The token here is not a well-formed JWT, so the bearer handler
    /// rejects it while reading the token format, before any signing-key resolution that would
    /// need an OIDC metadata fetch from the configured authority. That keeps the outcome
    /// deterministic and independent of whether the authority is reachable.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_an_invalid_bearer_token
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(collection => collection.AddTransient(_ => _tokenManager));
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-valid-token");
            _response = await _client.PostAsync(
                "/connect/revoke",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "opaque-token") })
            );
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_401() => _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// <c>POST /connect/revoke</c> requires an authenticated caller and only revokes a token whose
/// <c>client_id</c> claim matches the caller's own. A token belonging to another client is a
/// silent no-op that still answers 200 OK, so nothing leaks about whether the token exists or who
/// owns it (RFC 7009). These fixtures drive the real <see cref="OpenIddictTokenManager"/> over HTTP
/// against a faked token repository, so the route's authorization requirement, the caller-claim
/// wiring, the target token's signature verification and the ownership decision are all exercised
/// together. Non-fixture container; the runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class RevocationOwnershipTests
{
    private const string TestIssuer = "https://cms.example.test";
    private const string TestAudience = "ed-fi-cms-tests";
    private const string OwnerClientId = "revoke-owner-client";
    private const string OtherClientId = "revoke-other-client";

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

    private static OpenIddictTokenManager CreateTokenManager(IOpenIddictTokenRepository tokenRepository) =>
        new(
            Options.Create(new OpenIddictIdentityOptions { Authority = TestIssuer, Audience = TestAudience }),
            NullLogger<OpenIddictTokenManager>.Instance,
            A.Fake<IClientSecretHasher>(),
            tokenRepository
        );

    private static WebApplicationFactory<Program> CreateFactory(ITokenManager tokenManager) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTestAuthentication();
                collection.AddTransient(_ => tokenManager);
            });
        });

    /// <summary>
    /// Creates a client authenticated as <paramref name="callerClientId"/>, using the
    /// <c>X-Test-ClientId</c> override so a single run can act as more than one client.
    /// </summary>
    private static HttpClient CreateClientFor(WebApplicationFactory<Program> factory, string callerClientId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeaderName, callerClientId);
        return client;
    }

    private static Task<HttpResponseMessage> PostRevocation(HttpClient client, string token) =>
        client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", token) })
        );

    [TestFixture]
    public class Given_a_revocation_request_for_a_token_the_caller_owns
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
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

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository));
            _client = CreateClientFor(_factory, OwnerClientId);
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Guards the "bare authorization requirement, no named policy" decision in
    /// <c>tasks/plan.md</c>. The caller here is an ordinary client-credentials principal — a
    /// <c>client_id</c> and a non-admin scope, no service-role claim — which is what a token minted
    /// by <c>/connect/token</c> actually carries. If the route were ever tightened to
    /// <c>RequireAuthorization(SecurityConstants.ServicePolicy)</c> or to the admin-scope policy,
    /// this caller would get 403 and this fixture would fail, whereas the other revocation fixtures
    /// would all stay green because their principals happen to satisfy both policies.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_from_a_caller_without_the_service_role
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private Guid _jti;

        /// <summary>
        /// A principal looking like an ordinary client-credentials token from
        /// <c>/connect/token</c>: a <c>client_id</c> and a non-admin scope, but no service-role
        /// claim. Such a caller satisfies neither <c>SecurityConstants.ServicePolicy</c> nor the
        /// admin-scope policy, which is what lets this fixture detect a named policy on the route.
        /// </summary>
        private static HttpClient CreateOrdinaryClientFor(
            WebApplicationFactory<Program> factory,
            string callerClientId
        )
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.ReadOnlyScope.Name);
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeaderName, callerClientId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.OmitRoleClaimHeaderName, "true");
            return client;
        }

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

            _jti = Guid.NewGuid();
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

            _factory = CreateFactory(CreateTokenManager(_tokenRepository));
            _client = CreateOrdinaryClientFor(_factory, OwnerClientId);
            _response = await PostRevocation(_client, CreateSignedToken(signingKey, OwnerClientId, _jti));
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_is_not_rejected_by_a_policy() => _response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_revokes_its_own_token() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_a_revocation_request_for_a_token_owned_by_another_client
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
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

            _factory = CreateFactory(CreateTokenManager(_tokenRepository));

            // The token belongs to OwnerClientId; the caller authenticates as OtherClientId.
            _client = CreateClientFor(_factory, OtherClientId);
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// The ownership check is only meaningful if the target token's signature is verified before
    /// its <c>client_id</c> claim is trusted. This fixture forges a token naming the caller while
    /// embedding another client's <c>jti</c>.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_forged_token_naming_the_caller
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
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

            _factory = CreateFactory(CreateTokenManager(_tokenRepository));
            _client = CreateClientFor(_factory, OwnerClientId);
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_revocation_request_with_a_malformed_token
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(CreateTokenManager(_tokenRepository));
            _client = CreateClientFor(_factory, OwnerClientId);
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
