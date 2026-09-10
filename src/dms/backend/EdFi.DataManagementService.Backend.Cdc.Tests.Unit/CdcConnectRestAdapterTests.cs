// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal sealed class CdcConnectHttpFixture : IDisposable
{
    internal sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];
        public Queue<Func<CancellationToken, Task<HttpResponseMessage>>> Responses { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(
                (
                    request.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Content is not null
                        ? await request.Content.ReadAsStringAsync(cancellationToken)
                        : ""
                )
            );
            if (Responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected HTTP call.");
            }
            return await Responses.Dequeue()(cancellationToken);
        }
    }

    public Handler Http { get; } = new();
    public HttpClient Client { get; }
    public CdcConnectRestAdapter Adapter { get; }
    public CdcDeploymentRequest Request { get; }
    public CdcKafkaConnectRegistrationPayload Payload { get; }
    public string Config => JsonSerializer.Serialize(Payload.Config);

    public string Path(string suffix) => "/management/connectors/" + Request.Binding.ConnectorName + suffix;

    public CdcConnectHttpFixture(
        CdcProvider provider = CdcProvider.Postgresql,
        int callMilliseconds = 1000,
        int waitMilliseconds = 5000
    )
    {
        var original = CdcDeploymentRequestTestData.Request(
            provider,
            endpoint: "http://connect:8083/management"
        );
        Request = new(
            original.Binding,
            original.DmsSettings,
            original.ProviderSetup,
            original.ConnectEndpoint,
            original.WorkerMetricsEndpoint,
            original.ConnectorPolicy,
            original.WorkerPolicy,
            original.ProviderConnectionProperties,
            original.KafkaClientSecurityProperties,
            new(
                TimeSpan.FromMilliseconds(callMilliseconds),
                TimeSpan.FromMilliseconds(waitMilliseconds),
                TimeSpan.FromMilliseconds(1)
            )
        );
        Payload = new(
            new(Request.Binding.ConnectorName),
            new Dictionary<string, string>
            {
                ["connector.class"] =
                    provider == CdcProvider.Postgresql
                        ? "io.debezium.connector.postgresql.PostgresConnector"
                        : "io.debezium.connector.sqlserver.SqlServerConnector",
                ["database.password"] = "${env:DATABASE_PASSWORD}",
                ["producer.override.max.request.size"] = "2000000",
            }
        );
        Client = new(Http) { Timeout = Timeout.InfiniteTimeSpan };
        Adapter = new(Client);
    }

    public void Respond(HttpStatusCode code = HttpStatusCode.OK, string body = "{}") =>
        Http.Responses.Enqueue(_ =>
            Task.FromResult(
                new HttpResponseMessage(code)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            )
        );

    public void TimeoutOnce() =>
        Http.Responses.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });

    public string Status(
        string state = "RUNNING",
        string taskState = "RUNNING",
        int taskCount = 1,
        bool worker = true
    ) =>
        JsonSerializer.Serialize(
            new
            {
                name = Request.Binding.ConnectorName,
                connector = new { state, worker_id = worker ? "private-worker:8083" : "" },
                tasks = Enumerable
                    .Range(0, taskCount)
                    .Select(id => new
                    {
                        id,
                        state = taskState,
                        worker_id = worker ? "private-worker:8083" : "",
                    }),
            }
        );

    public string Offsets(string offset, string server = "", string database = "", int count = 1)
    {
        Dictionary<string, string> partition = new()
        {
            ["server"] = server.Length > 0 ? server : Request.Binding.ConnectorName,
        };
        if (Request.Binding.Provider == CoreCdc.CdcProvider.SqlServer)
        {
            partition["database"] =
                database.Length > 0
                    ? database
                    : Request.ProviderConnectionProperties.Properties["database.names"];
        }
        return JsonSerializer.Serialize(
            new
            {
                offsets = Enumerable
                    .Range(0, count)
                    .Select(_ => new { partition, offset = JsonSerializer.Deserialize<JsonElement>(offset) }),
            }
        );
    }

    public void Dispose() => Client.Dispose();
}

[TestFixture]
public class Given_CdcConnectRestAdapter_configuration_reads
{
    private CdcConnectHttpFixture _fixture = null!;

    [SetUp]
    public void Setup() => _fixture = new();

