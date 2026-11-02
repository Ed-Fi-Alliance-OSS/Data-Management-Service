// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
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
              "type": "urn:ed-fi:management-api:bad-gateway",
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

    /// <summary>
    /// The uniqueness precheck is not atomic: a client registered between it and the creation
    /// reaches the provider, which refuses the key. The answer is the precheck's own 400 body, not
    /// the insert endpoints' 409 (DMS-1529 D-5), so the two bodies are compared outright. Fails
    /// against the implementation whose create switch let the variant fall through to the 500
    /// default.
    /// </summary>
    [Test]
    public async Task Given_a_client_whose_key_the_provider_reports_already_registered()
    {
        // Arrange: the precheck sees no client under the key, and the provider refuses the creation.
        var providerConflictRepository = A.Fake<IIdentityProviderRepository>();
        A.CallTo(() => providerConflictRepository.GetAllClientsAsync())
            .Returns(new ClientClientsResult.Success([]));
        A.CallTo(() =>
                providerConflictRepository.CreateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored
                )
            )
            .Returns(new ClientCreateResult.FailureDuplicateClientId());

        // The precheck path, for the body the provider-detected conflict has to match.
        var precheckRepository = A.Fake<IIdentityProviderRepository>();
        A.CallTo(() => precheckRepository.GetAllClientsAsync())
            .Returns(new ClientClientsResult.Success(["CSClient4"]));

        // Act
        using HttpResponseMessage providerConflictResponse = await RegisterAsync(providerConflictRepository);
        using HttpResponseMessage precheckResponse = await RegisterAsync(precheckRepository);
        JsonNode providerConflictBody = JsonNode.Parse(
            await providerConflictResponse.Content.ReadAsStringAsync()
        )!;
        JsonNode precheckBody = JsonNode.Parse(await precheckResponse.Content.ReadAsStringAsync())!;

        // Assert
        providerConflictResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        providerConflictResponse
            .Content.Headers.ContentType?.MediaType.Should()
            .Be("application/problem+json");
        providerConflictBody["validationErrors"]!["ClientId"]![0]!
            .GetValue<string>()
            .Should()
            .Be("Client with the same Client Id already exists. Please provide different Client Id.");

        precheckResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        providerConflictBody["correlationId"] = precheckBody["correlationId"]!.GetValue<string>();
        JsonNode.DeepEquals(providerConflictBody, precheckBody).Should().Be(true);

        A.CallTo(() =>
                providerConflictRepository.CreateClientAsync(
                    "CSClient4",
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored
                )
            )
            .MustHaveHappenedOnceExactly();

        static async Task<HttpResponseMessage> RegisterAsync(IIdentityProviderRepository clientRepository)
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((_) => CreateRegisterRequestValidator());
                        collection.AddTransient((_) => clientRepository);
                    }
                );
            });
            using var client = factory.CreateClient();

            var requestContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("clientid", "CSClient4"),
                new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
                new KeyValuePair<string, string>("displayname", "CSClient4"),
            ]);
            HttpResponseMessage response = await client.PostAsync("/connect/register", requestContent);
            // The body is read before the factory is disposed, so the caller can parse it freely.
            await response.Content.LoadIntoBufferAsync();
            return response;
        }
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
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:security:authorization");
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
              "type": "urn:ed-fi:management-api:bad-gateway",
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

    /// <summary>
    /// Unlike revoke and introspect, the token endpoint accepts a multipart/form-data body, so a
    /// client that posts "form-data" (Postman's default) still gets a token.
    /// </summary>
    [Test]
    public async Task Given_valid_client_credentials_in_a_multipart_body()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((_) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        using var requestContent = new MultipartFormDataContent
        {
            { new StringContent("CSClient1"), "client_id" },
            { new StringContent("test123@Puiu"), "client_secret" },
            { new StringContent("client_credentials"), "grant_type" },
        };
        var response = await client.PostAsync("/connect/token", requestContent);
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("input123token");
        A.CallTo(() =>
                _tokenManager!.GetAccessTokenAsync(
                    A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(credentials =>
                        credentials.Any(pair => pair.Key == "client_id" && pair.Value == "CSClient1")
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
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

        // Assert: the request shape is judged before the credentials, so the empty grant_type is
        // the first finding (DMS-1327 D-03)
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        JsonObject body = JsonNode.Parse(content)!.AsObject();
        body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
        body["error"]!.GetValue<string>().Should().Be("invalid_request");
        body["error_description"]!.GetValue<string>().Should().Be("The grant_type parameter is missing.");
    }

    /// <summary>
    /// The token-limit rejection over the real HTTP pipeline. Driving it through
    /// <c>WebApplicationFactory</c> rather than calling the module directly is what proves the
    /// status, the content type and every member of the body survive
    /// <c>GlobalExceptionHandler</c> and <c>FrameworkErrorResponseMiddleware</c> unreshaped.
    /// </summary>
    [TestFixture(5)]
    [TestFixture(15)]
    public class Given_a_client_that_has_reached_its_token_limit(int configuredLimit)
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
                .Returns(new TokenResult.FailureTokenLimitExceeded(configuredLimit));

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
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
        public void It_uses_the_json_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        [Test]
        public void It_does_not_challenge() => _response.Headers.WwwAuthenticate.Should().BeEmpty();

        [Test]
        public void It_returns_the_temporarily_unavailable_body_naming_the_limit()
        {
            JsonObject body = JsonNode.Parse(_content)!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(
                    $"Too many access tokens have been requested (limit is {configuredLimit}). Access tokens should be reused until they expire."
                );
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
        public void It_uses_the_json_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        [Test]
        public void It_does_not_challenge() => _response.Headers.WwwAuthenticate.Should().BeEmpty();

        [Test]
        public void It_tells_the_caller_to_retry()
        {
            JsonObject body = JsonNode.Parse(_content)!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "The token request could not be completed because of concurrent requests for the same client. Retry the request."
                );
        }
    }
}

/// <summary>
/// HTTP Basic client credentials at <c>/connect/token</c> are parsed in every identity provider mode, and a
/// parsed Basic header wins over form credentials. Non-fixture container; the runnable fixtures are the nested
/// <c>Given_…</c> classes. The identity provider is read while services are registered, so it is set with
/// <c>UseSetting</c>; the unreachable connection string only lets the host boot (the token manager is faked).
/// </summary>
public class TokenEndpointBasicAuthTests
{
    private static AuthenticationHeaderValue Basic(string credentials) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

    private static List<KeyValuePair<string, string>> Form(params (string Key, string Value)[] fields) =>
        [.. fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))];

    private static (string Key, string Value)[] GrantFields =>
        [("grant_type", "client_credentials"), ("scope", "edfi_admin_api/full_access")];

    public abstract class PostedTokenRequest
    {
        private const string UnreachableDatabase =
            "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

        private static ITokenManager CreateTokenManager()
        {
            var tokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(
                    new TokenResult.Success(
                        """{"access_token":"input123token","expires_in":900,"token_type":"bearer"}"""
                    )
                );
            return tokenManager;
        }

        private static WebApplicationFactory<Program> CreateFactory(
            string identityProvider,
            ITokenManager tokenManager
        ) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:IdentityProvider", identityProvider);
                builder.UseSetting("DatabaseSettings:DatabaseConnection", UnreachableDatabase);
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient((_) => tokenManager);
                });
            });

        private static bool Carries(
            IEnumerable<KeyValuePair<string, string>> credentials,
            string clientId,
            string clientSecret
        ) =>
            credentials.Any(pair => pair.Key == "client_id" && pair.Value == clientId)
            && credentials.Any(pair => pair.Key == "client_secret" && pair.Value == clientSecret);

        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        protected ITokenManager TokenManager = null!;
        protected HttpResponseMessage Response = null!;
        private string _body = string.Empty;

        protected abstract string IdentityProvider { get; }
        protected abstract AuthenticationHeaderValue? Authorization { get; }
        protected abstract List<KeyValuePair<string, string>> FormFields { get; }

        [SetUp]
        public async Task Act()
        {
            TokenManager = CreateTokenManager();
            _factory = CreateFactory(IdentityProvider, TokenManager);
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Authorization = Authorization;
            Response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(FormFields));
            _body = await Response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            Response.Dispose();
            _client.Dispose();
            _factory.Dispose();
        }

        [Test]
        public void It_returns_ok() => Response.StatusCode.Should().Be(HttpStatusCode.OK, _body);

        protected List<KeyValuePair<string, string>> ForwardedPairs() =>
            [
                .. Fake.GetCalls(TokenManager)
                    .Where(call => call.Method.Name == nameof(ITokenManager.GetAccessTokenAsync))
                    .SelectMany(call => (IEnumerable<KeyValuePair<string, string>>)call.Arguments[0]!),
            ];

        protected void TokenManagerMustHaveReceived(string clientId, string clientSecret) =>
            A.CallTo(() =>
                    TokenManager.GetAccessTokenAsync(
                        A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(credentials =>
                            Carries(credentials, clientId, clientSecret)
                        )
                    )
                )
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_keycloak_mode_with_a_basic_header_and_form_grant_fields : PostedTokenRequest
    {
        protected override string IdentityProvider => "keycloak";
        protected override AuthenticationHeaderValue? Authorization => Basic("CSClient1:test123@Puiu");
        protected override List<KeyValuePair<string, string>> FormFields => Form(GrantFields);

        [Test]
        public void It_passes_the_basic_credentials_to_the_token_manager() =>
            TokenManagerMustHaveReceived("CSClient1", "test123@Puiu");
    }

    [TestFixture]
    public class Given_keycloak_mode_with_form_only_credentials : PostedTokenRequest
    {
        protected override string IdentityProvider => "keycloak";
        protected override AuthenticationHeaderValue? Authorization => null;

        protected override List<KeyValuePair<string, string>> FormFields =>
            Form([("client_id", "FormClient"), ("client_secret", "form-secret"), .. GrantFields]);

        [Test]
        public void It_passes_the_form_credentials_to_the_token_manager() =>
            TokenManagerMustHaveReceived("FormClient", "form-secret");
    }

    [TestFixture]
    public class Given_keycloak_mode_with_a_basic_header_and_different_form_credentials : PostedTokenRequest
    {
        protected override string IdentityProvider => "keycloak";
        protected override AuthenticationHeaderValue? Authorization => Basic("BasicClient:basic-secret");

        protected override List<KeyValuePair<string, string>> FormFields =>
            Form([("client_id", "FormClient"), ("client_secret", "form-secret"), .. GrantFields]);

        [Test]
        public void It_passes_the_basic_credentials_to_the_token_manager() =>
            TokenManagerMustHaveReceived("BasicClient", "basic-secret");
    }

    [TestFixture]
    public class Given_keycloak_mode_with_a_malformed_basic_header_and_form_credentials : PostedTokenRequest
    {
        protected override string IdentityProvider => "keycloak";
        protected override AuthenticationHeaderValue? Authorization => new("Basic", "not*base64!");

        protected override List<KeyValuePair<string, string>> FormFields =>
            Form([("client_id", "FormClient"), ("client_secret", "form-secret"), .. GrantFields]);

        [Test]
        public void It_passes_the_form_credentials_to_the_token_manager() =>
            TokenManagerMustHaveReceived("FormClient", "form-secret");
    }

    [TestFixture]
    public class Given_self_contained_mode_with_a_basic_header_and_form_grant_fields : PostedTokenRequest
    {
        protected override string IdentityProvider => "self-contained";
        protected override AuthenticationHeaderValue? Authorization => Basic("CSClient1:test123@Puiu");
        protected override List<KeyValuePair<string, string>> FormFields => Form(GrantFields);

        [Test]
        public void It_passes_the_basic_credentials_to_the_token_manager() =>
            TokenManagerMustHaveReceived("CSClient1", "test123@Puiu");
    }
}

/// <summary>
/// An empty <c>scope</c> (omitted or <c>scope=</c>) is not forwarded to the identity provider; a non-empty
/// one is forwarded unchanged. Non-fixture container; the runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class TokenEndpointScopeForwardingTests
{
    private static List<KeyValuePair<string, string>> Form(params (string Key, string Value)[] fields) =>
        [
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            .. fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)),
        ];

    public abstract class ScopeRequest : TokenEndpointBasicAuthTests.PostedTokenRequest
    {
        protected override AuthenticationHeaderValue? Authorization => null;

        [Test]
        public void It_forwards_client_id_client_secret_and_grant_type_in_order() =>
            ForwardedPairs()
                .Select(pair => pair.Key)
                .Where(key => key != "scope")
                .Should()
                .Equal("client_id", "client_secret", "grant_type");
    }

    public abstract class EmptyScopeRequest : ScopeRequest
    {
        [Test]
        public void It_does_not_forward_a_scope_pair() =>
            ForwardedPairs().Should().NotContain(pair => pair.Key == "scope");
    }

    public abstract class FullAccessScopeRequest : ScopeRequest
    {
        protected override List<KeyValuePair<string, string>> FormFields =>
            Form(("scope", "edfi_admin_api/full_access"));

        [Test]
        public void It_forwards_the_scope_unchanged() =>
            ForwardedPairs()
                .Should()
                .ContainSingle(pair => pair.Key == "scope")
                .Which.Value.Should()
                .Be("edfi_admin_api/full_access");
    }

    [TestFixture]
    public class Given_keycloak_mode_without_a_scope_field : EmptyScopeRequest
    {
        protected override string IdentityProvider => "keycloak";
        protected override List<KeyValuePair<string, string>> FormFields => Form();
    }

    [TestFixture]
    public class Given_keycloak_mode_with_an_empty_scope : EmptyScopeRequest
    {
        protected override string IdentityProvider => "keycloak";
        protected override List<KeyValuePair<string, string>> FormFields => Form(("scope", ""));
    }

    [TestFixture]
    public class Given_keycloak_mode_with_a_scope : FullAccessScopeRequest
    {
        protected override string IdentityProvider => "keycloak";
    }

    [TestFixture]
    public class Given_self_contained_mode_without_a_scope_field : EmptyScopeRequest
    {
        protected override string IdentityProvider => "self-contained";
        protected override List<KeyValuePair<string, string>> FormFields => Form();
    }

    [TestFixture]
    public class Given_self_contained_mode_with_an_empty_scope : EmptyScopeRequest
    {
        protected override string IdentityProvider => "self-contained";
        protected override List<KeyValuePair<string, string>> FormFields => Form(("scope", ""));
    }

    [TestFixture]
    public class Given_self_contained_mode_with_a_scope : FullAccessScopeRequest
    {
        protected override string IdentityProvider => "self-contained";
    }
}

