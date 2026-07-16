// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
        content.Should().Contain("Identity provider error during client registration");
        content.Should().NotContain("Unauthorized");
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
        content.Should().Contain("Identity provider error during client registration");
        content.Should().NotContain("Forbidden");
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
                "Identity provider error during client registration"
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
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var actualResponse = JsonNode.Parse(content);
        actualResponse!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:security:authorization",
              "title": "Authorization Failed",
              "status": 403,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
                "Registration is disabled."
              ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
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
                "Identity provider error during client registration"
            ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }
}

[TestFixture]
public class Given_A_Registration_Request_Whose_Form_Cannot_Be_Read
{
    private static readonly ClientSecretValidationOptions ValidationOptions = new()
    {
        MinimumLength = 8,
        MaximumLength = 12,
    };

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                // A one-value form limit makes ReadFormAsync throw while parsing the multi-field body.
                collection.Configure<FormOptions>(options => options.ValueCountLimit = 1);
                collection.AddTransient(_ => new RegisterRequest.Validator(
                    Options.Create(ValidationOptions)
                ));
                // The repository is never reached: the form-read failure short-circuits ahead of it.
                collection.AddTransient(_ => A.Fake<IIdentityProviderRepository>());
            });
        });
        _client = _factory.CreateClient();
        // Three form values exceed the configured limit of one, so ReadFormAsync throws InvalidDataException.
        _response = await _client.PostAsync(
            "/connect/register",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("clientid", "CSClient1"),
                new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
                new KeyValuePair<string, string>("displayname", "CSClient1"),
            ])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_the_ed_fi_bad_request_contract()
    {
        // An unreadable form is a client bad request under the Ed-Fi contract, not a 500, and the framework
        // message and raw request values are never surfaced. /connect/register is not an OAuth endpoint.
        _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var actualResponse = JsonNode.Parse(_content);
        actualResponse!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request was invalid.",
              "type": "urn:ed-fi:api:bad-request",
              "title": "Bad Request",
              "status": 400,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": []
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }
}

[TestFixture]
public class Given_Registration_Is_Disabled_And_The_Form_Cannot_Be_Read
{
    private static readonly ClientSecretValidationOptions ValidationOptions = new()
    {
        MinimumLength = 8,
        MaximumLength = 12,
    };

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        // Registration disabled AND a one-value form limit that would make ReadFormAsync throw. The disabled
        // response must be returned before the body is read, so the unreadable form never produces a
        // framework error — proving the disabled check short-circuits ahead of the form read.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.Configure<IdentitySettings>(opts => opts.AllowRegistration = false);
                collection.Configure<FormOptions>(options => options.ValueCountLimit = 1);
                collection.AddTransient(_ => new RegisterRequest.Validator(
                    Options.Create(ValidationOptions)
                ));
                collection.AddTransient(_ => A.Fake<IIdentityProviderRepository>());
            });
        });
        _client = _factory.CreateClient();
        // Three form values would exceed the limit of one if the form were read.
        _response = await _client.PostAsync(
            "/connect/register",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("clientid", "CSClient2"),
                new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
                new KeyValuePair<string, string>("displayname", "CSClient2@cs.com"),
            ])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_the_disabled_registration_contract_without_reading_the_form()
    {
        // The exact disabled-registration 403 contract, not a 400/500 from a form-read failure.
        _response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var actualResponse = JsonNode.Parse(_content);
        actualResponse!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        var expectedResponse = JsonNode.Parse(
            """
            {
              "detail": "The request could not be processed. See 'errors' for details.",
              "type": "urn:ed-fi:api:security:authorization",
              "title": "Authorization Failed",
              "status": 403,
              "correlationId": "{correlationId}",
              "validationErrors": {},
              "errors": [
                "Registration is disabled."
              ]
            }
            """.Replace("{correlationId}", actualResponse!["correlationId"]!.GetValue<string>())
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }
}