    [TearDown]
    public void Teardown() => _fixture.Dispose();

    [TestCase(HttpStatusCode.NotFound, CdcTransportEvidenceState.Absent)]
    [TestCase(HttpStatusCode.Unauthorized, CdcTransportEvidenceState.Unavailable)]
    [TestCase(HttpStatusCode.Forbidden, CdcTransportEvidenceState.Unavailable)]
    [TestCase(HttpStatusCode.Conflict, CdcTransportEvidenceState.Unavailable)]
    [TestCase(HttpStatusCode.InternalServerError, CdcTransportEvidenceState.Unavailable)]
    [TestCase(HttpStatusCode.Redirect, CdcTransportEvidenceState.Unavailable)]
    public async Task It_distinguishes_authoritative_absence_from_failed_reads(
        HttpStatusCode code,
        CdcTransportEvidenceState expected
    )
    {
        _fixture.Respond(code, "private-source secret-error");
        var result = await _fixture.Adapter.ReadConfigurationAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(expected);
        JsonSerializer.Serialize(result).Should().NotContain("private-source").And.NotContain("secret-error");
        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.AuthenticationFailed);
        }
    }

    [TestCase("{broken")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"key\":null}")]
    [TestCase("{\"key\":42}")]
    [TestCase("{\"key\":\"first\",\"key\":\"last\"}")]
    public async Task It_rejects_malformed_configuration(string body)
    {
        _fixture.Respond(body: body);
        var result = await _fixture.Adapter.ReadConfigurationAsync(_fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.ValidationFailed);
    }

    [Test]
    public async Task It_preserves_masked_credentials_without_substituting_request_values()
    {
        _fixture.Respond(body: "{\"database.password\":\"********\"}");
        var result = await _fixture.Adapter.ReadConfigurationAsync(_fixture.Request, CancellationToken.None);
        result
            .Should()
            .BeOfType<CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed>()
            .Which.Value["database.password"]
            .Should()
            .Be("********");
        JsonSerializer.Serialize(result).Should().NotContain("password");
        _fixture.Http.Calls.Single().Path.Should().Be(_fixture.Path("/config"));
    }

    [Test]
    public async Task It_bounds_response_size()
    {
        _fixture.Respond(
            body: "{\"key\":\"" + new string('x', CdcConnectRestAdapter.MaximumResponseBytes) + "\"}"
        );
        var result = await _fixture.Adapter.ReadConfigurationAsync(_fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.ValidationFailed);
    }

    [Test]
    public async Task It_sanitizes_transport_exceptions()
    {
        _fixture.Http.Responses.Enqueue(_ => throw new HttpRequestException("secret private-source"));
        var result = await _fixture.Adapter.ReadConfigurationAsync(_fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("secret").And.NotContain("private-source");
    }
}

[TestFixture]
public class Given_CdcConnectRestAdapter_creation_and_preflight
{
    private CdcConnectHttpFixture _fixture = null!;

    [SetUp]
    public void Setup() => _fixture = new(callMilliseconds: 40);

    [TearDown]
    public void Teardown() => _fixture.Dispose();

    [TestCase(HttpStatusCode.Created)]
    [TestCase(HttpStatusCode.Conflict)]
    [TestCase(HttpStatusCode.GatewayTimeout)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task It_reconciles_the_single_create_after_acknowledgement_or_uncertain_outcome(
        HttpStatusCode code
    )
    {
        _fixture.Respond(HttpStatusCode.NotFound);
        _fixture.Respond(code, "secret mutation response");
        _fixture.Respond(body: _fixture.Config);
        var result = await _fixture.Adapter.CreateAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture
            .Http.Calls.Select(call => call.Method)
            .Should()
            .Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get);
        _fixture.Http.Calls[1].Path.Should().Be("/management/connectors");
        using var body = JsonDocument.Parse(_fixture.Http.Calls[1].Body);
        body.RootElement.GetProperty("name").GetString().Should().Be(_fixture.Payload.Name);
        body.RootElement.GetProperty("config")
            .GetProperty("database.password")
            .GetString()
            .Should()
            .Be("${env:DATABASE_PASSWORD}");
    }

