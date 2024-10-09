// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class RegisterEndpointTests
{
    private IClientRepository? _clientRepository;

    [SetUp]
    public void Setup()
    {
        _clientRepository = A.Fake<IClientRepository>();
        A.CallTo(() => _clientRepository.CreateClientAsync(A<string>.Ignored, A<string>.Ignored, A<string>.Ignored)).Returns(Task.FromResult(true));
        var clientList = A.Fake<IEnumerable<string>>();
        A.CallTo(() => _clientRepository.GetAllClientsAsync()).Returns(Task.FromResult(clientList));
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
                    collection.AddTransient((x) => new RegisterRequest.Validator(_clientRepository!));
                    collection.AddTransient((x) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient1", clientsecret = "test123@Puiu", displayname = "CSClient1" };
        var response = await client.PostAsJsonAsync("/connect/register", requestContent);
        var content = await response.Content.ReadAsStringAsync();

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
                    collection.AddTransient((x) => new RegisterRequest.Validator(_clientRepository!));
                    collection.AddTransient((x) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "", clientsecret = "", displayname = "" };
        var response = await client.PostAsJsonAsync("/connect/register", requestContent);
        var content = await response.Content.ReadAsStringAsync();
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
                    collection.AddTransient((x) => new RegisterRequest.Validator(_clientRepository!));
                    collection.AddTransient((x) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient2", clientsecret = secret, displayname = "CSClient2@cs.com" };
        var response = await client.PostAsJsonAsync("/connect/register", requestContent);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content.Should().Contain("Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 8 to 12 characters long.");
    }

    [Test]
    public async Task When_error_from_backend()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _clientRepository = A.Fake<IClientRepository>();
            A.CallTo(() => _clientRepository.CreateClientAsync(A<string>.Ignored, A<string>.Ignored, A<string>.Ignored))
            .Throws(new Exception("Error from Keycloak"));
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => new RegisterRequest.Validator(_clientRepository!));
                    collection.AddTransient((x) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient3", clientsecret = "test123@Puiu", displayname = "CSClient3" };
        var response = await client.PostAsJsonAsync("/connect/register", requestContent);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        content.Should().Contain("Error from Keycloak");
    }

    [Test]
    public async Task Given_client_with_existing_client_id()
    {
        // Arrange
        var clientList = A.Fake<IEnumerable<string>>();
        _clientRepository = A.Fake<IClientRepository>();
        clientList = clientList.Append("CSClient2");
        A.CallTo(() => _clientRepository.GetAllClientsAsync()).Returns(Task.FromResult(clientList));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => new RegisterRequest.Validator(_clientRepository!));
                    collection.AddTransient((x) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient2", clientsecret = "test123@Puiu", displayname = "CSClient2@cs.com" };
        var response = await client.PostAsJsonAsync("/connect/register", requestContent);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content.Should().Contain("Client with the same Client Id already exists. Please provide different Client Id.");
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
                    collection.AddTransient((x) => new RegisterRequest.Validator(_clientRepository!));
                    collection.AddTransient((x) => _clientRepository!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient2", clientsecret = "test123@Puiu", displayname = "CSClient2@cs.com" };
        var response = await client.PostAsJsonAsync("/connect/register", requestContent);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
        var token =
            """
            {
                "access_token":"input123token",
                "expires_in":900,
                "token_type":"bearer"
            }
            """;
        A.CallTo(() => _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored)).Returns(Task.FromResult(token));
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
                    collection.AddTransient((x) => new TokenRequest.Validator());
                    collection.AddTransient((x) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient1", clientsecret = "test123@Puiu" };
        var response = await client.PostAsJsonAsync("/connect/token", requestContent);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().NotBeNull();
        content.Should().Contain("input123token");
        content.Should().Contain("bearer");
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
                    collection.AddTransient((x) => new TokenRequest.Validator());
                    collection.AddTransient((x) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "", clientsecret = "" };
        var response = await client.PostAsJsonAsync("/connect/token", requestContent);
        var content = await response.Content.ReadAsStringAsync();
        content = System.Text.RegularExpressions.Regex.Unescape(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        content.Should().Contain("'Client Id' must not be empty.");
        content.Should().Contain("'Client Secret' must not be empty.");
    }


    [Test]
    public async Task When_error_from_backend()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _tokenManager = A.Fake<ITokenManager>();
            A.CallTo(() => _tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>.Ignored))
            .Throws(new Exception("Error from Keycloak"));

            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => new TokenRequest.Validator());
                    collection.AddTransient((x) => _tokenManager!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var requestContent = new { clientid = "CSClient3", clientsecret = "test123@Puiu" };
        var response = await client.PostAsJsonAsync("/connect/token", requestContent);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        content.Should().Contain("Error from Keycloak");
    }
}