/// <summary>
/// Shared harness for /connect/token tests. Every failure returns the OAuth 2.0 protocol contract
/// (application/json { error, error_description }, RFC 6749 section 5.2) rather than the Ed-Fi Problem
/// Details contract, and never leaks provider, database, or exception detail.
/// </summary>
public abstract class TokenEndpointTestBase
{
    protected ITokenManager TokenManager = null!;
    protected HttpResponseMessage Response = null!;
    protected string RawBody = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void BaseSetup() => TokenManager = A.Fake<ITokenManager>();

    [TearDown]
    public void BaseTearDown()
    {
        Response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    protected void ArrangeTokenResult(TokenResult tokenResult) =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .Returns(tokenResult);

    protected HttpClient CreateClient(string identityProvider = "self-contained")
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:IdentityProvider"] = identityProvider }
                )
            );
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => TokenManager);
            });
        });
        _client = _factory.CreateClient();
        return _client;
    }

    // Builds a client whose host rejects any request form carrying more than one value, so a form-read
    // failure (InvalidDataException from the form reader) can be exercised without a real oversized body —
    // the in-memory test server does not enforce request-body-size limits.
    protected HttpClient CreateClientWithSingleValueFormLimit()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => TokenManager);
                collection.Configure<FormOptions>(options => options.ValueCountLimit = 1);
            });
        });
        _client = _factory.CreateClient();
        return _client;
    }

    // Builds a client whose OAuth form read throws a BadHttpRequestException carrying the given HTTP status,
    // modelling the framework's request-body-size rejection (413 Payload Too Large) that the in-memory test
    // server cannot itself raise. Exercises the TryReadOAuthFormAsync catch and its status preservation.
    protected HttpClient CreateClientThatFailsFormReadWith(int statusCode)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => TokenManager);
                collection.AddSingleton<IStartupFilter>(new ThrowingFormStartupFilter(statusCode));
            });
        });
        _client = _factory.CreateClient();
        return _client;
    }

    protected async Task PostTokenRequestAsync(
        HttpClient client,
        params KeyValuePair<string, string>[] fields
    )
    {
        Response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(fields));
        RawBody = await Response.Content.ReadAsStringAsync();
    }

    protected void AssertOAuthError(HttpStatusCode status, string error, string description)
    {
        Response.StatusCode.Should().Be(status);
        Response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = JsonNode.Parse(RawBody)!;
        body["error"]!.GetValue<string>().Should().Be(error);
        body["error_description"]!.GetValue<string>().Should().Be(description);

        // The OAuth error contract must carry none of the Ed-Fi Problem Details members.
        body["type"].Should().BeNull();
        body["title"].Should().BeNull();
        body["status"].Should().BeNull();
        body["correlationId"].Should().BeNull();
        body["validationErrors"].Should().BeNull();
        body["errors"].Should().BeNull();
    }

    protected void AssertBasicAuthChallenge() =>
        Response.Headers.WwwAuthenticate.Should().Contain(header => header.Scheme == "Basic");

    private const string SuccessTokenJson = """
        {
            "access_token":"input123token",
            "expires_in":900,
            "token_type":"bearer"
        }
        """;

    protected static TokenResult SuccessResult() => new TokenResult.Success(SuccessTokenJson);
}

[TestFixture]
public class Given_A_Valid_Client_Credentials_Token_Request : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

    [Test]
    public void It_returns_the_access_token()
    {
        RawBody.Should().Contain("input123token");
        RawBody.Should().Contain("bearer");
    }
}

[TestFixture]
public class Given_Basic_Auth_Credentials_With_Reserved_Characters : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        var encoded = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("client%3Awith%2Breserved:secret%3Awith%25reserved%2Bchars")
        );
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

    [Test]
    public void It_decodes_the_reserved_characters_before_calling_the_token_manager() =>
        A.CallTo(() =>
                TokenManager.GetAccessTokenAsync(
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

[TestFixture]
public class Given_A_Token_Request_Missing_The_Client_Id : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_www_authenticate_challenge() => AssertBasicAuthChallenge();
}

[TestFixture]
public class Given_A_Token_Request_Missing_The_Client_Secret : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("grant_type", "client_credentials")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_www_authenticate_challenge() => AssertBasicAuthChallenge();
}