    [Test]
    public async Task It_reconciles_timeout_after_commit_with_an_independent_read_budget()
    {
        _fixture.Respond(HttpStatusCode.NotFound);
        _fixture.TimeoutOnce();
        _fixture.Respond(body: _fixture.Config);
        var result = await _fixture.Adapter.CreateAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture.Http.Calls.Count(call => call.Method == HttpMethod.Post).Should().Be(1);
    }

    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task It_creates_only_after_authoritative_absence(HttpStatusCode code)
    {
        _fixture.Respond(code, _fixture.Config);
        var result = await _fixture.Adapter.CreateAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _fixture.Http.Calls.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [TestCase(HttpStatusCode.NotFound, "{}")]
    [TestCase(HttpStatusCode.OK, "{\"database.password\":\"********\"}")]
    [TestCase(HttpStatusCode.OK, "{\"drift\":\"secret\"}")]
    [TestCase(HttpStatusCode.Forbidden, "secret")]
    public async Task It_does_not_turn_an_acknowledged_create_into_authority_without_matching_readback(
        HttpStatusCode code,
        string readback
    )
    {
        _fixture.Respond(HttpStatusCode.NotFound);
        _fixture.Respond(HttpStatusCode.Created);
        _fixture.Respond(code, readback);
        var result = await _fixture.Adapter.CreateAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("secret");
    }

    [Test]
    public async Task It_rejects_foreign_payload_identity_before_http()
    {
        var payload = new CdcKafkaConnectRegistrationPayload(
            new("another-connector"),
            _fixture.Payload.Config
        );
        var result = await _fixture.Adapter.CreateAsync(_fixture.Request, payload, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.InvalidInput);
        _fixture.Http.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_validates_against_the_selected_plugin_without_creating_a_connector()
    {
        _fixture.Respond(
            body: "{\"error_count\":0,\"configs\":[{\"value\":{\"name\":\"connector.class\",\"errors\":[]}}]}"
        );
        var result = await _fixture.Adapter.ValidateConfigurationAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        var call = _fixture.Http.Calls.Single();
        call.Method.Should().Be(HttpMethod.Put);
        call.Path.Should()
            .Be(
                "/management/connector-plugins/io.debezium.connector.postgresql.PostgresConnector/config/validate"
            );
        call.Body.Should().Be(_fixture.Config);
    }

    [TestCase("{\"error_count\":1,\"configs\":[],\"message\":\"secret\"}")]
    [TestCase(
        "{\"error_count\":0,\"configs\":[{\"value\":{\"name\":\"connector.class\",\"errors\":[\"secret\"]}}]}"
    )]
    [TestCase("{\"error_count\":0,\"configs\":[]}")]
    [TestCase("{\"error_count\":0}")]
    [TestCase("{\"error_count\":-1,\"configs\":[]}")]
    [TestCase("{}")]
    public async Task It_rejects_invalid_or_incomplete_preflight_without_echoing_plugin_errors(string body)
    {
        _fixture.Respond(body: body);
        var result = await _fixture.Adapter.ValidateConfigurationAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.ValidationFailed);
        JsonSerializer.Serialize(result).Should().NotContain("secret");
    }
}

[TestFixture]
public class Given_CdcConnectRestAdapter_offsets
{
    [TestCase(CdcProvider.Postgresql, "{\"lsn_proc\":42}")]
    [TestCase(CdcProvider.Postgresql, "{\"lsn_proc\":-1}")]
    [TestCase(
        CdcProvider.SqlServer,
        "{\"commit_lsn\":\"00000001:00000002:0003\",\"change_lsn\":\"00000001:00000002:0004\",\"event_serial_no\":2}"
    )]
    public async Task It_hands_streaming_offsets_to_existing_provider_position_and_hash_contracts(
        CdcProvider provider,
        string offset
    )
    {
        using CdcConnectHttpFixture fixture = new(provider);
        fixture.Respond(body: fixture.Offsets(offset));
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        var value = result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
            .Which.Value;
        value.State.Should().Be(CdcConnectOffsetState.Streaming);
        value
            .SourcePartitionHash.Should()
            .Be(
                CoreCdc
                    .CdcSourcePartitionHashCalculator.Compute(
                        fixture.Request.Binding.Provider,
                        fixture.Request.Binding.ConnectorName,
                        provider == CdcProvider.SqlServer
                            ? fixture.Request.ProviderConnectionProperties.Properties["database.names"]
                            : null
                    )
                    .Hash
            );
        JsonSerializer.Serialize(value).Should().NotContain("Lsn").And.NotContain("sha256");
        fixture.Http.Calls.Single().Path.Should().Be(fixture.Path("/offsets"));
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public async Task It_preserves_missing_multiple_and_mismatched_partition_evidence(CdcProvider provider)
    {
        using CdcConnectHttpFixture fixture = new(provider);
        foreach (
            var (body, expected) in new[]
            {
                (fixture.Offsets("{}", count: 0), CdcConnectOffsetState.Missing),
                (fixture.Offsets("{}", count: 2), CdcConnectOffsetState.Multiple),
                (fixture.Offsets("{}", server: "other"), CdcConnectOffsetState.SourcePartitionMismatch),
                (fixture.Offsets("null"), CdcConnectOffsetState.Null),
                (fixture.Offsets("{\"snapshot\":true}"), CdcConnectOffsetState.Snapshot),
                (fixture.Offsets("{\"snapshot\":\"last\"}"), CdcConnectOffsetState.Snapshot),
                (fixture.Offsets("{\"snapshot\":\"INITIAL\"}"), CdcConnectOffsetState.Snapshot),
                (fixture.Offsets("{\"snapshot\":\"BLOCKING\"}"), CdcConnectOffsetState.Snapshot),
                (fixture.Offsets("{\"snapshot\":\"INCREMENTAL\"}"), CdcConnectOffsetState.Snapshot),
            }
        )
        {
            fixture.Respond(body: body);
            var result = await fixture.Adapter.ReadOffsetEvidenceAsync(
                fixture.Request,
                CancellationToken.None
            );
            result
                .Should()
                .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
                .Which.Value.State.Should()
                .Be(expected);
        }
    }

    [TestCase("NULL")]
    [TestCase("00000032:00003ff8:0050")]
    public async Task It_distinguishes_the_sql_server_initial_null_lsn_marker_from_an_absent_offset(
        string commit
    )
    {
        using CdcConnectHttpFixture fixture = new(CdcProvider.SqlServer);
        fixture.Respond(
            body: fixture.Offsets($$"""{"commit_lsn":"{{commit}}","change_lsn":"NULL","event_serial_no":1}""")
        );
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        var value = result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
            .Which.Value;
        value.State.Should().Be(CdcConnectOffsetState.AwaitingStreaming);
        value.SourcePartitionHash.Should().NotBeNullOrEmpty();
        value.SqlServer.CommitLsn.Should().BeNull();
    }

    [Test]
    public async Task It_compares_the_actual_sql_server_catalog_case_sensitively()
    {
        using CdcConnectHttpFixture fixture = new(CdcProvider.SqlServer);
        fixture.Respond(body: fixture.Offsets("{}", database: "another-private-database"));
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
            .Which.Value.State.Should()
            .Be(CdcConnectOffsetState.SourcePartitionMismatch);
    }

    [TestCase(CdcProvider.SqlServer, """{"commit_lsn":"NULL","change_lsn":"NULL","event_serial_no":2}""")]
    [TestCase(
        CdcProvider.SqlServer,
        """{"commit_lsn":"NULL","change_lsn":"00000001:00000002:0003","event_serial_no":1}"""
    )]
    [TestCase(CdcProvider.SqlServer, """{"commit_lsn":null,"change_lsn":null,"event_serial_no":1}""")]
    [TestCase(CdcProvider.Postgresql, "{}")]
    [TestCase(CdcProvider.Postgresql, "{\"lsn_proc\":\"42\"}")]
    [TestCase(CdcProvider.Postgresql, "{\"lsn_proc\":1.5}")]
    [TestCase(CdcProvider.Postgresql, "{\"lsn_proc\":18446744073709551615}")]
    [TestCase(CdcProvider.Postgresql, "{\"lsn_proc\":42,\"snapshot\":\"unexpected\"}")]
    [TestCase(CdcProvider.Postgresql, "[]")]
    [TestCase(
        CdcProvider.SqlServer,
        "{\"commit_lsn\":\"secret\",\"change_lsn\":\"secret\",\"event_serial_no\":2}"
    )]
    [TestCase(
        CdcProvider.SqlServer,
        "{\"commit_lsn\":\"00000001:00000002:0003\",\"change_lsn\":\"00000001:00000002:0004\",\"event_serial_no\":-1}"
    )]
    public async Task It_preserves_malformed_provider_evidence_without_exposing_invalid_text(
        CdcProvider provider,
        string offset
    )
    {
        using CdcConnectHttpFixture fixture = new(provider);
        fixture.Respond(body: fixture.Offsets(offset));
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        var value = result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
            .Which.Value;
        value.State.Should().Be(CdcConnectOffsetState.Malformed);
        JsonSerializer.Serialize(value).Should().NotContain("secret");
        value.SqlServer.CommitLsn.Should().BeNull();
    }

    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.MethodNotAllowed)]
    [TestCase(HttpStatusCode.NotImplemented)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task It_never_treats_an_unsupported_or_failed_offset_endpoint_as_missing_offsets(
        HttpStatusCode code
    )
    {
        using CdcConnectHttpFixture fixture = new();
        fixture.Respond(code);
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("{}")]
    [TestCase("{\"offsets\":null}")]
    [TestCase("{\"offsets\":[],\"offsets\":[{}]}")]
    public async Task It_rejects_malformed_offset_envelopes(string body)
    {
        using CdcConnectHttpFixture fixture = new();
        fixture.Respond(body: body);
        var result = await fixture.Adapter.ReadOffsetEvidenceAsync(fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.ValidationFailed);
    }
}

[TestFixture]
public class Given_CdcConnectRestAdapter_lifecycle
{
    private CdcConnectHttpFixture _fixture = null!;

