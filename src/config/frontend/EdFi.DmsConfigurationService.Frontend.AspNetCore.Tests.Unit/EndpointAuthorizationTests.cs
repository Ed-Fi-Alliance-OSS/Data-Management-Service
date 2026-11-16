// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

public class EndpointAuthorizationTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S3398",
        Justification = "Shared production composition for inventory and forthcoming fallback fixtures."
    )]
    private static WebApplicationFactory<Program> CreateFactory(string identityProvider, bool multiTenancy) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSettings:IdentityProvider"] = identityProvider,
                        ["AppSettings:MultiTenancy"] = multiTenancy.ToString(),
                        ["Cors:SwaggerUIOrigin"] = "http://localhost:8082",
                    }
                )
            );
            builder.ConfigureServices(services =>
                services.Configure<AppSettings>(options =>
                {
                    options.IdentityProvider = identityProvider;
                    options.MultiTenancy = multiTenancy;
                })
            );
        });

    private static string NormalizePattern(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return "/";
        }
        return rawText.StartsWith('/') ? rawText : "/" + rawText;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S3398",
        Justification = "Keeps the independent anonymous inventory projection beside shared route normalization."
    )]
    private static string[] AnonymousMethodTemplates(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
                methods.Should().NotBeNull();
                methods!.HttpMethods.Should().NotBeEmpty();
                return methods.HttpMethods.Select(method =>
                    $"{method} {NormalizePattern(endpoint.RoutePattern.RawText!)}"
                );
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    [TestFixture("self-contained", false)]
    [TestFixture("self-contained", true)]
    [TestFixture("keycloak", false)]
    [TestFixture("keycloak", true)]
    public class Given_the_production_endpoint_inventory(string identityProvider, bool multiTenancy)
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private RouteEndpoint[] _endpoints = null!;

        [OneTimeSetUp]
        public void Setup()
        {
            _factory = CreateFactory(identityProvider, multiTenancy);
            _client = _factory.CreateClient();
            _endpoints = _factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .ToArray();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _client.Dispose();
            _factory.Dispose();
        }

        [Test]
        public void It_requires_exclusive_authorization_metadata()
        {
            _endpoints.Should().NotBeEmpty();
            foreach (var endpoint in _endpoints)
            {
                bool anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                bool authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
                (anonymous ^ authorized)
                    .Should()
                    .BeTrue(
                        "endpoint {0} requires exactly one authorization metadata kind",
                        endpoint.DisplayName
                    );
            }
        }

        [Test]
        public void It_preserves_the_exact_anonymous_method_and_template_set()
        {
            _endpoints.Should().NotBeEmpty();
            string[] expected =
            [
                "GET /",
                "GET /health",
                "GET /tenancy",
                "GET /metadata/specifications",
                "GET /.well-known/openiddict-status",
                "GET /openapi/{documentName}.json",
                "POST /connect/register/{**contextPath}",
                "POST /connect/token/{**contextPath}",
                "POST /connect/introspect/{**contextPath}",
                "POST /connect/revoke/{**contextPath}",
            ];
            if (identityProvider == "self-contained")
            {
                expected =
                [
                    .. expected,
                    "GET /.well-known/jwks.json",
                    "GET /.well-known/openid-configuration",
                ];
            }
            AnonymousMethodTemplates(_endpoints).Should().Equal(expected.Order(StringComparer.Ordinal));
        }

        [Test]
        public void It_keeps_tenant_management_protected_and_configuration_gated()
        {
            _endpoints.Should().NotBeEmpty();
            var tenants = _endpoints
                .Where(endpoint =>
                    NormalizePattern(endpoint.RoutePattern.RawText!) is var path
                    && (path == "/v3/tenants" || path.StartsWith("/v3/tenants/", StringComparison.Ordinal))
                )
                .ToArray();
            tenants.Any().Should().Be(multiTenancy);
            foreach (var endpoint in tenants)
            {
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().NotBeEmpty();
                endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
            }
        }
    }
}
