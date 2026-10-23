// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.ConnectRateLimitTestHost;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// The source gate in the real pipeline (DMS-1482 REQ-1, REQ-2, REQ-3, REQ-4). Each fixture owns its host,
/// so its limiter starts empty. Admitted requests are answered by fakes or by validation; only the
/// limiter answers 429.
/// </summary>
public class ConnectRateLimitPipelineTests
{
    private const string Detail =
        "The number of allowed requests has been exceeded. Retry the request later.";

    [TestFixture]
    public class Given_an_enumeration_that_varies_client_id_from_one_source
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 2,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("guess-1"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("guess-2"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("guess-3"), "198.51.100.7"),
                ]
            );
        }

        [Test]
        public void It_rejects_the_request_past_the_source_limit() => _statuses.Should().Equal(200, 200, 429);
    }

    [TestFixture]
    public class Given_requests_that_vary_host_tenant_suffix_and_path_case
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 2,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a"), "198.51.100.7"),
                    () =>
                    {
                        HttpRequestMessage request = Post(
                            "/CONNECT/TOKEN/district-1",
                            TokenForm("b"),
                            "198.51.100.7"
                        );
                        request.Headers.Host = "other.example";
                        request.Headers.Add("Tenant", "tenant-b");
                        return request;
                    },
                    () => Post("/connect/token/district-2/2026", TokenForm("c"), "198.51.100.7"),
                ]
            );
        }

        [Test]
        public void It_counts_every_variant_against_one_source_budget() =>
            _statuses.Should().Equal(200, 200, 429);
    }

    [TestFixture]
    public class Given_a_second_source_address
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("b"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("c"), "198.51.100.8"),
                ]
            );
        }

        [Test]
        public void It_gives_each_address_its_own_budget() => _statuses.Should().Equal(200, 429, 200);
    }

    [TestFixture]
    public class Given_ipv6_sources
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a"), "2001:db8::1"),
                    () => Post("/connect/token", TokenForm("b"), "2001:db8::2"),
                    () => Post("/connect/token", TokenForm("c"), "2001:db8:0:1::1"),
                ]
            );
        }

        [Test]
        public void It_shares_a_budget_within_a_64_and_not_across() =>
            _statuses.Should().Equal(200, 429, 200);
    }

    [TestFixture("/connect/token", false)]
    [TestFixture("/connect/register", false)]
    [TestFixture("/connect/introspect", false)]
    [TestFixture("/connect/revoke", true)]
    public class Given_a_connect_route_over_the_source_limit(string path, bool oauthContract)
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage first = await client.SendAsync(Post(path, EmptyForm()));
            _response = await client.SendAsync(Post(path, EmptyForm()));
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _response.Dispose();

        [Test]
        public void It_answers_429() => ((int)_response.StatusCode).Should().Be(429);

        [Test]
        public void It_answers_in_the_route_contract()
        {
            if (oauthContract)
            {
                _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
                _body.Count.Should().Be(2);
                _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
                _body["error_description"]!.GetValue<string>().Should().Be(Detail);
                return;
            }

            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _body.Count.Should().Be(7);
            _body["detail"]!.GetValue<string>().Should().Be(Detail);
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["title"]!.GetValue<string>().Should().Be("Too Many Requests");
            _body["status"]!.GetValue<int>().Should().Be(429);
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_sends_a_whole_second_retry_after() =>
            _response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        [Test]
        public void It_keeps_the_security_headers() =>
            _response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
    }

    [TestFixture]
    public class Given_routes_that_are_not_connect_endpoints
    {
        private List<int> _nonConnect = null!;
        private List<int> _connect = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            _nonConnect = await SendAll(
                client,
                [
                    () => new HttpRequestMessage(HttpMethod.Get, "/health"),
                    () => new HttpRequestMessage(HttpMethod.Get, "/health"),
                    () => Post("/connectx/token", TokenForm("a")),
                    () => Post("/connectx/token", TokenForm("a")),
                    () => new HttpRequestMessage(HttpMethod.Get, "/v3/vendors"),
                    () => new HttpRequestMessage(HttpMethod.Get, "/v3/vendors"),
                ]
            );
            _connect = await SendAll(
                client,
                [() => Post("/connect/token", TokenForm("a")), () => Post("/connect/token", TokenForm("b"))]
            );
        }

        [Test]
        public void It_never_limits_them() => _nonConnect.Should().NotContain(429);

        [Test]
        public void It_leaves_the_connect_budget_unspent() => _connect.Should().Equal(200, 429);
    }

    [TestFixture]
    public class Given_limiting_is_disabled
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1,
                enabled: false
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a")),
                    () => Post("/connect/token", TokenForm("a")),
                    () => Post("/connect/token", TokenForm("a")),
                ]
            );
        }

        [Test]
        public void It_admits_every_request() => _statuses.Should().Equal(200, 200, 200);
    }

    /// <summary>
    /// Order of operations: the source gate runs before the revoke handler reads the body, so a malformed
    /// body past the limit is answered 429, while the same body within the limit still reaches the
    /// handler's percent-encoding and UTF-8 validation.
    /// </summary>
    [TestFixture]
    public class Given_a_malformed_revocation_body
    {
        private const string MalformedBody = "token=%GG&client_id=c&client_secret=s";
        private HttpResponseMessage _withinLimit = null!;
        private string _withinLimitBody = null!;
        private HttpResponseMessage _pastLimit = null!;

        private static StringContent Malformed() =>
            new(MalformedBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1000
            );
            using HttpClient client = factory.CreateClient();
            _withinLimit = await client.SendAsync(Post("/connect/revoke", Malformed()));
            _withinLimitBody = await _withinLimit.Content.ReadAsStringAsync();
            _pastLimit = await client.SendAsync(Post("/connect/revoke", Malformed()));
        }

        [TearDown]
        public void TearDown()
        {
            _withinLimit.Dispose();
            _pastLimit.Dispose();
        }

        [Test]
        public void It_still_rejects_the_encoding_within_the_limit()
        {
            _withinLimit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(_withinLimitBody)!["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The request form payload is malformed.");
        }

        [Test]
        public void It_answers_429_past_the_limit_without_reading_the_body() =>
            ((int)_pastLimit.StatusCode).Should().Be(429);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_a_trusted_reverse_proxy
    {
        private const string UseForwardedHeadersEnv = "AppSettings__ReverseProxy__UseForwardedHeaders";
        private const string KnownProxiesEnv = "AppSettings__ReverseProxy__KnownProxies";
        private List<int> _viaTrustedProxy = null!;
        private List<int> _fromUntrustedPeer = null!;

        private static Func<HttpRequestMessage> Forwarded(
            string peer,
            string forwardedFor,
            string clientId
        ) =>
            () =>
            {
                HttpRequestMessage request = Post("/connect/token", TokenForm(clientId), peer);
                request.Headers.Add("X-Forwarded-For", forwardedFor);
                return request;
            };

        [SetUp]
        public async Task Setup()
        {
            // NUnit skips [TearDown] when [SetUp] throws, so restore the variables here on failure.
            try
            {
                Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, "true");
                Environment.SetEnvironmentVariable(KnownProxiesEnv, "10.0.0.5");
                await using WebApplicationFactory<Program> factory = Create(
                    sourcePermitLimit: 1,
                    clientPermitLimit: 1000
                );
                using HttpClient client = factory.CreateClient();
                _viaTrustedProxy = await SendAll(
                    client,
                    [
                        Forwarded("10.0.0.5", "198.51.100.1", "a"),
                        Forwarded("10.0.0.5", "198.51.100.2", "b"),
                        Forwarded("10.0.0.5", "198.51.100.1", "c"),
                    ]
                );
                _fromUntrustedPeer = await SendAll(
                    client,
                    [
                        Forwarded("203.0.113.9", "198.51.100.3", "d"),
                        Forwarded("203.0.113.9", "198.51.100.4", "e"),
                    ]
                );
            }
            catch
            {
                RestoreEnvironment();
                throw;
            }
        }

        [TearDown]
        public void TearDown() => RestoreEnvironment();

        private static void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, null);
            Environment.SetEnvironmentVariable(KnownProxiesEnv, null);
        }

        [Test]
        public void It_keys_on_the_forwarded_client_behind_a_trusted_proxy() =>
            _viaTrustedProxy.Should().Equal(200, 200, 429);

        [Test]
        public void It_ignores_forwarded_headers_from_an_untrusted_peer() =>
            _fromUntrustedPeer.Should().Equal(200, 429);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_source_limit_set_through_the_environment
    {
        private const string SourceLimitEnv = "ConnectRateLimit__SourcePermitLimit";
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            // NUnit skips [TearDown] when [SetUp] throws, so restore the variable here on failure.
            try
            {
                Environment.SetEnvironmentVariable(SourceLimitEnv, "1");
                await using WebApplicationFactory<Program> factory = Create();
                using HttpClient client = factory.CreateClient();
                _statuses = await SendAll(
                    client,
                    [
                        () => Post("/connect/token", TokenForm("a")),
                        () => Post("/connect/token", TokenForm("b")),
                    ]
                );
            }
            catch
            {
                Environment.SetEnvironmentVariable(SourceLimitEnv, null);
                throw;
            }
        }

        [TearDown]
        public void TearDown() => Environment.SetEnvironmentVariable(SourceLimitEnv, null);

        [Test]
        public void It_applies_the_environment_value() => _statuses.Should().Equal(200, 429);
    }

    [TestFixture]
    public class Given_a_host_that_is_disposed
    {
        private ConnectRateLimiter _limiter = null!;

        [SetUp]
        public void Setup()
        {
            WebApplicationFactory<Program> factory = Create();
            using (factory.CreateClient())
            {
                _limiter = factory.Services.GetRequiredService<ConnectRateLimiter>();
            }
            factory.Dispose();
        }

        [Test]
        public void It_disposes_the_limiter() =>
            FluentActions
                .Invoking(() => _limiter.AttemptClient("a"))
                .Should()
                .Throw<ObjectDisposedException>();
    }
}