    [SetUp]
    public void Setup() => _fixture = new(callMilliseconds: 40, waitMilliseconds: 300);

    [TearDown]
    public void Teardown() => _fixture.Dispose();

    [Test]
    public async Task It_waits_for_stopped_with_no_tasks_after_asynchronous_stop()
    {
        _fixture.Respond(HttpStatusCode.Accepted);
        _fixture.Respond(body: _fixture.Status("RUNNING"));
        _fixture.Respond(body: _fixture.Status("STOPPED"));
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        var result = await _fixture.Adapter.StopAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture
            .Http.Calls.Select(call => call.Method)
            .Should()
            .Equal(HttpMethod.Put, HttpMethod.Get, HttpMethod.Get, HttpMethod.Get);
        _fixture.Http.Calls[0].Path.Should().Be(_fixture.Path("/stop"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_exposes_resume_and_restart_and_reads_running_connector_and_task_state(bool restart)
    {
        _fixture.Respond(HttpStatusCode.Accepted);
        _fixture.Respond(body: _fixture.Status(taskState: "UNASSIGNED"));
        _fixture.Respond(body: _fixture.Status());
        var result = restart
            ? await _fixture.Adapter.RestartAsync(_fixture.Request, CancellationToken.None)
            : await _fixture.Adapter.ResumeAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture.Http.Calls[0].Method.Should().Be(restart ? HttpMethod.Post : HttpMethod.Put);
        _fixture
            .Http.Calls[0]
            .Path.Should()
            .Be(_fixture.Path(restart ? "/restart?includeTasks=true&onlyFailed=false" : "/resume"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reconciles_a_lost_stop_or_resume_response(bool resume)
    {
        _fixture.TimeoutOnce();
        _fixture.Respond(body: resume ? _fixture.Status() : _fixture.Status("STOPPED", taskCount: 0));
        var result = resume
            ? await _fixture.Adapter.ResumeAsync(_fixture.Request, CancellationToken.None)
            : await _fixture.Adapter.StopAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture.Http.Calls.Count(call => call.Method == HttpMethod.Put).Should().Be(1);
    }

    [TestCase("connection", CdcDeploymentFailure.Unavailable)]
    [TestCase("timeout", CdcDeploymentFailure.Timeout)]
    [TestCase("conflict", CdcDeploymentFailure.Conflict)]
    public async Task It_preserves_an_unsuccessful_restart_response_without_inferring_acknowledgement(
        string failure,
        CdcDeploymentFailure expected
    )
    {
        switch (failure)
        {
            case "connection":
                _fixture.Http.Responses.Enqueue(_ => throw new HttpRequestException("private-password"));
                break;
            case "timeout":
                _fixture.TimeoutOnce();
                break;
            default:
                _fixture.Respond(HttpStatusCode.Conflict);
                break;
        }
        _fixture.Respond(body: _fixture.Status());

        var result = await _fixture.Adapter.RestartAsync(_fixture.Request, CancellationToken.None);

        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result.Diagnostics.Should().ContainSingle().Which.Failure.Should().Be(expected);
        _fixture.Http.Calls.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
        JsonSerializer.Serialize(result).Should().NotContain("private-password");
    }

    [Test]
    public async Task It_requires_stopped_before_and_after_offset_reset_and_reads_empty_offsets()
    {
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        _fixture.Respond();
        _fixture.Respond(body: "{\"offsets\":[]}");
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        var result = await _fixture.Adapter.DeleteOffsetsAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture
            .Http.Calls.Select(call => call.Path)
            .Should()
            .Equal(
                _fixture.Path("/status"),
                _fixture.Path("/offsets"),
                _fixture.Path("/offsets"),
                _fixture.Path("/status")
            );
        _fixture.Http.Calls[1].Method.Should().Be(HttpMethod.Delete);
    }

    [TestCase("RUNNING", 1)]
    [TestCase("PAUSED", 1)]
    [TestCase("STOPPED", 1)]
    public async Task It_rejects_offset_reset_unless_stopped_with_no_tasks(string state, int tasks)
    {
        _fixture.Respond(body: _fixture.Status(state, taskCount: tasks));
        var result = await _fixture.Adapter.DeleteOffsetsAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _fixture.Http.Calls.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_reset_when_offsets_remain_or_the_connector_resumes(bool resumed)
    {
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        _fixture.Respond();
        _fixture.Respond(body: resumed ? "{\"offsets\":[]}" : _fixture.Offsets("{\"lsn_proc\":42}"));
        _fixture.Respond(body: resumed ? _fixture.Status() : _fixture.Status("STOPPED", taskCount: 0));
        var result = await _fixture.Adapter.DeleteOffsetsAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_verifies_connector_absence_after_delete()
    {
        _fixture.Respond(body: _fixture.Config);
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        _fixture.Respond(HttpStatusCode.NoContent);
        _fixture.Respond(body: _fixture.Config);
        _fixture.Respond(HttpStatusCode.NotFound);
        var result = await _fixture.Adapter.DeleteAsync(_fixture.Request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Absent);
        _fixture.Http.Calls.Count(call => call.Method == HttpMethod.Delete).Should().Be(1);
    }

    [Test]
    public async Task It_updates_configuration_only_through_the_explicit_stopped_record_size_operation()
    {
        _fixture.Respond(body: _fixture.Config);
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        _fixture.Respond();
        _fixture.Respond(body: _fixture.Config);
        var result = await _fixture.Adapter.UpdateConfigurationForRecordSizeIncreaseAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _fixture.Http.Calls[2].Method.Should().Be(HttpMethod.Put);
        _fixture.Http.Calls[2].Path.Should().Be(_fixture.Path("/config"));
    }

    [TestCase("lower")]
    [TestCase("unrelated")]
    [TestCase("added")]
    public async Task It_CdcRecordSizeIncrease_rejects_non_size_and_lowering_PUTs(string scenario)
    {
        _fixture.Respond(body: _fixture.Config);
        _fixture.Respond(body: _fixture.Status("STOPPED", taskCount: 0));
        Dictionary<string, string> updated = new(_fixture.Payload.Config);
        switch (scenario)
        {
            case "lower":
                updated["producer.override.max.request.size"] = "1";
                break;
            case "unrelated":
                updated["connector.class"] = "replacement";
                break;
            case "added":
                updated["snapshot.mode"] = "initial";
                break;
        }
        var result = await _fixture.Adapter.UpdateConfigurationForRecordSizeIncreaseAsync(
            _fixture.Request,
            new(new(_fixture.Payload.Name), updated),
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _fixture.Http.Calls.Should().NotContain(c => c.Method == HttpMethod.Put);
    }

    [Test]
    public async Task It_never_creates_a_missing_connector_through_configuration_update()
    {
        _fixture.Respond(HttpStatusCode.NotFound);
        var result = await _fixture.Adapter.UpdateConfigurationForRecordSizeIncreaseAsync(
            _fixture.Request,
            _fixture.Payload,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Absent);
        _fixture.Http.Calls.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Test]
    public async Task It_retains_worker_assignment_without_claiming_process_identity_or_snapshot_completion()
    {
        _fixture.Respond(body: _fixture.Status());
        var result = await _fixture.Adapter.ReadStatusAsync(_fixture.Request, CancellationToken.None);
        var status = result.Should().BeOfType<CdcTransportResult<CdcConnectStatus>.Observed>().Which.Value;
        status.WorkerId.Should().Be("private-worker:8083");
        status.Tasks.Single().WorkerId.Should().Be(status.WorkerId);
        status.Runtime.SnapshotState.Should().Be(CoreCdc.CdcConnectorSnapshotState.Unknown);
        JsonSerializer.Serialize(status).Should().NotContain("private-worker");
    }

    [Test]
    public async Task It_retains_unavailable_worker_identity_without_inventing_one_from_the_endpoint()
    {
        _fixture.Respond(body: _fixture.Status(worker: false));
        var result = await _fixture.Adapter.ReadStatusAsync(_fixture.Request, CancellationToken.None);
        result
            .Should()
            .BeOfType<CdcTransportResult<CdcConnectStatus>.Observed>()
            .Which.Value.WorkerId.Should()
            .BeEmpty();
    }

    [Test]
    public async Task It_bounds_the_whole_poll_pass()
    {
        using CdcConnectHttpFixture fixture = new(callMilliseconds: 10, waitMilliseconds: 60);
        fixture.Respond(HttpStatusCode.Accepted);
        for (int index = 0; index < 100; index++)
        {
            fixture.Respond(body: fixture.Status());
        }
        var result = await fixture.Adapter.StopAsync(fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
    }

    [Test]
    public async Task It_preserves_caller_cancellation_and_does_not_retry_the_write()
    {
        using CancellationTokenSource cancellation = new();
        _fixture.Http.Responses.Enqueue(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        Func<Task> act = () => _fixture.Adapter.StopAsync(_fixture.Request, cancellation.Token);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        _fixture.Http.Calls.Should().ContainSingle();
    }
}

[TestFixture]
public class Given_CdcConnectRestAdapter_failure_boundaries
{
    [TestCase("delete")]
    [TestCase("update")]
    [TestCase("offsets")]
    public async Task It_preserves_authentication_failure_during_mutation_guards(string operation)
    {
        using CdcConnectHttpFixture fixture = new();
        if (operation != "offsets")
        {
            fixture.Respond(body: fixture.Config);
        }
        fixture.Respond(HttpStatusCode.Forbidden, "private-secret");
        var result = operation switch
        {
            "delete" => await fixture.Adapter.DeleteAsync(fixture.Request, CancellationToken.None),
            "update" => await fixture.Adapter.UpdateConfigurationForRecordSizeIncreaseAsync(
                fixture.Request,
                fixture.Payload,
                CancellationToken.None
            ),
            _ => await fixture.Adapter.DeleteOffsetsAsync(fixture.Request, CancellationToken.None),
        };
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.AuthenticationFailed);
        fixture.Http.Calls.Should().OnlyContain(call => call.Method == HttpMethod.Get);
    }

    [TestCase("delete")]
    [TestCase("update")]
    [TestCase("offsets")]
    public async Task It_reconciles_lost_mutation_responses_without_resubmission(string operation)
    {
        using CdcConnectHttpFixture fixture = new(callMilliseconds: 40);
        if (operation != "offsets")
        {
            fixture.Respond(body: fixture.Config);
        }
        fixture.Respond(body: fixture.Status("STOPPED", taskCount: 0));
        fixture.TimeoutOnce();
        if (operation == "delete")
        {
            fixture.Respond(HttpStatusCode.NotFound);
        }
        else if (operation == "update")
        {
            fixture.Respond(body: fixture.Config);
        }
        else
        {
            fixture.Respond(body: "{\"offsets\":[]}");
            fixture.Respond(body: fixture.Status("STOPPED", taskCount: 0));
        }
        var result = operation switch
        {
            "delete" => await fixture.Adapter.DeleteAsync(fixture.Request, CancellationToken.None),
            "update" => await fixture.Adapter.UpdateConfigurationForRecordSizeIncreaseAsync(
                fixture.Request,
                fixture.Payload,
                CancellationToken.None
            ),
            _ => await fixture.Adapter.DeleteOffsetsAsync(fixture.Request, CancellationToken.None),
        };
        result
            .State.Should()
            .Be(
                operation == "delete" ? CdcTransportEvidenceState.Absent : CdcTransportEvidenceState.Observed
            );
        fixture.Http.Calls.Count(call => call.Method != HttpMethod.Get).Should().Be(1);
    }

    [TestCase("foreign-name")]
    [TestCase("duplicate-task")]
    [TestCase("unexpected-task")]
    [TestCase("missing-state")]
    [TestCase("null-tasks")]
    public async Task It_rejects_malformed_or_foreign_connector_status(string scenario)
    {
        using CdcConnectHttpFixture fixture = new();
        var root = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(fixture.Status())!;
        var tasks = root["tasks"]!.AsArray();
        switch (scenario)
        {
            case "foreign-name":
                root["name"] = "other";
                break;
            case "duplicate-task":
                tasks.Add(tasks[0]!.DeepClone());
                break;
            case "unexpected-task":
                tasks[0]!["id"] = 42;
                break;
            case "missing-state":
                root["connector"]!.AsObject().Remove("state");
                break;
            case "null-tasks":
                root["tasks"] = null;
                break;
        }
        fixture.Respond(body: root.ToJsonString());
        var result = await fixture.Adapter.ReadRuntimeAsync(fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.ValidationFailed);
    }

    [TestCase("RUNNING", "RUNNING", CoreCdc.CdcConnectorRuntimeState.Running)]
    [TestCase("FAILED", "RUNNING", CoreCdc.CdcConnectorRuntimeState.Failed)]
    [TestCase("RUNNING", "FAILED", CoreCdc.CdcConnectorRuntimeState.Running)]
    [TestCase("RESTARTING", "RESTARTING", CoreCdc.CdcConnectorRuntimeState.Unknown)]
    [TestCase("UNASSIGNED", "UNASSIGNED", CoreCdc.CdcConnectorRuntimeState.Unassigned)]
    public async Task It_preserves_runtime_states_and_core_readiness_rejection_without_error_traces(
        string connector,
        string task,
        CoreCdc.CdcConnectorRuntimeState expected
    )
    {
        using CdcConnectHttpFixture fixture = new();
        fixture.Respond(body: fixture.Status(connector, task));
        var result = await fixture.Adapter.ReadRuntimeAsync(fixture.Request, CancellationToken.None);
        var runtime = result
            .Should()
            .BeOfType<CdcTransportResult<CoreCdc.CdcConnectorRuntimeObservation>.Observed>()
            .Which.Value;
        runtime.ConnectorState.Should().Be(expected);
        CoreCdc
            .CdcConnectorRuntimeObservationValidator.ValidateForBinding(
                runtime,
                fixture.Request.Binding,
                new(
                    runtime.OperationId,
                    runtime.TargetIdentity,
                    runtime.PhysicalSourceFingerprint,
                    DateTimeOffset.UtcNow
                )
            )
            .Succeeded.Should()
            .Be(connector != "RUNNING" || task == "RUNNING");
    }

    [Test]
    public async Task It_rejects_raw_registration_secrets_before_http()
    {
        using CdcConnectHttpFixture fixture = new();
        var payload = new CdcKafkaConnectRegistrationPayload(
            new(fixture.Payload.Name),
            new Dictionary<string, string> { ["database.password"] = "private-secret" }
        );
        var result = await fixture.Adapter.CreateAsync(fixture.Request, payload, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.InvalidInput);
        fixture.Http.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_mutate_when_the_caller_is_already_cancelled()
    {
        using CdcConnectHttpFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        Func<Task> act = () =>
            fixture.Adapter.CreateAsync(fixture.Request, fixture.Payload, cancellation.Token);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        fixture.Http.Calls.Should().BeEmpty();
    }

    private sealed class HangingBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Test]
    public async Task It_bounds_response_body_reads_after_successful_headers()
    {
        using CdcConnectHttpFixture fixture = new(callMilliseconds: 40);
        fixture.Http.Responses.Enqueue(_ =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingBody()) }
            )
        );
        var result = await fixture.Adapter.ReadConfigurationAsync(fixture.Request, CancellationToken.None);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
    }
}