[TestFixture]
public class Given_A_Token_Request_Missing_The_Grant_Type : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error() =>
        AssertOAuthError(
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request is missing a required parameter or is otherwise malformed."
        );
}

[TestFixture]
public class Given_A_Token_Request_With_An_Unsupported_Grant_Type : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "authorization_code"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_unsupported_grant_type_error() =>
        AssertOAuthError(
            HttpStatusCode.BadRequest,
            "unsupported_grant_type",
            "The specified grant type is not supported."
        );
}

[TestFixture]
public class Given_A_Token_Request_Whose_Form_Cannot_Be_Read : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // A success is arranged so that, if the unreadable form were somehow processed, the request would
        // succeed — proving the form-read failure is what produces the error rather than a fallback path.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClientWithSingleValueFormLimit();
        // Two form values exceed the configured limit of one, so ReadFormAsync throws while parsing.
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error() =>
        AssertOAuthError(
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request is missing a required parameter or is otherwise malformed."
        );

    [Test]
    public void It_does_not_call_the_token_manager() =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_A_Token_Request_Whose_Form_Read_Exceeds_The_Size_Limit : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // A success is arranged so that, if the oversized form were somehow processed, the request would
        // succeed — proving the form-read failure is what produces the error rather than a fallback path.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClientThatFailsFormReadWith(StatusCodes.Status413PayloadTooLarge);
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_preserves_the_413_status_on_the_oauth_invalid_request_error() =>
        AssertOAuthError(
            HttpStatusCode.RequestEntityTooLarge,
            "invalid_request",
            "The request is missing a required parameter or is otherwise malformed."
        );

    [Test]
    public void It_does_not_call_the_token_manager() =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_Invalid_Client_Credentials_Supplied_Through_Form : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // The provider surfaces an OAuth-shaped message; it must be logged server-side, not echoed.
        ArrangeTokenResult(
            new TokenResult.FailureIdentityProvider(
                new IdentityProviderError.Unauthorized(
                    """
                    {"error":"invalid_client","error_description":"Invalid client or Invalid client credentials"}
                    """
                )
            )
        );
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "wrong"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_www_authenticate_challenge() => AssertBasicAuthChallenge();

    [Test]
    public void It_does_not_leak_the_provider_message() =>
        RawBody.Should().NotContain("Invalid client or Invalid client credentials");
}

[TestFixture]
public class Given_Invalid_Client_Credentials_Supplied_Through_Basic_Authentication : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(
            new TokenResult.FailureIdentityProvider(
                new IdentityProviderError.InvalidClient("Invalid client or Invalid client credentials")
            )
        );
        var client = CreateClient("self-contained");
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CSClient1:wrong"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_www_authenticate_challenge() => AssertBasicAuthChallenge();

    [Test]
    public void It_does_not_leak_the_provider_message() =>
        RawBody.Should().NotContain("Invalid client or Invalid client credentials");
}

[TestFixture]
public class Given_The_Provider_Reports_The_Client_As_Forbidden : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(
            new TokenResult.FailureIdentityProvider(
                new IdentityProviderError.Forbidden("Insufficient permissions for this client")
            )
        );
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_does_not_leak_the_provider_message() =>
        RawBody.Should().NotContain("Insufficient permissions");
}

[TestFixture]
public class Given_The_Provider_Is_Unreachable : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(
            new TokenResult.FailureIdentityProvider(
                new IdentityProviderError.Unreachable(
                    "No connection could be made because the target machine actively refused it."
                )
            )
        );
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );

    [Test]
    public void It_does_not_leak_the_provider_message() =>
        RawBody.Should().NotContain("target machine actively refused");
}

[TestFixture]
public class Given_The_Provider_Returns_Not_Found : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(
            new TokenResult.FailureIdentityProvider(
                new IdentityProviderError.NotFound(
                    """
                    { "error":"Realm does not exist","error_description":"For more on this error consult the server log at the debug level."}
                    """
                )
            )
        );
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );

    [Test]
    public void It_does_not_leak_the_provider_message() =>
        RawBody.Should().NotContain("Realm does not exist");
}