/// <summary>
/// The token response passes through the <c>scope</c> member the token manager returns, unchanged, under
/// both providers, and omits it when the manager returns none. <c>access_token</c>, <c>expires_in</c> and
/// <c>token_type</c> are unchanged, and members outside the response contract are still dropped (DMS-1492).
/// Non-fixture container; the runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class TokenEndpointScopeResponseTests
{
    public abstract class SuccessfulTokenRequest
    {
        private const string UnreachableDatabase =
            "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        protected HttpResponseMessage Response = null!;
        protected string Body = string.Empty;

        protected abstract string IdentityProvider { get; }
        protected abstract string ManagerBody { get; }
        protected abstract string ExpectedBody { get; }

        [SetUp]
        public async Task Act()
        {
            var tokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(new TokenResult.Success(ManagerBody));

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:IdentityProvider", IdentityProvider);
                builder.UseSetting("DatabaseSettings:DatabaseConnection", UnreachableDatabase);
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient((_) => tokenManager);
                });
            });
            _client = _factory.CreateClient();
            Response = await _client.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("client_id", "CSClient1"),
                    new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("scope", "A B"),
                ])
            );
            Body = await Response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            Response.Dispose();
            _client.Dispose();
            _factory.Dispose();
        }

        [Test]
        public void It_returns_ok() => Response.StatusCode.Should().Be(HttpStatusCode.OK, Body);

        [Test]
        public void It_returns_exactly_the_response_members() =>
            JsonNode.DeepEquals(JsonNode.Parse(Body), JsonNode.Parse(ExpectedBody)).Should().BeTrue(Body);
    }

    public abstract class ManagerBodyCarryingAScope : SuccessfulTokenRequest
    {
        protected abstract string ExpectedScope { get; }

        [Test]
        public void It_returns_the_scope_unchanged() =>
            JsonNode.Parse(Body)!["scope"]!.GetValue<string>().Should().Be(ExpectedScope);
    }

    public abstract class ManagerBodyWithoutAScope : SuccessfulTokenRequest
    {
        [Test]
        public void It_omits_the_scope_member() =>
            JsonNode.Parse(Body)!.AsObject().ContainsKey("scope").Should().BeFalse(Body);
    }

    [TestFixture]
    public class Given_self_contained_mode_and_a_manager_body_carrying_a_scope : ManagerBodyCarryingAScope
    {
        protected override string IdentityProvider => "self-contained";
        protected override string ExpectedScope => "A B";
        protected override string ManagerBody =>
            """{"access_token":"input123token","expires_in":900,"refresh_expires_in":0,"token_type":"bearer","scope":"A B"}""";
        protected override string ExpectedBody =>
            """{"access_token":"input123token","expires_in":900,"token_type":"bearer","scope":"A B"}""";
    }

    [TestFixture]
    public class Given_self_contained_mode_and_a_manager_body_without_a_scope : ManagerBodyWithoutAScope
    {
        protected override string IdentityProvider => "self-contained";
        protected override string ManagerBody =>
            """{"access_token":"input123token","expires_in":900,"refresh_expires_in":0,"token_type":"bearer"}""";
        protected override string ExpectedBody =>
            """{"access_token":"input123token","expires_in":900,"token_type":"bearer"}""";
    }

    // The self-contained manager reports an empty granted set, from a client with no registered scopes
    // that omitted scope, as a null scope; the response leaves it out rather than sending "scope":"".
    [TestFixture]
    public class Given_self_contained_mode_and_a_manager_body_with_a_null_scope : ManagerBodyWithoutAScope
    {
        protected override string IdentityProvider => "self-contained";
        protected override string ManagerBody =>
            """{"access_token":"input123token","expires_in":900,"refresh_expires_in":0,"token_type":"Bearer","scope":null}""";
        protected override string ExpectedBody =>
            """{"access_token":"input123token","expires_in":900,"token_type":"Bearer"}""";
    }

    [TestFixture]
    public class Given_keycloak_mode_and_a_provider_body_carrying_a_scope : ManagerBodyCarryingAScope
    {
        protected override string IdentityProvider => "keycloak";
        protected override string ExpectedScope => "edfi_admin_api/full_access profile email";
        protected override string ManagerBody =>
            """{"access_token":"kc-token","expires_in":300,"refresh_expires_in":0,"token_type":"Bearer","not-before-policy":0,"scope":"edfi_admin_api/full_access profile email"}""";
        protected override string ExpectedBody =>
            """{"access_token":"kc-token","expires_in":300,"token_type":"Bearer","scope":"edfi_admin_api/full_access profile email"}""";
    }

    [TestFixture]
    public class Given_keycloak_mode_and_a_provider_body_without_a_scope : ManagerBodyWithoutAScope
    {
        protected override string IdentityProvider => "keycloak";
        protected override string ManagerBody =>
            """{"access_token":"kc-token","expires_in":300,"refresh_expires_in":0,"token_type":"Bearer","not-before-policy":0}""";
        protected override string ExpectedBody =>
            """{"access_token":"kc-token","expires_in":300,"token_type":"Bearer"}""";
    }
}

/// <summary>
/// End-to-end verification that the OAuth endpoints, <c>/connect/token</c>, <c>/connect/revoke</c> and
/// <c>/connect/introspect</c>, answer every error in the OAuth <c>{ error, error_description }</c>
/// format (DMS-1327 D-01, DMS-1491), including a malformed form and a faulting manager, pinned here and
/// in <c>RevocationRequestContractTests</c>. Non-fixture container; the runnable fixtures are the
/// nested <c>Given_…</c> classes.
/// </summary>
public class OAuthEndpointErrorTests
{
    // Provider failure texts for the 502 fixtures, declared here so the fixture attributes can name them.
    private const string UnreachableMessage =
        "No connection could be made because SENTINEL_PROVIDER_TEXT refused it.";
    private const string NotFoundBody =
        """{"error":"invalid_grant","error_description":"Realm does not exist SENTINEL_PROVIDER_TEXT"}""";
    private const string ForbiddenBody =
        """{"error":"invalid_request","error_description":"HTTPS required SENTINEL_PROVIDER_TEXT"}""";
    private const string GenericBody =
        """{"error":"invalid_request","error_description":"Missing form parameter SENTINEL_PROVIDER_TEXT"}""";

    private static WebApplicationFactory<Program> CreateFactory(
        Action<IServiceCollection>? configureServices,
        bool keycloak = false
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (keycloak)
            {
                // The identity provider is read while services are registered, so it is set with
                // UseSetting; the unreachable connection string only lets the host boot, since the
                // token manager is faked.
                builder.UseSetting("AppSettings:IdentityProvider", "keycloak");
                builder.UseSetting(
                    "DatabaseSettings:DatabaseConnection",
                    "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1"
                );
            }
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
    }

    /// <summary>
    /// A token request answered in the RFC 6749 §5.2 format (DMS-1491): the body's only root members are
    /// <c>error</c> and <c>error_description</c>, and <c>WWW-Authenticate</c> is present only when the
    /// caller attempted Basic client authentication and failed it.
    /// </summary>
    public abstract class TokenErrorRequest
    {
        protected const string BasicChallenge = "Basic realm=\"EdFi.DmsConfigurationService\"";

        protected readonly ITokenManager TokenManager = A.Fake<ITokenManager>();
        private protected RevocationLogCapture Logs { get; } = new();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        protected HttpResponseMessage Response = null!;
        protected string Content = null!;

        protected virtual void ArrangeManager() { }

        protected virtual bool KeycloakMode => false;

        protected abstract HttpRequestMessage BuildRequest();

        [SetUp]
        public async Task Setup()
        {
            ArrangeManager();
            _factory = CreateFactory(
                collection =>
                {
                    collection.AddTransient(_ => TokenManager);
                    collection.AddSingleton<ILoggerProvider>(Logs);
                },
                KeycloakMode
            );
            _client = _factory.CreateClient();
            Response = await _client.SendAsync(BuildRequest());
            Content = await Response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            Response?.Dispose();
            _client?.Dispose();
            _factory?.Dispose();
        }

