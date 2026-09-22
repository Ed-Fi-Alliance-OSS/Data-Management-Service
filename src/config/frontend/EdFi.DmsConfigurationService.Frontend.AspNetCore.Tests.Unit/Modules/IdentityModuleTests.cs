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

        [Test]
        public void It_returns_the_ed_fi_bad_request_contract() =>
            AssertBadRequestContract(_response, _content, "The token parameter is missing.");
    }

    /// <summary>
    /// This <c>ITokenManager</c> fake implements no <c>ITokenRevocationManager</c>, the same as
    /// <c>KeycloakTokenManager</c> in production — so there is no local way to check client
    /// credentials, and the handler's no-op branch answers before authentication is even
    /// considered. A bearer token presented here (there being no client credentials at all) is
    /// simply irrelevant to the outcome; see RevocationOwnershipTests for the self-contained-mode
    /// credential checks this fake cannot exercise.
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
        public void It_still_returns_200() => _response.StatusCode.Should().Be(HttpStatusCode.OK);
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
    }

    /// <summary>
    /// RFC 6749 §2.3 permits credentials in the request body for clients that cannot use HTTP
    /// Basic auth; RevokeToken mirrors GetClientAccessToken's fallback for this. No other fixture
    /// in this class exercises that path — the rest all authenticate via Basic auth.
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
        /// Authorization header, exercising the fallback RevokeToken shares with
        /// GetClientAccessToken. Used only here, so it lives inside this fixture.
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).Returns(true);

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_jti)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Guards the RFC 7009 §2.1 requirement itself: with no client credentials presented at all,
    /// the caller is rejected before the target token is even looked at.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_no_client_credentials
    {
        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _body = string.Empty;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(CreateTokenManager(_tokenRepository, _secretHasher));
            _client = _factory.CreateClient(); // No Authorization header and no client_id/secret form fields.

            _response = await PostRevocation(_client, "irrelevant-token");
            _body = await _response.Content.ReadAsStringAsync();
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
        public void It_reports_the_oauth_error_code() => _body.Should().Contain("invalid_client");

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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
        private string _body = string.Empty;

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
            _body = await _response.Content.ReadAsStringAsync();
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
        /// The OAuth error code has to survive into the response body. A bare sentence would
        /// fail the structured-provider-error parse in FailureResults and be replaced by its
        /// "unexpected response" fallback, which tells the client nothing about what to fix.
        /// </summary>
        [Test]
        public void It_reports_the_oauth_error_code() =>
            _body.Should().Contain("invalid_client. Invalid client or Invalid client credentials");

        /// <summary>
        /// RFC 6749 §5.2 requires a challenge matching the scheme the client used, and this
        /// caller authenticated through the Authorization header.
        /// </summary>
        [Test]
        public void It_sends_a_basic_challenge() =>
            _response.Headers.WwwAuthenticate.Should().ContainSingle(header => header.Scheme == "Basic");

        [Test]
        public void It_does_not_attempt_revocation() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._)).MustNotHaveHappened();
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