[TestFixture]
public class Given_The_Token_Request_Fails_Unexpectedly : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(
            new TokenResult.FailureUnknown(
                "No connection could be made because the target machine actively refused it."
            )
        );
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );

    [Test]
    public void It_does_not_leak_the_failure_message() =>
        RawBody.Should().NotContain("target machine actively refused");
}

[TestFixture]
public class Given_Basic_Authentication_In_Keycloak_Mode : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // Basic client authentication is honored in Keycloak mode: the parsed credentials are forwarded
        // through the token manager exactly as form credentials are.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient("keycloak");
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CSClient1:test123@Puiu"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

    [Test]
    public void It_forwards_the_basic_credentials_to_the_token_manager() =>
        A.CallTo(() =>
                TokenManager.GetAccessTokenAsync(
                    A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(credentials =>
                        credentials.Any(pair => pair.Key == "client_id" && pair.Value == "CSClient1")
                        && credentials.Any(pair =>
                            pair.Key == "client_secret" && pair.Value == "test123@Puiu"
                        )
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
}

[TestFixture]
public class Given_Basic_Authentication_In_Self_Contained_Mode : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient("self-contained");
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CSClient1:test123@Puiu"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

    [Test]
    public void It_forwards_the_basic_credentials_to_the_token_manager() =>
        A.CallTo(() =>
                TokenManager.GetAccessTokenAsync(
                    A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(credentials =>
                        credentials.Any(pair => pair.Key == "client_id" && pair.Value == "CSClient1")
                        && credentials.Any(pair =>
                            pair.Key == "client_secret" && pair.Value == "test123@Puiu"
                        )
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
}

[TestFixture]
public class Given_A_Basic_Header_That_Is_Not_Valid_Base64 : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // A success is arranged so that a fallback to any other credential source would succeed; the
        // malformed header must be rejected instead.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic @@@not-base64@@@");
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_full_basic_challenge_with_realm()
    {
        Response.Headers.WwwAuthenticate.Should().ContainSingle();
        var challenge = Response.Headers.WwwAuthenticate.Single();
        challenge.Scheme.Should().Be("Basic");
        challenge.Parameter.Should().Be("realm=\"Ed-Fi DMS Configuration Service\"");
    }

    [Test]
    public void It_does_not_call_the_token_manager() =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_A_Basic_Credential_Without_A_Colon_Separator : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("no-colon-credentials"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_www_authenticate_challenge() => AssertBasicAuthChallenge();
}

[TestFixture]
public class Given_Both_Basic_And_Form_Client_Credentials : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // RFC 6749 section 2.3 forbids using more than one client-authentication mechanism per request.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CSClient1:test123@Puiu"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error() =>
        AssertOAuthError(
            HttpStatusCode.BadRequest,
            "invalid_request",
            "The request is missing a required parameter or is otherwise malformed."
        );

    [Test]
    public void It_does_not_call_the_token_manager() =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_An_Unsupported_Authorization_Scheme : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // Arrange a success so that, if the endpoint wrongly fell back to another credential source, the
        // request would succeed — proving the unsupported scheme is rejected instead.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "Digest username=\"CSClient1\""
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_includes_the_full_basic_challenge_with_realm()
    {
        Response.Headers.WwwAuthenticate.Should().ContainSingle();
        var challenge = Response.Headers.WwwAuthenticate.Single();
        challenge.Scheme.Should().Be("Basic");
        challenge.Parameter.Should().Be("realm=\"Ed-Fi DMS Configuration Service\"");
    }

    [Test]
    public void It_does_not_call_the_token_manager() =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_An_Unsupported_Authorization_Scheme_With_Form_Credentials : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // A non-Basic Authorization header must not be ignored in favor of form credentials: this is a
        // failed header authentication, not an accepted form authentication.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Digest realm=\"x\"");
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_invalid_client_error() =>
        AssertOAuthError(HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");

    [Test]
    public void It_does_not_accept_the_form_credentials() =>
        A.CallTo(() => TokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_Basic_Credentials_With_A_Form_Encoded_Space : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // RFC 6749 section 2.3.1 encodes the client id and secret with application/x-www-form-urlencoded,
        // where a space is "+". The parser must decode "+" back to a space.
        ArrangeTokenResult(SuccessResult());
        var client = CreateClient();
        var encoded = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("CSClient1:secret+with+spaces")
        );
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            encoded
        );
        await PostTokenRequestAsync(
            client,
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

    [Test]
    public void It_decodes_the_plus_signs_to_spaces_before_calling_the_token_manager() =>
        A.CallTo(() =>
                TokenManager.GetAccessTokenAsync(
                    A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(credentials =>
                        credentials.Any(pair =>
                            pair.Key == "client_secret" && pair.Value == "secret with spaces"
                        )
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
}

[TestFixture]
public class Given_The_Provider_Returns_A_Success_That_Is_Not_Valid_Json : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(new TokenResult.Success("this is not json"));
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );

    [Test]
    public void It_does_not_leak_the_provider_payload() => RawBody.Should().NotContain("this is not json");
}

[TestFixture]
public class Given_The_Provider_Returns_A_Json_Null_Success : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(new TokenResult.Success("null"));
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );
}

[TestFixture]
public class Given_The_Provider_Returns_A_Success_Without_An_Access_Token : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        ArrangeTokenResult(new TokenResult.Success("""{ "token_type": "bearer", "expires_in": 900 }"""));
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );

    [Test]
    public void It_does_not_return_a_token_response() => RawBody.Should().NotContain("access_token");
}

[TestFixture]
public class Given_The_Provider_Returns_A_Success_Without_A_Token_Type : TokenEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // RFC 6749 §5.1 requires token_type in addition to access_token.
        ArrangeTokenResult(new TokenResult.Success("""{ "access_token": "abc123token" }"""));
        var client = CreateClient();
        await PostTokenRequestAsync(
            client,
            new("client_id", "CSClient1"),
            new("client_secret", "test123@Puiu"),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access")
        );
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() =>
        AssertOAuthError(
            HttpStatusCode.ServiceUnavailable,
            "temporarily_unavailable",
            "The authorization server is temporarily unable to handle the request."
        );

    [Test]
    public void It_does_not_return_a_token_response() => RawBody.Should().NotContain("abc123token");
}

