// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_A_Rate_Limit_Partition_Key_For_An_Address
{
    private static RateLimitPartitionKey KeyFor(string address) =>
        RateLimitPartitionKey.ForAddress(IPAddress.Parse(address));

    [Test]
    public void It_keys_an_ipv4_address_as_itself()
    {
        KeyFor("10.0.0.1").Should().Be(new RateLimitPartitionKey(RateLimitPartitionKind.Address, "10.0.0.1"));
    }

    [Test]
    public void It_keys_an_ipv4_mapped_address_as_its_ipv4_address()
    {
        KeyFor("::ffff:10.0.0.1").Should().Be(KeyFor("10.0.0.1"));
    }

    [Test]
    public void It_keys_a_native_ipv6_address_on_its_64_bit_prefix()
    {
        KeyFor("2001:db8:1:1:aaaa:bbbb:cccc:dddd")
            .Should()
            .Be(new RateLimitPartitionKey(RateLimitPartitionKind.Address, "2001:db8:1:1::"));
        KeyFor("2001:db8:1:2::1").Should().NotBe(KeyFor("2001:db8:1:1::1"));
    }

    [Test]
    public void It_drops_the_ipv6_scope_id()
    {
        KeyFor("fe80::1%3").Should().Be(KeyFor("fe80::1%4"));
        KeyFor("fe80::1%3").Value.Should().Be("fe80::");
    }

    [Test]
    public void It_keys_a_missing_address_as_one_shared_unknown_partition()
    {
        RateLimitPartitionKey unknown = RateLimitPartitionKey.ForAddress(null);

        unknown.Kind.Should().Be(RateLimitPartitionKind.UnknownAddress);
        RateLimitPartitionKey.ForAddress(null).Should().Be(unknown);
    }

    [Test]
    public void It_never_matches_an_unknown_address_with_a_known_one_of_the_same_value()
    {
        RateLimitPartitionKey
            .ForAddress(null)
            .Should()
            .NotBe(new RateLimitPartitionKey(RateLimitPartitionKind.Address, string.Empty));
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Rate_Limit_Partition_Key_For_A_Client
{
    [Test]
    public void It_keys_a_client_id_exactly_and_ordinally()
    {
        RateLimitPartitionKey.ForClient("client-a").Should().Be(RateLimitPartitionKey.ForClient("client-a"));
        RateLimitPartitionKey
            .ForClient("client-a")
            .Should()
            .NotBe(RateLimitPartitionKey.ForClient("CLIENT-A"));
    }

    [Test]
    public void It_never_matches_an_address_whose_value_is_the_client_id()
    {
        RateLimitPartitionKey
            .ForClient("10.0.0.1")
            .Should()
            .NotBe(RateLimitPartitionKey.ForAddress(IPAddress.Parse("10.0.0.1")));
        RateLimitPartitionKey.ForClient(string.Empty).Should().NotBe(RateLimitPartitionKey.ForAddress(null));
    }
}

/// <summary>
/// Drives the real host under the TestRateLimit environment, which admits one request per sixty-second
/// window per partition; the wide window keeps every assertion independent of timing. TestServer leaves
/// the connection address null, so a test-only startup filter sets it from X-Test-Remote-Ip ahead of the
/// application's forwarded-headers middleware. Each test uses addresses no other test in its fixture
/// uses, so tests sharing a host never share a partition.
///
/// The reverse-proxy and rate-limit settings are read before the host is built, so they are supplied
/// through environment variables, which is why these fixtures do not run in parallel with others.
/// Settings read through options once the host is built are supplied in memory.
/// </summary>
public abstract class RateLimitPartitionHostFixture
{
    private const string RemoteIpHeader = "X-Test-Remote-Ip";

    private static readonly string[] _environmentVariables =
    [
        "AppSettings__ReverseProxy__UseForwardedHeaders",
        "AppSettings__ReverseProxy__KnownProxies",
        "AppSettings__ReverseProxy__KnownNetworks",
        "RateLimit__PermitLimit",
        "RateLimit__Window",
        "RateLimit__QueueLimit",
    ];

    // Null until set up; one-time teardown also runs after a setup that failed part way.
    private EnvironmentVariableScope? _environment;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    protected abstract IReadOnlyDictionary<string, string> HostEnvironmentVariables { get; }

    protected virtual IReadOnlyDictionary<string, string?> HostSettings { get; } =
        new Dictionary<string, string?>();

    protected WebApplicationFactory<Program> Factory => _factory!;

    /// <summary>Registers this fixture's service replacements after the essential mocks.</summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    [OneTimeSetUp]
    public void StartHost()
    {
        _environment = new EnvironmentVariableScope(_environmentVariables);
        foreach ((string name, string value) in HostEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("TestRateLimit");
            builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(HostSettings)
            );
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
                ConfigureServices(collection);
            });
        });
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        try
        {
            _client?.Dispose();
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }
        }
        finally
        {
            // Restored even when the host never started or fails to shut down, so the fixtures that
            // run next see the process environment this fixture found.
            _environment?.Dispose();
        }
    }

    /// <summary>
    /// Sends GET /health, which is anonymous and answers 200 whenever the limiter admits it, so its
    /// status reports admission alone.
    /// </summary>
    protected async Task<HttpStatusCode> GetHealthAsync(
        string? remoteIp = null,
        string? host = null,
        string? forwardedFor = null,
        string? forwardedHost = null,
        string? authorization = null
    )
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/health",
            remoteIp,
            authorization,
            host,
            forwardedFor,
            forwardedHost
        );
        using HttpResponseMessage response = await SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Builds a request from <paramref name="remoteIp"/>. The Authorization value is added without
    /// validation, so a malformed value reaches the host as written.
    /// </summary>
    protected static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string? remoteIp,
        string? authorization = null,
        string? host = null,
        string? forwardedFor = null,
        string? forwardedHost = null
    )
    {
        HttpRequestMessage request = new(method, path);
        AddHeader(request, RemoteIpHeader, remoteIp);
        AddHeader(request, "X-Forwarded-For", forwardedFor);
        AddHeader(request, "X-Forwarded-Host", forwardedHost);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return request;
    }

    protected Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client!.SendAsync(request);

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            request.Headers.Add(name, value);
        }
    }

    /// <summary>
    /// Runs before the application's forwarded-headers middleware and sets the connection address from
    /// X-Test-Remote-Ip, so each request can come from a chosen peer.
    /// </summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        if (
                            context.Request.Headers.TryGetValue(RemoteIpHeader, out var value)
                            && IPAddress.TryParse(value.ToString(), out var ip)
                        )
                        {
                            context.Connection.RemoteIpAddress = ip;
                        }

                        await nextMiddleware();
                    }
                );
                next(app);
            };
    }
}

