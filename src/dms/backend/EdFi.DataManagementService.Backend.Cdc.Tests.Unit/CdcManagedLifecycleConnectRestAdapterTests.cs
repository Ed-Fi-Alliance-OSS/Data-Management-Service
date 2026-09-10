// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcManagedLifecycle_with_production_Connect_adapter(Ddl.CdcProvider provider)
    : CdcReadinessTestBase(provider)
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }

    private HttpClient _client = null!;
    private CdcManagedLifecycle _managed = null!;
    private string _state = "RUNNING";
    private string _response = "accepted";
    private List<(HttpMethod Method, string Path)> _httpCalls = null!;

    [SetUp]
    public async Task SetupProductionAdapter()
    {
        // Real REST status omits snapshot state, so retain completed initial admission before restart.
        (await ReadyAsync())
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed);
        ShortTiming(1000);
        _state = "RUNNING";
        _response = "accepted";
        _httpCalls = [];
        // Expire HTTP before the enclosing controller call so its independent reconciliation can run.
        _client = new(new Handler(SendAsync)) { Timeout = TimeSpan.FromMilliseconds(250) };
        CdcConnectRestAdapter connect = new(_client);
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        CdcEstablishedValidation validation = new(
            _store,
            bindings,
            _provider,
            _templates,
            _kafka,
            connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );
        CdcControllerStatus status = new(_store, bindings, connect, _ => validation, TimeProvider.System);
        _managed = new(_store, bindings, connect, _worker, status, TimeProvider.System);
    }

    [TearDown]
    public void DisposeHttpClient() => _client.Dispose();

    [TestCase("RUNNING", "connection")]
    [TestCase("RUNNING", "timeout")]
    [TestCase("RUNNING", "conflict")]
    [TestCase("RUNNING", "accepted")]
    [TestCase("FAILED", "connection")]
    [TestCase("FAILED", "timeout")]
    [TestCase("FAILED", "conflict")]
    [TestCase("FAILED", "accepted")]
    [TestCase("STOPPED", "connection")]
    [TestCase("STOPPED", "timeout")]
    [TestCase("STOPPED", "conflict")]
    [TestCase("STOPPED", "accepted")]
    public async Task It_requires_acknowledgement_or_an_observed_transition_to_complete_restart(
        string initialState,
        string response
    )
    {
        if (initialState == "STOPPED")
        {
            (await Execute(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        }
        _state = initialState;
        _response = response;
        _httpCalls.Clear();

        var result = await Execute(CdcManagedLifecycleOperation.Restart);

        bool expectedSuccess = initialState != "RUNNING" || response == "accepted";
        result.Succeeded.Should().Be(expectedSuccess, JsonSerializer.Serialize(result));
        result.Ready.Should().Be(expectedSuccess);
        var operation = ReadJournal().Operations.Last();
        operation.Effect.Should().Be(CdcWorkflowEffect.ResumeConnector);
        operation.Completions.Should().HaveCount(expectedSuccess ? 1 : 0);
        _httpCalls
            .Where(call => call.Method != HttpMethod.Get)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                initialState == "STOPPED"
                    ? (HttpMethod.Put, ConnectorPath("/resume"))
                    : (HttpMethod.Post, ConnectorPath("/restart?includeTasks=true&onlyFailed=false"))
            );
        if (response != "accepted" && initialState != "STOPPED")
        {
            var expectedFailure = response switch
            {
                "timeout" => CdcDeploymentFailure.Timeout,
                "conflict" => CdcDeploymentFailure.Conflict,
                _ => CdcDeploymentFailure.Unavailable,
            };
            result
                .Diagnostics.Should()
                .Contain(d => d.Component == CdcDeploymentComponent.Connect && d.Failure == expectedFailure);
        }
        JsonSerializer.Serialize(result).Should().NotContain("private-password");
    }

    private Task<CdcManagedLifecycleResult> Execute(CdcManagedLifecycleOperation operation) =>
        _managed.ExecuteAsync(new(_request, _runtime, 1000), operation, CancellationToken.None);

    private string ConnectorPath(string suffix) =>
        _request.ConnectEndpoint.AbsolutePath.TrimEnd('/')
        + "/connectors/"
        + _request.Binding.ConnectorName
        + suffix;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.PathAndQuery;
        _httpCalls.Add((request.Method, path));
        if (request.Method == HttpMethod.Get)
        {
            if (path == ConnectorPath("/config"))
            {
                return JsonResponse(_live);
            }
            if (path == ConnectorPath("/status"))
            {
                return JsonResponse(
                    new
                    {
                        name = _request.Binding.ConnectorName,
                        connector = new
                        {
                            state = _state == "FAILED" ? "RUNNING" : _state,
                            worker_id = "worker:8083",
                        },
                        tasks = Enumerable
                            .Range(0, _state == "STOPPED" ? 0 : 1)
                            .Select(id => new
                            {
                                id,
                                state = _state,
                                worker_id = "worker:8083",
                            }),
                    }
                );
            }
            if (path == ConnectorPath("/offsets"))
            {
                Dictionary<string, string> partition = new() { ["server"] = _request.Binding.ConnectorName };
                if (Provider == Ddl.CdcProvider.SqlServer)
                {
                    partition["database"] = _request.ProviderConnectionProperties.Properties[
                        "database.names"
                    ];
                }
                return JsonResponse(
                    new
                    {
                        offsets = new[]
                        {
                            new
                            {
                                partition,
                                offset = Provider == Ddl.CdcProvider.Postgresql
                                    ? (object)new { lsn_proc = 16L, snapshot = false }
                                    : new
                                    {
                                        commit_lsn = "00000001:00000002:0003",
                                        change_lsn = "00000001:00000002:0003",
                                        event_serial_no = CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo,
                                        snapshot = false,
                                    },
                            },
                        },
                    }
                );
            }
        }
        if (request.Method == HttpMethod.Put && path == ConnectorPath("/stop"))
        {
            _state = "STOPPED";
            return new(HttpStatusCode.Accepted);
        }
        if (
            (
                request.Method == HttpMethod.Post
                && path == ConnectorPath("/restart?includeTasks=true&onlyFailed=false")
            ) || (request.Method == HttpMethod.Put && path == ConnectorPath("/resume"))
        )
        {
            // The request may take effect even when its reply is lost or unsuccessful.
            _state = "RUNNING";
            if (_response == "connection")
            {
                throw new HttpRequestException("private-password");
            }
            if (_response == "timeout")
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(_response == "conflict" ? HttpStatusCode.Conflict : HttpStatusCode.Accepted);
        }
        throw new InvalidOperationException("Unexpected HTTP call.");
    }

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
}