        protected static HttpRequestMessage Post(
            IEnumerable<KeyValuePair<string, string>> form,
            string? authorization = null
        )
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
            {
                Content = new FormUrlEncodedContent(form),
            };
            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }
            return request;
        }

        protected void AssertStatus(HttpStatusCode expected) => Response.StatusCode.Should().Be(expected);

        protected void AssertOAuthError(string error, string description)
        {
            Response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            JsonObject body = JsonNode.Parse(Content)!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be(error);
            body["error_description"]!.GetValue<string>().Should().Be(description);
        }

        protected void AssertBasicChallenge() =>
            Response.Headers.GetValues("WWW-Authenticate").Should().Equal(BasicChallenge);

        protected void AssertNoChallenge() => Response.Headers.WwwAuthenticate.Should().BeEmpty();

        protected void AssertManagerNotCalled() =>
            A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_a_token_request_with_an_unsupported_grant_type : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([
                new("client_id", "CSClient1"),
                new("client_secret", "test123@Puiu"),
                new("grant_type", "password"),
                new("scope", "edfi_admin_api/full_access"),
            ]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_unsupported_grant_type_body() =>
            AssertOAuthError("unsupported_grant_type", "The specified grant type is not supported.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    /// <summary>
    /// The grant type is judged before the credentials: a request with an unsupported grant type and
    /// no credentials gets the grant type finding, not <c>invalid_client</c>.
    /// </summary>
    [TestFixture]
    public class Given_a_token_request_with_an_unsupported_grant_type_and_no_credentials : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() => Post([new("grant_type", "password")]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_unsupported_grant_type_body() =>
            AssertOAuthError("unsupported_grant_type", "The specified grant type is not supported.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_without_a_grant_type_but_with_valid_credentials : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([new("client_id", "CSClient1"), new("client_secret", "test123@Puiu")]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body() =>
            AssertOAuthError("invalid_request", "The grant_type parameter is missing.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    /// <summary>
    /// The request shape is judged before the credentials (DMS-1327 D-03): a Basic attempt that cannot
    /// be decoded still gets the grant_type finding, and no challenge, when grant_type is absent.
    /// </summary>
    [TestFixture]
    public class Given_a_token_request_without_a_grant_type_and_with_an_undecodable_basic_header
        : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post(Array.Empty<KeyValuePair<string, string>>(), "Basic !!!not-base64!!!");

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body() =>
            AssertOAuthError("invalid_request", "The grant_type parameter is missing.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    [TestFixture]
    public class Given_a_json_token_request : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            new(HttpMethod.Post, "/connect/token")
            {
                Content = new StringContent(
                    """{"client_id":"CSClient1","client_secret":"test123@Puiu","grant_type":"client_credentials"}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body() =>
            AssertOAuthError(
                "invalid_request",
                "The request body must be application/x-www-form-urlencoded."
            );

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    /// <summary>
    /// RFC 6749 §5.2 makes a parameter sent more than once <c>invalid_request</c>. Every copy is
    /// rejected, never resolved by picking one, including an empty copy beside a valid one.
    /// </summary>
    [TestFixture("grant_type", "client_credentials")]
    [TestFixture("grant_type", "")]
    [TestFixture("client_id", "CSClient1")]
    [TestFixture("client_secret", "test123@Puiu")]
    [TestFixture("scope", "edfi_admin_api/full_access")]
    public class Given_a_token_request_with_a_repeated_parameter(string parameter, string secondValue)
        : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([
                new("client_id", "CSClient1"),
                new("client_secret", "test123@Puiu"),
                new("grant_type", "client_credentials"),
                new("scope", "edfi_admin_api/full_access"),
                new(parameter, secondValue),
            ]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body() =>
            AssertOAuthError("invalid_request", "A request parameter or header was included more than once.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_with_two_authorization_headers : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest()
        {
            string basic =
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("CSClient1:test123@Puiu"));
            HttpRequestMessage request = Post([new("grant_type", "client_credentials")]);
            request.Headers.TryAddWithoutValidation("Authorization", [basic, basic]);
            return request;
        }

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body() =>
            AssertOAuthError("invalid_request", "A request parameter or header was included more than once.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_without_credentials_or_basic_header : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([new("grant_type", "client_credentials")]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_client_body() =>
            AssertOAuthError("invalid_client", "Client authentication is required.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_with_an_empty_client_secret : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([
                new("client_id", "CSClient1"),
                new("client_secret", ""),
                new("grant_type", "client_credentials"),
            ]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_client_body() =>
            AssertOAuthError("invalid_client", "Client authentication is required.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    /// <summary>
    /// A client id or secret of only whitespace is as missing as an empty one: it is answered before
    /// the token manager, which would report it as a failure the service cannot classify.
    /// </summary>
    [TestFixture("client_id")]
    [TestFixture("client_secret")]
    public class Given_a_token_request_with_a_whitespace_credential(string parameter) : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([
                new("client_id", parameter == "client_id" ? " " : "CSClient1"),
                new("client_secret", parameter == "client_secret" ? "\t" : "test123@Puiu"),
                new("grant_type", "client_credentials"),
            ]);

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_client_body() =>
            AssertOAuthError("invalid_client", "Client authentication is required.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_with_whitespace_basic_credentials : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post(
                [new("grant_type", "client_credentials")],
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(" : "))
            );

        [Test]
        public void It_returns_401() => AssertStatus(HttpStatusCode.Unauthorized);

        [Test]
        public void It_returns_the_oauth_invalid_client_body() =>
            AssertOAuthError("invalid_client", "Client authentication is required.");

        [Test]
        public void It_challenges_for_basic_authentication() => AssertBasicChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_with_an_undecodable_basic_header_and_no_form_credentials
        : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([new("grant_type", "client_credentials")], "Basic !!!not-base64!!!");

        [Test]
        public void It_returns_401() => AssertStatus(HttpStatusCode.Unauthorized);

        [Test]
        public void It_returns_the_oauth_invalid_client_body() =>
            AssertOAuthError("invalid_client", "Client authentication is required.");

        [Test]
        public void It_challenges_for_basic_authentication() => AssertBasicChallenge();

        [Test]
        public void It_does_not_call_the_token_manager() => AssertManagerNotCalled();
    }

    [TestFixture]
    public class Given_a_token_request_with_a_bearer_header_and_no_form_credentials : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest() =>
            Post([new("grant_type", "client_credentials")], "Bearer x");

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_client_body() =>
            AssertOAuthError("invalid_client", "Client authentication is required.");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    [TestFixture]
    public class Given_a_token_request_with_a_malformed_form_payload : TokenErrorRequest
    {
        protected override HttpRequestMessage BuildRequest()
        {
            // multipart/form-data without a boundary makes form reading throw InvalidDataException.
            var content = new StringContent("client_id=x");
            content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
            return new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = content };
        }

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body_with_the_fixed_form_message() =>
            AssertOAuthError("invalid_request", "The request form payload is malformed.");

        [Test]
        public void It_does_not_leak_the_framework_parsing_message() =>
            Content.Should().NotContain("boundary");
    }

    /// <summary>
    /// A fault escaping the token endpoint is answered in the OAuth format with a fixed description
    /// (DMS-1491), as on <c>/connect/revoke</c> (DMS-1327 D-17).
    /// </summary>
    [TestFixture]
    public class Given_a_token_request_whose_token_manager_faults : TokenErrorRequest
    {
        private const string Sentinel = "SENTINEL_TOKEN_MANAGER_FAULT_must_not_leak";

        protected override void ArrangeManager() =>
            A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
                .Throws(new InvalidOperationException(Sentinel));

        protected override HttpRequestMessage BuildRequest() =>
            Post([
                new("client_id", "CSClient1"),
                new("client_secret", "test123@Puiu"),
                new("grant_type", "client_credentials"),
                new("scope", "edfi_admin_api/full_access"),
            ]);

        [Test]
        public void It_returns_500() => AssertStatus(HttpStatusCode.InternalServerError);

        [Test]
        public void It_returns_the_oauth_server_error_body() =>
            AssertOAuthError("server_error", "The token request could not be processed.");

        [Test]
        public void It_does_not_leak_the_exception_text() => Content.Should().NotContain(Sentinel);
    }

    /// <summary>
    /// A token request that reaches the token manager, with the client credentials sent either in an
    /// HTTP Basic header or in the form. Which one decides whether a client authentication failure is
    /// answered with 401 and a challenge or with 400.
    /// </summary>
    public abstract class ManagerOutcomeRequest(bool basic) : TokenErrorRequest
    {
        protected bool Basic { get; } = basic;

        protected void Returns(TokenResult result) =>
            A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
                .Returns(result);

        protected void ReturnsProviderFailure(IdentityProviderError error) =>
            Returns(new TokenResult.FailureIdentityProvider(error));

        protected override bool KeycloakMode => true;

        protected override HttpRequestMessage BuildRequest() =>
            Basic
                ? Post(
                    [new("grant_type", "client_credentials")],
                    "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("CSClient1:test123@Puiu"))
                )
                : Post([
                    new("client_id", "CSClient1"),
                    new("client_secret", "test123@Puiu"),
                    new("grant_type", "client_credentials"),
                    new("scope", "edfi_admin_api/full_access"),
                ]);

        protected void AssertClientAuthenticationAnswer(string error, string description)
        {
            AssertStatus(Basic ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest);
            AssertOAuthError(error, description);
            if (Basic)
            {
                AssertBasicChallenge();
            }
            else
            {
                AssertNoChallenge();
            }
        }
    }

    /// <summary>
    /// A failed client authentication is <c>invalid_client</c> whatever code the token manager
    /// reports: RFC 6749 §5.2 defines no other code for it, and a second code would tell the caller
    /// whether the client id it sent exists.
    /// </summary>
    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_a_service_owned_authentication_failure(bool basic) : ManagerOutcomeRequest(basic)
    {
        protected override bool KeycloakMode => false;

        protected override void ArrangeManager() =>
            Returns(
                new TokenResult.FailureAuthentication(
                    "unauthorized_client",
                    "Service-owned failure description"
                )
            );

        // The description differs from the handler's fixed one, so this fails if the manager's is dropped.
        [Test]
        public void It_answers_invalid_client_with_the_manager_description() =>
            AssertClientAuthenticationAnswer("invalid_client", "Service-owned failure description");
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_the_token_manager_reports_an_unknown_failure(bool basic) : ManagerOutcomeRequest(basic)
    {
        private const string Sentinel = "SENTINEL_UNKNOWN_FAILURE_must_not_leak";

        protected override bool KeycloakMode => false;

        protected override void ArrangeManager() => Returns(new TokenResult.FailureUnknown(Sentinel));

        [Test]
        public void It_returns_500() => AssertStatus(HttpStatusCode.InternalServerError);

        [Test]
        public void It_returns_the_server_error_body() =>
            AssertOAuthError("server_error", "The token request could not be processed.");

        [Test]
        public void It_does_not_leak_the_failure_text() => Content.Should().NotContain(Sentinel);

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    /// <summary>
    /// The token limit and a lock timeout are not client authentication failures, so a caller that
    /// attempted Basic gets no challenge with them. The form-credential answers are pinned in
    /// <c>TokenEndpointTests</c>.
    /// </summary>
    [TestFixture]
    public class Given_the_token_manager_reports_the_token_limit_to_a_basic_caller : ManagerOutcomeRequest
    {
        public Given_the_token_manager_reports_the_token_limit_to_a_basic_caller()
            : base(true) { }

        protected override bool KeycloakMode => false;

        protected override void ArrangeManager() => Returns(new TokenResult.FailureTokenLimitExceeded(3));

        [Test]
        public void It_returns_429() => AssertStatus(HttpStatusCode.TooManyRequests);

        [Test]
        public void It_answers_temporarily_unavailable_with_the_limit() =>
            AssertOAuthError(
                "temporarily_unavailable",
                "Too many access tokens have been requested (limit is 3). Access tokens should be reused until they expire."
            );

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    [TestFixture]
    public class Given_the_token_manager_reports_a_lock_timeout_to_a_basic_caller : ManagerOutcomeRequest
    {
        public Given_the_token_manager_reports_a_lock_timeout_to_a_basic_caller()
            : base(true) { }

        protected override bool KeycloakMode => false;

        protected override void ArrangeManager() => Returns(new TokenResult.FailureLockTimeout());

        [Test]
        public void It_returns_409() => AssertStatus(HttpStatusCode.Conflict);

        [Test]
        public void It_answers_temporarily_unavailable() =>
            AssertOAuthError(
                "temporarily_unavailable",
                "The token request could not be completed because of concurrent requests for the same client. Retry the request."
            );

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    /// <summary>
    /// A Keycloak 401 or the provider's invalid_client answer, in both the Basic and the form shape,
    /// is always <c>invalid_client</c> with the fixed description: Keycloak words some rejections
    /// differently, so its text could tell the caller that the client id exists.
    /// </summary>
    [TestFixture(true, "Unauthorized")]
    [TestFixture(false, "Unauthorized")]
    [TestFixture(true, "InvalidClient")]
    [TestFixture(false, "InvalidClient")]
    public class Given_the_identity_provider_rejects_the_client_credentials(bool basic, string kind)
        : ManagerOutcomeRequest(basic)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                kind == "Unauthorized"
                    ? new IdentityProviderError.Unauthorized(
                        """{"error":"invalid_client","error_description":"Invalid client secret"}"""
                    )
                    : new IdentityProviderError.InvalidClient(
                        """{"error":"invalid_client","error_description":"Invalid client secret"}"""
                    )
            );

        [Test]
        public void It_answers_invalid_client_with_the_fixed_description() =>
            AssertClientAuthenticationAnswer(
                "invalid_client",
                "Invalid client or Invalid client credentials"
            );

        [Test]
        public void It_does_not_echo_the_provider_description() =>
            Content.Should().NotContain("Invalid client secret");
    }

    /// <summary>
    /// Keycloak answers a public client with any secret by saying it is a public client, which tells
    /// the caller the client id exists; the caller gets the same answer as for an unknown client.
    /// </summary>
    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_the_identity_provider_rejects_a_public_client(bool basic)
        : ManagerOutcomeRequest(basic)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.Unauthorized(
                    """{"error":"unauthorized_client","error_description":"Public client not allowed to retrieve service account"}"""
                )
            );

        [Test]
        public void It_answers_as_for_an_unknown_client() =>
            AssertClientAuthenticationAnswer(
                "invalid_client",
                "Invalid client or Invalid client credentials"
            );

        [Test]
        public void It_does_not_echo_the_provider_description() =>
            Content.Should().NotContain("Public client");
    }

    /// <summary>
    /// Keycloak answers a wrong secret with a 401 carrying <c>unauthorized_client</c>; the caller still
    /// gets <c>invalid_client</c>.
    /// </summary>
    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_the_identity_provider_rejects_the_client_credentials_with_another_defined_code(
        bool basic
    ) : ManagerOutcomeRequest(basic)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.Unauthorized(
                    """{"error":"unauthorized_client","error_description":"Invalid client or Invalid client credentials"}"""
                )
            );

        [Test]
        public void It_answers_invalid_client_with_the_fixed_description() =>
            AssertClientAuthenticationAnswer(
                "invalid_client",
                "Invalid client or Invalid client credentials"
            );
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_the_identity_provider_rejects_the_client_credentials_without_a_structured_body(
        bool basic
    ) : ManagerOutcomeRequest(basic)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(new IdentityProviderError.Unauthorized("Insufficient Permissions"));

        [Test]
        public void It_answers_with_the_fixed_invalid_client_description() =>
            AssertClientAuthenticationAnswer(
                "invalid_client",
                "Invalid client or Invalid client credentials"
            );

        [Test]
        public void It_does_not_echo_the_provider_text() => Content.Should().NotContain("Insufficient");
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_the_identity_provider_rejects_the_client_credentials_with_an_undefined_code(bool basic)
        : ManagerOutcomeRequest(basic)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.Unauthorized(
                    """{"error":"access_denied","error_description":"SENTINEL_PROVIDER_DESCRIPTION"}"""
                )
            );

        [Test]
        public void It_replaces_the_provider_code_and_description_with_the_fixed_ones() =>
            AssertClientAuthenticationAnswer(
                "invalid_client",
                "Invalid client or Invalid client credentials"
            );

        [Test]
        public void It_does_not_echo_the_provider_text() =>
            Content.Should().NotContain("access_denied").And.NotContain("SENTINEL_PROVIDER_DESCRIPTION");
    }

    [TestFixture]
    public class Given_the_identity_provider_matches_error_codes_case_sensitively : ManagerOutcomeRequest
    {
        public Given_the_identity_provider_matches_error_codes_case_sensitively()
            : base(false) { }

        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.InvalidScope(
                    """{"error":"Invalid_Scope","error_description":"SENTINEL_CASE"}"""
                )
            );

        [Test]
        public void It_replaces_the_description_of_a_code_that_differs_only_in_case() =>
            AssertOAuthError(
                "invalid_scope",
                "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
            );

        [Test]
        public void It_does_not_echo_the_provider_description() =>
            Content.Should().NotContain("SENTINEL_CASE");
    }

    [TestFixture]
    public class Given_the_identity_provider_rejects_the_scope : ManagerOutcomeRequest
    {
        public Given_the_identity_provider_rejects_the_scope()
            : base(true) { }

        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.InvalidScope(
                    """{"error":"invalid_scope","error_description":"Invalid scopes: edfi_admin_api/readonly_access"}"""
                )
            );

        [Test]
        public void It_returns_400() => AssertStatus(HttpStatusCode.BadRequest);

        [Test]
        public void It_answers_with_the_provider_description() =>
            AssertOAuthError("invalid_scope", "Invalid scopes: edfi_admin_api/readonly_access");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    /// <summary>
    /// Keycloak echoes the requested scope into its <c>invalid_scope</c> description, so the description
    /// can carry characters RFC 6749 §5.2 does not allow in <c>error_description</c>: anything
    /// outside printable ASCII, a double quote or a backslash. Such a description is replaced.
    /// </summary>
    [TestFixture("Invalid scopes: a\"b")]
    [TestFixture("Invalid scopes: a\\b")]
    [TestFixture("Invalid scopes: caf\u00e9")]
    [TestFixture("Invalid scopes: a\tb")]
    [TestFixture("Invalid scopes: a\nb")]
    public class Given_the_identity_provider_rejects_the_scope_with_a_description_outside_the_oauth_character_set(
        string description
    ) : ManagerOutcomeRequest(false)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.InvalidScope(
                    new JsonObject
                    {
                        ["error"] = "invalid_scope",
                        ["error_description"] = description,
                    }.ToJsonString()
                )
            );

        [Test]
        public void It_answers_with_the_fixed_invalid_scope_description() =>
            AssertOAuthError(
                "invalid_scope",
                "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
            );
    }

    [TestFixture]
    public class Given_the_identity_provider_rejects_the_scope_with_an_undefined_code : ManagerOutcomeRequest
    {
        public Given_the_identity_provider_rejects_the_scope_with_an_undefined_code()
            : base(false) { }

        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.InvalidScope(
                    """{"error":"access_denied","error_description":"SENTINEL_PROVIDER_DESCRIPTION"}"""
                )
            );

        [Test]
        public void It_answers_with_the_fixed_invalid_scope_description() =>
            AssertOAuthError(
                "invalid_scope",
                "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
            );

        [Test]
        public void It_does_not_echo_the_provider_text() =>
            Content.Should().NotContain("SENTINEL_PROVIDER_DESCRIPTION");
    }

    /// <summary>
    /// Every provider failure that is not the caller's to fix, whatever its text, is the same 502 with
    /// a fixed description: provider text, structured or not, never reaches the body. A provider 403
    /// is one of them, since the one Keycloak's token endpoint sends reports the provider's own
    /// configuration, such as a realm that requires HTTPS. The log records the failure kind and, from a
    /// structured body with a code RFC 6749 §5.2 defines, its code and description, never the body.
    /// </summary>
    [TestFixture("Unreachable", UnreachableMessage, false)]
    [TestFixture("Unreachable", UnreachableMessage, true)]
    [TestFixture("NotFound", NotFoundBody, false)]
    [TestFixture("NotFound", NotFoundBody, true)]
    [TestFixture("Forbidden", ForbiddenBody, false)]
    [TestFixture("Forbidden", ForbiddenBody, true)]
    [TestFixture("Generic", GenericBody, false)]
    [TestFixture("Generic", GenericBody, true)]
    public class Given_the_identity_provider_fails_in_a_way_the_caller_cannot_fix(
        string kind,
        string message,
        bool basic
    ) : ManagerOutcomeRequest(basic)
    {
        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                kind switch
                {
                    "Unreachable" => new IdentityProviderError.Unreachable(message),
                    "NotFound" => new IdentityProviderError.NotFound(message),
                    "Forbidden" => new IdentityProviderError.Forbidden(message),
                    _ => new IdentityProviderError(message),
                }
            );

        [Test]
        public void It_logs_the_failure_kind_at_warning() =>
            Logs
                .Records.Should()
                .Contain(record =>
                    record.Level == LogLevel.Warning
                    && record.Message.Contains(kind == "Generic" ? nameof(IdentityProviderError) : kind)
                );

        [Test]
        public void It_logs_the_provider_description_only_from_a_structured_body() =>
            Logs.AllCapturedText().Contains("SENTINEL_PROVIDER_TEXT").Should().Be(kind != "Unreachable");

        [Test]
        public void It_returns_502() => AssertStatus(HttpStatusCode.BadGateway);

        [Test]
        public void It_answers_with_the_fixed_server_error_body() =>
            AssertOAuthError("server_error", "The identity provider returned an unexpected response.");

        [Test]
        public void It_does_not_echo_the_provider_text() =>
            Content.Should().NotContain("SENTINEL_PROVIDER_TEXT");

        [Test]
        public void It_does_not_challenge() => AssertNoChallenge();
    }

    /// <summary>
    /// A structured provider body is read for its <c>error</c> and <c>error_description</c> only: a
    /// further member never reaches the response, and a description that is not a string is not
    /// structured, so the fixed description answers.
    /// </summary>
    [TestFixture]
    public class Given_a_provider_body_with_an_extra_member : ManagerOutcomeRequest
    {
        public Given_a_provider_body_with_an_extra_member()
            : base(false) { }

        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.InvalidScope(
                    """{"error":"invalid_scope","error_description":"Invalid scopes: edfi_admin_api/readonly_access","stack":"SENTINEL_EXTRA_MEMBER"}"""
                )
            );

        [Test]
        public void It_answers_with_only_the_two_oauth_members() =>
            AssertOAuthError("invalid_scope", "Invalid scopes: edfi_admin_api/readonly_access");

        [Test]
        public void It_does_not_echo_the_extra_member() =>
            Content.Should().NotContain("SENTINEL_EXTRA_MEMBER");
    }

    [TestFixture]
    public class Given_a_provider_body_whose_description_is_not_a_string : ManagerOutcomeRequest
    {
        public Given_a_provider_body_whose_description_is_not_a_string()
            : base(false) { }

        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                new IdentityProviderError.InvalidScope(
                    """{"error":"invalid_scope","error_description":{"detail":"SENTINEL_NON_STRING"}}"""
                )
            );

        [Test]
        public void It_answers_with_the_fixed_description() =>
            AssertOAuthError(
                "invalid_scope",
                "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
            );

        [Test]
        public void It_does_not_echo_the_provider_value() =>
            Content.Should().NotContain("SENTINEL_NON_STRING");
    }

    /// <summary>
    /// A provider body whose string escape does not decode, such as an unpaired surrogate, is not a
    /// structured body: each failure kind gets its fixed answer rather than a 500.
    /// </summary>
    [TestFixture("InvalidScope")]
    [TestFixture("Unauthorized")]
    [TestFixture("Forbidden")]
    public class Given_a_provider_body_with_an_undecodable_escape(string kind) : ManagerOutcomeRequest(false)
    {
        private const string Body = """{"error":"invalid_scope","error_description":"\uD800"}""";

        protected override void ArrangeManager() =>
            ReturnsProviderFailure(
                kind switch
                {
                    "InvalidScope" => new IdentityProviderError.InvalidScope(Body),
                    "Unauthorized" => new IdentityProviderError.Unauthorized(Body),
                    _ => new IdentityProviderError.Forbidden(Body),
                }
            );

        [Test]
        public void It_answers_with_the_fixed_answer_for_the_failure_kind()
        {
            switch (kind)
            {
                case "InvalidScope":
                    AssertStatus(HttpStatusCode.BadRequest);
                    AssertOAuthError(
                        "invalid_scope",
                        "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
                    );
                    break;
                case "Unauthorized":
                    AssertClientAuthenticationAnswer(
                        "invalid_client",
                        "Invalid client or Invalid client credentials"
                    );
                    break;
                default:
                    AssertStatus(HttpStatusCode.BadGateway);
                    AssertOAuthError(
                        "server_error",
                        "The identity provider returned an unexpected response."
                    );
                    break;
            }
        }
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
            _factory = CreateFactory(services =>
            {
                services.RemoveAll<ITokenIntrospectionManager>();
                services.AddSingleton(A.Fake<ITokenIntrospectionManager>());
            });
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
        public void It_returns_400() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_oauth_invalid_request_body()
        {
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            JsonObject body = JsonNode.Parse(_content)!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("invalid_request");
            body["error_description"]!.GetValue<string>().Should().Be("The token parameter is missing.");
        }
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
            _factory = CreateFactory(services =>
            {
                services.RemoveAll<ITokenIntrospectionManager>();
                services.AddSingleton(A.Fake<ITokenIntrospectionManager>());
            });
            _client = _factory.CreateClient();
            // No client credentials: introspection is client-authenticated, so the token is never evaluated.
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
        public void It_returns_400() => _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_is_not_problem_details() =>
            _response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");

        [Test]
        public void It_reports_invalid_client() =>
            _body["error"]!.GetValue<string>().Should().Be("invalid_client");

        [Test]
        public void It_explains_that_client_authentication_is_required() =>
            _body["error_description"]!.GetValue<string>().Should().Be("Client authentication is required.");
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
    /// A caller with no client credentials, which was answered 200 before DMS-1327. The endpoint's own
    /// credential-presence check (D-03 row 7) rejects it before the revocation manager is consulted.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_token_from_an_unauthenticated_caller
    {
        private readonly ITokenManager _tokenManager = A.Fake<ITokenManager>();
        private readonly ITokenRevocationManager _revocationManager = A.Fake<ITokenRevocationManager>();
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
                collection.AddSingleton(_revocationManager);
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

        [Test]
        public void It_does_not_consult_the_revocation_manager() =>
            A.CallTo(_revocationManager).MustNotHaveHappened();
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

    internal static (string KeyId, byte[] PublicKeySpki, RsaSecurityKey SigningKey) CreateSigningKey()
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

    /// <summary>
    /// The real manager over the faked repository and secret hasher, with a real snapshot provider and certificate
    /// store of its own. The factory's container creates this owner and disposes it, and with it the provider and the
    /// store, when the factory is disposed. The placeholder is the host's own, which the host initialized at startup.
    /// </summary>
    internal sealed class RealTokenManagerOwner : IDisposable
    {
        private readonly SigningKeySnapshotProvider _signingKeyProvider;
        private readonly DevelopmentCertificateStore _developmentCertificateStore;

        public RealTokenManagerOwner(
            IOpenIddictTokenRepository tokenRepository,
            IClientSecretHasher secretHasher,
            ClientSecretPlaceholderHash placeholderHash,
            bool faultOwnershipComparison = false
        )
        {
            IOptions<OpenIddictIdentityOptions> options = Options.Create(
                // EncryptionKey lets the database signing-key path run, so the same manager can also
                // issue a token. Only issuance reads it, and the faked repository ignores its value.
                new OpenIddictIdentityOptions
                {
                    Authority = TestIssuer,
                    Audience = TestAudience,
                    EncryptionKey = "test-encryption-key",
                }
            );
            _signingKeyProvider = new SigningKeySnapshotProvider(
                new DatabaseSigningKeySource(tokenRepository, NullLogger<DatabaseSigningKeySource>.Instance),
                options,
                TimeProvider.System,
                NullLogger<SigningKeySnapshotProvider>.Instance
            );
            _developmentCertificateStore = new DevelopmentCertificateStore(
                options,
                NullLogger<DevelopmentCertificateStore>.Instance
            );
            TokenManager = faultOwnershipComparison
                ? new FaultingOwnershipTokenManager(
                    options,
                    secretHasher,
                    tokenRepository,
                    _signingKeyProvider,
                    _developmentCertificateStore,
                    placeholderHash
                )
                : new OpenIddictTokenManager(
                    options,
                    NullLogger<OpenIddictTokenManager>.Instance,
                    secretHasher,
                    tokenRepository,
                    _signingKeyProvider,
                    _developmentCertificateStore,
                    placeholderHash
                );
        }

        public OpenIddictTokenManager TokenManager { get; }

        public void Dispose()
        {
            _signingKeyProvider.Dispose();
            _developmentCertificateStore.Dispose();
        }
    }

    /// <summary>
    /// Registers <paramref name="clientId"/> as a real, approved application so
    /// <c>ValidateClientCredentialsAsync</c> authenticates it. Pair with a secret hasher fake that
    /// returns true from <c>VerifySecretAsync</c>, which is what actually accepts the secret.
    /// </summary>
    internal static void RegisterApprovedClient(
        IOpenIddictTokenRepository tokenRepository,
        string clientId
    ) =>
        A.CallTo(() => tokenRepository.GetApplicationByClientIdAsync(clientId))
            .Returns(new ApplicationInfo { ClientId = clientId, IsApproved = true });

    /// <summary>
    /// A faked token repository whose client credential, unless a test arranges one, is read from
    /// the application the test arranges for that client id, as the shipped repositories read both
    /// from the same row.
    /// </summary>
    internal static IOpenIddictTokenRepository NewTokenRepository()
    {
        IOpenIddictTokenRepository tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        A.CallTo(() => tokenRepository.GetClientCredentialAsync(A<string>._))
            .ReturnsLazily(
                (string clientId) => CredentialOfArrangedApplicationAsync(tokenRepository, clientId)
            );
        return tokenRepository;
    }

    private static async Task<ClientCredential?> CredentialOfArrangedApplicationAsync(
        IOpenIddictTokenRepository tokenRepository,
        string clientId
    ) =>
        await tokenRepository.GetApplicationByClientIdAsync(clientId) is { } application
            ? new ClientCredential(
                application.Id,
                application.ClientId,
                application.ClientSecret,
                application.IsApproved
            )
            : null;

    /// <summary>
    /// A host whose <see cref="ITokenManager"/> and <see cref="ITokenRevocationManager"/> are the real manager over
    /// <paramref name="tokenRepository"/>. The owner is registered through a factory, so the host's container owns it
    /// and disposes it with the host.
    /// </summary>
    internal static WebApplicationFactory<Program> CreateFactory(
        IOpenIddictTokenRepository tokenRepository,
        IClientSecretHasher secretHasher,
        bool faultOwnershipComparison = false
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTestAuthentication();
                collection.AddSingleton(services => new RealTokenManagerOwner(
                    tokenRepository,
                    secretHasher,
                    services.GetRequiredService<ClientSecretPlaceholderHash>(),
                    faultOwnershipComparison
                ));
                collection.AddTransient<ITokenManager>(services =>
                    services.GetRequiredService<RealTokenManagerOwner>().TokenManager
                );
                collection.AddTransient<ITokenRevocationManager>(services =>
                    services.GetRequiredService<RealTokenManagerOwner>().TokenManager
                );
            });
        });

    /// <summary>
    /// Injects a fault into the manager's ownership comparison, which sits between its dependency
    /// boundaries and must not be relabelled as an outage.
    /// </summary>
    private sealed class FaultingOwnershipTokenManager(
        IOptions<OpenIddictIdentityOptions> options,
        IClientSecretHasher secretHasher,
        IOpenIddictTokenRepository tokenRepository,
        ISigningKeySnapshotProvider signingKeyProvider,
        DevelopmentCertificateStore developmentCertificateStore,
        ClientSecretPlaceholderHash placeholderHash
    )
        : OpenIddictTokenManager(
            options,
            NullLogger<OpenIddictTokenManager>.Instance,
            secretHasher,
            tokenRepository,
            signingKeyProvider,
            developmentCertificateStore,
            placeholderHash
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private Guid _jti;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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

        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private Guid _jti;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OtherClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(_tokenRepository, _secretHasher);

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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, _) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
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

            _factory = CreateFactory(_tokenRepository, _secretHasher);
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private readonly IClientSecretHasher _secretHasher = A.Fake<IClientSecretHasher>();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RegisterApprovedClient(_tokenRepository, OwnerClientId);
            A.CallTo(() => _secretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);

            _factory = CreateFactory(_tokenRepository, _secretHasher, faultOwnershipComparison: true);
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

    internal const int RealHasherIterations = 1000;
    private const string RealHasherSecret = "SECRET-CALLER-SENTINEL-http-real-hasher";

    internal static ClientSecretHasher CreateRealHasher(int iterations, ILogger<ClientSecretHasher> logger) =>
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, CreateRealHasher(0, _hasherLogger));
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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
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

            _factory = CreateFactory(_tokenRepository, CreateRealHasher(RealHasherIterations, _hasherLogger));
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
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .MustNotHaveHappened();

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
        private readonly IOpenIddictTokenRepository _tokenRepository = NewTokenRepository();
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            await RegisterClientWithRealHash(_tokenRepository, OwnerClientId);

            _factory = CreateFactory(
                _tokenRepository,
                CreateRealHasher(RealHasherIterations, NullLogger<ClientSecretHasher>.Instance)
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

    /// <summary>
    /// Revocation over HTTP through a repository whose credential read and full application lookup
    /// are arranged separately, with the real hasher. The caller is authenticated from the
    /// credential alone: an absent caller, a wrong secret and an unapproved caller receive the same
    /// invalid_client answer; the caller's own token is revoked under its application id even when
    /// the stored client id is spelled differently from the one presented; a token issued to
    /// another client is left alone; a credential that cannot be read is an outage. The full
    /// application is never loaded.
    /// </summary>
    [TestFixture]
    public class Given_revocation_requests_through_a_credential_reading_repository
    {
        private const string StoredOwnerClientId = "Revoke-Owner-Client";
        private const string AbsentCallerId = "absent-caller";
        private const string UnapprovedCallerId = "unapproved-caller";
        private const string FaultingCallerId = "faulting-caller";
        private const string WrongSecret = "Wrong-Secret-0123456789";

        private static readonly (string Failure, string ClientId, string Secret)[] _failureCases =
        [
            ("an absent caller", AbsentCallerId, RealHasherSecret),
            ("a wrong secret", OwnerClientId, WrongSecret),
            ("an unapproved caller with its secret", UnapprovedCallerId, RealHasherSecret),
        ];

        private readonly IOpenIddictTokenRepository _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        private readonly Guid _ownerApplicationId = Guid.NewGuid();
        private readonly Guid _ownJti = Guid.NewGuid();
        private readonly Guid _foreignJti = Guid.NewGuid();
        private readonly Dictionary<
            string,
            (HttpStatusCode Status, string? MediaType, bool BasicChallenge, string Body)
        > _failures = [];
        private WebApplicationFactory<Program> _factory = null!;
        private HttpStatusCode _ownTokenStatus;
        private HttpStatusCode _foreignTokenStatus;
        private HttpResponseMessage _faultResponse = null!;
        private JsonObject _faultBody = null!;

        private async Task<HttpResponseMessage> RevokeAs(string clientId, string secret, string token)
        {
            using HttpClient client = CreateClientWithCredentials(_factory, clientId, secret);
            return await PostRevocation(client, token);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            string storedHash = await CreateRealHasher(
                    RealHasherIterations,
                    NullLogger<ClientSecretHasher>.Instance
                )
                .HashSecretAsync(RealHasherSecret);
            A.CallTo(() => _tokenRepository.GetClientCredentialAsync(AbsentCallerId))
                .Returns((ClientCredential?)null);
            A.CallTo(() => _tokenRepository.GetClientCredentialAsync(OwnerClientId))
                .Returns(
                    new ClientCredential(
                        _ownerApplicationId,
                        StoredOwnerClientId,
                        storedHash,
                        IsApproved: true
                    )
                );
            A.CallTo(() => _tokenRepository.GetClientCredentialAsync(UnapprovedCallerId))
                .Returns(
                    new ClientCredential(Guid.NewGuid(), UnapprovedCallerId, storedHash, IsApproved: false)
                );
            A.CallTo(() => _tokenRepository.GetClientCredentialAsync(FaultingCallerId))
                .Throws(new InvalidOperationException("credential store unavailable"));

            var (keyId, publicKeySpki, signingKey) = CreateSigningKey();
            A.CallTo(() => _tokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_ownJti, _ownerApplicationId)).Returns(true);

            _factory = CreateFactory(
                _tokenRepository,
                CreateRealHasher(RealHasherIterations, NullLogger<ClientSecretHasher>.Instance)
            );

            foreach ((string failure, string clientId, string secret) in _failureCases)
            {
                using HttpResponseMessage response = await RevokeAs(clientId, secret, "irrelevant-token");
                _failures[failure] = (
                    response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType,
                    response.Headers.WwwAuthenticate.Any(header => header.Scheme == "Basic"),
                    await response.Content.ReadAsStringAsync()
                );
            }

            using (
                HttpResponseMessage own = await RevokeAs(
                    OwnerClientId,
                    RealHasherSecret,
                    CreateSignedToken(signingKey, StoredOwnerClientId, _ownJti)
                )
            )
            {
                _ownTokenStatus = own.StatusCode;
            }

            using (
                HttpResponseMessage foreign = await RevokeAs(
                    OwnerClientId,
                    RealHasherSecret,
                    CreateSignedToken(signingKey, OtherClientId, _foreignJti)
                )
            )
            {
                _foreignTokenStatus = foreign.StatusCode;
            }

            _faultResponse = await RevokeAs(FaultingCallerId, RealHasherSecret, "irrelevant-token");
            _faultBody = await ReadOAuthError(_faultResponse);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _faultResponse?.Dispose();
            _factory?.Dispose();
        }

        public static IEnumerable<string> Failures() =>
            _failureCases.Select(failureCase => failureCase.Failure);

        [TestCaseSource(nameof(Failures))]
        public void It_answers_401_in_the_oauth_error_format(string failure)
        {
            _failures[failure].Status.Should().Be(HttpStatusCode.Unauthorized);
            _failures[failure].MediaType.Should().Be("application/json");
        }

        [TestCaseSource(nameof(Failures))]
        public void It_reports_invalid_client(string failure)
        {
            JsonObject body = JsonNode.Parse(_failures[failure].Body)!.AsObject();
            body["error"]!.GetValue<string>().Should().Be("invalid_client");
            body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("Invalid client or Invalid client credentials");
        }

        [TestCaseSource(nameof(Failures))]
        public void It_sends_a_basic_challenge(string failure) =>
            _failures[failure].BasicChallenge.Should().BeTrue();

        [Test]
        public void It_returns_one_body_for_every_authentication_failure() =>
            _failures.Values.Select(observed => observed.Body).Distinct().Should().ContainSingle();

        [Test]
        public void It_revokes_the_callers_own_token_under_its_application_id()
        {
            _ownTokenStatus.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_ownJti, _ownerApplicationId))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_leaves_a_token_issued_to_another_client_alone()
        {
            _foreignTokenStatus.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(_foreignJti, A<Guid>._)).MustNotHaveHappened();
        }

        [Test]
        public void It_answers_an_unreadable_credential_as_temporarily_unavailable() =>
            AssertTemporarilyUnavailable(_faultResponse, _faultBody);

        [Test]
        public void It_authenticates_every_request_from_the_credential() =>
            A.CallTo(() => _tokenRepository.GetClientCredentialAsync(A<string>._))
                .MustHaveHappened(_failureCases.Length + 3, Times.Exactly);

        [Test]
        public void It_never_loads_the_full_application() =>
            A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(A<string>._)).MustNotHaveHappened();

        [Test]
        public void It_revokes_nothing_for_a_failed_or_unavailable_authentication() =>
            A.CallTo(() => _tokenRepository.RevokeTokenAsync(A<Guid>._, A<Guid>._))
                .MustHaveHappenedOnceExactly();
    }
}

/// <summary>
/// Drives POST /connect/introspect through the in-process host with the real
/// <see cref="OpenIddictTokenManager"/> over a faked <see cref="IOpenIddictTokenRepository"/> and
/// <see cref="IClientSecretHasher"/>, so signature verification, ownership comparison and the
/// authenticate-before-evaluate order are the production code's, not a fake's. The secret hasher
/// is faked to a blanket "always matches" except where a fixture says otherwise, because the
/// hashing algorithm is covered by the manager's own tests.
/// </summary>
public class IntrospectionOwnershipTests
{
    private const string TestIssuer = "https://cms.example.test";
    private const string TestAudience = "ed-fi-cms-tests";
    private const string OwnerClientId = "introspect-owner-client";
    private const string OtherClientId = "introspect-other-client";
    private const string TestClientSecret = "test-secret";
    private const string TestScope = "edfi_admin_api/full_access";
    private const string TestSubject = "owner-subject";
    private const string InactiveBody = """{"active":false}""";

    private sealed class StoreUnavailableException(string message) : DbException(message);

    /// <summary>
    /// Issues a signed JWT that carries everything an active introspection reports. The "kid"
    /// header comes from the signing key. <c>exp</c> and <c>iat</c> are integer claims, so they
    /// are written to the payload as JSON numbers.
    /// </summary>
    private static string CreateIntrospectableToken(
        RsaSecurityKey signingKey,
        string clientId,
        Guid jti,
        DateTimeOffset issuedAt,
        DateTimeOffset expires
    )
    {
        var jwt = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, TestSubject),
                new Claim("client_id", clientId),
                new Claim("scope", TestScope),
                new Claim(
                    JwtRegisteredClaimNames.Iat,
                    issuedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ClaimValueTypes.Integer64
                ),
            ],
            notBefore: issuedAt.UtcDateTime.AddMinutes(-1),
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>
    /// Arranges the host and sends the introspection request the fixture describes. The default
    /// arrangement registers one verification key, an approved owner application and a hasher that
    /// accepts any secret; a fixture overrides <see cref="Arrange"/> to change any of that.
    /// </summary>
    public abstract class IntrospectionFixture
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        protected readonly IOpenIddictTokenRepository TokenRepository =
            RevocationOwnershipTests.NewTokenRepository();
        protected readonly IClientSecretHasher SecretHasher = A.Fake<IClientSecretHasher>();
        protected readonly Guid Jti = Guid.NewGuid();
        protected readonly Guid OwnerApplicationId = Guid.NewGuid();
        protected readonly DateTimeOffset IssuedAt = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
        );
        protected readonly DateTimeOffset Expires = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        );

        protected RsaSecurityKey SigningKey { get; private set; } = null!;
        protected HttpClient Client => _client;
        protected HttpResponseMessage Response { get; private set; } = null!;
        protected string Content { get; private set; } = null!;

        /// <summary>The client id the caller authenticates with.</summary>
        protected virtual string CallerClientId => OwnerClientId;

        protected virtual string CallerSecret => TestClientSecret;

        protected virtual bool UseFormCredentials => false;

        protected virtual void Arrange() { }

        /// <summary>The token the request carries, minted by the default signing key.</summary>
        protected abstract string Token { get; }

        protected string OwnedToken(string clientId = OwnerClientId) =>
            CreateIntrospectableToken(SigningKey, clientId, Jti, IssuedAt, Expires);

        protected Task<HttpResponseMessage> PostIntrospection(string token) =>
            PostIntrospection(CallerClientId, CallerSecret, token);

        protected async Task<HttpResponseMessage> PostIntrospection(
            string clientId,
            string clientSecret,
            string token
        )
        {
            List<KeyValuePair<string, string>> fields = [new("token", token)];
            using HttpRequestMessage request = new(HttpMethod.Post, "/connect/introspect");
            if (UseFormCredentials)
            {
                fields.Add(new("client_id", clientId));
                fields.Add(new("client_secret", clientSecret));
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"))
                );
            }

            request.Content = new FormUrlEncodedContent(fields);
            return await _client.SendAsync(request);
        }

        [SetUp]
        public async Task Setup()
        {
            var (keyId, publicKeySpki, signingKey) = RevocationOwnershipTests.CreateSigningKey();
            SigningKey = signingKey;
            A.CallTo(() => TokenRepository.GetActivePublicKeysAsync(A<CancellationToken>._))
                .Returns(
                    new[]
                    {
                        new PublicKeyInfo { KeyId = keyId, PublicKey = publicKeySpki },
                    }
                );
            RevocationOwnershipTests.RegisterApprovedClient(TokenRepository, OwnerClientId);
            RevocationOwnershipTests.RegisterApprovedClient(TokenRepository, OtherClientId);
            A.CallTo(() => TokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Returns(
                    new ApplicationInfo
                    {
                        Id = OwnerApplicationId,
                        ClientId = OwnerClientId,
                        IsApproved = true,
                    }
                );
            A.CallTo(() => SecretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(true);
            A.CallTo(() => TokenRepository.GetTokenStatusForApplicationAsync(Jti, OwnerApplicationId))
                .Returns("valid");
            Arrange();

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddTestAuthentication();
                    collection.AddSingleton(services => new RevocationOwnershipTests.RealTokenManagerOwner(
                        TokenRepository,
                        SecretHasher,
                        services.GetRequiredService<ClientSecretPlaceholderHash>()
                    ));
                    collection.AddTransient<ITokenManager>(services =>
                        services
                            .GetRequiredService<RevocationOwnershipTests.RealTokenManagerOwner>()
                            .TokenManager
                    );
                    collection.RemoveAll<ITokenIntrospectionManager>();
                    collection.AddTransient<ITokenIntrospectionManager>(services =>
                        services
                            .GetRequiredService<RevocationOwnershipTests.RealTokenManagerOwner>()
                            .TokenManager
                    );
                });
            });
            _client = _factory.CreateClient();

            Response = await PostIntrospection(Token);
            Content = await Response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            Response?.Dispose();
            _client?.Dispose();
            _factory?.Dispose();
        }

        protected JsonElement Body() => JsonDocument.Parse(Content).RootElement;

        protected void AssertInactive()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);
            Content.Should().Be(InactiveBody);
        }
    }

    /// <summary>The owner's own live token is reported in full, however the owner authenticates.</summary>
    public abstract class ActiveIntrospection : IntrospectionFixture
    {
        protected override string Token => OwnedToken();

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);

        [Test]
        public void It_answers_json() =>
            Response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        [Test]
        public void It_reports_active_true() =>
            Body().GetProperty("active").ValueKind.Should().Be(JsonValueKind.True);

        [Test]
        public void It_reports_the_owner_as_client_id() =>
            Body().GetProperty("client_id").GetString().Should().Be(OwnerClientId);

        [Test]
        public void It_reports_exp_as_the_numeric_token_expiry()
        {
            Body().GetProperty("exp").ValueKind.Should().Be(JsonValueKind.Number);
            Body().GetProperty("exp").GetInt64().Should().Be(Expires.ToUnixTimeSeconds());
        }

        [Test]
        public void It_reports_iat_as_the_numeric_token_issue_time()
        {
            Body().GetProperty("iat").ValueKind.Should().Be(JsonValueKind.Number);
            Body().GetProperty("iat").GetInt64().Should().Be(IssuedAt.ToUnixTimeSeconds());
        }

        [Test]
        public void It_reports_the_scope_subject_audience_issuer_and_token_type()
        {
            Body().GetProperty("scope").GetString().Should().Be(TestScope);
            Body().GetProperty("sub").GetString().Should().Be(TestSubject);
            Body().GetProperty("aud").GetString().Should().Be(TestAudience);
            Body().GetProperty("iss").GetString().Should().Be(TestIssuer);
            Body().GetProperty("token_type").GetString().Should().Be("Bearer");
        }
    }

    [TestFixture]
    public class Given_an_introspection_of_a_token_the_basic_authenticated_caller_owns : ActiveIntrospection;

    [TestFixture]
    public class Given_an_introspection_of_a_token_the_form_authenticated_caller_owns : ActiveIntrospection
    {
        protected override bool UseFormCredentials => true;
    }

    /// <summary>
    /// Casing defect at the HTTP level: a collation-insensitive lookup resolves a mis-cased
    /// client_id to the stored application, while minted tokens carry the stored spelling. The
    /// ownership comparison must be given the canonical id or the caller's own token reads inactive.
    /// </summary>
    [TestFixture]
    public class Given_an_introspection_authenticated_with_non_canonical_casing : ActiveIntrospection
    {
        private const string NonCanonicalClientId = "INTROSPECT-Owner-Client";

        protected override string CallerClientId => NonCanonicalClientId;

        protected override void Arrange() =>
            A.CallTo(() => TokenRepository.GetApplicationByClientIdAsync(NonCanonicalClientId))
                .Returns(
                    new ApplicationInfo
                    {
                        Id = OwnerApplicationId,
                        ClientId = OwnerClientId,
                        IsApproved = true,
                    }
                );
    }

    /// <summary>
    /// Another authenticated client must learn nothing about a token it does not own: the answer is
    /// the same bytes it gets for a token that does not exist at all.
    /// </summary>
    [TestFixture]
    public class Given_an_introspection_of_a_token_owned_by_another_client : IntrospectionFixture
    {
        private string _unknownTokenContent = null!;

        protected override string CallerClientId => OtherClientId;

        protected override string Token => OwnedToken(OwnerClientId);

        [SetUp]
        public async Task SetupUnknownTokenComparison()
        {
            using HttpResponseMessage unknown = await PostIntrospection("not-a-token");
            _unknownTokenContent = await unknown.Content.ReadAsStringAsync();
        }

        [Test]
        public void It_answers_200_active_false() => AssertInactive();

        [Test]
        public void It_answers_the_same_bytes_as_for_an_unknown_token() =>
            Content.Should().Be(_unknownTokenContent);

        [Test]
        public void It_does_not_read_the_token_status() =>
            A.CallTo(() => TokenRepository.GetTokenStatusForApplicationAsync(A<Guid>._, A<Guid>._))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_introspection_of_a_garbage_token_by_the_owner : IntrospectionFixture
    {
        protected override string Token => "not-a-token";

        [Test]
        public void It_answers_200_active_false() => AssertInactive();
    }

    [TestFixture]
    public class Given_an_introspection_of_a_well_formed_token_unknown_to_the_status_store
        : IntrospectionFixture
    {
        protected override string Token => OwnedToken();

        protected override void Arrange() =>
            A.CallTo(() => TokenRepository.GetTokenStatusForApplicationAsync(Jti, OwnerApplicationId))
                .Returns((string?)null);

        [Test]
        public void It_answers_200_active_false() => AssertInactive();
    }

    /// <summary>
    /// A token naming the caller but signed with a key that is not registered must not verify, so
    /// the caller's name on it proves nothing.
    /// </summary>
    [TestFixture]
    public class Given_an_introspection_of_a_forged_token_naming_the_caller : IntrospectionFixture
    {
        protected override string Token
        {
            get
            {
                var (_, _, attackerKey) = RevocationOwnershipTests.CreateSigningKey();
                return CreateIntrospectableToken(attackerKey, OwnerClientId, Jti, IssuedAt, Expires);
            }
        }

        [Test]
        public void It_answers_200_active_false() => AssertInactive();
    }

    /// <summary>
    /// Authentication runs before the token is evaluated: a wrong secret is 401 invalid_client
    /// even though the token alone would have answered <c>{"active":false}</c>.
    /// </summary>
    [TestFixture]
    public class Given_an_introspection_of_a_garbage_token_with_a_wrong_secret : IntrospectionFixture
    {
        protected override string Token => "not-a-token";

        protected override string CallerSecret => "wrong-secret";

        protected override void Arrange() =>
            A.CallTo(() => SecretHasher.VerifySecretAsync(A<string>._, A<string>._)).Returns(false);

        [Test]
        public void It_answers_401() => Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, Content);

        [Test]
        public void It_answers_invalid_client() =>
            Body().GetProperty("error").GetString().Should().Be("invalid_client");

        [Test]
        public void It_does_not_answer_active_false() => Content.Should().NotContain("active");

        [Test]
        public void It_sends_a_basic_challenge() =>
            Response.Headers.WwwAuthenticate.Should().ContainSingle(header => header.Scheme == "Basic");
    }

    [TestFixture]
    public class Given_an_introspection_when_the_application_lookup_fails : IntrospectionFixture
    {
        protected override string Token => OwnedToken();

        protected override void Arrange() =>
            A.CallTo(() => TokenRepository.GetApplicationByClientIdAsync(OwnerClientId))
                .Throws(new TimeoutException("database unavailable"));

        [Test]
        public void It_answers_503() =>
            Response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, Content);

        [Test]
        public void It_answers_temporarily_unavailable() =>
            Body().GetProperty("error").GetString().Should().Be("temporarily_unavailable");

        [Test]
        public void It_does_not_claim_the_token_is_inactive() => Content.Should().NotContain("\"active\"");
    }

    /// <summary>
    /// A token-status read failure is not a verdict on the token, but introspection answers it as
    /// inactive, as the bearer validation path always has.
    /// </summary>
    [TestFixture]
    public class Given_an_introspection_when_the_token_status_read_fails : IntrospectionFixture
    {
        protected override string Token => OwnedToken();

        protected override void Arrange() =>
            A.CallTo(() => TokenRepository.GetTokenStatusForApplicationAsync(Jti, OwnerApplicationId))
                .Throws(new StoreUnavailableException("status store unavailable"));

        [Test]
        public void It_answers_200_active_false() => AssertInactive();
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
        public void It_returns_the_fixed_server_error_instead_of_the_partial_payload()
        {
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            _body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            _body["error"]!.GetValue<string>().Should().Be("server_error");
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The identity provider returned an unexpected response.");
        }

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
/// install the harness's test authentication, present no credentials, and assert register and token are
/// never 401. <c>/connect/introspect</c> authenticates the client itself, so it answers 400
/// <c>invalid_client</c>, never a framework 401. Non-fixture container; the runnable fixture is the
/// nested <c>Given_…</c> class.
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
                builder.ConfigureServices(collection =>
                {
                    collection.AddTestAuthentication();
                    collection.RemoveAll<ITokenIntrospectionManager>();
                    collection.AddSingleton(A.Fake<ITokenIntrospectionManager>());
                });
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
                new FormUrlEncodedContent([new KeyValuePair<string, string>("token", "opaque-token")])
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
        public async Task It_rejects_the_credential_less_introspection_with_invalid_client()
        {
            _introspectResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonObject body = JsonNode
                .Parse(await _introspectResponse.Content.ReadAsStringAsync())!
                .AsObject();
            body["error"]!.GetValue<string>().Should().Be("invalid_client");
        }
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

    /// <summary>
    /// One character past <c>FormOptions.KeyLengthLimit</c>, so <c>ReadFormAsync</c> throws.
    /// </summary>
    private static readonly string OverLongFormKey = new('k', 2049);

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

    /// <summary>
    /// The body exactly as written, for encodings <see cref="FormUrlEncodedContent"/> cannot produce:
    /// it would escape every <c>%</c> and send a well-formed body.
    /// </summary>
    private static ByteArrayContent RawForm(
        string encoded,
        string contentType = "application/x-www-form-urlencoded"
    )
    {
        ByteArrayContent content = new(Encoding.UTF8.GetBytes(encoded));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType).Should().BeTrue();
        return content;
    }

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
    /// choose the manager's outcome.
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
                    collection.AddSingleton<ITokenRevocationManager>(Manager);
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

    // ----- D-03 row 1: not application/x-www-form-urlencoded -----

    public static IEnumerable<TestFixtureData> NonFormBodies()
    {
        yield return new TestFixtureData(
            new Func<HttpContent?>(() =>
                new StringContent($"{{\"token\":\"{Token}\"}}", Encoding.UTF8, "application/json")
            )
        ).SetArgDisplayNames("a JSON body");
        yield return new TestFixtureData(new Func<HttpContent?>(() => null)).SetArgDisplayNames("no body");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() =>
                new MultipartFormDataContent { { new StringContent(Token), "token" } }
            )
        ).SetArgDisplayNames("a well-formed multipart/form-data body");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(NonFormBodies))]
    public class Given_a_revocation_request_without_a_url_encoded_body(Func<HttpContent?> body)
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
    /// A form key longer than <c>FormOptions.KeyLengthLimit</c> (2048 characters) makes
    /// <c>ReadFormAsync</c> throw <see cref="InvalidDataException"/>. The handler selects the OAuth
    /// writer from the route's marker, read off <c>IExceptionHandlerFeature.Endpoint</c> (A-05).
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_malformed_form_payload : RevocationRequestFixture
    {
        protected override HttpContent? Body => RawForm($"{OverLongFormKey}=x&token={Sentinel}");

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_invalid_request_in_the_oauth_format() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "The request form payload is malformed."
            );

        [Test]
        public void It_does_not_leak_the_framework_parsing_message() => Content.Should().NotContain("limit");

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_is_not_logged_as_a_failed_request() =>
            Logs
                .Records.Should()
                .NotContain(record => record.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id);
    }

    // ----- D-03 row 2: malformed URL encoding, which ReadFormAsync accepts without throwing -----

    private const string MalformedFormDescription = "The request form payload is malformed.";

    /// <summary>
    /// <c>ReadFormAsync</c> keeps a malformed escape as literal text and passes percent-encoded bytes
    /// that are not UTF-8 through undecoded, so none of these bodies makes it throw.
    /// </summary>
    public static IEnumerable<TestFixtureData> MalformedEncodings()
    {
        yield return new TestFixtureData("%").SetArgDisplayNames("a bare percent sign");
        yield return new TestFixtureData("%GG").SetArgDisplayNames("an escape with non-hex digits");
        yield return new TestFixtureData("%FF").SetArgDisplayNames("an escape that is not UTF-8");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(MalformedEncodings))]
    public class Given_a_revocation_request_with_a_malformed_url_encoded_token(string encodedToken)
        : RevocationRequestFixture
    {
        protected override HttpContent? Body =>
            RawForm($"token={encodedToken}&client_id={ClientId}&client_secret={Sentinel}");

        [Test]
        public void It_answers_invalid_request() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", MalformedFormDescription);

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_does_not_log_the_credentials() => AssertNotLogged(Sentinel);

        [Test]
        public void It_is_not_logged_as_a_failed_request() =>
            Logs
                .Records.Should()
                .NotContain(record => record.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id);
    }

    /// <summary>
    /// Controls for the fixture above: correctly encoded input, including an encoded literal
    /// <c>%</c> followed by text that would itself be a malformed escape, reaches the manager
    /// decoded exactly once.
    /// </summary>
    public static IEnumerable<TestFixtureData> WellFormedEncodings()
    {
        yield return new TestFixtureData("%25", "%").SetArgDisplayNames("an encoded percent sign");
        yield return new TestFixtureData("%25GG", "%GG").SetArgDisplayNames(
            "an encoded percent sign before non-hex text"
        );
        yield return new TestFixtureData("%2B", "+").SetArgDisplayNames("an encoded plus sign");
        yield return new TestFixtureData("a+b", "a b").SetArgDisplayNames("a plus sign for a space");
        yield return new TestFixtureData("%C3%A9", "é").SetArgDisplayNames("an encoded UTF-8 sequence");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(WellFormedEncodings))]
    public class Given_a_revocation_request_with_a_well_formed_url_encoded_token(
        string encodedToken,
        string decodedToken
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body =>
            RawForm($"token={encodedToken}&client_id={ClientId}&client_secret={ClientSecret}");

        [Test]
        public void It_answers_an_empty_200()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);
            Content.Should().BeEmpty();
        }

        [Test]
        public void It_hands_the_manager_the_token_decoded_once() =>
            Manager.Requests.Should().ContainSingle().Which.Token.Should().Be(decodedToken);
    }

    /// <summary>
    /// The form is rejected before any credential is processed: the malformed Basic value alone would
    /// be answered 401 <c>invalid_client</c> with a challenge.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_with_a_malformed_url_encoded_form_and_malformed_basic_credentials
        : RevocationRequestFixture
    {
        protected override HttpContent? Body => RawForm("token=%GG");

        protected override IEnumerable<string> AuthorizationValues => [$"Basic {Sentinel}"];

        [Test]
        public void It_answers_invalid_request_rather_than_invalid_client() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", MalformedFormDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_does_not_log_the_header() => AssertNotLogged(Sentinel);
    }

    /// <summary>
    /// <c>ReadFormAsync</c> decodes with the declared charset and the endpoint's encoding check is
    /// UTF-8, so only no charset or UTF-8 is accepted; the token is then decoded as UTF-8.
    /// </summary>
    public static IEnumerable<TestFixtureData> AcceptedCharsets()
    {
        yield return new TestFixtureData("application/x-www-form-urlencoded").SetArgDisplayNames(
            "no charset"
        );
        yield return new TestFixtureData(
            "application/x-www-form-urlencoded; charset=utf-8"
        ).SetArgDisplayNames("charset=utf-8");
        yield return new TestFixtureData(
            "Application/X-WWW-Form-UrlEncoded; Charset=\"UTF-8\""
        ).SetArgDisplayNames("a quoted upper-case UTF-8 charset");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(AcceptedCharsets))]
    public class Given_a_revocation_request_declaring_an_accepted_charset(string contentType)
        : RevocationRequestFixture
    {
        protected override HttpContent? Body =>
            RawForm($"token=%C3%A9&client_id={ClientId}&client_secret={ClientSecret}", contentType);

        [Test]
        public void It_answers_an_empty_200()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);
            Content.Should().BeEmpty();
        }

        [Test]
        public void It_hands_the_manager_the_token_decoded_as_utf8() =>
            Manager.Requests.Should().ContainSingle().Which.Token.Should().Be("é");
    }

    /// <summary>
    /// Bodies the encoding check would accept as UTF-8 but <c>ReadFormAsync</c> would decode
    /// differently under the declared charset (<c>%C3%A9</c> as <c>??</c> under US-ASCII, raw UTF-8
    /// <c>é</c> as <c>Ã©</c> under ISO-8859-1), and a charset .NET does not know.
    /// </summary>
    public static IEnumerable<TestFixtureData> RejectedCharsets()
    {
        yield return new TestFixtureData(
            "token=%C3%A9",
            "application/x-www-form-urlencoded; charset=us-ascii"
        ).SetArgDisplayNames("an escaped UTF-8 token declared US-ASCII");
        yield return new TestFixtureData(
            "token=é",
            "application/x-www-form-urlencoded; charset=iso-8859-1"
        ).SetArgDisplayNames("a raw UTF-8 token declared ISO-8859-1");
        yield return new TestFixtureData(
            "token=opaque",
            "application/x-www-form-urlencoded; charset=x-unknown"
        ).SetArgDisplayNames("an unknown charset");
        yield return new TestFixtureData(
            "token=opaque",
            "application/x-www-form-urlencoded; charset=utf-7"
        ).SetArgDisplayNames("UTF-7, which .NET refuses to load");
    }

    [TestFixtureSource(typeof(RevocationRequestContractTests), nameof(RejectedCharsets))]
    public class Given_a_revocation_request_declaring_an_unsupported_charset(
        string fields,
        string contentType
    ) : RevocationRequestFixture
    {
        protected override HttpContent? Body =>
            RawForm($"{fields}&client_id={ClientId}&client_secret={Sentinel}", contentType);

        [Test]
        public void It_answers_invalid_request() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "The request form payload must be encoded as UTF-8."
            );

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();

        [Test]
        public void It_does_not_log_the_credentials() => AssertNotLogged(Sentinel);
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

    // ----- D-03 row 13 / D-17: a programming fault escaping the handler -----

    /// <summary>
    /// A fault inside the endpoint after the manager was called is a 500 <c>server_error</c> in the
    /// OAuth format, logged once as a failed request that names the exception types and carries no
    /// exception object, and nothing the caller sent (token, secret, Authorization value) or the
    /// exception's text reaches any log field (D-15, D-17).
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
        public void It_logs_one_failed_request_naming_the_exception_types_without_the_exception()
        {
            RevocationLogCapture.Record failed = Logs
                .Records.Should()
                .ContainSingle(record => record.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id)
                .Subject;
            failed.Exception.Should().BeNull();
            failed
                .State.Should()
                .Contain(
                    new KeyValuePair<string, object?>(
                        "ExceptionTypes",
                        typeof(InvalidOperationException).FullName
                    )
                );
        }

        [Test]
        public void It_does_not_log_the_exception_text() => AssertNotLogged(FaultText);

        [Test]
        public void It_does_not_log_the_caller_input() =>
            AssertNotLogged(Token, ClientSecret, _validBasic["Basic ".Length..]);
    }
}

/// <summary>
/// The <c>/connect/introspect</c> request contract through the real HTTP pipeline: it shares the
/// DMS-1327 D-03 request-shape and client-credential seam with <c>/connect/revoke</c> (the full
/// matrix lives in <see cref="RevocationRequestContractTests"/>), then maps the manager's outcome per
/// RFC 7662. The manager is a recorder, and every rejection asserts it was never called.
/// Non-fixture container; the runnable fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class IntrospectionRequestContractTests
{
    private const string ClientId = "introspect-client";
    private const string ClientSecret = "introspect-secret";
    private const string Token = "SECRET-TOKEN-SENTINEL-introspect";
    private const string Challenge = "Basic realm=\"EdFi.DmsConfigurationService\"";
    private const string InvalidCredentialsDescription = "Invalid client or Invalid client credentials";

    private static readonly string _validBasic =
        $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"))}";

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));

    private static (string, string)[] WithFormCredentials(params (string, string)[] fields) =>
        [("client_id", ClientId), ("client_secret", ClientSecret), .. fields];

    private static HttpContent RawForm(string encoded)
    {
        ByteArrayContent content = new(Encoding.UTF8.GetBytes(encoded));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        return content;
    }

    private static IntrospectedToken ActiveToken { get; } =
        new(
            ClientId,
            "edfi_admin_api/full_access",
            1893456000,
            1893455100,
            "subject-1",
            ["audience-1"],
            "issuer-1",
            "Bearer"
        );

    internal sealed class RecordingIntrospectionManager(Func<TokenIntrospectionResult> respond)
        : ITokenIntrospectionManager
    {
        public List<TokenIntrospectionRequest> Requests { get; } = [];

        public Task<TokenIntrospectionResult> IntrospectTokenAsync(
            TokenIntrospectionRequest request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(respond());
        }
    }

    /// <summary>
    /// Arranges a host with a recording manager and a capturing log provider, sends the request the
    /// fixture builds, and keeps the response.
    /// </summary>
    public abstract class IntrospectionRequestFixture
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        private protected RecordingIntrospectionManager Manager { get; private set; } = null!;
        private protected RevocationLogCapture Logs { get; private set; } = null!;
        protected HttpResponseMessage Response { get; private set; } = null!;
        protected string Content { get; private set; } = null!;

        protected virtual TokenIntrospectionResult Respond() => new TokenIntrospectionResult.Inactive();

        protected abstract HttpContent? Body { get; }

        protected virtual IEnumerable<string> AuthorizationValues => [];

        [SetUp]
        public async Task Setup()
        {
            Manager = new RecordingIntrospectionManager(Respond);
            Logs = new RevocationLogCapture();
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    collection.AddSingleton<ILoggerProvider>(Logs);
                    collection.RemoveAll<ITokenIntrospectionManager>();
                    collection.AddSingleton<ITokenIntrospectionManager>(Manager);
                });
            });
            _client = _factory.CreateClient();

            using HttpRequestMessage request = new(HttpMethod.Post, "/connect/introspect") { Content = Body };
            string[] authorization = [.. AuthorizationValues];
            if (authorization.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization).Should().BeTrue();
            }

            Response = await _client.SendAsync(request);
            Content = await Response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            Response?.Dispose();
            _client?.Dispose();
            _factory?.Dispose();
            Logs?.Dispose();
        }

        protected void AssertOAuthError(HttpStatusCode status, string error, string description) =>
            IntrospectionRequestContractTests.AssertOAuthErrorBody(
                Response,
                Content,
                status,
                error,
                description
            );

        protected void AssertChallenge() => IntrospectionRequestContractTests.AssertBasicChallenge(Response);

        protected void AssertNoChallenge() => Response.Headers.WwwAuthenticate.Should().BeEmpty();

        protected void AssertManagerNotCalled() => Manager.Requests.Should().BeEmpty();
    }

    private static void AssertOAuthErrorBody(
        HttpResponseMessage response,
        string content,
        HttpStatusCode status,
        string error,
        string description
    )
    {
        response.StatusCode.Should().Be(status, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        JsonObject body = JsonNode.Parse(content)!.AsObject();
        body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
        body["error"]!.GetValue<string>().Should().Be(error);
        body["error_description"]!.GetValue<string>().Should().Be(description);
    }

    private static void AssertBasicChallenge(HttpResponseMessage response) =>
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle().Which.Should().Be(Challenge);

    // ----- D-03 rows 1-7: one case per row, each rejected before the manager -----

    public static IEnumerable<TestFixtureData> RejectedRequests()
    {
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => new StringContent("{}", Encoding.UTF8, "application/json")),
            new[] { _validBasic },
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request body must be application/x-www-form-urlencoded.",
            false
        ).SetArgDisplayNames("row 1, no form content type");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() =>
                new MultipartFormDataContent { { new StringContent("x"), "token" } }
            ),
            new[] { _validBasic },
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request body must be application/x-www-form-urlencoded.",
            false
        ).SetArgDisplayNames("row 1, a well-formed multipart/form-data body");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => RawForm($"{new string('k', 2049)}=x&token=x")),
            new[] { _validBasic },
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request form payload is malformed.",
            false
        ).SetArgDisplayNames("row 2, malformed form payload");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() =>
                RawForm($"token=%GG&client_id={ClientId}&client_secret={ClientSecret}")
            ),
            Array.Empty<string>(),
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request form payload is malformed.",
            false
        ).SetArgDisplayNames("row 2a, malformed URL encoding");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => Form(WithFormCredentials(("token", Token), ("token", Token)))),
            Array.Empty<string>(),
            HttpStatusCode.BadRequest,
            "invalid_request",
            "A request parameter or header was included more than once.",
            false
        ).SetArgDisplayNames("row 3, duplicated token");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => Form(WithFormCredentials(("token", Token)))),
            new[] { _validBasic },
            HttpStatusCode.BadRequest,
            "invalid_request",
            "Only one client authentication mechanism may be used.",
            false
        ).SetArgDisplayNames("row 4, mixed mechanisms");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => Form(WithFormCredentials())),
            Array.Empty<string>(),
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The token parameter is missing.",
            false
        ).SetArgDisplayNames("row 5, no token");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => Form(("token", Token))),
            new[] { "Basic !!!!" },
            HttpStatusCode.Unauthorized,
            "invalid_client",
            InvalidCredentialsDescription,
            true
        ).SetArgDisplayNames("row 6, malformed Basic");
        yield return new TestFixtureData(
            new Func<HttpContent?>(() => Form(("token", Token))),
            Array.Empty<string>(),
            HttpStatusCode.BadRequest,
            "invalid_client",
            "Client authentication is required.",
            false
        ).SetArgDisplayNames("row 7, no credentials");
    }

    [TestFixtureSource(typeof(IntrospectionRequestContractTests), nameof(RejectedRequests))]
    public class Given_an_introspection_request_the_shared_checks_reject(
        Func<HttpContent?> body,
        string[] authorization,
        HttpStatusCode status,
        string error,
        string description,
        bool challenged
    ) : IntrospectionRequestFixture
    {
        protected override HttpContent? Body => body();

        protected override IEnumerable<string> AuthorizationValues => authorization;

        [Test]
        public void It_answers_the_oauth_error() => AssertOAuthError(status, error, description);

        [Test]
        public void It_sends_the_challenge_only_for_basic()
        {
            if (challenged)
            {
                AssertChallenge();
            }
            else
            {
                AssertNoChallenge();
            }
        }

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();
    }

    /// <summary>
    /// A bearer header is not client authentication here: it is ignored and, with no form
    /// credentials, the caller is unauthenticated.
    /// </summary>
    [TestFixture]
    public class Given_an_introspection_request_with_only_a_bearer_header : IntrospectionRequestFixture
    {
        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => ["Bearer some-access-token"];

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_client",
                "Client authentication is required."
            );

        [Test]
        public void It_does_not_call_the_manager() => AssertManagerNotCalled();
    }

    // ----- Manager outcomes -----

    [TestFixture]
    public class Given_a_basic_authenticated_introspection_the_manager_rejects : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() => new TokenIntrospectionResult.InvalidClient();

        protected override HttpContent? Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_401_invalid_client() =>
            AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_sends_the_basic_challenge() => AssertChallenge();
    }

    [TestFixture]
    public class Given_a_form_authenticated_introspection_the_manager_rejects : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() => new TokenIntrospectionResult.InvalidClient();

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();
    }

    [TestFixture]
    public class Given_an_introspection_the_manager_reports_inactive : IntrospectionRequestFixture
    {
        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);

        [Test]
        public void It_answers_exactly_active_false() => Content.Should().Be("""{"active":false}""");
    }

    [TestFixture]
    public class Given_an_introspection_the_manager_reports_active : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() =>
            new TokenIntrospectionResult.Active(ActiveToken);

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        private JsonElement Body_() => JsonDocument.Parse(Content).RootElement;

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);

        [Test]
        public void It_answers_json() =>
            Response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        [Test]
        public void It_answers_exactly_the_rfc_7662_members() =>
            Body_()
                .EnumerateObject()
                .Select(member => member.Name)
                .Should()
                .BeEquivalentTo(
                    "active",
                    "client_id",
                    "scope",
                    "exp",
                    "iat",
                    "sub",
                    "aud",
                    "iss",
                    "token_type"
                );

        [Test]
        public void It_reports_active_as_a_boolean() =>
            Body_().GetProperty("active").ValueKind.Should().Be(JsonValueKind.True);

        [Test]
        public void It_reports_exp_as_a_number()
        {
            Body_().GetProperty("exp").ValueKind.Should().Be(JsonValueKind.Number);
            Body_().GetProperty("exp").GetInt64().Should().Be(ActiveToken.Exp);
        }

        [Test]
        public void It_reports_iat_as_a_number()
        {
            Body_().GetProperty("iat").ValueKind.Should().Be(JsonValueKind.Number);
            Body_().GetProperty("iat").GetInt64().Should().Be(ActiveToken.Iat);
        }

        [Test]
        public void It_reports_the_client_id() =>
            Body_().GetProperty("client_id").GetString().Should().Be(ClientId);

        [Test]
        public void It_reports_the_scope() =>
            Body_().GetProperty("scope").GetString().Should().Be("edfi_admin_api/full_access");

        [Test]
        public void It_reports_the_subject_audience_and_issuer()
        {
            Body_().GetProperty("sub").GetString().Should().Be("subject-1");
            Body_().GetProperty("aud").GetString().Should().Be("audience-1");
            Body_().GetProperty("iss").GetString().Should().Be("issuer-1");
        }

        [Test]
        public void It_reports_the_bearer_token_type() =>
            Body_().GetProperty("token_type").GetString().Should().Be("Bearer");

        [Test]
        public void It_answers_a_single_audience_as_a_string_in_the_established_byte_form() =>
            Content
                .Should()
                .Be(
                    """{"active":true,"client_id":"introspect-client","scope":"edfi_admin_api/full_access","exp":1893456000,"iat":1893455100,"sub":"subject-1","aud":"audience-1","iss":"issuer-1","token_type":"Bearer"}"""
                );
    }

    [TestFixture]
    public class Given_an_introspection_the_manager_reports_active_without_an_audience
        : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() =>
            new TokenIntrospectionResult.Active(ActiveToken with { Aud = [] });

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_the_audience_as_null_in_the_established_byte_form() =>
            Content
                .Should()
                .Be(
                    """{"active":true,"client_id":"introspect-client","scope":"edfi_admin_api/full_access","exp":1893456000,"iat":1893455100,"sub":"subject-1","aud":null,"iss":"issuer-1","token_type":"Bearer"}"""
                );
    }

    /// <summary>RFC 7662 §2.2 and RFC 7519 §4.1.3: several audiences are an array, in the manager's order.</summary>
    [TestFixture]
    public class Given_an_introspection_the_manager_reports_active_with_several_audiences
        : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() =>
            new TokenIntrospectionResult.Active(ActiveToken with { Aud = ["introspect-client", "account"] });

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_the_audiences_as_an_array() =>
            Content
                .Should()
                .Be(
                    """{"active":true,"client_id":"introspect-client","scope":"edfi_admin_api/full_access","exp":1893456000,"iat":1893455100,"sub":"subject-1","aud":["introspect-client","account"],"iss":"issuer-1","token_type":"Bearer"}"""
                );
    }

    /// <summary>RFC 9449 §6.2: a DPoP-bound access token is reported with token_type DPoP, never Bearer.</summary>
    [TestFixture]
    public class Given_an_introspection_the_manager_reports_an_active_dpop_bound_token
        : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() =>
            new TokenIntrospectionResult.Active(ActiveToken with { TokenType = "DPoP" });

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_reports_the_dpop_token_type_in_the_established_byte_form() =>
            Content
                .Should()
                .Be(
                    """{"active":true,"client_id":"introspect-client","scope":"edfi_admin_api/full_access","exp":1893456000,"iat":1893455100,"sub":"subject-1","aud":"audience-1","iss":"issuer-1","token_type":"DPoP"}"""
                );
    }

    [TestFixture]
    public class Given_an_introspection_the_manager_cannot_complete : IntrospectionRequestFixture
    {
        protected override TokenIntrospectionResult Respond() =>
            new TokenIntrospectionResult.TemporarilyUnavailable("contract-boundary");

        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_503_temporarily_unavailable() =>
            AssertOAuthError(
                HttpStatusCode.ServiceUnavailable,
                "temporarily_unavailable",
                "Token introspection is temporarily unavailable. Retry the request."
            );

        [Test]
        public void It_does_not_claim_the_token_is_inactive() => Content.Should().NotContain("active");

        [Test]
        public void It_logs_the_sanitized_client_and_reason_at_warning() =>
            Logs
                .Records.Should()
                .ContainSingle(record =>
                    record.Level == LogLevel.Warning
                    && record.Message.Contains(ClientId)
                    && record.Message.Contains("contract-boundary")
                );

        [Test]
        public void It_does_not_log_the_secret_or_token() =>
            Logs.AllCapturedText().Should().NotContain(ClientSecret).And.NotContain(Token);
    }

    [TestFixture]
    public class Given_a_basic_authenticated_introspection_with_a_token_type_hint
        : IntrospectionRequestFixture
    {
        protected override HttpContent? Body => Form(("token", Token), ("token_type_hint", "refresh_token"));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_hands_the_manager_the_callers_id_secret_token_and_hint() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenIntrospectionRequest(ClientId, ClientSecret, Token, TokenTypeHint.RefreshToken));
    }

    [TestFixture]
    public class Given_a_form_authenticated_introspection : IntrospectionRequestFixture
    {
        protected override HttpContent? Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_hands_the_manager_the_callers_id_secret_and_token() =>
            Manager
                .Requests.Should()
                .ContainSingle()
                .Which.Should()
                .Be(new TokenIntrospectionRequest(ClientId, ClientSecret, Token, TokenTypeHint.None));
    }

    // ----- Keycloak mode: the shipped Keycloak manager, with Keycloak's answers scripted -----

    private const string KeycloakInactiveAnswer = """{"active":false}""";

    /// <summary>
    /// What Keycloak answers an active access token (I-01 on 26.1.4; 26.7.5 gives the same members with
    /// the owner in <c>aud</c>, I-02), Keycloak-only members included, for a token whose <c>azp</c> and <c>client_id</c> name <paramref name="azp"/>.
    /// </summary>
    private static string KeycloakActiveAnswer(string azp) =>
        $$$"""{"exp":1792607774,"iat":1792605974,"jti":"0f1e2d3c-jti","iss":"http://keycloak.test/realms/edfi","aud":"account","sub":"9f0c5a1e-subject","typ":"Bearer","azp":"{{{azp}}}","acr":"1","realm_access":{"roles":["offline_access"]},"resource_access":{"account":{"roles":["manage-account"]}},"scope":"profile email","namespacePrefixes":"uri://ed-fi.org","email_verified":false,"preferred_username":"service-account-{{{azp}}}","client_id":"{{{azp}}}","username":"service-account-{{{azp}}}","token_type":"Bearer","active":true}""";

    internal sealed record DelegatedRequest(
        Uri? Uri,
        string? Authorization,
        IReadOnlyList<KeyValuePair<string, string>> FormFields
    );

    /// <summary>
    /// Stands in for Keycloak's transport under the introspection manager's named client: records each
    /// delegated request, then answers it with the next scripted answer, repeating the last.
    /// </summary>
    internal sealed class ScriptedKeycloak(params (int Status, string Body)[] answers) : HttpMessageHandler
    {
        public List<DelegatedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(
                new DelegatedRequest(
                    request.RequestUri,
                    request.Headers.Authorization?.ToString(),
                    [
                        .. body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                            .Select(pair => pair.Split('=', 2))
                            .Select(parts => new KeyValuePair<string, string>(
                                WebUtility.UrlDecode(parts[0]),
                                WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : "")
                            )),
                    ]
                )
            );
            (int status, string answer) = answers[Math.Min(Requests.Count, answers.Length) - 1];
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// A Keycloak-mode host running the shipped <see cref="KeycloakTokenIntrospectionManager"/>; only the
    /// transport of its named client is replaced, by a <see cref="ScriptedKeycloak"/> answering as Keycloak
    /// did in the characterization (rows I-01 to I-27). The shared client-authentication seam, the
    /// delegation, the ownership filter and the endpoint's status and challenge rules are the production
    /// ones.
    /// </summary>
    public abstract class KeycloakIntrospectionFixture
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        private protected ScriptedKeycloak Keycloak { get; private set; } = null!;
        protected HttpResponseMessage Response { get; private set; } = null!;
        protected string Content { get; private set; } = null!;

        /// <summary>Keycloak's answers, one per delegated request, the last repeating.</summary>
        protected virtual (int Status, string Body)[] KeycloakAnswers => [(200, KeycloakInactiveAnswer)];

        protected abstract HttpContent Body { get; }

        protected virtual IEnumerable<string> AuthorizationValues => [];

        [SetUp]
        public async Task Setup()
        {
            Keycloak = new ScriptedKeycloak(KeycloakAnswers);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:IdentityProvider", "keycloak");
                builder.ConfigureServices(collection =>
                    collection
                        .AddHttpClient(KeycloakTokenIntrospectionManager.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => Keycloak)
                );
            });
            _client = _factory.CreateClient();
            (Response, Content) = await SendAsync(Body);
        }

        [TearDown]
        public void TearDown()
        {
            Response?.Dispose();
            _client?.Dispose();
            _factory?.Dispose();
            Keycloak?.Dispose();
        }

        protected async Task<(HttpResponseMessage Response, string Content)> SendAsync(HttpContent body)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/connect/introspect") { Content = body };
            string[] authorization = [.. AuthorizationValues];
            if (authorization.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization).Should().BeTrue();
            }

            HttpResponseMessage response = await _client.SendAsync(request);
            return (response, await response.Content.ReadAsStringAsync());
        }

        protected void AssertOAuthError(HttpStatusCode status, string error, string description) =>
            IntrospectionRequestContractTests.AssertOAuthErrorBody(
                Response,
                Content,
                status,
                error,
                description
            );

        protected void AssertChallenge() => IntrospectionRequestContractTests.AssertBasicChallenge(Response);

        protected void AssertNoChallenge() => Response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_keycloak_mode_and_an_introspection_without_client_credentials
        : KeycloakIntrospectionFixture
    {
        protected override HttpContent Body => Form(("token", Token));

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_client",
                "Client authentication is required."
            );

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_does_not_ask_keycloak() => Keycloak.Requests.Should().BeEmpty();
    }

    /// <summary>Keycloak 26.7.5 refuses a wrong secret with <c>invalid_client</c> (I-06).</summary>
    [TestFixture]
    public class Given_keycloak_mode_and_a_wrong_basic_secret : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers =>
            [(401, """{"error":"invalid_client","error_description":"Client authentication failed."}""")];

        protected override HttpContent Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues =>
            [$"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:wrong-secret"))}"];

        [Test]
        public void It_answers_401_invalid_client() =>
            AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_sends_the_basic_challenge() => AssertChallenge();

        [Test]
        public void It_lets_keycloak_judge_the_credentials_it_was_given() =>
            Keycloak
                .Requests.Should()
                .ContainSingle()
                .Which.FormFields.Should()
                .Contain(new KeyValuePair<string, string>("client_secret", "wrong-secret"));
    }

    /// <summary>Keycloak 26.1.4 refuses every authentication failure with <c>invalid_request</c> (I-06).</summary>
    [TestFixture]
    public class Given_keycloak_mode_and_a_wrong_form_secret : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers =>
            [(401, """{"error":"invalid_request","error_description":"Authentication failed."}""")];

        protected override HttpContent Body =>
            Form(("client_id", ClientId), ("client_secret", "wrong-secret"), ("token", Token));

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_client", InvalidCredentialsDescription);

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();
    }

    /// <summary>Keycloak refuses a public client itself, on both characterized versions (I-10).</summary>
    [TestFixture]
    public class Given_keycloak_mode_and_a_public_client : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers =>
            [(403, """{"error":"invalid_request","error_description":"Client not allowed."}""")];

        protected override HttpContent Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_400_invalid_client() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_client", InvalidCredentialsDescription);
    }

    [TestFixture]
    public class Given_keycloak_mode_and_the_owners_own_token : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers =>
            [(200, KeycloakActiveAnswer(ClientId))];

        protected override HttpContent Body => Form(("token", Token), ("token_type_hint", "access_token"));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);

        [Test]
        public void It_answers_only_the_rfc_7662_members_with_azp_as_the_client_id() =>
            Content
                .Should()
                .Be(
                    """{"active":true,"client_id":"introspect-client","scope":"profile email","exp":1792607774,"iat":1792605974,"sub":"9f0c5a1e-subject","aud":"account","iss":"http://keycloak.test/realms/edfi","token_type":"Bearer"}"""
                );

        [Test]
        public void It_delegates_once_to_the_realms_introspection_endpoint() =>
            Keycloak
                .Requests.Should()
                .ContainSingle()
                .Which.Uri.Should()
                .Be(new Uri("http://localhost/realms/dms/protocol/openid-connect/token/introspect"));

        [Test]
        public void It_delegates_the_basic_credentials_as_form_fields_with_the_token_only() =>
            Keycloak
                .Requests.Single()
                .FormFields.Should()
                .BeEquivalentTo([
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("client_secret", ClientSecret),
                    new KeyValuePair<string, string>("token", Token),
                ]);

        [Test]
        public void It_sends_keycloak_no_authorization_header() =>
            Keycloak.Requests.Single().Authorization.Should().BeNull();
    }

    /// <summary>
    /// RFC 7662 §2.2 makes <c>scope</c> optional, and Keycloak omits it for a token without scopes (I-27).
    /// The owner still receives its token's metadata, with the empty scope the self-contained provider
    /// reports for such a token.
    /// </summary>
    [TestFixture]
    public class Given_keycloak_mode_and_the_owners_own_token_without_a_scope : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers =>
            [(200, KeycloakActiveAnswerWithoutScope(ClientId))];

        protected override HttpContent Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [Test]
        public void It_answers_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);

        [Test]
        public void It_answers_the_rfc_7662_members_with_an_empty_scope() =>
            Content
                .Should()
                .Be(
                    """{"active":true,"client_id":"introspect-client","scope":"","exp":1792607774,"iat":1792605974,"sub":"9f0c5a1e-subject","aud":"account","iss":"http://keycloak.test/realms/edfi","token_type":"Bearer"}"""
                );

        /// <summary><see cref="KeycloakActiveAnswer"/> without <c>scope</c>.</summary>
        private static string KeycloakActiveAnswerWithoutScope(string azp)
        {
            JsonObject answer = JsonNode.Parse(KeycloakActiveAnswer(azp))!.AsObject();
            answer.Remove("scope");
            return answer.ToJsonString();
        }
    }

    /// <summary>
    /// Keycloak checks no ownership (I-03, I-04), so it answers another client's live token in full. CMS
    /// answers it exactly as it answers a token Keycloak does not know.
    /// </summary>
    [TestFixture]
    public class Given_keycloak_mode_and_another_clients_token : KeycloakIntrospectionFixture
    {
        private HttpResponseMessage _unknownTokenResponse = null!;
        private string _unknownTokenContent = null!;

        protected override (int Status, string Body)[] KeycloakAnswers =>
            [(200, KeycloakActiveAnswer("another-client")), (200, KeycloakInactiveAnswer)];

        protected override HttpContent Body => Form(("token", Token));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];

        [SetUp]
        public async Task IntrospectAnUnknownToken() =>
            (_unknownTokenResponse, _unknownTokenContent) = await SendAsync(Form(("token", "unknown-token")));

        [TearDown]
        public void DisposeUnknownTokenResponse() => _unknownTokenResponse?.Dispose();

        [Test]
        public void It_answers_200_with_exactly_active_false()
        {
            Response.StatusCode.Should().Be(HttpStatusCode.OK, Content);
            Content.Should().Be(KeycloakInactiveAnswer);
        }

        [Test]
        public void It_answers_byte_for_byte_what_an_unknown_token_gets()
        {
            Response.StatusCode.Should().Be(_unknownTokenResponse.StatusCode);
            Response
                .Content.Headers.ContentType.Should()
                .Be(_unknownTokenResponse.Content.Headers.ContentType);
            Content.Should().Be(_unknownTokenContent);
        }

        [Test]
        public void It_asked_keycloak_about_both_tokens() => Keycloak.Requests.Should().HaveCount(2);
    }

    [TestFixture]
    public class Given_keycloak_mode_and_keycloak_unusable : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers => [(500, "")];

        protected override HttpContent Body => Form(WithFormCredentials(("token", Token)));

        [Test]
        public void It_answers_503_temporarily_unavailable() =>
            AssertOAuthError(
                HttpStatusCode.ServiceUnavailable,
                "temporarily_unavailable",
                "Token introspection is temporarily unavailable. Retry the request."
            );

        [Test]
        public void It_does_not_claim_the_token_is_inactive() => Content.Should().NotContain("active");
    }

    /// <summary>
    /// Keycloak rejects any form value over its form attribute limit (128 KiB by default) with this
    /// answer before it authenticates the caller, with valid and wrong credentials alike (recorded on
    /// 26.1.4 and 26.7.5). The Configuration Service accepts form values up to 4 MiB, so the caller
    /// controls whether Keycloak sees one.
    /// </summary>
    private const string KeycloakFormLimitAnswer = """{"error":"HTTP 413 Request Entity Too Large"}""";

    /// <summary>One character over Keycloak's default form attribute limit of 131072 characters.</summary>
    private static readonly string _overFormLimit = new('x', 131073);

    public abstract class KeycloakRejectsAnOversizedValue : KeycloakIntrospectionFixture
    {
        protected override (int Status, string Body)[] KeycloakAnswers => [(413, KeycloakFormLimitAnswer)];

        /// <summary>The form field the caller made oversized.</summary>
        protected abstract string OversizedField { get; }

        [Test]
        public void It_answers_400_invalid_request() =>
            AssertOAuthError(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "The identity provider rejected the introspection request."
            );

        [Test]
        public void It_does_not_send_a_challenge() => AssertNoChallenge();

        [Test]
        public void It_forwarded_the_oversized_value_to_keycloak() =>
            Keycloak
                .Requests.Should()
                .ContainSingle()
                .Which.FormFields.Should()
                .Contain(new KeyValuePair<string, string>(OversizedField, _overFormLimit));
    }

    [TestFixture]
    public class Given_keycloak_mode_and_a_basic_caller_whose_token_is_over_keycloaks_form_limit
        : KeycloakRejectsAnOversizedValue
    {
        protected override string OversizedField => "token";

        protected override HttpContent Body => Form(("token", _overFormLimit));

        protected override IEnumerable<string> AuthorizationValues => [_validBasic];
    }

    [TestFixture]
    public class Given_keycloak_mode_and_a_form_caller_whose_secret_is_over_keycloaks_form_limit
        : KeycloakRejectsAnOversizedValue
    {
        protected override string OversizedField => "client_secret";

        protected override HttpContent Body =>
            Form(("client_id", ClientId), ("client_secret", _overFormLimit), ("token", Token));
    }

    [TestFixture]
    public class Given_keycloak_mode_and_an_introspection_without_a_token : KeycloakIntrospectionFixture
    {
        protected override HttpContent Body => Form();

        [Test]
        public void It_answers_400_invalid_request() =>
            AssertOAuthError(HttpStatusCode.BadRequest, "invalid_request", "The token parameter is missing.");

        [Test]
        public void It_does_not_ask_keycloak() => Keycloak.Requests.Should().BeEmpty();
    }

    // ----- Route metadata, the same under both identity providers -----

    [TestFixture("self-contained")]
    [TestFixture("keycloak")]
    public class Given_the_introspect_route(string identityProvider)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private IReadOnlyList<object> _metadata = null!;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:IdentityProvider", identityProvider);
            });
            _metadata =
            [
                .. _factory
                    .Services.GetRequiredService<EndpointDataSource>()
                    .Endpoints.OfType<RouteEndpoint>()
                    .Single(endpoint =>
                        endpoint.RoutePattern.RawText?.TrimStart('/') == "connect/introspect/{**contextPath}"
                    )
                    .Metadata,
            ];
        }

        [TearDown]
        public void TearDown() => _factory?.Dispose();

        [Test]
        public void It_carries_the_oauth_error_contract() =>
            _metadata.Should().Contain(OAuthErrorContractMetadata.Introspection);

        [Test]
        public void It_carries_exception_type_only_logging() =>
            _metadata.Should().Contain(ExceptionTypeOnlyLoggingMetadata.Instance);

        [Test]
        public void It_is_exempt_from_bearer_authentication() =>
            _metadata.Should().Contain(BearerAuthenticationExemptMetadata.Instance);

        [Test]
        public void It_is_described_as_requiring_client_credentials() =>
            _metadata.Should().Contain(ClientCredentialAuthenticationMetadata.Instance);
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

/// <summary>
/// An identity-provider rejection of the request itself (<c>invalid_scope</c>) is answered with a 400
/// <c>invalid_scope</c> carrying the provider's <c>error_description</c>, or a fixed description when the
/// provider sends none, through form credentials. A self-contained scope rejection answers the same code
/// with the fixed description (DMS-1492). The other provider failures are pinned by
/// <see cref="OAuthEndpointErrorTests"/>. Non-fixture container; the runnable fixtures are the nested
/// <c>Given_…</c> classes.
/// </summary>
public class TokenEndpointInvalidScopeTests
{
    public abstract class TokenFailureRequest
    {
        private const string UnreachableDatabase =
            "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        protected HttpResponseMessage Response = null!;
        protected string Body = string.Empty;

        protected abstract TokenResult Result { get; }
        protected abstract string IdentityProvider { get; }

        [SetUp]
        public async Task Act()
        {
            var tokenManager = A.Fake<ITokenManager>();
            A.CallTo(() =>
                    tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)
                )
                .Returns(Result);

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:IdentityProvider", IdentityProvider);
                builder.UseSetting("DatabaseSettings:DatabaseConnection", UnreachableDatabase);
                builder.ConfigureServices(collection =>
                {
                    collection.AddTransient((_) => tokenManager);
                });
            });
            _client = _factory.CreateClient();
            Response = await _client.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("client_id", "CSClient1"),
                    new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("scope", "edfi_admin_api/readonly_access"),
                ])
            );
            Body = await Response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            Response.Dispose();
            _client.Dispose();
            _factory.Dispose();
        }
    }

    /// <summary>A Keycloak failure, reported by the identity provider the manager calls.</summary>
    public abstract class ProviderFailureRequest : TokenFailureRequest
    {
        protected abstract IdentityProviderError ProviderError { get; }

        protected sealed override TokenResult Result =>
            new TokenResult.FailureIdentityProvider(ProviderError);
        protected sealed override string IdentityProvider => "keycloak";
    }

    [TestFixture]
    public class Given_the_self_contained_provider_rejects_the_scope : TokenFailureRequest
    {
        protected override TokenResult Result => new TokenResult.FailureInvalidScope();
        protected override string IdentityProvider => "self-contained";

        [Test]
        public void It_returns_400() => Response.StatusCode.Should().Be(HttpStatusCode.BadRequest, Body);

        [Test]
        public void It_returns_json() =>
            (Response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");

        [Test]
        public void It_returns_exactly_the_oauth_invalid_scope_error()
        {
            var expected = JsonNode.Parse(
                """
                {
                  "error": "invalid_scope",
                  "error_description": "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
                }
                """
            );
            JsonNode.DeepEquals(JsonNode.Parse(Body), expected).Should().BeTrue(Body);
        }

        [Test]
        public void It_issues_no_access_token() =>
            JsonNode.Parse(Body)!.AsObject().ContainsKey("access_token").Should().BeFalse(Body);

        [Test]
        public void It_carries_no_correlation_id() =>
            JsonNode.Parse(Body)!.AsObject().ContainsKey("correlationId").Should().BeFalse(Body);

        [Test]
        public void It_sends_no_authentication_challenge() =>
            Response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_identity_provider_rejects_the_scope : ProviderFailureRequest
    {
        protected override IdentityProviderError ProviderError =>
            new IdentityProviderError.InvalidScope(
                """{"error":"invalid_scope","error_description":"Invalid scopes: edfi_admin_api/readonly_access"}"""
            );

        [Test]
        public void It_returns_400() => Response.StatusCode.Should().Be(HttpStatusCode.BadRequest, Body);

        [Test]
        public void It_returns_json() =>
            (Response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");

        [Test]
        public void It_returns_the_oauth_body_with_the_provider_description()
        {
            JsonObject body = JsonNode.Parse(Body)!.AsObject();
            body.Select(member => member.Key).Should().BeEquivalentTo("error", "error_description");
            body["error"]!.GetValue<string>().Should().Be("invalid_scope");
            body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("Invalid scopes: edfi_admin_api/readonly_access");
        }
    }

    [TestFixture]
    public class Given_the_identity_provider_rejects_the_scope_without_a_description : ProviderFailureRequest
    {
        protected override IdentityProviderError ProviderError =>
            new IdentityProviderError.InvalidScope("""{"error":"invalid_scope"}""");

        [Test]
        public void It_returns_400() => Response.StatusCode.Should().Be(HttpStatusCode.BadRequest, Body);

        [Test]
        public void It_names_invalid_scope_with_a_fixed_description() =>
            JsonNode.Parse(Body)!["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
                );
    }

    [TestFixture]
    public class Given_the_identity_provider_rejects_the_scope_with_a_blank_description
        : ProviderFailureRequest
    {
        protected override IdentityProviderError ProviderError =>
            new IdentityProviderError.InvalidScope("""{"error":"invalid_scope","error_description":" "}""");

        [Test]
        public void It_names_invalid_scope_with_a_fixed_description() =>
            JsonNode.Parse(Body)!["error_description"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."
                );
    }
}

/// <summary>
/// The token endpoint served by the real self-contained token manager and the real client secret
/// hasher at a non-default iteration count. An unknown client, a wrong secret and an unapproved
/// client all receive the same invalid_client body: 401 with the Basic challenge through HTTP Basic,
/// 400 through the form. The requests share one host, because each host start is costly and every
/// request is independent of the others.
/// </summary>
[TestFixture]
public class Given_TokenEndpoint_requests_that_fail_client_authentication
{
    private const string AbsentClientId = "absent-client";
    private const string KnownClientId = "known-client";
    private const string KnownSecret = "Known-Client-Secret-0123456789";
    private const string UnapprovedClientId = "unapproved-client";
    private const string UnapprovedSecret = "Unapproved-Client-Secret-0123456789";
    private const string WrongSecret = "Wrong-Secret-0123456789";
    private const string Scope = "edfi_admin_api/full_access";

    private const string ExpectedBody = """
        {
          "error": "invalid_client",
          "error_description": "Invalid client or Invalid client credentials"
        }
        """;

    private static readonly (string Failure, string ClientId, string Secret)[] _failureCases =
    [
        ("an absent client", AbsentClientId, KnownSecret),
        ("a wrong secret", KnownClientId, WrongSecret),
        ("an unapproved client with its secret", UnapprovedClientId, UnapprovedSecret),
        ("an unapproved client with a wrong secret", UnapprovedClientId, WrongSecret),
    ];

    private static readonly string[] _credentialPaths = ["form", "basic"];

    public static IEnumerable<TestCaseData> FailureRequests() =>
        from failureCase in _failureCases
        from path in _credentialPaths
        select new TestCaseData(failureCase.Failure, path);

    private sealed record Observed(HttpStatusCode Status, string? MediaType, string Challenge, JsonNode Body);

    /// <summary>Every failure case through both credential paths, and the one successful grant.</summary>
    private static readonly int _requestCount = (_failureCases.Length * _credentialPaths.Length) + 1;

    private IOpenIddictTokenRepository _tokenRepository = null!;
    private readonly Dictionary<(string Failure, string Path), Observed> _failures = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Observed _success = null!;

    private static ApplicationInfo Application(string clientId, string storedHash, bool isApproved) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ClientSecret = storedHash,
            IsApproved = isApproved,
            ProtocolMappers = "[]",
            Permissions = [Scope],
            Scopes = [Scope],
        };

    /// <summary>A usable signing key, no roles, and a store that accepts the token.</summary>
    private void ArrangeIssuance()
    {
        using RSA rsa = RSA.Create(2048);
        A.CallTo(() => _tokenRepository.GetActivePrivateKeyAsync(A<string>._))
            .Returns(
                new PrivateKeyInfo
                {
                    PrivateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
                    KeyId = Guid.NewGuid().ToString(),
                }
            );
        A.CallTo(() => _tokenRepository.GetClientRolesAsync(A<Guid>._)).Returns(Enumerable.Empty<string>());
        A.CallTo(() =>
                _tokenRepository.StoreTokenAsync(
                    A<Guid>._,
                    A<Guid>._,
                    A<string>._,
                    A<DateTimeOffset>._,
                    A<int>._,
                    A<string>._
                )
            )
            .Returns(TokenStoreOutcome.Stored);
    }

    private async Task<Observed> RequestToken(string clientId, string secret, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/connect/token");
        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "client_credentials"),
            new("scope", Scope),
        ];
        if (path == "basic")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"))
            );
        }
        else
        {
            form.Add(new("client_id", clientId));
            form.Add(new("client_secret", secret));
        }

        request.Content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await _client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        return new Observed(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.WwwAuthenticate.ToString(),
            JsonNode.Parse(content)!
        );
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        ClientSecretHasher hasher = RevocationOwnershipTests.CreateRealHasher(
            RevocationOwnershipTests.RealHasherIterations,
            NullLogger<ClientSecretHasher>.Instance
        );
        _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
        ApplicationInfo known = Application(
            KnownClientId,
            await hasher.HashSecretAsync(KnownSecret),
            isApproved: true
        );
        ApplicationInfo unapproved = Application(
            UnapprovedClientId,
            await hasher.HashSecretAsync(UnapprovedSecret),
            isApproved: false
        );
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(KnownClientId)).Returns(known);
        A.CallTo(() => _tokenRepository.GetClientCredentialAsync(AbsentClientId))
            .Returns((ClientCredential?)null);
        A.CallTo(() => _tokenRepository.GetClientCredentialAsync(KnownClientId))
            .Returns(new ClientCredential(known.Id, known.ClientId, known.ClientSecret, known.IsApproved));
        A.CallTo(() => _tokenRepository.GetClientCredentialAsync(UnapprovedClientId))
            .Returns(
                new ClientCredential(
                    unapproved.Id,
                    unapproved.ClientId,
                    unapproved.ClientSecret,
                    unapproved.IsApproved
                )
            );
        ArrangeIssuance();

        _factory = RevocationOwnershipTests.CreateFactory(_tokenRepository, hasher);
        _client = _factory.CreateClient();

        foreach ((string failure, string clientId, string secret) in _failureCases)
        {
            foreach (string path in _credentialPaths)
            {
                _failures[(failure, path)] = await RequestToken(clientId, secret, path);
            }
        }

        _success = await RequestToken(KnownClientId, KnownSecret, "form");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestCaseSource(nameof(FailureRequests))]
    public void It_answers_401_through_basic_and_400_through_the_form(string failure, string path) =>
        _failures[(failure, path)]
            .Status.Should()
            .Be(path == "basic" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest);

    [TestCaseSource(nameof(FailureRequests))]
    public void It_challenges_only_the_basic_attempt(string failure, string path) =>
        _failures[(failure, path)]
            .Challenge.Should()
            .Be(path == "basic" ? "Basic realm=\"EdFi.DmsConfigurationService\"" : string.Empty);

    [TestCaseSource(nameof(FailureRequests))]
    public void It_answers_in_the_oauth_error_format(string failure, string path) =>
        _failures[(failure, path)].MediaType.Should().Be("application/json");

    [TestCaseSource(nameof(FailureRequests))]
    public void It_returns_the_exact_invalid_client_body(string failure, string path)
    {
        JsonNode actual = _failures[(failure, path)].Body;

        JsonNode.DeepEquals(actual, JsonNode.Parse(ExpectedBody)).Should().BeTrue(actual.ToJsonString());
    }

    [Test]
    public void It_returns_one_body_for_every_failure() =>
        _failures.Values.Select(observed => observed.Body.ToJsonString()).Distinct().Should().ContainSingle();

    [Test]
    public void It_still_issues_a_token_to_an_approved_client_presenting_its_secret() =>
        _success.Status.Should().Be(HttpStatusCode.OK, _success.Body.ToJsonString());

    [Test]
    public void It_returns_an_access_token_to_that_client() =>
        _success.Body["access_token"]!.GetValue<string>().Should().NotBeNullOrEmpty();

    [Test]
    public void It_authenticates_every_request_from_the_credential() =>
        A.CallTo(() => _tokenRepository.GetClientCredentialAsync(A<string>._))
            .MustHaveHappened(_requestCount, Times.Exactly);

    /// <summary>
    /// Only the successful grant loads the full application; a failed authentication never does.
    /// </summary>
    [Test]
    public void It_loads_the_full_application_only_for_the_successful_grant() =>
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(A<string>._))
            .MustHaveHappenedOnceExactly();
}

/// <summary>
/// A replacement client secret hasher with its own stored format, behind the real token manager
/// over HTTP, registered as the host's hasher, as a plugin registers one. An unknown client is still
/// answered invalid_client, and its presented secret is verified against the placeholder the host
/// generated through the replacement at startup, in the replacement's format: the host assumes
/// nothing about the built-in hasher's format, and the request generates nothing.
/// </summary>
[TestFixture]
public class Given_TokenEndpoint_requests_with_a_replacement_client_secret_hasher
{
    private const string AbsentClientId = "absent-client";
    private const string PresentedSecret = "absent-client-secret";

    /// <summary>Stores "custom:" followed by the hex SHA-256 of the secret.</summary>
    private sealed class PrefixedSha256Hasher : IClientSecretHasher
    {
        public const string Prefix = "custom:";

        private static string Hash(string plainTextSecret) =>
            Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainTextSecret)));

        public Task<string> HashSecretAsync(string plainTextSecret) => Task.FromResult(Hash(plainTextSecret));

        public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret) =>
            Task.FromResult(
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Hash(plainTextSecret)),
                    Encoding.UTF8.GetBytes(hashedSecret)
                )
            );

        public bool IsSecretHashed(string secret) => secret.StartsWith(Prefix, StringComparison.Ordinal);
    }

    private readonly IOpenIddictTokenRepository _tokenRepository =
        RevocationOwnershipTests.NewTokenRepository();
    private IClientSecretHasher _hasher = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private int _hashesBeforeTheRequest;
    private HttpResponseMessage _response = null!;
    private JsonNode _body = null!;

    [SetUp]
    public async Task Setup()
    {
        _hasher = A.Fake<IClientSecretHasher>(options => options.Wrapping(new PrefixedSha256Hasher()));
        A.CallTo(() => _tokenRepository.GetApplicationByClientIdAsync(AbsentClientId))
            .Returns((ApplicationInfo?)null);

        _factory = RevocationOwnershipTests
            .CreateFactory(_tokenRepository, _hasher)
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.AddSingleton(_hasher))
            );
        _client = _factory.CreateClient();
        _hashesBeforeTheRequest = Fake.GetCalls(_hasher)
            .Count(call => call.Method.Name == nameof(IClientSecretHasher.HashSecretAsync));
        _response = await _client.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", AbsentClientId),
                new KeyValuePair<string, string>("client_secret", PresentedSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            ])
        );
        _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!;
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_answers_400_to_form_credentials() =>
        _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Test]
    public void It_reports_invalid_client()
    {
        _body["error"]!.GetValue<string>().Should().Be("invalid_client");
        _body["error_description"]!
            .GetValue<string>()
            .Should()
            .Be("Invalid client or Invalid client credentials");
    }

    [Test]
    public void It_generates_the_placeholder_through_the_replacement_at_startup() =>
        _hashesBeforeTheRequest.Should().Be(1);

    [Test]
    public void It_hashes_nothing_for_the_request() =>
        A.CallTo(() => _hasher.HashSecretAsync(A<string>._)).MustHaveHappenedOnceExactly();

    [Test]
    public void It_verifies_against_a_placeholder_in_the_replacements_format() =>
        A.CallTo(() =>
                _hasher.VerifySecretAsync(
                    PresentedSecret,
                    A<string>.That.StartsWith(PrefixedSha256Hasher.Prefix)
                )
            )
            .MustHaveHappenedOnceExactly();
}
