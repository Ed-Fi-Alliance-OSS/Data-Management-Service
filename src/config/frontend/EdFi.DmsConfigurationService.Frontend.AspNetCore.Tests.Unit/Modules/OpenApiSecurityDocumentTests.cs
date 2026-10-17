// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// The generated /openapi/v1.json document describes client-credentials sign-in itself: a security
/// scheme whose token URL is the request base the document is served for, a root requirement on it,
/// and an empty requirement on every operation that needs no token.
/// </summary>
public class OpenApiSecurityDocumentTests
{
    private const string SchemeName = "oauth2_client_credentials";

    private static readonly string[] HttpMethodNames =
    [
        "get",
        "put",
        "post",
        "delete",
        "patch",
        "head",
        "options",
        "trace",
    ];

    private static async Task<JsonDocument> FetchDocumentAsync(
        bool multiTenancy = false,
        string? authority = null,
        string? host = null
    )
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(configuration =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["AppSettings:MultiTenancy"] = multiTenancy.ToString(),
                    ["AppSettings:PathBase"] = "config",
                };
                if (authority is not null)
                {
                    settings["IdentitySettings:Authority"] = authority;
                }

                configuration.AddInMemoryCollection(settings);
            });
        });
        using var client = factory.CreateClient();
        if (host is not null)
        {
            client.DefaultRequestHeaders.Host = host;
        }

        var response = await client.GetAsync("/config/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<(string Key, JsonElement Operation)> Operations(JsonDocument document) =>
        document
            .RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path =>
                path.Value.EnumerateObject()
                    .Where(op => HttpMethodNames.Contains(op.Name))
                    .Select(op => ($"{op.Name.ToUpperInvariant()} {path.Name}", op.Value))
            );

    private static string[] OperationsWithEmptySecurity(JsonDocument document) =>
        [
            .. Operations(document)
                .Where(op =>
                    op.Operation.TryGetProperty("security", out var security)
                    && security.ValueKind == JsonValueKind.Array
                    && security.GetArrayLength() == 0
                )
                .Select(op => op.Key)
                .Order(),
        ];

    [TestFixture]
    public class Given_the_document_served_under_a_path_base
    {
        private JsonDocument _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _document = await FetchDocumentAsync();
        }

        [TearDown]
        public void TearDown() => _document.Dispose();

        private JsonElement Flow =>
            _document
                .RootElement.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty(SchemeName)
                .GetProperty("flows")
                .GetProperty("clientCredentials");

        [Test]
        public void It_titles_the_document_as_the_configuration_service()
        {
            var info = _document.RootElement.GetProperty("info");
            info.GetProperty("title").GetString().Should().Be("Ed-Fi API Configuration Service API");
            info.GetProperty("description")
                .GetString()
                .Should()
                .Be("API for configuring and managing the Ed-Fi API.");
            info.GetProperty("contact")
                .GetProperty("url")
                .GetString()
                .Should()
                .Be("https://www.ed-fi.org/what-is-ed-fi/contact/");
            info.GetProperty("version").GetString().Should().Be("1");
        }

        [Test]
        public void It_declares_an_oauth2_scheme()
        {
            _document
                .RootElement.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty(SchemeName)
                .GetProperty("type")
                .GetString()
                .Should()
                .Be("oauth2");
        }

        [Test]
        public void It_builds_the_token_url_from_the_server_url()
        {
            var serverUrl = _document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString();

            Flow.GetProperty("tokenUrl").GetString().Should().Be(serverUrl + "/connect/token");
        }

        [Test]
        public void It_builds_an_absolute_token_url_with_the_path_base()
        {
            Flow.GetProperty("tokenUrl").GetString().Should().Be("http://localhost/config/connect/token");
        }

        [Test]
        public void It_lists_every_authorization_scope_with_its_description()
        {
            var scopes = Flow.GetProperty("scopes")
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString());

            scopes
                .Should()
                .BeEquivalentTo(AuthorizationScopes.AllScopes.ToDictionary(s => s.Name, s => s.Description));
        }

        [Test]
        public void It_requires_the_scheme_at_the_root_without_scopes()
        {
            var security = _document.RootElement.GetProperty("security");

            security.GetArrayLength().Should().Be(1);
            var requirement = security[0].EnumerateObject().ToArray();
            requirement.Select(p => p.Name).Should().Equal(SchemeName);
            requirement[0].Value.GetArrayLength().Should().Be(0);
        }

        [Test]
        public void It_marks_exactly_the_anonymous_operations_with_an_empty_requirement()
        {
            OperationsWithEmptySecurity(_document)
                .Should()
                .Equal(
                    new[]
                    {
                        "GET /",
                        "GET /.well-known/jwks.json",
                        "GET /.well-known/openid-configuration",
                        "GET /.well-known/openiddict-status",
                        "GET /health",
                        "GET /tenancy",
                        "POST /connect/introspect/{contextPath}",
                        "POST /connect/register/{contextPath}",
                        "POST /connect/revoke/{contextPath}",
                        "POST /connect/token/{contextPath}",
                    }.Order()
                );
        }

        [Test]
        public void It_leaves_every_other_operation_without_a_security_property()
        {
            var anonymous = OperationsWithEmptySecurity(_document);
            var others = Operations(_document).Where(op => !anonymous.Contains(op.Key)).ToArray();

            // The root requirement they inherit must exist, or "no property" would prove nothing.
            _document.RootElement.TryGetProperty("security", out _).Should().BeTrue();
            others.Should().NotBeEmpty();
            others
                .Where(op => op.Operation.TryGetProperty("security", out _))
                .Select(op => op.Key)
                .Should()
                .BeEmpty();
        }
    }

    [TestFixture]
    public class Given_an_identity_authority_on_a_different_host
    {
        private JsonDocument _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _document = await FetchDocumentAsync(authority: "https://idp.example.org/realms/other");
        }

        [TearDown]
        public void TearDown() => _document.Dispose();

        [Test]
        public void It_does_not_derive_the_token_url_from_the_authority()
        {
            _document
                .RootElement.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty(SchemeName)
                .GetProperty("flows")
                .GetProperty("clientCredentials")
                .GetProperty("tokenUrl")
                .GetString()
                .Should()
                .Be("http://localhost/config/connect/token");
        }
    }

    [TestFixture]
    public class Given_the_document_requested_on_another_host
    {
        private JsonDocument _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _document = await FetchDocumentAsync(host: "cms.example.org");
        }

        [TearDown]
        public void TearDown() => _document.Dispose();

        [Test]
        public void It_builds_the_token_url_from_the_requested_host()
        {
            _document
                .RootElement.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty(SchemeName)
                .GetProperty("flows")
                .GetProperty("clientCredentials")
                .GetProperty("tokenUrl")
                .GetString()
                .Should()
                .Be("http://cms.example.org/config/connect/token");
        }
    }

    [TestFixture]
    public class Given_multi_tenancy_enabled
    {
        private JsonDocument _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _document = await FetchDocumentAsync(multiTenancy: true);
        }

        [TearDown]
        public void TearDown() => _document.Dispose();

        [Test]
        public void It_keeps_the_tenant_operations_inheriting_the_root_requirement()
        {
            var tenantOperations = Operations(_document)
                .Where(op => op.Key.Contains(" /v3/tenants"))
                .ToArray();

            tenantOperations.Should().NotBeEmpty();
            _document.RootElement.GetProperty("security").GetArrayLength().Should().Be(1);
            tenantOperations
                .Where(op => op.Operation.TryGetProperty("security", out _))
                .Select(op => op.Key)
                .Should()
                .BeEmpty();
            OperationsWithEmptySecurity(_document).Should().NotContain(k => k.Contains(" /v3/tenants"));
        }
    }
}