[TestFixture]
public class Given_An_Introspect_Request_Without_A_Token
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
        );
        _client = _factory.CreateClient();
        _response = await _client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token_type_hint", "access_token")])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var actualResponse = JsonNode.Parse(_content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "error": "invalid_request",
              "error_description": "The token parameter is missing."
            }
            """
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }
}

[TestFixture]
public class Given_An_Introspect_Request_Whose_Form_Cannot_Be_Read
{
    private IEnhancedTokenValidator _tokenValidator = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        // A token validator is registered so that, if the form-read guard failed and execution fell
        // through to the introspection branch, ValidateTokenAsync would be invoked — letting the
        // It_does_not_introspect_the_token assertion detect an unintended introspection.
        _tokenValidator = A.Fake<IEnhancedTokenValidator>();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => _tokenValidator);
                collection.Configure<FormOptions>(options => options.ValueCountLimit = 1);
            });
        });
        _client = _factory.CreateClient();
        // Two form values exceed the configured limit of one, so ReadFormAsync throws while parsing.
        _response = await _client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", "some-token"),
                new KeyValuePair<string, string>("token_type_hint", "access_token"),
            ])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var actualResponse = JsonNode.Parse(_content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "error": "invalid_request",
              "error_description": "The request is missing a required parameter or is otherwise malformed."
            }
            """
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public void It_does_not_introspect_the_token() =>
        A.CallTo(() => _tokenValidator.ValidateTokenAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_An_Introspect_Request_Whose_Form_Read_Exceeds_The_Size_Limit
{
    private IEnhancedTokenValidator _tokenValidator = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        // A token validator is registered so that, if the form-read guard failed and execution fell through
        // to the introspection branch, ValidateTokenAsync would be invoked — letting the
        // It_does_not_introspect_the_token assertion detect an unintended introspection.
        _tokenValidator = A.Fake<IEnhancedTokenValidator>();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => _tokenValidator);
                collection.AddSingleton<IStartupFilter>(
                    new ThrowingFormStartupFilter(StatusCodes.Status413PayloadTooLarge)
                );
            });
        });
        _client = _factory.CreateClient();
        // The form read throws a BadHttpRequestException carrying 413, modelling an oversized body.
        _response = await _client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", "some-token")])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_preserves_the_413_status_on_the_oauth_invalid_request_error()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var actualResponse = JsonNode.Parse(_content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "error": "invalid_request",
              "error_description": "The request is missing a required parameter or is otherwise malformed."
            }
            """
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public void It_does_not_introspect_the_token() =>
        A.CallTo(() => _tokenValidator.ValidateTokenAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_A_Revoke_Request_Without_A_Token
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        var tokenManager = A.Fake<ITokenManager>();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection => collection.AddTransient(_ => tokenManager));
        });
        _client = _factory.CreateClient();
        _response = await _client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token_type_hint", "access_token")])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var actualResponse = JsonNode.Parse(_content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "error": "invalid_request",
              "error_description": "The token parameter is missing."
            }
            """
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }
}

