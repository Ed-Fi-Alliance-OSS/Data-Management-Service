// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

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
                    A<long[]?>.Ignored
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
        content.Should().Contain("Unauthorized");
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
        content.Should().Contain("Forbidden");
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
                "No connection could be made because the target machine actively refused it."
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
    public async Task When_grant_type_is_unsupported()
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

        // Act — passes validation (all fields present) but uses an unsupported grant type.
        var requestContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("client_id", "CSClient1"),
                new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                new KeyValuePair<string, string>("grant_type", "password"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            }
        );
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert — Ed-Fi bad-request contract, not an OAuth { error, error_description } body.
        OAuthErrorResponseAssertions.AssertBadRequest(
            response,
            content,
            "The specified grant type is not supported."
        );
        content.Should().NotContain("error_description");
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
                "No connection could be made because the target machine actively refused it."
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
}

/// <summary>
/// Shared assertion for the Ed-Fi bad-request contract that CMS-generated OAuth/OIDC error branches now
/// return in place of the OAuth <c>{ error, error_description }</c> shape (DMS-1218 INV-16/17/18).
/// </summary>
internal static class OAuthErrorResponseAssertions
{
    public static void AssertBadRequest(HttpResponseMessage response, string content, string expectedDetail)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["detail"]!.GetValue<string>().Should().Be(expectedDetail);
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }
}

[TestFixture]
public class IntrospectEndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Test"));

    [Test]
    public async Task When_the_token_parameter_is_missing()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
        );
        string content = await response.Content.ReadAsStringAsync();

        OAuthErrorResponseAssertions.AssertBadRequest(response, content, "The token parameter is missing.");
        content.Should().NotContain("error_description");
    }

    [Test]
    public async Task When_a_token_is_present_it_preserves_the_protocol_inactive_success()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // An unresolved/opaque token yields the RFC 7662 { active: false } 200 success, unchanged.
        var response = await client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "opaque-token") })
        );
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");
        JsonNode.Parse(content)!["active"]!.GetValue<bool>().Should().BeFalse();
    }
}

[TestFixture]
public class RevokeEndpointTests
{
    private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection => collection.AddTransient(_ => _tokenManager));
        });

    [Test]
    public async Task When_the_token_parameter_is_missing()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
        );
        string content = await response.Content.ReadAsStringAsync();

        OAuthErrorResponseAssertions.AssertBadRequest(response, content, "The token parameter is missing.");
        content.Should().NotContain("error_description");
    }

    [Test]
    public async Task When_a_token_is_present_it_preserves_the_protocol_success()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // RFC 7009 requires 200 OK for revocation regardless of the token; this success is unchanged.
        var response = await client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "opaque-token") })
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
