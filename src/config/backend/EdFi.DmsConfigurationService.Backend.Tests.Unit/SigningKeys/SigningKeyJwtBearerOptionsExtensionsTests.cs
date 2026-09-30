// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// Spec §5 step 3.1: <see cref="SigningKeyJwtBearerOptionsExtensions.UseSigningKeySnapshot"/> puts a scheme on the
/// snapshot and composes the shared events with the scheme's own, so non-dependency failures keep the scheme's
/// handling (§4.3.8). The pipeline itself is covered by the frontend's <c>BearerSchemePipelineTests</c>.
/// </summary>
public class SigningKeyJwtBearerOptionsExtensionsTests
{
    private const string Issuer = "https://cms.example.test";
    private const string Audience = "ed-fi-cms-tests";

    private static readonly AuthenticationScheme _authenticationScheme = new(
        JwtBearerDefaults.AuthenticationScheme,
        displayName: null,
        typeof(JwtBearerHandler)
    );

    private static SigningKeysUnavailableException Unavailable() =>
        new(
            SigningKeysUnavailableReason.SnapshotExpired,
            new SigningKeyRefreshOutcome.Failed(
                SigningKeyFailureKind.Retrieval,
                new TimeoutException("store down")
            )
        );

    /// <summary>A scheme configured as <c>Bearer</c> is, then put on the snapshot; the scheme's own handlers count calls.</summary>
    private sealed class Scheme
    {
        public Scheme()
        {
            var provider = A.Fake<ISigningKeySnapshotProvider>();
            IOptions<IdentityOptions> identityOptions = Microsoft.Extensions.Options.Options.Create(
                new IdentityOptions { Authority = Issuer }
            );
            ConfigurationManager = new SigningKeyConfigurationManager(provider, identityOptions);
            BearerEvents = new SigningKeyBearerEvents(
                provider,
                A.Fake<ITokenManager>(),
                identityOptions,
                NullLogger<SigningKeyBearerEvents>.Instance
            );

            Options = new JwtBearerOptions
            {
                TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, _, _) => [],
                },
                Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = _ =>
                    {
                        SchemeAuthenticationFailedCalls++;
                        return Task.CompletedTask;
                    },
                    OnChallenge = _ =>
                    {
                        SchemeChallengeCalls++;
                        return Task.CompletedTask;
                    },
                },
            };
            Returned = Options.UseSigningKeySnapshot(ConfigurationManager, BearerEvents);
        }

        public SigningKeyConfigurationManager ConfigurationManager { get; }

        public SigningKeyBearerEvents BearerEvents { get; }

        public JwtBearerOptions Options { get; }

        public JwtBearerOptions Returned { get; }

        public int SchemeAuthenticationFailedCalls { get; private set; }

        public int SchemeChallengeCalls { get; private set; }

        public async Task<AuthenticationFailedContext> FailAsync(Exception exception)
        {
            AuthenticationFailedContext context = new(HttpContext(), _authenticationScheme, Options)
            {
                Exception = exception,
            };
            await Options.Events.OnAuthenticationFailed(context);
            return context;
        }

        public async Task<JwtBearerChallengeContext> ChallengeAsync(Exception? failure)
        {
            JwtBearerChallengeContext context = new(
                HttpContext(),
                _authenticationScheme,
                Options,
                new AuthenticationProperties()
            )
            {
                AuthenticateFailure = failure,
            };
            await Options.Events.OnChallenge(context);
            return context;
        }

        private static DefaultHttpContext HttpContext()
        {
            DefaultHttpContext context = new();
            context.Response.Body = new MemoryStream();
            return context;
        }
    }

    [TestFixture]
    public class Given_a_scheme_put_on_the_snapshot
    {
        private Scheme _scheme = null!;

        [SetUp]
        public void Act() => _scheme = new Scheme();

        [Test]
        public void It_supplies_the_configuration_manager() =>
            _scheme.Options.ConfigurationManager.Should().BeSameAs(_scheme.ConfigurationManager);

        [Test]
        public void It_turns_the_framework_unknown_key_refresh_off() =>
            _scheme.Options.RefreshOnIssuerKeyNotFound.Should().BeFalse();

        [Test]
        public void It_removes_the_key_resolver() =>
            _scheme.Options.TokenValidationParameters.IssuerSigningKeyResolver.Should().BeNull();

        [Test]
        public void It_keeps_the_validation_rules()
        {
            TokenValidationParameters parameters = _scheme.Options.TokenValidationParameters;
            parameters.ValidateIssuer.Should().BeTrue();
            parameters.ValidIssuer.Should().Be(Issuer);
            parameters.ValidateAudience.Should().BeTrue();
            parameters.ValidAudience.Should().Be(Audience);
            parameters.ValidateLifetime.Should().BeTrue();
            parameters.ValidateIssuerSigningKey.Should().BeTrue();
        }

        [Test]
        public void It_runs_the_shared_boundary_on_message_received() =>
            _scheme.Options.Events.OnMessageReceived.Target.Should().BeSameAs(_scheme.BearerEvents);

        [Test]
        public void It_runs_the_shared_status_check_on_token_validated() =>
            _scheme.Options.Events.OnTokenValidated.Target.Should().BeSameAs(_scheme.BearerEvents);

        [Test]
        public void It_returns_the_same_options() => _scheme.Returned.Should().BeSameAs(_scheme.Options);
    }

    [TestFixture]
    public class Given_authentication_failed_on_a_dependency
    {
        private readonly SigningKeysUnavailableException _unavailable = Unavailable();
        private Scheme _scheme = null!;
        private AuthenticationFailedContext _context = null!;

        [SetUp]
        public async Task Act()
        {
            _scheme = new Scheme();
            _context = await _scheme.FailAsync(_unavailable);
        }

        [Test]
        public void It_fails_with_the_typed_exception() =>
            _context.Result!.Failure.Should().BeSameAs(_unavailable);

        [Test]
        public void It_does_not_run_the_schemes_handler() =>
            _scheme.SchemeAuthenticationFailedCalls.Should().Be(0);
    }

    [TestFixture]
    public class Given_authentication_failed_for_another_reason
    {
        private Scheme _scheme = null!;
        private AuthenticationFailedContext _context = null!;

        [SetUp]
        public async Task Act()
        {
            _scheme = new Scheme();
            _context = await _scheme.FailAsync(new SecurityTokenExpiredException("expired"));
        }

        [Test]
        public void It_runs_the_schemes_handler() => _scheme.SchemeAuthenticationFailedCalls.Should().Be(1);

        [Test]
        public void It_sets_no_result() => _context.Result.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_challenge_after_a_dependency_failure
    {
        private Scheme _scheme = null!;
        private JwtBearerChallengeContext _context = null!;

        [SetUp]
        public async Task Act()
        {
            _scheme = new Scheme();
            _context = await _scheme.ChallengeAsync(Unavailable());
        }

        [Test]
        public void It_answers_503() =>
            _context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        [Test]
        public void It_suppresses_the_default_challenge() => _context.Handled.Should().BeTrue();

        [Test]
        public void It_does_not_run_the_schemes_handler() => _scheme.SchemeChallengeCalls.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_challenge_after_an_ordinary_failure
    {
        private Scheme _scheme = null!;
        private JwtBearerChallengeContext _context = null!;

        [SetUp]
        public async Task Act()
        {
            _scheme = new Scheme();
            _context = await _scheme.ChallengeAsync(new SecurityTokenExpiredException("expired"));
        }

        [Test]
        public void It_runs_the_schemes_handler() => _scheme.SchemeChallengeCalls.Should().Be(1);

        [Test]
        public void It_leaves_the_default_challenge_to_run() => _context.Handled.Should().BeFalse();
    }
}
