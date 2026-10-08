// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public abstract class OpenIdConfigurationTestBase
{
    protected static WebApplicationFactory<Program> CreateFactory(
        string authority,
        string? pathBase,
        bool multiTenancy = false
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:IdentityProvider"] = "self-contained",
                            ["AppSettings:MultiTenancy"] = multiTenancy.ToString(),
                            ["AppSettings:PathBase"] = pathBase,
                            ["IdentitySettings:Authority"] = authority,
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                services.Configure<AppSettings>(settings => settings.MultiTenancy = multiTenancy);
            });
        });
    }

    protected static async Task<(
        HttpResponseMessage Response,
        byte[] Body,
        JsonDocument Document
    )> FetchAsync(HttpClient client, string requestUri)
    {
        var response = await client.GetAsync(requestUri);
        var body = await response.Content.ReadAsByteArrayAsync();
        return (response, body, JsonDocument.Parse(body));
    }

    protected static void AssertPropertyNames(JsonDocument document, params string[] expected)
    {
        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(expected);
    }
}

[TestFixture]
public class Given_OpenIdConfiguration_With_A_Configured_Base_And_Untrusted_Request_Authority
    : OpenIdConfigurationTestBase
{
    private HttpResponseMessage _firstResponse = null!;
    private HttpResponseMessage _secondResponse = null!;
    private byte[] _firstBody = null!;
    private byte[] _secondBody = null!;
    private JsonDocument _document = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = CreateFactory("https://identity.example:8443/root/", "/config/");
        using var client = factory.CreateClient();
        (_firstResponse, _firstBody, _document) = await FetchAsync(
            client,
            "http://attacker-one.example:18080/config/.well-known/openid-configuration"
        );
        (_secondResponse, _secondBody, _) = await FetchAsync(
            client,
            "http://attacker-two.example:18080/config/.well-known/openid-configuration"
        );
    }

    [Test]
    public void It_returns_ok_for_both_requests() =>
        (_firstResponse.StatusCode, _secondResponse.StatusCode)
            .Should()
            .Be((HttpStatusCode.OK, HttpStatusCode.OK));

    [Test]
    public void It_returns_byte_identical_bodies() => _secondBody.Should().Equal(_firstBody);

    [Test]
    public void It_preserves_the_configured_issuer() =>
        _document
            .RootElement.GetProperty("issuer")
            .GetString()
            .Should()
            .Be("https://identity.example:8443/root/");

    [Test]
    public void It_uses_the_configured_base_for_all_endpoints()
    {
        var expectedBase = "https://identity.example:8443/root/config";
        foreach (
            var (name, suffix) in new[]
            {
                ("token_endpoint", "/connect/token"),
                ("registration_endpoint", "/connect/register"),
                ("jwks_uri", "/.well-known/jwks.json"),
                ("introspection_endpoint", "/connect/introspect"),
                ("revocation_endpoint", "/connect/revoke"),
            }
        )
        {
            _document.RootElement.GetProperty(name).GetString().Should().Be(expectedBase + suffix);
        }
    }

    [Test]
    public void It_preserves_the_document_contract()
    {
        foreach (var name in new[] { "authorization_endpoint", "userinfo_endpoint", "end_session_endpoint" })
        {
            _document.RootElement.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Null);
        }

        AssertPropertyNames(
            _document,
            "issuer",
            "authorization_endpoint",
            "token_endpoint",
            "userinfo_endpoint",
            "jwks_uri",
            "registration_endpoint",
            "introspection_endpoint",
            "revocation_endpoint",
            "end_session_endpoint",
            "scopes_supported",
            "response_types_supported",
            "response_modes_supported",
            "grant_types_supported",
            "token_endpoint_auth_methods_supported",
            "token_endpoint_auth_signing_alg_values_supported",
            "id_token_signing_alg_values_supported",
            "claims_supported",
            "subject_types_supported",
            "code_challenge_methods_supported",
            "frontchannel_logout_supported",
            "frontchannel_logout_session_supported",
            "backchannel_logout_supported",
            "backchannel_logout_session_supported",
            "request_parameter_supported",
            "request_uri_parameter_supported",
            "require_request_uri_registration",
            "claims_parameter_supported",
            "introspection_endpoint_auth_methods_supported",
            "revocation_endpoint_auth_methods_supported"
        );
    }

    [Test]
    public void It_disables_storage_without_varying_by_host()
    {
        foreach (var response in new[] { _firstResponse, _secondResponse })
        {
            response.Headers.CacheControl.Should().NotBeNull();
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            response
                .Headers.Vary.Should()
                .NotContain(value => value.Equals("Host", StringComparison.OrdinalIgnoreCase));
        }
    }
}

[TestFixture]
public class Given_OpenIdConfiguration_With_An_Empty_PathBase : OpenIdConfigurationTestBase
{
    private HttpResponseMessage _response = null!;
    private JsonDocument _document = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = CreateFactory("https://identity.example:8443/root/", null);
        using var client = factory.CreateClient();
        (_response, _, _document) = await FetchAsync(
            client,
            "http://attacker.example:18080/.well-known/openid-configuration"
        );
    }

    [Test]
    public void It_uses_the_authority_root_for_endpoints()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.OK);
        _document
            .RootElement.GetProperty("token_endpoint")
            .GetString()
            .Should()
            .Be("https://identity.example:8443/root/connect/token");
        _document
            .RootElement.GetProperty("jwks_uri")
            .GetString()
            .Should()
            .Be("https://identity.example:8443/root/.well-known/jwks.json");
    }
}

[TestFixture]
public class Given_MultiTenant_OpenIdConfiguration_With_PathBase_And_No_Tenant_Header
    : OpenIdConfigurationTestBase
{
    private HttpResponseMessage _response = null!;
    private JsonDocument _document = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = CreateFactory("https://identity.example:8443", "mt-config", true);
        using var client = factory.CreateClient();
        (_response, _, _document) = await FetchAsync(
            client,
            "http://attacker.example:18080/mt-config/.well-known/openid-configuration"
        );
    }

    [Test]
    public void It_succeeds_without_a_tenant_header_and_uses_path_base()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.OK);
        _document
            .RootElement.GetProperty("token_endpoint")
            .GetString()
            .Should()
            .Be("https://identity.example:8443/mt-config/connect/token");
    }
}
