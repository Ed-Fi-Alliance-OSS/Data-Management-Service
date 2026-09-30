// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.BearerSchemePipelineTests;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Spec §5 step 3.2: the <c>DmsJwtBearer</c> scheme on the same snapshot, manager and events as <c>Bearer</c>. Every
/// pipeline fixture selects the scheme explicitly: it requests <see cref="BearerPipelineHost.DmsJwtBearerProbePath"/>,
/// whose endpoint carries <see cref="JwtAuthenticationExtensions.CreateJwtAuthorizeAttribute"/>, and asserts that
/// <c>DmsJwtBearer</c> was the only scheme to run, so the default <c>Bearer</c> scheme cannot stand in for it.
/// </summary>
public class DmsJwtBearerSchemePipelineTests
{
    private const string Scheme = JwtAuthenticationExtensions.JwtSchemeName;

    private static BearerPipelineHost CreateHost() => new(Scheme);

    private static bool IsChallengeLog((LogLevel Level, string Message) entry) =>
        entry.Level == LogLevel.Warning && entry.Message.StartsWith("JWT authentication challenge");

    // Cold success: concurrent requests on a cold instance all wait on the one startup load and then succeed, each
    // validated with the shared manager's configuration and each with its own status check.
    [TestFixture]
    public class Given_concurrent_requests_on_a_cold_instance
    {
        private const int Requests = 16;
        private BearerPipelineHost _host = null!;
        private int _completedBeforeTheLoad;
        private HttpResponseMessage[] _responses = [];
        private string[] _bodies = [];

