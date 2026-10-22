// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
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
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// Drives the real CMS pipeline to verify OAuth responses and authorization-required responses carry
/// exactly one no-store Cache-Control value, and that anonymous informational routes are left alone.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_ResponseCachePolicyTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _authenticatedClient = null!;
    private HttpClient _anonymousClient = null!;
    private HttpResponseMessage _vendors = null!;
    private HttpResponseMessage _tokenSuccess = null!;
    private HttpResponseMessage _tokenInvalidClient = null!;
    private HttpResponseMessage _register = null!;
    private HttpResponseMessage _health = null!;
    private HttpResponseMessage _root = null!;
    private HttpResponseMessage _openApi = null!;
    private HttpResponseMessage _openIdConfiguration = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        var vendorRepository = A.Fake<IVendorRepository>();
        A.CallTo(() => vendorRepository.QueryVendor(A<VendorQuery>.Ignored))
            .Returns(new VendorQueryResult.Success([]));

        var clientRepository = A.Fake<IIdentityProviderRepository>();
        A.CallTo(() =>
                clientRepository.CreateClientAsync(
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

        A.CallTo(() => clientRepository.GetAllClientsAsync()).Returns(new ClientClientsResult.Success([]));

        var tokenManager = A.Fake<ITokenManager>();
        A.CallTo(() => tokenManager.GetAccessTokenAsync(ForClient("GoodClient")))
            .Returns(
                new TokenResult.Success(
                    """{"access_token":"input123token","expires_in":900,"token_type":"bearer"}"""
                )
            );
        A.CallTo(() => tokenManager.GetAccessTokenAsync(ForClient("BadClient")))
            .Returns(new TokenResult.FailureAuthentication("invalid_client", "Invalid client"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
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

                    collection
                        .AddTransient(_ => vendorRepository)
                        .AddTransient(_ => clientRepository)
                        .AddTransient(_ => tokenManager)
                        .AddTransient(_ => new TokenRequest.Validator())
                        .AddTransient(_ => new RegisterRequest.Validator(
                            Options.Create(
                                new ClientSecretValidationOptions { MinimumLength = 8, MaximumLength = 12 }
                            )
                        ));
                }
            );
        });

        _authenticatedClient = _factory.CreateClient();
        _authenticatedClient.DefaultRequestHeaders.Add("X-Test-Scope", "edfi_admin_api/full_access");
        _anonymousClient = _factory.CreateClient();

        _vendors = await _authenticatedClient.GetAsync("/v3/vendors");
        _tokenSuccess = await _anonymousClient.PostAsync("/connect/token", TokenForm("GoodClient"));
        _tokenInvalidClient = await _anonymousClient.PostAsync("/connect/token", TokenForm("BadClient"));
        _register = await _anonymousClient.PostAsync(
            "/connect/register",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("clientid", "CSClient1"),
                new KeyValuePair<string, string>("clientsecret", "test123@Puiu"),
                new KeyValuePair<string, string>("displayname", "CSClient1"),
            ])
        );
        _health = await _anonymousClient.GetAsync("/health");
        _root = await _anonymousClient.GetAsync("/");
        _openApi = await _anonymousClient.GetAsync("/openapi/v1.json");
        _openIdConfiguration = await _anonymousClient.GetAsync("/.well-known/openid-configuration");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        foreach (
            var response in new[]
            {
                _vendors,
                _tokenSuccess,
                _tokenInvalidClient,
                _register,
                _health,
                _root,
                _openApi,
                _openIdConfiguration,
            }
        )
        {
            response?.Dispose();
        }

        _authenticatedClient?.Dispose();
        _anonymousClient?.Dispose();
        _factory?.Dispose();
    }

    private static IEnumerable<KeyValuePair<string, string>> ForClient(string clientId) =>
        A<IEnumerable<KeyValuePair<string, string>>>.That.Matches(fields =>
            fields.Any(pair => pair.Key == "client_id" && pair.Value == clientId)
        );

    private static FormUrlEncodedContent TokenForm(string clientId) =>
        new([
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", "test123@Puiu"),
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
        ]);

    private static void ShouldHaveSingleNoStore(HttpResponseMessage response) =>
        response.Headers.GetValues("Cache-Control").Should().Equal("no-store");

    private static void ShouldHaveNoPragma(HttpResponseMessage response) =>
        response.Headers.Contains("Pragma").Should().BeFalse();

    private static void ShouldHaveNoStoreAndPragma(HttpResponseMessage response)
    {
        ShouldHaveSingleNoStore(response);
        response.Headers.GetValues("Pragma").Should().Equal("no-cache");
    }

    [Test]
    public void It_marks_an_authenticated_vendor_list_no_store_without_pragma()
    {
        _vendors.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveSingleNoStore(_vendors);
        ShouldHaveNoPragma(_vendors);
    }

    [Test]
    public void It_marks_a_successful_token_response_no_store_with_pragma()
    {
        _tokenSuccess.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveNoStoreAndPragma(_tokenSuccess);
    }

    [Test]
    public void It_marks_an_invalid_client_token_response_no_store_with_pragma()
    {
        _tokenInvalidClient.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ShouldHaveNoStoreAndPragma(_tokenInvalidClient);
    }

    [Test]
    public void It_marks_a_register_response_no_store_with_pragma()
    {
        _register.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveNoStoreAndPragma(_register);
    }

    [Test]
    public void It_leaves_the_health_response_without_cache_control()
    {
        _health.StatusCode.Should().Be(HttpStatusCode.OK);
        _health.Headers.Contains("Cache-Control").Should().BeFalse();
    }

    [Test]
    public void It_leaves_the_root_response_without_cache_control()
    {
        _root.StatusCode.Should().Be(HttpStatusCode.OK);
        _root.Headers.Contains("Cache-Control").Should().BeFalse();
    }

    [Test]
    public void It_leaves_the_openapi_response_without_cache_control()
    {
        _openApi.StatusCode.Should().Be(HttpStatusCode.OK);
        _openApi.Headers.Contains("Cache-Control").Should().BeFalse();
    }

    [Test]
    public void It_keeps_a_single_no_store_on_the_openid_configuration_response_without_pragma()
    {
        _openIdConfiguration.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveSingleNoStore(_openIdConfiguration);
        ShouldHaveNoPragma(_openIdConfiguration);
    }
}
