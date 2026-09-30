// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using static EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.IdentityCmsStubHost;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Proves that a claim-set fetch already in flight when the host begins a graceful shutdown still
/// completes, so the draining request that needs it gets its answer rather than a failure: the
/// shutdown signal fires while the fetch is blocked at the Configuration Service. The same cache serves
/// every resource route. The identity tenant refresh is deliberately different: it stops when shutdown
/// begins, so a request waiting on it is answered from a still-fresh snapshot or with 503. The host
/// runs on real Kestrel over loopback, because Kestrel,
/// unlike the in-memory test server, keeps serving in-flight requests after shutdown begins.
/// </summary>
public class IdentityShutdownDrainTests
{
    /// <summary>
    /// Timing scenario, run once in <c>[OneTimeSetUp]</c>: shutdown must begin while the claim-set
    /// fetch is blocked, and the gate is released only afterwards.
    /// </summary>
    [TestFixture]
    public class Given_Host_Shutdown_Begins_While_A_Claim_Set_Fetch_Is_In_Flight
    {
        private StubHost _host = null!;
        private HttpStatusCode _status;
        private JsonNode? _body;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _host = Create(new CmsStub("v3/authorizationMetadata"));
            _host.Factory.UseKestrel(0);
            _host.Factory.StartServer();
            string serverAddress = _host
                .Factory.Services.GetRequiredService<IServer>()
                .Features.GetRequiredFeature<IServerAddressesFeature>()
                .Addresses.First();
            using HttpClient client = _host.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { BaseAddress = new Uri(serverAddress) }
            );

            using HttpRequestMessage request = IdentityGet("605943412");
            Task<HttpResponseMessage> requestTask = client.SendAsync(request);
            await _host.Cms.GateReached.WaitAsync(TimeSpan.FromSeconds(10));

            _host.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            _host.Cms.ReleaseGate();

            using HttpResponseMessage response = await requestTask.WaitAsync(TimeSpan.FromSeconds(10));
            _status = response.StatusCode;
            _body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown() => await _host.Factory.DisposeAsync();

        [Test]
        public void It_answers_the_draining_request_as_if_no_shutdown_had_begun()
        {
            _status.Should().Be(HttpStatusCode.NotFound);
            _body!["type"]!.GetValue<string>().Should().Be(IdentityFailureResponse.OperationNotSupportedType);
        }

        [Test]
        public void It_reaches_the_identity_service()
        {
            A.CallTo(() => _host.IdentityService.Capabilities).MustHaveHappened();
        }
    }
}
