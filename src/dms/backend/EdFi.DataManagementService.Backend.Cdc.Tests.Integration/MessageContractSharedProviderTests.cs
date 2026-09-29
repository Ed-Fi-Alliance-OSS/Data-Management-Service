// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
[Category("CdcMessageContract")]
[Property("CdcInvariant", "CDC-INV-10")]
public sealed class Given_MessageContractProviderAttachment(CdcProvider provider)
{
    private CdcDeploymentRequest _request = null!;

    [SetUp]
    public void Setup() =>
        _request = CdcDeploymentRequestTestData.Request(provider, endpoint: "http://attached-connect:18083/");

    [TestCase(false)]
    [TestCase(true)]
    [Property("CdcInvariant", "CDC-INV-06")]
    public async Task It_checks_the_effective_include_list_at_the_supplied_endpoint(bool includeWork)
    {
        using var handler = new ConfigurationHandler(includeWork);
        using var client = new HttpClient(handler);
        var observer = new MessageContractProviderObserver(
            _request.Binding,
            () => throw new AssertionException("Configuration inspection must not open the database.")
        );
        var fences = new MessageContractProviderFences(
            observer,
            _request,
            new CdcConnectRestAdapter(client, TimeProvider.System)
        );
        using var scope = new AssertionScope();
        await fences.AssertConnectorIncludeListAsync(CancellationToken.None);
        string[] failures = scope.Discard();
        handler
            .RequestUri.Should()
            .Be(new Uri(_request.ConnectEndpoint, $"connectors/{_request.Binding.ConnectorName}/config"));
        failures.Length.Should().Be(includeWork ? 1 : 0);
    }

    [TestCase("CdcHeartbeat", false)]
    [TestCase("DocumentProjectionWork", false)]
    [TestCase("CdcHeartbeat", true)]
    public void It_classifies_retained_progress_without_source_observer_instrumentation(
        string table,
        bool wrongBinding
    )
    {
        var record = Record(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    source = new
                    {
                        schema = "dms",
                        table,
                        name = wrongBinding ? "other-binding" : _request.Binding.ConnectorName,
                    },
                }
            )
        );
        using var scope = new AssertionScope();
        var kind = MessageContractProgressAssertions.AssertHeartbeat(_request.Binding, record);
        string[] failures = scope.Discard();
        failures.Length.Should().Be(table == "CdcHeartbeat" && !wrongBinding ? 0 : 1);
        kind.Should().Be(MessageContractProgressKind.RelationalHeartbeat);
    }

    [Test]
    public void It_classifies_native_progress_from_the_retained_record()
    {
        var kind = MessageContractProgressAssertions.AssertHeartbeat(
            _request.Binding,
            Record("{\"ts_ms\":23}"u8.ToArray())
        );
        kind.Should().Be(MessageContractProgressKind.NativeHeartbeat);
    }

    private MessageContractKafkaRecord Record(byte[] value) =>
        new(
            CoreCdc
                .CdcArtifactNameGenerator.RecoverFromBinding(_request.Binding)
                .Inventory!.ProgressTopicName,
            0,
            42,
            MessageContractKafkaBytes.From(Encoding.UTF8.GetBytes("cdc-progress")),
            MessageContractKafkaBytes.From(value),
            [],
            0
        );

    private sealed class ConfigurationHandler(bool includeWork) : HttpMessageHandler
    {
        public Uri RequestUri { get; private set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri!;
            string includeList =
                @"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat"
                + (includeWork ? @",dms\.DocumentProjectionWork" : "");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new Dictionary<string, string> { ["table.include.list"] = includeList }
                        )
                    ),
                }
            );
        }
    }
}

[TestFixture("FAILED", "RUNNING")]
[TestFixture("RUNNING", "FAILED")]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractProviderFence_WithFailedStatus(
    string connectorState,
    string taskState
)
{
    private MessageContractProviderFences _fences = null!;
    private HttpClient _client = null!;
    private StatusHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        var request = CdcDeploymentRequestTestData.Request(CdcProvider.Postgresql);
        var observer = new MessageContractProviderObserver(
            request.Binding,
            () =>
            {
                var connection = A.Fake<DbConnection>();
                var command = A.Fake<DbCommand>();
                A.CallTo(connection)
                    .WithReturnType<DbCommand>()
                    .Where(call => call.Method.Name == "CreateDbCommand")
                    .Returns(command);
                A.CallTo(() => command.ExecuteScalarAsync(A<CancellationToken>._)).Returns("0/1");
                A.CallTo(() => command.ExecuteNonQueryAsync(A<CancellationToken>._)).Returns(1);
                return connection;
            }
        );
        _handler = new(request.Binding.ConnectorName, connectorState, taskState);
        _client = new(_handler);
        _fences = new(observer, request, new CdcConnectRestAdapter(_client));
    }

    [TearDown]
    public void Teardown() => _client.Dispose();

    [Test]
    public async Task It_fails_on_the_first_failed_status_instead_of_waiting_for_the_fence_timeout()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Func<Task> fence = () => _fences.FencePostgresqlSourceAsync("failed-connector", timeout.Token);
        await fence
            .Should()
            .ThrowAsync<AssertionException>()
            .WithMessage("Kafka Connect connector or task failed during source fence. Details redacted.");
        _handler.RequestCount.Should().Be(1);
    }

    private sealed class StatusHandler(string connectorName, string connectorState, string taskState)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new
                            {
                                name = connectorName,
                                connector = new { state = connectorState, worker_id = "private-worker" },
                                tasks = new[]
                                {
                                    new
                                    {
                                        id = 0,
                                        state = taskState,
                                        worker_id = "private-worker",
                                    },
                                },
                            }
                        )
                    ),
                }
            );
        }
    }
}
