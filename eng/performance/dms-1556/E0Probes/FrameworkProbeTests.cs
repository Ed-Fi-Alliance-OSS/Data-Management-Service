// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// DMS-1556 step 0.2 (E0): empirical probes of the pinned framework versions
// (Microsoft.AspNetCore.Authentication.JwtBearer 10.0.1, Microsoft.IdentityModel 8.12.0)
// backing the spec's verified facts V-1/V-2/V-5 and the design decisions D-2/D-3/I-5.
// Each fixture states which observations are FRAMEWORK facts and which come from the
// SCRATCH provider's own design. E0 establishes integration feasibility only; it does not
// establish the ticket's failure mechanism and satisfies no part of AC 1.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Dms1556.E0Probes;

public abstract class ProbeFixtureBase
{
    protected const string Authority = "http://e0-probe-authority";

    protected WebApplication? App;
    protected HttpClient? Client;
    protected ScratchConfigurationManager Manager = null!;
    protected ProbeCapture Capture = null!;

    protected static HttpRequestMessage AuthorizedRequest(string token) =>
        new(HttpMethod.Get, "/probe")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };

    [OneTimeTearDown]
    public async Task TearDownHost()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.DisposeAsync();
        }
    }
}

// E0(a), first half — FRAMEWORK fact (V-1): when the supplied plain manager throws and no
// event supplies a result, OnAuthenticationFailed receives the exact exception type and
// the handler RETHROWS (today's 500-shaped behavior).
[TestFixture]
public class Given_a_throwing_manager_without_failure_translation : ProbeFixtureBase
{
    private Exception? _clientObservedException;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority)
        {
            ExceptionToThrow = new ProbeDependencyException("key store down"),
        };
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(Manager, Capture, Authority);

        try
        {
            using var response = await Client.SendAsync(
                AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience))
            );
        }
        catch (Exception ex)
        {
            _clientObservedException = ex;
        }
    }

    [Test]
    public void It_delivers_the_exception_type_to_OnAuthenticationFailed()
    {
        Capture
            .AuthenticationFailures.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<ProbeDependencyException>();
    }

    [Test]
    public void It_rethrows_when_no_event_supplies_a_result()
    {
        _clientObservedException.Should().BeOfType<ProbeDependencyException>();
    }

    [Test]
    public void It_called_the_manager()
    {
        Manager.CallCount.Should().Be(1);
    }
}

// E0(a), second half — FRAMEWORK fact (V-1): context.Fail(exception) in
// OnAuthenticationFailed prevents the rethrow, and OnChallenge.AuthenticateFailure is the
// exact exception type — the mechanism D-3 builds on.
[TestFixture]
public class Given_a_throwing_manager_with_failure_translation : ProbeFixtureBase
{
    private HttpResponseMessage _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority)
        {
            ExceptionToThrow = new ProbeDependencyException("key store down"),
        };
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(
            Manager,
            Capture,
            Authority,
            translateOnAuthenticationFailed: true
        );

        _response = await Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience))
        );
    }

    [OneTimeTearDown]
    public void DisposeResponse()
    {
        _response.Dispose();
    }

    [Test]
    public void It_does_not_rethrow_and_challenges_instead()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public void It_exposes_the_exception_to_OnChallenge_as_AuthenticateFailure()
    {
        Capture
            .ChallengeFailures.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<ProbeDependencyException>();
    }
}

// E0(b) — FRAMEWORK fact (V-1/E0(b)): context.Fail(exception) in OnMessageReceived
// short-circuits the handler: NO GetConfigurationAsync call follows, no 500 escapes, and
// OnChallenge sees the exact exception. This is the load-shedding property the D-3
// boundary relies on during retry-delay windows.
[TestFixture]
public class Given_failure_at_the_message_received_boundary : ProbeFixtureBase
{
    private HttpResponseMessage _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority);
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(
            Manager,
            Capture,
            Authority,
            onMessageReceived: context =>
            {
                context.Fail(new ProbeDependencyException("boundary says no"));
                return Task.CompletedTask;
            }
        );

        _response = await Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience))
        );
    }

    [OneTimeTearDown]
    public void DisposeResponse()
    {
        _response.Dispose();
    }

    [Test]
    public void It_short_circuits_with_zero_manager_calls()
    {
        Manager.CallCount.Should().Be(0);
    }

    [Test]
    public void It_responds_with_the_challenge_not_an_exception()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public void It_carries_the_exception_to_OnChallenge()
    {
        Capture
            .ChallengeFailures.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<ProbeDependencyException>();
    }
}