public abstract class RevokeEndpointTestBase
{
    protected HttpResponseMessage Response = null!;
    protected string RawBody = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [TearDown]
    public void TearDown()
    {
        Response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    protected async Task PostRevokeAsync(ITokenManager tokenManager)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection => collection.AddTransient(_ => tokenManager));
        });
        _client = _factory.CreateClient();
        Response = await _client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", "some-token")])
        );
        RawBody = await Response.Content.ReadAsStringAsync();
    }

    protected void AssertTemporarilyUnavailable()
    {
        Response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        Response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = JsonNode.Parse(RawBody)!;
        body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
        body["error_description"]!
            .GetValue<string>()
            .Should()
            .Be("The authorization server is temporarily unable to handle the request.");
    }
}

[TestFixture]
public class Given_A_Token_Is_Revoked_Successfully : RevokeEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        var tokenManager = A.Fake<ITokenManager>(x => x.Implements<ITokenRevocationManager>());
        A.CallTo(() => ((ITokenRevocationManager)tokenManager).RevokeTokenAsync(A<string>._)).Returns(true);
        await PostRevokeAsync(tokenManager);
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);
}

[TestFixture]
public class Given_A_Revocation_Request_For_An_Unknown_Token : RevokeEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // RFC 7009 §2.2: an invalid or unknown token is still a successful revocation (200).
        var tokenManager = A.Fake<ITokenManager>(x => x.Implements<ITokenRevocationManager>());
        A.CallTo(() => ((ITokenRevocationManager)tokenManager).RevokeTokenAsync(A<string>._)).Returns(false);
        await PostRevokeAsync(tokenManager);
    }

    [Test]
    public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);
}

[TestFixture]
public class Given_The_Revocation_Service_Fails : RevokeEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        var tokenManager = A.Fake<ITokenManager>(x => x.Implements<ITokenRevocationManager>());
        A.CallTo(() => ((ITokenRevocationManager)tokenManager).RevokeTokenAsync(A<string>._))
            .Throws(new InvalidOperationException("database unavailable"));
        await PostRevokeAsync(tokenManager);
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() => AssertTemporarilyUnavailable();

    [Test]
    public void It_does_not_leak_the_failure() => RawBody.Should().NotContain("database unavailable");
}

[TestFixture]
public class Given_The_Provider_Does_Not_Support_Revocation : RevokeEndpointTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // A plain ITokenManager (e.g. the Keycloak manager) does not implement ITokenRevocationManager.
        await PostRevokeAsync(A.Fake<ITokenManager>());
    }

    [Test]
    public void It_returns_the_oauth_temporarily_unavailable_error() => AssertTemporarilyUnavailable();
}