        [SetUp]
        public async Task Act()
        {
            _host = CreateHost();
            _host.Store.AddKey("key-1");
            TaskCompletionSource gate = _host.Store.GateKeyReads();
            await _host.StartAsync();

            Task<HttpResponseMessage>[] requests =
            [
                .. Enumerable
                    .Range(0, Requests)
                    .Select(_ => _host.GetDmsJwtBearerProbeAsync(_host.Store.Mint("key-1").Token)),
            ];
            await BearerPipelineHost.WaitUntilAsync(
                () => _host.Provider.UsableRequests >= Requests,
                () => $"Only {_host.Provider.UsableRequests} requests reached the provider."
            );
            _completedBeforeTheLoad = requests.Count(request => request.IsCompleted);

            gate.SetResult();
            _responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));
            _bodies = await Task.WhenAll(_responses.Select(response => response.Content.ReadAsStringAsync()));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var response in _responses)
            {
                response.Dispose();
            }

            _host.Dispose();
        }

        [Test]
        public void It_holds_every_request_until_the_keys_are_loaded() =>
            _completedBeforeTheLoad.Should().Be(0);

        [Test]
        public void It_answers_every_request_with_200() =>
            _responses.Select(response => response.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);

        [Test]
        public void It_authenticates_every_request_with_the_named_scheme() => _bodies.Should().AllBe(Scheme);

        [Test]
        public void It_runs_only_the_named_scheme() =>
            _host.SchemesAuthenticating.Should().HaveCount(Requests).And.AllBe(Scheme);

        [Test]
        public void It_reaches_the_endpoint_for_every_request() => _host.ProbeHits.Should().Be(Requests);

        [Test]
        public void It_reads_the_key_table_once() => _host.Store.KeyReads.Should().Be(1);

        [Test]
        public void It_validates_every_request_with_the_managers_configuration() =>
            _host.Manager.Calls.Should().Be(Requests);

        [Test]
        public void It_checks_the_status_of_every_token() => _host.Store.StatusReads.Should().Be(Requests);

        [Test]
        public void It_sends_nothing_over_the_backchannel() => _host.Backchannel.Sent.Should().BeEmpty();
    }

    // Key-store failure: a cold instance whose key store fails answers 503 at the boundary, before the manager, the
    // status check, or the scheme's own challenge logging.
    [TestFixture]
    public class Given_a_cold_request_when_the_key_store_fails
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;

        [SetUp]
        public async Task Act()
        {
            _host = CreateHost();
            _host.Store.AddKey("key-1");
            _host.Store.FailKeyReads(new TimeoutException("key store down at 10.0.0.9"));
            await _host.StartAsync();

            _response = await _host.GetDmsJwtBearerProbeAsync(_host.Store.Mint("key-1").Token);
            _content = await _response.Content.ReadAsStringAsync();
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public Task It_answers_a_dependency_503() => ShouldBeADependency503(_response);

        [Test]
        public void It_runs_only_the_named_scheme() => _host.SchemesAuthenticating.Should().Equal(Scheme);

        [Test]
        public void It_never_asks_the_configuration_manager() => _host.Manager.Calls.Should().Be(0);

        [Test]
        public void It_never_reads_the_token_status() => _host.Store.StatusReads.Should().Be(0);

        [Test]
        public void It_does_not_disclose_the_store_error() => _content.Should().NotContain("10.0.0.9");

        [Test]
        public void It_leaves_the_schemes_own_logging_to_ordinary_failures() =>
            _host.HandlerLogger.Entries.Should().BeEmpty();

        [Test]
        public void It_never_reaches_the_endpoint() => _host.ProbeHits.Should().Be(0);

        [Test]
        public void It_sends_nothing_over_the_backchannel() => _host.Backchannel.Sent.Should().BeEmpty();
    }

    // Status-store failure: warm keys, but the token status cannot be read: 503, not the 401 of an invalid token.
    [TestFixture]
    public class Given_warm_keys_when_the_token_status_store_fails
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = CreateHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();
            _host.Store.StatusFailure = new TimeoutException("status store down");

            _response = await _host.GetDmsJwtBearerProbeAsync(_host.Store.Mint("key-1").Token);
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public Task It_answers_a_dependency_503() => ShouldBeADependency503(_response);

        [Test]
        public void It_runs_only_the_named_scheme() => _host.SchemesAuthenticating.Should().Equal(Scheme);

        [Test]
        public void It_validated_the_signature_first() => _host.Manager.Calls.Should().Be(1);

        [Test]
        public void It_read_the_token_status_once() => _host.Store.StatusReads.Should().Be(1);

        [Test]
        public void It_leaves_the_schemes_own_logging_to_ordinary_failures() =>
            _host.HandlerLogger.Entries.Should().BeEmpty();

        [Test]
        public void It_never_reaches_the_endpoint() => _host.ProbeHits.Should().Be(0);
    }

    // Revoked token: the shared status check rejects it with the ordinary 401, and the scheme's own challenge logging
    // still runs.
    [TestFixture]
    public class Given_a_revoked_token
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = CreateHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();

            _response = await _host.GetDmsJwtBearerProbeAsync(
                _host.Store.Mint("key-1", status: "revoked").Token
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_answers_401() => ShouldBeAnOrdinaryUnauthorized(_response);

        [Test]
        public void It_runs_only_the_named_scheme() => _host.SchemesAuthenticating.Should().Equal(Scheme);

        [Test]
        public void It_checks_the_token_status_once() => _host.Store.StatusReads.Should().Be(1);

        [Test]
        public void It_logs_the_schemes_own_challenge_warning() =>
            _host.HandlerLogger.Entries.Should().ContainSingle(entry => IsChallengeLog(entry));

        [Test]
        public void It_never_reaches_the_endpoint() => _host.ProbeHits.Should().Be(0);
    }

    // Ordinary validation failure: a forged signature is the ordinary 401, logged by the scheme's own
    // authentication-failed and challenge events, with no key reload and no status read.
    [TestFixture]
    public class Given_a_token_with_a_forged_signature
    {
        private BearerPipelineHost _host = null!;
        private HttpResponseMessage _response = null!;

        [SetUp]
        public async Task Act()
        {
            _host = CreateHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();

            _response = await _host.GetDmsJwtBearerProbeAsync(
                _host.Store.Mint("key-1", signer: RSA.Create(2048)).Token
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        private static bool IsAuthenticationFailedLog((LogLevel Level, string Message) entry) =>
            entry.Level == LogLevel.Error && entry.Message == "JWT authentication failed";

        [Test]
        public void It_answers_401() => ShouldBeAnOrdinaryUnauthorized(_response);

        [Test]
        public void It_runs_only_the_named_scheme() => _host.SchemesAuthenticating.Should().Equal(Scheme);

        [Test]
        public void It_logs_the_schemes_own_authentication_failed_error() =>
            _host.HandlerLogger.Entries.Should().ContainSingle(entry => IsAuthenticationFailedLog(entry));

        [Test]
        public void It_logs_the_schemes_own_challenge_warning() =>
            _host.HandlerLogger.Entries.Should().ContainSingle(entry => IsChallengeLog(entry));

        [Test]
        public void It_reloads_no_keys() => _host.Store.KeyReads.Should().Be(1);

        [Test]
        public void It_never_reads_the_token_status() => _host.Store.StatusReads.Should().Be(0);

        [Test]
        public void It_never_reaches_the_endpoint() => _host.ProbeHits.Should().Be(0);
    }

    // Structural (s): the production wiring, with nothing decorated.
    [TestFixture]
    public class Given_the_DmsJwtBearer_scheme_of_a_self_contained_host
    {
        private WebApplicationFactory<Program> _factory = null!;
        private JwtBearerOptions _options = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory("self-contained");
            _options = _factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(Scheme);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_has_no_blocking_key_resolver() =>
            _options.TokenValidationParameters.IssuerSigningKeyResolver.Should().BeNull();

        [Test]
        public void It_uses_the_shared_configuration_manager() =>
            _options
                .ConfigurationManager.Should()
                .BeSameAs(_factory.Services.GetRequiredService<SigningKeyConfigurationManager>());

        [Test]
        public void It_shares_the_configuration_manager_with_the_default_scheme() =>
            _options
                .ConfigurationManager.Should()
                .BeSameAs(
                    _factory
                        .Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                        .Get(JwtBearerDefaults.AuthenticationScheme)
                        .ConfigurationManager
                );

        [Test]
        public void It_uses_a_plain_configuration_manager() =>
            _options.ConfigurationManager.Should().NotBeAssignableTo<BaseConfigurationManager>();

        [Test]
        public void It_leaves_identity_model_without_a_configuration_manager() =>
            _options.TokenValidationParameters.ConfigurationManager.Should().BeNull();

        [Test]
        public void It_turns_the_framework_unknown_key_refresh_off() =>
            _options.RefreshOnIssuerKeyNotFound.Should().BeFalse();

        [Test]
        public void It_creates_no_backchannel() => _options.Backchannel.Should().BeNull();

        [Test]
        public void It_still_validates_the_issuer()
        {
            _options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
            _options.TokenValidationParameters.ValidIssuer.Should().Be(BearerPipelineHost.Issuer);
        }

        [Test]
        public void It_still_validates_the_audience()
        {
            _options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
            _options.TokenValidationParameters.ValidAudience.Should().Be(BearerPipelineHost.Audience);
        }

        [Test]
        public void It_still_validates_the_lifetime()
        {
            _options.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
            _options.TokenValidationParameters.RequireExpirationTime.Should().BeTrue();
            _options
                .TokenValidationParameters.ClockSkew.Should()
                .Be(JwtTokenValidator.TokenValidationClockSkew);
        }

        [Test]
        public void It_still_validates_the_signing_key()
        {
            _options.TokenValidationParameters.ValidateIssuerSigningKey.Should().BeTrue();
            _options.TokenValidationParameters.RequireSignedTokens.Should().BeTrue();
        }

        [Test]
        public void It_runs_the_shared_boundary_on_message_received()
        {
            _options.Events.OnMessageReceived.Target.Should().BeSameAs(BearerEvents());
            _options
                .Events.OnMessageReceived.Method.Name.Should()
                .Be(nameof(SigningKeyBearerEvents.MessageReceivedAsync));
        }

        [Test]
        public void It_runs_the_shared_status_check_on_token_validated()
        {
            _options.Events.OnTokenValidated.Target.Should().BeSameAs(BearerEvents());
            _options
                .Events.OnTokenValidated.Method.Name.Should()
                .Be(nameof(SigningKeyBearerEvents.TokenValidatedAsync));
        }

        private SigningKeyBearerEvents BearerEvents() =>
            _factory.Services.GetRequiredService<SigningKeyBearerEvents>();
    }

    // Keycloak mode registers no DmsJwtBearer scheme, so nothing there changes.
    [TestFixture]
    public class Given_a_keycloak_host
    {
        private WebApplicationFactory<Program> _factory = null!;

        [SetUp]
        public void Act() => _factory = CreateFactory("keycloak");

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public async Task It_registers_no_DmsJwtBearer_scheme() =>
            (
                await _factory
                    .Services.GetRequiredService<IAuthenticationSchemeProvider>()
                    .GetSchemeAsync(Scheme)
            )
                .Should()
                .BeNull();
    }
}