// E0(c) — FRAMEWORK fact (V-5/I-5): with a ConfigurationManager supplied, Authority AND
// MetadataAddress set exactly as CMS sets them cause ZERO backchannel HTTP traffic. The
// authority points at a live local socket that counts every connection attempt.
[TestFixture]
public class Given_a_supplied_manager_with_authority_and_metadata_address_set : ProbeFixtureBase
{
    private TcpListener _authorityListener = null!;
    private int _connectionAttempts;
    private readonly List<HttpStatusCode> _statusCodes = [];
    private CancellationTokenSource _acceptLoopCts = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _authorityListener = new TcpListener(IPAddress.Loopback, 0);
        _authorityListener.Start();
        int port = ((IPEndPoint)_authorityListener.LocalEndpoint).Port;
        string authority = $"http://127.0.0.1:{port}";
        _acceptLoopCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_acceptLoopCts.IsCancellationRequested)
            {
                using TcpClient accepted = await _authorityListener.AcceptTcpClientAsync(
                    _acceptLoopCts.Token
                );
                Interlocked.Increment(ref _connectionAttempts);
            }
        });

        Manager = new ScratchConfigurationManager(authority);
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(Manager, Capture, authority);

        for (int i = 0; i < 3; i++)
        {
            using var response = await Client.SendAsync(
                AuthorizedRequest(ScratchSigning.MintToken(authority, ProbeHost.Audience))
            );
            _statusCodes.Add(response.StatusCode);
        }
    }

    [OneTimeTearDown]
    public void StopListener()
    {
        _acceptLoopCts.Cancel();
        _authorityListener.Stop();
        _acceptLoopCts.Dispose();
    }

    [Test]
    public void It_authenticates_every_request_from_the_manager()
    {
        _statusCodes.Should().AllBeEquivalentTo(HttpStatusCode.OK);
    }

    [Test]
    public void It_sends_zero_backchannel_requests_to_the_authority()
    {
        Volatile.Read(ref _connectionAttempts).Should().Be(0);
    }

    [Test]
    public void It_keeps_the_supplied_manager_after_post_configuration()
    {
        var options = App!
            .Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        options.ConfigurationManager.Should().BeSameAs(Manager);
    }
}

// E0(d), first half — FRAMEWORK fact (V-2): the token handed to GetConfigurationAsync is
// the request's own HttpContext.RequestAborted, not CancellationToken.None or a detached
// token.
[TestFixture]
public class Given_a_completed_request_with_a_recording_manager : ProbeFixtureBase
{
    private HttpResponseMessage _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority);
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(Manager, Capture, Authority);

        _response = await Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience))
        );
    }

    [OneTimeTearDown]
    public void DisposeResponse()
    {
        _response.Dispose();
    }

    [Test]
    public void It_authenticates()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_passes_RequestAborted_to_GetConfigurationAsync()
    {
        Manager.ReceivedTokens.Should().ContainSingle();
        Capture.RequestAbortedTokens.Should().ContainSingle();
        Manager
            .ReceivedTokens.Single()
            .Should()
            .Be(Capture.RequestAbortedTokens.Single(), "V-2: the handler passes Context.RequestAborted");
    }
}

// E0(d), second half — the framework fact is cancellation PROPAGATION (aborting the
// request cancels the token inside GetConfigurationAsync); waiter-only detachment — the
// canceled request leaves the shared load and the OTHER waiter still completes 200 — is
// SCRATCH-PROVIDER design (the D-5 single-flight shape) demonstrated on top of it.
[TestFixture]
public class Given_cancellation_during_a_gated_cold_load : ProbeFixtureBase
{
    private Exception? _abortedRequestOutcome;
    private HttpResponseMessage _survivorResponse = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority) { AwaitGate = true };
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(Manager, Capture, Authority);

        using CancellationTokenSource abortSource = new();
        Task<HttpResponseMessage> abortedRequest = Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience)),
            abortSource.Token
        );
        Task<HttpResponseMessage> survivorRequest = Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience))
        );

        // Both requests must be waiting inside the manager before the abort.
        while (Manager.CallCount < 2)
        {
            await Task.Delay(25);
        }

        abortSource.Cancel();
        try
        {
            using var aborted = await abortedRequest;
        }
        catch (Exception ex)
        {
            _abortedRequestOutcome = ex;
        }

        // The canceled waiter must have detached before the load completes.
        while (!Manager.WaiterOutcomes.Contains("canceled"))
        {
            await Task.Delay(25);
        }

        Manager.ReleaseGate();
        _survivorResponse = await survivorRequest;
    }

    [OneTimeTearDown]
    public void DisposeResponse()
    {
        _survivorResponse?.Dispose();
    }

    [Test]
    public void It_cancels_the_aborted_requests_waiter()
    {
        // Framework fact: the abort reached the token inside GetConfigurationAsync.
        Manager.WaiterOutcomes.Should().Contain("canceled");
        _abortedRequestOutcome.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Test]
    public void It_cancels_only_that_waiter_and_the_survivor_completes()
    {
        // Scratch-provider design: per-waiter detachment from the shared load.
        _survivorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        Manager.WaiterOutcomes.Should().Contain("completed");
    }
}

