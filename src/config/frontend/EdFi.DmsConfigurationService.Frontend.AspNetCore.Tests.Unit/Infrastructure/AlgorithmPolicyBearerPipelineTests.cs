// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.BearerSchemePipelineTests;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// AC8 through both real self-contained schemes: the default <c>Bearer</c> scheme on a profile route and the
/// <c>DmsJwtBearer</c> scheme on its probe. Each scheme's handler accepts any RSA algorithm the registered key can
/// verify, and its token-validated event then verifies the token again through the token manager and the RS256-only
/// helper. Every token is signed by the same active key, with its <c>kid</c>, the host's issuer and audience, a valid
/// lifetime and a stored-valid <c>jti</c>, so the algorithm is the only difference between them.
/// </summary>
public class AlgorithmPolicyBearerPipelineTests
{
    [TestFixture(JwtBearerDefaults.AuthenticationScheme)]
    [TestFixture(JwtAuthenticationExtensions.JwtSchemeName)]
    public class Given_A_Trusted_Key_With_A_Disallowed_Algorithm(string scheme)
    {
        private sealed record Outcome(
            HttpResponseMessage Response,
            string Body,
            bool ReachedTheHandler,
            string? ValidatedJti,
            Guid TokenId
        );

        private BearerPipelineHost _host = null!;
        private readonly Dictionary<string, Outcome> _outcomes = [];

        /// <summary>How many times the protected handler behind the scheme under test has run.</summary>
        private int HandlerRuns =>
            scheme == JwtBearerDefaults.AuthenticationScheme
                ? Fake.GetCalls(_host.Profiles)
                    .Count(call => call.Method.Name == nameof(IProfileRepository.GetProfile))
                : _host.ProbeHits;

        private async Task SendAsync(string algorithm)
        {
            (string token, Guid tokenId) = _host.Store.Mint("key-1", signingAlgorithm: algorithm);
            int handlerRunsBefore = HandlerRuns;

            AuthenticationHeaderValue authorization = new("Bearer", token);
            HttpResponseMessage response =
                scheme == JwtBearerDefaults.AuthenticationScheme
                    ? await _host.SendAsync(authorization)
                    : await _host.SendAsync(authorization, BearerPipelineHost.DmsJwtBearerProbePath);

            _outcomes[algorithm] = new Outcome(
                response,
                await response.Content.ReadAsStringAsync(),
                HandlerRuns > handlerRunsBefore,
                _host
                    .LastValidatedClaims.SingleOrDefault(claim => claim.Type == JwtRegisteredClaimNames.Jti)
                    ?.Value,
                tokenId
            );
        }

        [SetUp]
        public async Task Act()
        {
            _outcomes.Clear();
            _host = new BearerPipelineHost(scheme);
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();

            await SendAsync(SecurityAlgorithms.RsaSha512);
            await SendAsync(SecurityAlgorithms.RsaSha256);
            await SendAsync(SecurityAlgorithms.RsaSsaPssSha256);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Outcome outcome in _outcomes.Values)
            {
                outcome.Response.Dispose();
            }

            _host.Dispose();
        }

        [Test]
        public void It_reaches_the_protected_handler_with_RS256()
        {
            Outcome outcome = _outcomes[SecurityAlgorithms.RsaSha256];
            outcome.Response.StatusCode.Should().Be(HttpStatusCode.OK);
            outcome.ReachedTheHandler.Should().BeTrue();
            outcome.ValidatedJti.Should().Be(outcome.TokenId.ToString());
        }

        [Test]
        public void It_authenticates_the_RS256_request_with_the_scheme_under_test()
        {
            if (scheme == JwtBearerDefaults.AuthenticationScheme)
            {
                A.CallTo(() => _host.Profiles.GetProfile(7)).MustHaveHappenedOnceExactly();
            }
            else
            {
                _outcomes[SecurityAlgorithms.RsaSha256].Body.Should().Be(scheme);
            }
        }

        [TestCase(SecurityAlgorithms.RsaSha512)]
        [TestCase(SecurityAlgorithms.RsaSsaPssSha256)]
        public void It_answers_the_disallowed_algorithm_with_an_ordinary_401(string algorithm) =>
            ShouldBeAnOrdinaryUnauthorized(_outcomes[algorithm].Response);

        [TestCase(SecurityAlgorithms.RsaSha512)]
        [TestCase(SecurityAlgorithms.RsaSsaPssSha256)]
        public void It_never_reaches_the_protected_handler_with_the_disallowed_algorithm(string algorithm) =>
            _outcomes[algorithm].ReachedTheHandler.Should().BeFalse();

        [TestCase(SecurityAlgorithms.RsaSha512)]
        [TestCase(SecurityAlgorithms.RsaSsaPssSha256)]
        public void It_validates_no_principal_for_the_disallowed_algorithm(string algorithm) =>
            _outcomes[algorithm].ValidatedJti.Should().BeNull();

        [Test]
        public void It_runs_only_the_scheme_under_test() =>
            _host.SchemesAuthenticating.Should().HaveCount(3).And.AllBe(scheme);

        // The key is known and stays loaded: no unknown-key refresh, so a disallowed algorithm is not a key failure.
        [Test]
        public void It_reloads_no_keys() => _host.Store.KeyReads.Should().Be(1);

        // Rejected by the helper's verification, before the token-status gate that only the RS256 token reaches.
        [Test]
        public void It_checks_the_status_of_the_RS256_token_only() => _host.Store.StatusReads.Should().Be(1);
    }
}