[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_With_Forwarded_Headers_Disabled : RateLimitPartitionHostFixture
{
    protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
        new Dictionary<string, string> { ["AppSettings__ReverseProxy__UseForwardedHeaders"] = "false" };

    [Test]
    public async Task It_budgets_each_address_independently()
    {
        (await GetHealthAsync(remoteIp: "10.0.0.1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.0.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync(remoteIp: "10.0.0.2")).Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_does_not_split_an_address_budget_by_host()
    {
        (await GetHealthAsync(remoteIp: "10.0.1.1", host: "first.example.com"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.1.1", host: "second.example.com"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_ignores_forwarded_for_headers()
    {
        (await GetHealthAsync(remoteIp: "10.0.2.1", forwardedFor: "203.0.113.1"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.2.1", forwardedFor: "203.0.113.2"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_shares_one_budget_between_an_ipv4_mapped_address_and_its_ipv4_address()
    {
        (await GetHealthAsync(remoteIp: "::ffff:10.0.3.1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.3.1")).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_shares_one_budget_within_an_ipv6_64_bit_prefix()
    {
        (await GetHealthAsync(remoteIp: "2001:db8:1:1::1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "2001:db8:1:1::2")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync(remoteIp: "2001:db8:1:2::1")).Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_shares_one_budget_among_requests_without_an_address()
    {
        (await GetHealthAsync()).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync()).Should().Be(HttpStatusCode.TooManyRequests);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_Behind_Configured_Reverse_Proxies : RateLimitPartitionHostFixture
{
    private const string TrustedProxy = "10.0.0.5";
    private const string TrustedNetworkPeer = "10.10.5.5";
    private const string OtherTrustedNetworkPeer = "10.10.5.6";

    protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
        new Dictionary<string, string>
        {
            ["AppSettings__ReverseProxy__UseForwardedHeaders"] = "true",
            ["AppSettings__ReverseProxy__KnownProxies"] = TrustedProxy,
            ["AppSettings__ReverseProxy__KnownNetworks"] = "10.10.0.0/16",
        };

    [Test]
    public async Task It_ignores_forwarded_for_headers_from_an_untrusted_peer()
    {
        (await GetHealthAsync(remoteIp: "203.0.113.99", forwardedFor: "198.51.100.1"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "203.0.113.99", forwardedFor: "198.51.100.2"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_budgets_forwarded_addresses_from_a_trusted_network_independently()
    {
        (await GetHealthAsync(remoteIp: TrustedNetworkPeer, forwardedFor: "198.51.100.11"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: TrustedNetworkPeer, forwardedFor: "198.51.100.12"))
            .Should()
            .Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_shares_the_budget_of_one_forwarded_address_across_trusted_peers()
    {
        (await GetHealthAsync(remoteIp: TrustedNetworkPeer, forwardedFor: "198.51.100.21"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: OtherTrustedNetworkPeer, forwardedFor: "198.51.100.21"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_does_not_split_a_forwarded_address_budget_by_forwarded_host()
    {
        (
            await GetHealthAsync(
                remoteIp: TrustedProxy,
                forwardedFor: "198.51.100.31",
                forwardedHost: "first.example.com"
            )
        )
            .Should()
            .Be(HttpStatusCode.OK);
        (
            await GetHealthAsync(
                remoteIp: TrustedProxy,
                forwardedFor: "198.51.100.31",
                forwardedHost: "second.example.com"
            )
        )
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }
}

/// <summary>
/// NUnit runs one-time teardown after a one-time setup that threw. A host fixture whose host fails to
/// start must still put back the process environment it changed, or the fixtures that run after it
/// would be configured by its values.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Rate_Limit_Host_Fixture_Whose_Host_Fails_To_Start
{
    private const string WindowVariable = "RateLimit__Window";
    private const string KnownProxiesVariable = "AppSettings__ReverseProxy__KnownProxies";

    /// <summary>A window that cannot bind, so building the host throws.</summary>
    private sealed class UnstartableHostFixture : RateLimitPartitionHostFixture
    {
        protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
            new Dictionary<string, string> { [WindowVariable] = "not-a-number" };
    }

    [Test]
    public async Task It_restores_the_process_environment_on_teardown()
    {
        using EnvironmentVariableScope outerEnvironment = new(WindowVariable, KnownProxiesVariable);
        Environment.SetEnvironmentVariable(WindowVariable, "60");
        Environment.SetEnvironmentVariable(KnownProxiesVariable, "10.0.0.9");
        UnstartableHostFixture fixture = new();

        Action startHost = fixture.StartHost;
        startHost.Should().Throw<Exception>();
        Environment.GetEnvironmentVariable(WindowVariable).Should().Be("not-a-number");
        Environment.GetEnvironmentVariable(KnownProxiesVariable).Should().BeNull();

        await fixture.StopHost();

        Environment.GetEnvironmentVariable(WindowVariable).Should().Be("60");
        Environment.GetEnvironmentVariable(KnownProxiesVariable).Should().Be("10.0.0.9");
    }
}

/// <summary>
/// The partition host with authorization enforced and the production JWT validation service. OIDC
/// metadata comes from a fixed configuration manager that names this fixture's issuer and one RSA
/// signing key, so startup warm-up passes and every token below is accepted or rejected by the real
/// validator. A token request reaches a fake OAuth manager that always succeeds, so an admitted token
/// request answers 200.
/// </summary>
public abstract class RateLimitAuthHostFixture : RateLimitPartitionHostFixture
{
    protected const string Issuer = "https://rate-limit-test-issuer/realms/edfi";
    protected const string Audience = "rate-limit-test-audience";
    protected const string RoleClaimType = "rate_limit_test_role";
    protected const string DocumentCacheRole = "document-cache-reader";

    private static readonly RsaSecurityKey _signingKey = new(RSA.Create(2048));
    private static readonly RsaSecurityKey _foreignKey = new(RSA.Create(2048));
    private static readonly JwtSecurityTokenHandler _tokenHandler = new();
    private static int _tokenSequence;

    /// <summary>How a token fails Core's validation.</summary>
    public enum TokenFlaw
    {
        None,
        ForeignSigningKey,
        Expired,
        WrongAudience,
        WrongIssuer,
        NotAJwt,
    }

    protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
        new Dictionary<string, string>();

    protected override IReadOnlyDictionary<string, string?> HostSettings { get; } =
        new Dictionary<string, string?>
        {
            ["AppSettings:BypassAuthorization"] = "false",
            ["JwtAuthentication:Authority"] = Issuer,
            ["JwtAuthentication:Audience"] = Audience,
            ["JwtAuthentication:RoleClaimType"] = RoleClaimType,
            ["DataManagement:DocumentCache:Status:RequiredRole"] = DocumentCacheRole,
        };

    /// <summary>The RSA key every <see cref="Bearer"/> token without a forged signature is signed with.</summary>
    protected static RSA SigningRsa => _signingKey.Rsa;

    /// <summary>
    /// The key the OIDC metadata publishes. A fixture that needs its own key object, for example to
    /// observe signature checks, overrides this instead of changing the shared key.
    /// </summary>
    protected virtual SecurityKey MetadataSigningKey => _signingKey;

    protected override void ConfigureServices(IServiceCollection services)
    {
        OpenIdConnectConfiguration metadata = new() { Issuer = Issuer };
        metadata.SigningKeys.Add(MetadataSigningKey);
        services.Replace(
            ServiceDescriptor.Singleton<IConfigurationManager<OpenIdConnectConfiguration>>(
                new FixedConfigurationManager(metadata)
            )
        );
        services.AddTransient(_ => CreateSuccessfulOAuthManager());
    }

    [Test]
    public void It_runs_with_authorization_enforced()
    {
        Factory
            .Services.GetRequiredService<IOptions<CoreAppSettings>>()
            .Value.BypassAuthorization.Should()
            .BeFalse();
    }

    /// <summary>
    /// A bearer header for a newly minted token. Every call mints a distinct token, so two calls for
    /// one client are a renewal. A <paramref name="flaw"/> other than None yields a token Core rejects.
    /// </summary>
    protected static string Bearer(
        string? clientId,
        TokenFlaw flaw = TokenFlaw.None,
        params Claim[] additionalClaims
    )
    {
        int sequence = Interlocked.Increment(ref _tokenSequence);
        if (flaw == TokenFlaw.NotAJwt)
        {
            return $"Bearer not-a-jwt-{sequence}";
        }

        List<Claim> claims = [new("jti", $"rate-limit-token-{sequence}"), .. additionalClaims];
        if (clientId is not null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        DateTime now = DateTime.UtcNow;
        bool expired = flaw == TokenFlaw.Expired;
        JwtSecurityToken token = new(
            issuer: flaw == TokenFlaw.WrongIssuer ? "https://another-issuer/realms/edfi" : Issuer,
            audience: flaw == TokenFlaw.WrongAudience ? "another-audience" : Audience,
            claims: claims,
            notBefore: expired ? now.AddHours(-2) : now.AddMinutes(-1),
            expires: expired ? now.AddHours(-1) : now.AddHours(1),
            signingCredentials: new SigningCredentials(
                flaw == TokenFlaw.ForeignSigningKey ? _foreignKey : _signingKey,
                SecurityAlgorithms.RsaSha256
            )
        );
        return $"Bearer {_tokenHandler.WriteToken(token)}";
    }

    private static IOAuthManager CreateSuccessfulOAuthManager()
    {
        IOAuthManager oAuthManager = A.Fake<IOAuthManager>();
        A.CallTo(() =>
                oAuthManager.GetAccessTokenAsync(
                    A<IHttpClientWrapper>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<TokenRequestCredentials>.Ignored,
                    A<string>.Ignored,
                    A<TraceId>.Ignored
                )
            )
            .ReturnsLazily(() =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"issued","token_type":"bearer","expires_in":300}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
        return oAuthManager;
    }

    private sealed class FixedConfigurationManager(OpenIdConnectConfiguration configuration)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(configuration);

        public void RequestRefresh() { }
    }
}

/// <summary>
/// One permit per partition. Every assertion that a request is budgeted by client sends the requests
/// from different addresses, and every assertion that one is budgeted by address sends a client's
/// token from that address, so each test fails under address-only partitioning as well as under
/// partitioning on anything a client chooses.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_With_Authorization_Enforced : RateLimitAuthHostFixture
{
    [Test]
    public async Task It_budgets_two_clients_at_one_address_independently()
    {
        (await GetHealthAsync("10.20.0.1", authorization: Bearer("isolation-client-a")))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.0.1", authorization: Bearer("isolation-client-a")))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync("10.20.0.1", authorization: Bearer("isolation-client-b")))
            .Should()
            .Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_shares_one_budget_across_renewed_tokens_of_one_client()
    {
        (await GetHealthAsync("10.20.1.1", authorization: Bearer("renewal-client")))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.1.2", authorization: Bearer("renewal-client")))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);

        // The second token was rejected on the client's budget, not on its address's.
        (await GetHealthAsync("10.20.1.2"))
            .Should()
            .Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_keeps_a_client_budget_apart_from_an_anonymous_flood_from_its_address()
    {
        (await GetHealthAsync("10.20.2.1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.2.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync("10.20.2.1", authorization: Bearer("anonymous-flood-client")))
            .Should()
            .Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A rejected token naming a client spends its address's budget, so the genuine client from the
    /// same address is still admitted. A second, different rejected token from that address is then
    /// rejected, so rejected tokens neither select a client nor create partitions of their own.
    /// </summary>
    [TestCase(TokenFlaw.ForeignSigningKey, "10.20.3.1", "forged-target")]
    [TestCase(TokenFlaw.Expired, "10.20.3.2", "expired-target")]
    [TestCase(TokenFlaw.WrongAudience, "10.20.3.3", "wrong-audience-target")]
    [TestCase(TokenFlaw.WrongIssuer, "10.20.3.4", "wrong-issuer-target")]
    [TestCase(TokenFlaw.NotAJwt, "10.20.3.5", "malformed-target")]
    public async Task It_budgets_a_rejected_token_by_address(TokenFlaw flaw, string address, string clientId)
    {
        string firstRejected = Bearer(clientId, flaw);
        string secondRejected = Bearer(clientId, flaw);
        firstRejected.Should().NotBe(secondRejected);

        (await GetHealthAsync(address, authorization: firstRejected)).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(address, authorization: Bearer(clientId))).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(address, authorization: secondRejected))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_budgets_a_valid_token_without_a_client_id_by_address()
    {
        (await GetHealthAsync("10.20.4.1", authorization: Bearer(clientId: null)))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.4.1")).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_does_not_budget_token_requests_by_the_client_they_name()
    {
        using HttpResponseMessage first = await SendAsync(BasicTokenRequest("10.20.5.1", "token-client-one"));
        using HttpResponseMessage second = await SendAsync(
            BasicTokenRequest("10.20.5.1", "token-client-two")
        );
        using HttpResponseMessage third = await SendAsync(
            FormTokenRequest(
                "10.20.5.1",
                "grant_type=client_credentials&client_id=token-client-three&client_secret=s"
            )
        );

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        JsonNode body = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
        body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
    }

    [Test]
    public async Task It_keeps_a_client_whose_id_spells_an_address_apart_from_that_address()
    {
        (await GetHealthAsync("10.20.6.9", authorization: Bearer("10.20.6.1")))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.6.9", authorization: Bearer("10.20.6.1")))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync("10.20.6.1")).Should().Be(HttpStatusCode.OK);

        (await GetHealthAsync("10.20.7.1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.7.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync("10.20.7.9", authorization: Bearer("10.20.7.1")))
            .Should()
            .Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_does_not_split_a_client_budget_by_host()
    {
        (await GetHealthAsync("10.20.8.1", host: "first.example.com", authorization: Bearer("host-client")))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.20.8.2", host: "second.example.com", authorization: Bearer("host-client")))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_serves_the_rejection_contract_on_a_client_budget()
    {
        const string SuppliedCorrelationId = "client-budget-correlation";
        (await GetHealthAsync("10.20.9.1", authorization: Bearer("contract-client")))
            .Should()
            .Be(HttpStatusCode.OK);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/health",
            "10.20.9.2",
            Bearer("contract-client")
        );
        request.Headers.Add("correlationid", SuppliedCorrelationId);
        using HttpResponseMessage response = await SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:too-many-requests");
        body["correlationId"]!.GetValue<string>().Should().Be(SuppliedCorrelationId);
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
    }

    private static HttpRequestMessage BasicTokenRequest(string remoteIp, string clientId)
    {
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:secret"));
        HttpRequestMessage request = FormTokenRequest(remoteIp, "grant_type=client_credentials");
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");
        return request;
    }

    private static HttpRequestMessage FormTokenRequest(string remoteIp, string form)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/oauth/token", remoteIp);
        request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        return request;
    }
}

[TestFixture]
[NonParallelizable]
public class Given_Concurrent_First_Requests_From_One_Client : RateLimitAuthHostFixture
{
    private const int PermitLimit = 5;
    private const int RequestCount = 25;

    protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
        new Dictionary<string, string> { ["RateLimit__PermitLimit"] = $"{PermitLimit}" };

    [Test]
    public async Task It_admits_exactly_the_permit_limit_across_every_address()
    {
        string authorization = Bearer("concurrent-client");

        HttpStatusCode[] statuses = await Task.WhenAll(
            Enumerable
                .Range(1, RequestCount)
                .Select(index => GetHealthAsync($"10.30.0.{index}", authorization: authorization))
        );

        statuses.Count(status => status == HttpStatusCode.OK).Should().Be(PermitLimit);
        statuses
            .Count(status => status == HttpStatusCode.TooManyRequests)
            .Should()
            .Be(RequestCount - PermitLimit);
    }
}

/// <summary>
/// Resolving a client at the limiter never authenticates or authorizes: admitted requests keep
/// Core's own 401 and 403 responses. The limit is raised so every request here is admitted.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Admitted_Requests_That_Core_Does_Not_Authorize : RateLimitAuthHostFixture
{
    private const string SuppliedCorrelationId = "core-authorization-correlation";

    protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
        new Dictionary<string, string> { ["RateLimit__PermitLimit"] = "1000" };

    private async Task<(HttpResponseMessage Response, JsonNode Body)> GetAsync(
        string path,
        string? authorization
    )
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path, "10.40.0.1", authorization);
        request.Headers.Add("correlationid", SuppliedCorrelationId);
        HttpResponseMessage response = await SendAsync(request);
        return (response, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static void AssertUnauthorized(HttpResponseMessage response, JsonNode body, string error)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer error=\"invalid_token\"");
        body["title"]!.GetValue<string>().Should().Be("Authentication Failed");
        body["errors"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal(error);
        body["correlationId"]!.GetValue<string>().Should().Be(SuppliedCorrelationId);
    }

    [Test]
    public async Task It_answers_a_data_request_without_a_token_with_the_missing_header_401()
    {
        (HttpResponseMessage response, JsonNode body) = await GetAsync("/data/ed-fi/students", null);
        using (response)
        {
            AssertUnauthorized(response, body, "Authorization header is missing.");
        }
    }

    [Test]
    public async Task It_answers_a_data_request_with_a_forged_token_with_the_invalid_token_401()
    {
        (HttpResponseMessage response, JsonNode body) = await GetAsync(
            "/data/ed-fi/students",
            Bearer("core-client", TokenFlaw.ForeignSigningKey)
        );
        using (response)
        {
            AssertUnauthorized(response, body, "Invalid token");
        }
    }

    [Test]
    public async Task It_answers_document_cache_status_without_a_token_with_the_missing_header_401()
    {
        (HttpResponseMessage response, JsonNode body) = await GetAsync("/health/document-cache", null);
        using (response)
        {
            AssertUnauthorized(response, body, "Authorization header is missing.");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Test]
    public async Task It_answers_document_cache_status_for_a_valid_client_without_the_role_with_403()
    {
        (HttpResponseMessage response, JsonNode body) = await GetAsync(
            "/health/document-cache",
            Bearer("core-client", TokenFlaw.None, new Claim(RoleClaimType, "another-role"))
        );
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            response.Headers.WwwAuthenticate.Should().BeEmpty();
            body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("Insufficient permissions");
            body["correlationId"]!.GetValue<string>().Should().Be(SuppliedCorrelationId);
        }
    }
}

/// <summary>
/// Counts constructions of the production JWT validation service. A request whose Authorization header
/// cannot parse as one bearer token must not construct it. The test client drops a blank header value,
/// so a startup filter, ahead of the application pipeline, sets the Authorization values each request
/// names in a JSON array, exactly as a client on the wire could have sent them.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_Counting_Jwt_Validation_Constructions : RateLimitAuthHostFixture
{
    private const string AuthorizationValuesHeader = "X-Test-Authorization-Values";

    private int _constructions;

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.Replace(
            ServiceDescriptor.Singleton<IJwtValidationService>(provider =>
            {
                Interlocked.Increment(ref _constructions);
                return new JwtValidationService(
                    provider.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>(),
                    provider.GetRequiredService<IOptions<JwtAuthenticationOptions>>(),
                    provider.GetRequiredService<ILogger<JwtValidationService>>()
                );
            })
        );
        services.AddSingleton<IStartupFilter, AuthorizationValuesStartupFilter>();
    }

    [Test]
    public async Task It_constructs_jwt_validation_only_for_a_header_that_parses_as_a_bearer_token()
    {
        Volatile
            .Read(ref _constructions)
            .Should()
            .Be(0, "no request has been served, so a construction here is eager and voids every count below");

        string valid = Bearer("counting-client");
        (string Address, string[] Values)[] unparseable =
        [
            ("10.50.0.1", []),
            ("10.50.0.2", ["Basic eDp5"]),
            ("10.50.0.3", [""]),
            ("10.50.0.4", ["Bearer a,b"]),
            ("10.50.0.5", [valid, valid]),
            ("10.50.0.6", [valid, ""]),
        ];

        foreach ((string address, string[] values) in unparseable)
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/health", address);
            if (values.Length > 0)
            {
                request.Headers.Add(AuthorizationValuesHeader, JsonSerializer.Serialize(values));
            }

            using HttpResponseMessage response = await SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            Volatile.Read(ref _constructions).Should().Be(0, $"[{string.Join("|", values)}] cannot parse");
        }

        (await GetHealthAsync("10.50.0.7", authorization: valid)).Should().Be(HttpStatusCode.OK);
        Volatile.Read(ref _constructions).Should().Be(1);
    }

    private sealed class AuthorizationValuesStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        if (context.Request.Headers.TryGetValue(AuthorizationValuesHeader, out var json))
                        {
                            context.Request.Headers.Authorization = new StringValues(
                                JsonSerializer.Deserialize<string[]>(json.ToString())
                            );
                        }

                        await nextMiddleware();
                    }
                );
                next(app);
            };
    }
}

/// <summary>
/// With BypassAuthorization on, the limiter budgets every request by address and never builds JWT
/// services. The OIDC metadata address is blank, so building them would throw, and a bearer token on
/// an anonymous route would turn into a 500.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_With_Bypass_Authorization_And_No_Oidc_Metadata
    : RateLimitPartitionHostFixture
{
    private int _constructions;

    protected override IReadOnlyDictionary<string, string> HostEnvironmentVariables =>
        new Dictionary<string, string>();

    protected override IReadOnlyDictionary<string, string?> HostSettings { get; } =
        new Dictionary<string, string?> { ["JwtAuthentication:MetadataAddress"] = "" };

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Replace(
            ServiceDescriptor.Singleton<IJwtValidationService>(provider =>
            {
                Interlocked.Increment(ref _constructions);
                return new JwtValidationService(
                    provider.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>(),
                    provider.GetRequiredService<IOptions<JwtAuthenticationOptions>>(),
                    provider.GetRequiredService<ILogger<JwtValidationService>>()
                );
            })
        );
    }

    [Test]
    public async Task It_budgets_bearer_requests_by_address_without_building_jwt_services()
    {
        Factory
            .Services.GetRequiredService<IOptions<CoreAppSettings>>()
            .Value.BypassAuthorization.Should()
            .BeTrue();

        (await GetHealthAsync("10.60.0.1", authorization: "Bearer first-token"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync("10.60.0.1", authorization: "Bearer second-token"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
        Volatile.Read(ref _constructions).Should().Be(0);
    }

    [Test]
    public void It_has_oidc_metadata_settings_that_cannot_build_jwt_services()
    {
        // The control for the test above: without the bypass rule the limiter would reach this throw.
        Action resolve = () =>
            Factory.Services.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*MetadataAddress*");
    }
}

/// <summary>
/// Records what the JWT validation service logs, apart from every other category, so a Warning
/// from Core's endpoint authorization is never mistaken for one from the validator.
/// </summary>
internal sealed class RecordingValidatorLogger : ILogger<JwtValidationService>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public void Clear() => _entries.Clear();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => _entries.Enqueue((logLevel, formatter(state, exception)));

    /// <summary>Replaces the validation service with the production one, logging to this recorder.</summary>
    public void ReplaceValidationService(IServiceCollection services) =>
        services.Replace(
            ServiceDescriptor.Singleton<IJwtValidationService>(provider => new JwtValidationService(
                provider.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>(),
                provider.GetRequiredService<IOptions<JwtAuthenticationOptions>>(),
                this
            ))
        );
}

/// <summary>
/// The limiter validates every bearer token before it admits or rejects the request. A token it
/// rejects is budgeted by address and logged by the validator at Debug only, so a flood of rejected
/// tokens answered with 429 leaves no Warning. GET /health is anonymous, so everything the validator
/// logs here comes from resolving the partition.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_Resolving_Rejected_Tokens : RateLimitAuthHostFixture
{
    private readonly RecordingValidatorLogger _validatorLogger = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        _validatorLogger.ReplaceValidationService(services);
    }

    [TestCase(TokenFlaw.ForeignSigningKey, "10.70.0.1", "forged-debug-target")]
    [TestCase(TokenFlaw.Expired, "10.70.0.2", "expired-debug-target")]
    [TestCase(TokenFlaw.NotAJwt, "10.70.0.3", "malformed-debug-target")]
    public async Task It_rejects_on_the_address_budget_and_logs_the_tokens_only_at_debug(
        TokenFlaw flaw,
        string address,
        string clientId
    )
    {
        _validatorLogger.Clear();

        (await GetHealthAsync(address, authorization: Bearer(clientId, flaw))).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(address, authorization: Bearer(clientId))).Should().Be(HttpStatusCode.OK);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/health",
            address,
            Bearer(clientId, flaw)
        );
        using HttpResponseMessage rejected = await SendAsync(request);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        JsonNode body = JsonNode.Parse(await rejected.Content.ReadAsStringAsync())!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:too-many-requests");

        IReadOnlyList<(LogLevel Level, string Message)> entries = _validatorLogger.Entries;
        entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
        entries
            .Where(entry => entry.Message.StartsWith("Token validation failed", StringComparison.Ordinal))
            .Select(entry => entry.Level)
            .Should()
            .Equal(LogLevel.Debug, LogLevel.Debug);
    }
}

/// <summary>
/// One request whose fresh token the limiter validates to choose a partition and Core then
/// validates again to authorize it. The token lacks the document-cache role, so Core answers 403
/// "Insufficient permissions", which it does only after accepting the token. The metadata key is a
/// key object of this fixture's own over the shared signing RSA, whose crypto provider factory counts
/// signature verifications, so the shared key and other fixtures are untouched.
/// </summary>
public abstract class ValidationAcrossTheLimiterAndAuthorizationFixture : RateLimitAuthHostFixture
{
    private readonly CountingCryptoProviderFactory _verifications = new();
    private readonly RecordingValidatorLogger _validatorLogger = new();
    private SecurityKey? _metadataSigningKey;

    protected abstract string ClientId { get; }

    protected abstract string Address { get; }

    protected abstract int ExpectedVerifications { get; }

    protected override SecurityKey MetadataSigningKey =>
        _metadataSigningKey ??= new RsaSecurityKey(SigningRsa) { CryptoProviderFactory = _verifications };

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        _validatorLogger.ReplaceValidationService(services);
    }

    [Test]
    public async Task It_verifies_the_token_signature_the_expected_number_of_times()
    {
        _verifications.Verifications.Should().Be(0, "no token has been validated yet");

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/health/document-cache",
            Address,
            Bearer(ClientId, TokenFlaw.None, new Claim(RoleClaimType, "another-role"))
        );
        using HttpResponseMessage response = await SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.Should().BeEmpty();
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["errors"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Equal("Insufficient permissions");

        _verifications.Verifications.Should().Be(ExpectedVerifications);

        // Endpoint authorization logs the missing role under its own category; the validator
        // accepted the token both times and logged nothing above Debug.
        _validatorLogger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// Creates the provider the default factory would, wrapped so that each call to a public Verify
    /// overload counts once. The wrapped provider is not counted, so a call it delegates internally
    /// between its own overloads is not counted twice. Providers are not cached, so every
    /// verification asks this factory.
    /// </summary>
    private sealed class CountingCryptoProviderFactory : CryptoProviderFactory
    {
        private int _verifications;

        public CountingCryptoProviderFactory()
        {
            CacheSignatureProviders = false;
        }

        public int Verifications => Volatile.Read(ref _verifications);

        public override SignatureProvider CreateForVerifying(SecurityKey key, string algorithm) =>
            new CountingSignatureProvider(base.CreateForVerifying(key, algorithm), this);

        public override void ReleaseSignatureProvider(SignatureProvider signatureProvider) =>
            signatureProvider.Dispose();

        private void CountVerification() => Interlocked.Increment(ref _verifications);

        private sealed class CountingSignatureProvider(
            SignatureProvider inner,
            CountingCryptoProviderFactory factory
        ) : SignatureProvider(inner.Key, inner.Algorithm)
        {
            public override byte[] Sign(byte[] input) => inner.Sign(input);

            public override bool Verify(byte[] input, byte[] signature)
            {
                factory.CountVerification();
                return inner.Verify(input, signature);
            }

            public override bool Verify(
                byte[] input,
                int inputOffset,
                int inputLength,
                byte[] signature,
                int signatureOffset,
                int signatureLength
            )
            {
                factory.CountVerification();
                return inner.Verify(
                    input,
                    inputOffset,
                    inputLength,
                    signature,
                    signatureOffset,
                    signatureLength
                );
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }
            }
        }
    }
}

/// <summary>
/// With validation caching on (the default), Core reuses the validation the limiter performed for
/// the same request, so the signature is verified once.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Fresh_Token_Validated_By_The_Limiter_And_Authorization_With_Caching
    : ValidationAcrossTheLimiterAndAuthorizationFixture
{
    protected override string ClientId => "cache-reuse-client";

    protected override string Address => "10.80.0.1";

    protected override int ExpectedVerifications => 1;
}

/// <summary>
/// The control: with validation caching off, Core verifies the signature again.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Fresh_Token_Validated_By_The_Limiter_And_Authorization_Without_Caching
    : ValidationAcrossTheLimiterAndAuthorizationFixture
{
    protected override IReadOnlyDictionary<string, string?> HostSettings =>
        new Dictionary<string, string?>(base.HostSettings)
        {
            ["JwtAuthentication:ValidatedTokenCacheEntryMaxLifetimeSeconds"] = "0",
        };

    protected override string ClientId => "no-cache-client";

    protected override string Address => "10.80.0.2";

    protected override int ExpectedVerifications => 2;
}
