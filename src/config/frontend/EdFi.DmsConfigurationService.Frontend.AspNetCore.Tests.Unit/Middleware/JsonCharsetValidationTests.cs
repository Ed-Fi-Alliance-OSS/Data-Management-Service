// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// End-to-end verification that a JSON request declaring an unsupported charset is rejected with the
/// Ed-Fi 415 contract instead of an unclassified 500, that missing-body and authentication and
/// authorization outcomes keep precedence over the charset, and that supported charsets continue into
/// normal binding. This is a non-fixture container; the runnable fixtures are the nested
/// <c>Given_…</c> classes.
/// </summary>
public class JsonCharsetValidationTests
{
    private const string ValidVendorJson = """
        {
          "company": "Charset Vendor",
          "contactName": "Test Contact",
          "contactEmailAddress": "test@test.com",
          "namespacePrefixes": "uri://test"
        }
        """;

    private IVendorRepository _vendorRepository = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [TearDown]
    public void DisposeFactory()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private HttpClient SetUpVendorClient() => SetUpVendorClient(AuthorizationScopes.AdminScope.Name);

    private HttpClient SetUpVendorClient(string? scope)
    {
        _vendorRepository = A.Fake<IVendorRepository>();
        A.CallTo(() => _vendorRepository.InsertVendor(A<VendorInsertCommand>.Ignored))
            .Returns(new VendorInsertResult.Success(1, IsNewVendor: true));

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

                    collection.AddTransient(_ => _vendorRepository);
                }
            );
        });
        _client = _factory.CreateClient();
        if (scope is not null)
        {
            _client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }

        return _client;
    }

    private static StringContent BodyWithContentType(string body, string contentType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static void AssertContract(
        HttpResponseMessage response,
        JsonObject body,
        HttpStatusCode status,
        string type,
        string title,
        string detail
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["detail"]!.GetValue<string>().Should().Be(detail);
        body["status"]!.GetValue<int>().Should().Be((int)status);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    private static void AssertUnsupportedMediaTypeContract(
        HttpResponseMessage response,
        JsonObject body,
        string content
    )
    {
        AssertContract(
            response,
            body,
            HttpStatusCode.UnsupportedMediaType,
            "urn:ed-fi:api:unsupported-media-type",
            "Unsupported Media Type",
            "The value specified in the 'Content-Type' header is not supported by this host."
        );
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
        content.Should().NotContain("not-an-encoding");
        content.Should().NotContain("Exception");
    }

    /// <summary>An otherwise valid JSON request whose declared charset is not a known encoding.</summary>
    [TestFixture]
    public class Given_a_json_request_with_an_unknown_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType(ValidVendorJson, "application/json; charset=not-an-encoding")
            );
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [Test]
        public void It_returns_the_full_unsupported_media_type_contract() =>
            AssertUnsupportedMediaTypeContract(_response, _body, _content);
    }

    /// <summary>
    /// A structured +json suffix is JSON to the framework's body reader, so its charset is validated
    /// the same way.
    /// </summary>
    [TestFixture]
    public class Given_a_structured_json_suffix_request_with_an_unknown_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType(ValidVendorJson, "application/vnd.ed-fi+json; charset=not-an-encoding")
            );
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [Test]
        public void It_returns_the_full_unsupported_media_type_contract() =>
            AssertUnsupportedMediaTypeContract(_response, _body, _content);
    }

    /// <summary>
    /// text/json is not JSON to the framework, so content negotiation rejects it regardless of its
    /// charset and the response is the shaped negotiation 415, never an unclassified 500.
    /// </summary>
    [TestFixture]
    public class Given_a_text_json_request_with_an_unknown_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType(ValidVendorJson, "text/json; charset=not-an-encoding")
            );
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [Test]
        public void It_returns_the_full_unsupported_media_type_contract() =>
            AssertUnsupportedMediaTypeContract(_response, _body, _content);
    }

    /// <summary>An empty body keeps its missing-body 400 even when the charset is also unsupported.</summary>
    [TestFixture]
    public class Given_an_empty_body_with_an_unknown_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType("", "application/json; charset=not-an-encoding")
            );
            _body = await ReadBodyAsync(_response);
        }

        [Test]
        public void It_returns_the_missing_body_bad_request_contract()
        {
            AssertContract(
                _response,
                _body,
                HttpStatusCode.BadRequest,
                "urn:ed-fi:api:bad-request",
                "Bad Request",
                "The request could not be processed. See 'errors' for details."
            );
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("A non-empty request body is required.");
        }
    }

    /// <summary>An unauthenticated request keeps its 401 even when the charset is unsupported.</summary>
    [TestFixture]
    public class Given_an_unauthenticated_json_request_with_an_unknown_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient(scope: null);
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType(ValidVendorJson, "application/json; charset=not-an-encoding")
            );
            _body = await ReadBodyAsync(_response);
        }

        [Test]
        public void It_returns_the_shaped_authentication_contract() =>
            AssertContract(
                _response,
                _body,
                HttpStatusCode.Unauthorized,
                "urn:ed-fi:api:security:authentication",
                "Authentication Failed",
                "Authentication is required to access this resource."
            );
    }

    /// <summary>An underprivileged request keeps its 403 even when the charset is unsupported.</summary>
    [TestFixture]
    public class Given_an_insufficient_scope_with_an_unknown_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient(scope: AuthorizationScopes.ReadOnlyScope.Name);
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType(ValidVendorJson, "application/json; charset=not-an-encoding")
            );
            _body = await ReadBodyAsync(_response);
        }

        [Test]
        public void It_returns_the_shaped_authorization_contract() =>
            AssertContract(
                _response,
                _body,
                HttpStatusCode.Forbidden,
                "urn:ed-fi:api:security:authorization",
                "Authorization Failed",
                "The authenticated client is not authorized to access this resource."
            );
    }

    /// <summary>A supported non-UTF-8 charset continues into normal binding.</summary>
    [TestFixture]
    public class Given_a_json_request_with_a_utf_16_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                new StringContent(ValidVendorJson, Encoding.Unicode, "application/json")
            );
        }

        [Test]
        public void It_binds_the_body_and_creates_the_vendor() =>
            _response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// The framework's JSON body reader does not unquote the charset parameter, so a quoted
    /// charset — even utf-8 — is an encoding it cannot decode and previously surfaced as an
    /// unclassified 500 (probe-verified on the real pipeline). The guard mirrors that exactly
    /// and classifies it as an unsupported media type instead.
    /// </summary>
    [TestFixture]
    public class Given_a_json_request_with_a_quoted_utf_8_charset : JsonCharsetValidationTests
    {
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var client = SetUpVendorClient();
            _response = await client.PostAsync(
                "/v3/vendors",
                BodyWithContentType(ValidVendorJson, "application/json; charset=\"utf-8\"")
            );
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [Test]
        public void It_returns_the_full_unsupported_media_type_contract()
        {
            AssertContract(
                _response,
                _body,
                HttpStatusCode.UnsupportedMediaType,
                "urn:ed-fi:api:unsupported-media-type",
                "Unsupported Media Type",
                "The value specified in the 'Content-Type' header is not supported by this host."
            );
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
            _content.Should().NotContain("utf-8");
            _content.Should().NotContain("Exception");
        }
    }
}