// E0(e) — the D-3/D-9 prototype end to end: boundary classification maps provider
// unavailability to 503 + Retry-After (cold and expired are SCRATCH-PROVIDER states; the
// Fail/challenge mechanics are the framework facts pinned above), recovery serves 200,
// and on the 200 the FRAMEWORK facts of V-2 are observed on the real validation call:
// the cloned TokenValidationParameters has ConfigurationManager == null and the manager's
// signing keys are concatenated into IssuerSigningKeys.
[TestFixture]
public class Given_the_boundary_translation_prototype : ProbeFixtureBase
{
    private HttpResponseMessage _coldResponse = null!;
    private HttpResponseMessage _expiredResponse = null!;
    private HttpResponseMessage _recoveredResponse = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority) { State = ScratchProviderState.ColdFailing };
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(
            Manager,
            Capture,
            Authority,
            boundaryProviderCheck: true,
            useRecordingTokenHandler: true
        );
        string token = ScratchSigning.MintToken(Authority, ProbeHost.Audience);

        _coldResponse = await Client.SendAsync(AuthorizedRequest(token));

        Manager.State = ScratchProviderState.Expired;
        _expiredResponse = await Client.SendAsync(AuthorizedRequest(token));

        Manager.State = ScratchProviderState.Usable;
        _recoveredResponse = await Client.SendAsync(AuthorizedRequest(token));
    }

    [OneTimeTearDown]
    public void DisposeResponses()
    {
        _coldResponse.Dispose();
        _expiredResponse.Dispose();
        _recoveredResponse.Dispose();
    }

    [Test]
    public void It_returns_503_with_retry_after_for_a_cold_failure()
    {
        _coldResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _coldResponse.Headers.RetryAfter.Should().NotBeNull();
    }

    [Test]
    public void It_returns_503_when_the_snapshot_is_expired()
    {
        _expiredResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public void It_returns_200_after_recovery()
    {
        _recoveredResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_makes_no_manager_call_while_unavailable()
    {
        // The two 503s happened at the boundary; only the recovered request retrieved.
        Manager.CallCount.Should().Be(1);
    }

    [Test]
    public void It_leaves_the_cloned_TokenValidationParameters_ConfigurationManager_null()
    {
        Capture
            .ValidationParameters.Should()
            .ContainSingle()
            .Which.ConfigurationManager.Should()
            .BeNull("V-2: a non-Base IConfigurationManager is awaited, not assigned to the clone");
    }

    [Test]
    public void It_concatenates_the_manager_keys_into_IssuerSigningKeys()
    {
        Capture
            .ValidationParameters.Single()
            .IssuerSigningKeys.Should()
            .Contain(key => key.KeyId == ScratchSigning.KeyId);
    }
}

// E0(f) — FRAMEWORK fact (V-5): options.Audience becomes TokenValidationParameters
// .ValidAudience when the TVP does not set one — exactly the CMS self-contained shape
// (ValidateAudience=true with no ValidAudience on the TVP).
[TestFixture]
public class Given_audience_configuration_from_options : ProbeFixtureBase
{
    private HttpResponseMessage _configuredAudienceResponse = null!;
    private HttpResponseMessage _wrongAudienceResponse = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Manager = new ScratchConfigurationManager(Authority);
        Capture = new ProbeCapture();
        (App, Client) = await ProbeHost.StartAsync(Manager, Capture, Authority);

        _configuredAudienceResponse = await Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, ProbeHost.Audience))
        );
        _wrongAudienceResponse = await Client.SendAsync(
            AuthorizedRequest(ScratchSigning.MintToken(Authority, "some-other-audience"))
        );
    }

    [OneTimeTearDown]
    public void DisposeResponses()
    {
        _configuredAudienceResponse.Dispose();
        _wrongAudienceResponse.Dispose();
    }

    [Test]
    public void It_accepts_the_audience_configured_on_options()
    {
        _configuredAudienceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_rejects_a_different_audience()
    {
        _wrongAudienceResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public void It_reports_the_audience_failure_type()
    {
        Capture
            .AuthenticationFailures.Should()
            .ContainSingle(ex => ex.GetType().Name.Contains("SecurityTokenInvalidAudienceException"));
    }
}
