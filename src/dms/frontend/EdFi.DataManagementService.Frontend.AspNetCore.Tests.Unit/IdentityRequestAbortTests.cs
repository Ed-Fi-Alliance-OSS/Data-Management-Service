// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.IdentityCmsStubHost;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Proves at the HTTP boundary that a client abort, delivered only through
/// <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestAborted" /> (the client's token is passed
/// straight into <c>SendAsync</c>, with no middleware driving an abort), propagates out of the real
/// identity pipeline while it is blocked inside each Configuration Service lookup it makes: token
/// acquisition, tenant retrieval, application lookup, and claim retrieval. Each fixture runs on an
/// <see cref="IdentityCmsStubHost" />, so every lookup is the production call over the stub, and a
/// second, live request held at the same gate must still finish once it opens.
/// </summary>
public class IdentityRequestAbortTests
{
    /// <summary>
    /// Timing/concurrency scenario, run once per gate in <c>[OneTimeSetUp]</c>: the cancelled request
    /// must reach the gate before the live request is sent, and the gate is only released after the
    /// cancelled request's outcome is observed.
    /// </summary>
    [TestFixture("connect/token")]
    [TestFixture("v3/tenants/")]
    [TestFixture("v3/apiClients/")]
    [TestFixture("v3/authorizationMetadata")]
    public class Given_The_Client_Cancels_While_The_Identity_Pipeline_Is_Blocked_In_A_Cms_Lookup(
        string gateUrlSubstring
    )
    {
        private Host _host = null!;
        private Exception? _clientException;
        private ServerOutcome _cancelledOutcome = null!;
        private bool _identityServiceTouchedBeforeRelease;
        private bool _liveRequestFinishedBeforeRelease;
        private HttpStatusCode _liveStatus;
        private JsonNode? _liveBody;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _host = Create(new CmsStub(gateUrlSubstring));
            using HttpClient client = _host.Factory.CreateClient();
            using var cancellationSource = new CancellationTokenSource();

            using HttpRequestMessage cancelledRequest = IdentityGet("605943412", caller: "cancelled");
            Task<HttpResponseMessage> cancelledTask = client.SendAsync(
                cancelledRequest,
                cancellationSource.Token
            );
            await _host.Cms.GateReached.WaitAsync(TimeSpan.FromSeconds(10));

            using HttpRequestMessage liveRequest = IdentityGet("605943413", caller: "live");
            Task<HttpResponseMessage> liveTask = client.SendAsync(liveRequest);

            // A short real-time settle so the live request has joined the blocked lookup before the
            // cancellation, matching the interleaving-wait precedent in IdentityCmsCancellationTests.
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            await cancellationSource.CancelAsync();
            try
            {
                using HttpResponseMessage unexpected = await cancelledTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                _clientException = exception;
            }

            _cancelledOutcome = await _host
                .Outcomes.OutcomeOf("cancelled")
                .WaitAsync(TimeSpan.FromSeconds(5));
            _identityServiceTouchedBeforeRelease = Fake.GetCalls(_host.IdentityService).Any();
            _liveRequestFinishedBeforeRelease = liveTask.IsCompleted;

            _host.Cms.ReleaseGate();

            using HttpResponseMessage liveResponse = await liveTask.WaitAsync(TimeSpan.FromSeconds(10));
            _liveStatus = liveResponse.StatusCode;
            _liveBody = JsonNode.Parse(await liveResponse.Content.ReadAsStringAsync());
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown() => await _host.Factory.DisposeAsync();

        [Test]
        public void It_cancels_the_client_request()
        {
            _clientException.Should().BeAssignableTo<TaskCanceledException>();
        }

        [Test]
        public void It_delivers_the_abort_through_RequestAborted()
        {
            _cancelledOutcome.RequestAborted.Should().BeTrue();
        }

        [Test]
        public void It_propagates_the_cancellation_out_of_the_pipeline()
        {
            _cancelledOutcome.Escaped.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_writes_no_response_for_the_cancelled_request()
        {
            _cancelledOutcome.ResponseStarted.Should().BeFalse();
        }

        [Test]
        public void It_never_reaches_the_identity_service_for_the_cancelled_request()
        {
            _identityServiceTouchedBeforeRelease.Should().BeFalse();
        }

        [Test]
        public void It_holds_the_live_request_at_the_same_lookup_until_the_gate_opens()
        {
            _liveRequestFinishedBeforeRelease.Should().BeFalse();
        }

        [Test]
        public void It_lets_the_live_request_reach_the_identity_service_once_the_gate_opens()
        {
            _liveStatus.Should().Be(HttpStatusCode.NotFound);
            _liveBody!["type"]!
                .GetValue<string>()
                .Should()
                .Be(IdentityFailureResponse.OperationNotSupportedType);
            A.CallTo(() => _host.IdentityService.Capabilities).MustHaveHappened();
        }
    }
}