[TestFixture]
public class Given_A_Revoke_Request_Whose_Form_Cannot_Be_Read
{
    private ITokenManager _tokenManager = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        // The fake implements ITokenRevocationManager so that, if the form-read guard failed and execution
        // fell through to the revocation branch, RevokeTokenAsync would be invoked — letting the
        // It_does_not_revoke_the_token assertion detect an unintended, state-changing revocation.
        _tokenManager = A.Fake<ITokenManager>(x => x.Implements<ITokenRevocationManager>());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => _tokenManager);
                collection.Configure<FormOptions>(options => options.ValueCountLimit = 1);
            });
        });
        _client = _factory.CreateClient();
        // Two form values exceed the configured limit of one, so ReadFormAsync throws while parsing.
        _response = await _client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", "some-token"),
                new KeyValuePair<string, string>("token_type_hint", "access_token"),
            ])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_returns_the_oauth_invalid_request_error()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var actualResponse = JsonNode.Parse(_content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "error": "invalid_request",
              "error_description": "The request is missing a required parameter or is otherwise malformed."
            }
            """
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public void It_does_not_revoke_the_token() =>
        A.CallTo(() => ((ITokenRevocationManager)_tokenManager).RevokeTokenAsync(A<string>._))
            .MustNotHaveHappened();
}

[TestFixture]
public class Given_A_Revoke_Request_Whose_Form_Read_Exceeds_The_Size_Limit
{
    private ITokenManager _tokenManager = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpResponseMessage _response = null!;
    private string _content = null!;

    [SetUp]
    public async Task Setup()
    {
        // The fake implements ITokenRevocationManager so that, if the form-read guard failed and execution
        // fell through to the revocation branch, RevokeTokenAsync would be invoked — letting the
        // It_does_not_revoke_the_token assertion detect an unintended, state-changing revocation.
        _tokenManager = A.Fake<ITokenManager>(x => x.Implements<ITokenRevocationManager>());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient(_ => _tokenManager);
                collection.AddSingleton<IStartupFilter>(
                    new ThrowingFormStartupFilter(StatusCodes.Status413PayloadTooLarge)
                );
            });
        });
        _client = _factory.CreateClient();
        // The form read throws a BadHttpRequestException carrying 413, modelling an oversized body.
        _response = await _client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", "some-token")])
        );
        _content = await _response.Content.ReadAsStringAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _response?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void It_preserves_the_413_status_on_the_oauth_invalid_request_error()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var actualResponse = JsonNode.Parse(_content);
        var expectedResponse = JsonNode.Parse(
            """
            {
              "error": "invalid_request",
              "error_description": "The request is missing a required parameter or is otherwise malformed."
            }
            """
        );
        JsonNode.DeepEquals(actualResponse, expectedResponse).Should().Be(true);
    }

    [Test]
    public void It_does_not_revoke_the_token() =>
        A.CallTo(() => ((ITokenRevocationManager)_tokenManager).RevokeTokenAsync(A<string>._))
            .MustNotHaveHappened();
}

// Replaces the request's form feature with one that throws a BadHttpRequestException carrying a chosen HTTP
// status (for example 413 Payload Too Large) when the endpoint reads the form. The in-memory test server
// does not enforce request-body-size limits, so a real oversized body cannot raise the framework 413 status
// on its own. This reproduces that exact failure so the OAuth form-read path can be exercised and its
// status preservation asserted. Registered as an IStartupFilter so the feature is set before the endpoint.
internal sealed class ThrowingFormStartupFilter(int statusCode) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(
                (context, nextMiddleware) =>
                {
                    context.Features.Set<IFormFeature>(new ThrowingFormFeature(statusCode));
                    return nextMiddleware();
                }
            );
            next(app);
        };

    private sealed class ThrowingFormFeature(int statusCode) : IFormFeature
    {
        public bool HasFormContentType => true;
        public IFormCollection? Form { get; set; }

        public IFormCollection ReadForm() => throw BuildException();

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) =>
            throw BuildException();

        private BadHttpRequestException BuildException() =>
            new("Request body too large. The max request body size is 30000000 bytes.", statusCode);
    }
}
