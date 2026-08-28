// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Kafka Connect REST operations against a stub transport. Every worker failure is a fail-closed,
/// nonterminal result rather than an exception, and no response body reaches the caller through a
/// failure summary.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcConnectRestAdapter")]
public class Given_CdcConnectRestAdapter
{
    private const string ConnectorName = "edfi-dms-source";
    private const string ConnectorClass = "io.debezium.connector.postgresql.PostgresConnector";
    private const string SentinelSecret = "sentinel-connection-secret";

    private static readonly IReadOnlyDictionary<string, string> RenderedConfig = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["connector.class"] = ConnectorClass,
        ["tasks.max"] = "1",
    };

    [Test]
    public async Task It_reports_a_clean_plugin_config_validation()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Json(HttpStatusCode.OK, """{"name":"cls","error_count":0,"configs":[]}""")
        );

        CdcConnectResult<CdcConnectConfigValidation> result = await client.ValidateConnectorPluginConfigAsync(
            ConnectorClass,
            RenderedConfig,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.Value!.ErrorCount.Should().Be(0);
        result.Value.ErrorPropertyNames.Should().BeEmpty();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Put);
        handler
            .Requests[0]
            .Uri.AbsolutePath.Should()
            .Be($"/connector-plugins/{Uri.EscapeDataString(ConnectorClass)}/config/validate");
        handler.Requests[0].Body.Should().Contain("tasks.max");
    }

    [Test]
    public async Task It_reports_the_offending_property_names_without_the_validation_messages()
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "name": "cls",
                  "error_count": 2,
                  "configs": [
                    { "value": { "name": "database.password", "errors": ["invalid value {{SentinelSecret}}"] } },
                    { "value": { "name": "tasks.max", "errors": ["must be 1"] } },
                    { "value": { "name": "topic.prefix", "errors": [] } }
                  ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectConfigValidation> result = await client.ValidateConnectorPluginConfigAsync(
            ConnectorClass,
            RenderedConfig,
            CancellationToken.None
        );

        result.Value!.ErrorCount.Should().Be(2);
        result.Value.ErrorPropertyNames.Should().BeEquivalentTo("database.password", "tasks.max");
        JsonSerializer.Serialize(result.Value).Should().NotContain(SentinelSecret);
    }

    [Test]
    public async Task It_registers_the_connector_configuration_with_a_put()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Json(HttpStatusCode.Created, """{"name":"edfi-dms-source","config":{}}""")
        );

        CdcConnectResult result = await client.PutConnectorConfigAsync(
            ConnectorName,
            RenderedConfig,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
        handler.Requests[0].Method.Should().Be(HttpMethod.Put);
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/config");
    }

    [Test]
    public async Task It_reads_the_live_configuration_back_as_a_string_map()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Json(HttpStatusCode.OK, """{"connector.class":"io.debezium.X","tasks.max":"1"}""")
        );

        CdcConnectResult<IReadOnlyDictionary<string, string>> result = await client.GetConnectorConfigAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.Value.Should().Contain(new KeyValuePair<string, string>("tasks.max", "1"));
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/config");
    }

    [Test]
    public async Task It_reports_an_absent_connector_as_not_found()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.NotFound, """{"message":"x"}"""));

        CdcConnectResult<IReadOnlyDictionary<string, string>> result = await client.GetConnectorConfigAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(CdcConnectOutcome.NotFound);
        result.Value.Should().BeNull();
        result.Failure!.StatusCode.Should().Be(404);
        result.Failure.Retryable.Should().BeFalse();
    }

    [Test]
    public async Task It_reports_a_rebalance_conflict_as_retryable()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.Conflict, """{"message":"x"}"""));

        CdcConnectResult result = await client.PutConnectorConfigAsync(
            ConnectorName,
            RenderedConfig,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.Conflict);
        result.Failure!.StatusCode.Should().Be(409);
        result.Failure.Retryable.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_a_worker_error_as_unavailable_and_retryable()
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(HttpStatusCode.InternalServerError, $$"""{"message":"{{SentinelSecret}}"}""")
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.Unavailable);
        result.Failure!.StatusCode.Should().Be(500);
        result.Failure.Retryable.Should().BeTrue();
        result.Failure.Summary.Should().NotContain(SentinelSecret);
    }

    [Test]
    public async Task It_reports_a_refused_request_as_rejected_and_not_retryable()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.Unauthorized, "{}"));

        CdcConnectResult result = await client.RestartConnectorAsync(ConnectorName, CancellationToken.None);

        result.Outcome.Should().Be(CdcConnectOutcome.Rejected);
        result.Failure!.StatusCode.Should().Be(401);
        result.Failure.Retryable.Should().BeFalse();
    }

    [Test]
    public async Task It_reports_malformed_json_as_a_malformed_response()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.OK, "{ this is not json"));

        CdcConnectResult<IReadOnlyDictionary<string, string>> result = await client.GetConnectorConfigAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.MalformedResponse);
        result.Value.Should().BeNull();
        result.Failure!.Retryable.Should().BeFalse();
    }

    [Test]
    public async Task It_reports_an_empty_body_as_a_malformed_response()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.OK, string.Empty));

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.MalformedResponse);
        result.Value.Should().BeNull();
    }

    [Test]
    public async Task It_reports_a_success_body_of_the_wrong_shape_as_a_malformed_response()
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(HttpStatusCode.OK, """{"connector":{"worker_id":"w"},"tasks":[]}""")
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.MalformedResponse);
    }

    [Test]
    public async Task It_maps_connector_and_task_state_from_the_status_document()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                """
                {
                  "name": "edfi-dms-source",
                  "connector": { "state": "RUNNING", "worker_id": "connect:8083" },
                  "tasks": [ { "id": 0, "state": "RUNNING", "worker_id": "connect:8083" } ],
                  "type": "source"
                }
                """
            )
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.Value!.ConnectorState.Should().Be("RUNNING");
        result.Value.Tasks.Should().ContainSingle();
        result.Value.Tasks[0].Id.Should().Be(0);
        result.Value.Tasks[0].State.Should().Be("RUNNING");
        result.Value.Tasks[0].ErrorCategory.Should().BeNull();
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/status");
    }

    [Test]
    public async Task It_reduces_a_failed_task_trace_to_its_exception_type()
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "connector": { "state": "RUNNING" },
                  "tasks": [
                    {
                      "id": 0,
                      "state": "FAILED",
                      "trace": "org.apache.kafka.connect.errors.ConnectException: failed for {{SentinelSecret}}\n\tat io.debezium.Foo(Foo.java:1)"
                    }
                  ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        CdcConnectorTaskStatus task = result.Value!.Tasks.Single(task => task.State == "FAILED");
        task.ErrorCategory.Should().Be("org.apache.kafka.connect.errors.ConnectException");
        JsonSerializer.Serialize(result.Value).Should().NotContain(SentinelSecret);
    }

    [TestCase("could not connect using " + SentinelSecret)]
    [TestCase("IllegalStateException: boom")]
    public async Task It_reports_an_unrecognizable_trace_as_unclassified(string trace)
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "connector": { "state": "RUNNING" },
                  "tasks": [ { "id": 0, "state": "FAILED", "trace": "{{trace}}" } ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Value!.Tasks[0].ErrorCategory.Should().Be(CdcConnectRestAdapter.UnclassifiedErrorCategory);
    }

    [Test]
    public async Task It_restarts_the_connector_together_with_its_tasks()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Status(HttpStatusCode.Accepted)
        );

        CdcConnectResult result = await client.RestartConnectorAsync(ConnectorName, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/restart");
        handler.Requests[0].Uri.Query.Should().Be("?includeTasks=true&onlyFailed=false");
    }

    [Test]
    public async Task It_fences_the_connector_with_a_stop_rather_than_a_config_delete()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Status(HttpStatusCode.NoContent)
        );

        CdcConnectResult result = await client.StopConnectorAsync(ConnectorName, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Put);
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/stop");
        handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
    }

    [Test]
    public async Task It_reads_committed_source_offsets_that_outlive_the_parsed_document()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                """
                {
                  "offsets": [
                    {
                      "partition": { "server": "edfi.dms.instance" },
                      "offset": { "lsn_proc": 42, "snapshot": false }
                    }
                  ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectorOffsets> result = await client.GetConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        CdcConnectorOffsetEntry entry = result.Value!.Entries.Single();
        entry.Partition.GetProperty("server").GetString().Should().Be("edfi.dms.instance");
        entry.Offset.GetProperty("lsn_proc").GetInt64().Should().Be(42);
        entry.Offset.GetProperty("snapshot").GetBoolean().Should().BeFalse();
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/offsets");
    }

    [Test]
    public async Task It_reports_an_uncommitted_connector_as_an_empty_offset_set()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.OK, """{"offsets":[]}"""));

        CdcConnectResult<CdcConnectorOffsets> result = await client.GetConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.Value!.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_an_offset_response_without_an_offsets_array_as_malformed()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.OK, """{"message":"none"}"""));

        CdcConnectResult<CdcConnectorOffsets> result = await client.GetConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.MalformedResponse);
    }

    [Test]
    public async Task It_deletes_committed_offsets_of_a_stopped_connector()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Json(HttpStatusCode.OK, """{"message":"offsets deleted"}""")
        );

        CdcConnectResult result = await client.DeleteConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}/offsets");
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Conflict)]
    public async Task It_refuses_an_offset_deletion_against_a_connector_that_is_not_stopped(
        HttpStatusCode statusCode
    )
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(
                statusCode,
                $$"""{"message":"Connectors must be in the STOPPED state: {{SentinelSecret}}"}"""
            )
        );

        CdcConnectResult result = await client.DeleteConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(CdcConnectOutcome.Conflict);
        result.Failure!.StatusCode.Should().Be((int)statusCode);
        result.Failure.Retryable.Should().BeTrue();
        result.Failure.Summary.Should().Contain("STOPPED").And.NotContain(SentinelSecret);
    }

    [Test]
    public async Task It_reports_an_offset_deletion_for_an_absent_connector_as_not_found()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Json(HttpStatusCode.NotFound, "{}"));

        CdcConnectResult result = await client.DeleteConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.NotFound);
    }

    [Test]
    public async Task It_deletes_the_connector_configuration()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Status(HttpStatusCode.NoContent)
        );

        CdcConnectResult result = await client.DeleteConnectorAsync(ConnectorName, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/connectors/{ConnectorName}");
    }

    [Test]
    public async Task It_reports_an_unanswered_request_as_unavailable_after_the_configured_timeout()
    {
        (ICdcConnectClient client, _) = Adapter(
            _ => Status(HttpStatusCode.OK),
            neverAnswers: true,
            requestTimeout: TimeSpan.FromMilliseconds(50)
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.Unavailable);
        result.Failure!.Summary.Should().Contain("did not answer");
        result.Failure.Retryable.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_an_unreachable_worker_as_unavailable()
    {
        (ICdcConnectClient client, _) = Adapter(_ => throw new HttpRequestException("connection refused"));

        CdcConnectResult<CdcConnectorOffsets> result = await client.GetConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        result.Outcome.Should().Be(CdcConnectOutcome.Unavailable);
        result.Failure!.Retryable.Should().BeTrue();
        result.Failure.Summary.Should().NotContain("connection refused");
    }

    [Test]
    public async Task It_propagates_caller_cancellation_rather_than_reporting_a_timeout()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Status(HttpStatusCode.OK), neverAnswers: true);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Func<Task> read = () => client.GetConnectorStatusAsync(ConnectorName, cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_escapes_the_connector_name_in_every_request_path()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(_ =>
            Status(HttpStatusCode.NoContent)
        );

        await client.StopConnectorAsync("edfi dms/source", CancellationToken.None);

        handler.Requests[0].Uri.AbsoluteUri.Should().EndWith("/connectors/edfi%20dms%2Fsource/stop");
    }

    [Test]
    public async Task It_preserves_a_base_url_path_prefix()
    {
        (ICdcConnectClient client, StubHttpMessageHandler handler) = Adapter(
            _ => Status(HttpStatusCode.NoContent),
            connectBaseUri: "http://connect.internal:8083/kafka-connect"
        );

        await client.DeleteConnectorAsync(ConnectorName, CancellationToken.None);

        handler
            .Requests[0]
            .Uri.AbsoluteUri.Should()
            .Be($"http://connect.internal:8083/kafka-connect/connectors/{ConnectorName}");
    }

    [Test]
    public async Task It_rejects_a_blank_connector_name()
    {
        (ICdcConnectClient client, _) = Adapter(_ => Status(HttpStatusCode.OK));

        Func<Task> read = () => client.GetConnectorStatusAsync("  ", CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentException>();
    }

    private static (ICdcConnectClient Client, StubHttpMessageHandler Handler) Adapter(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool neverAnswers = false,
        TimeSpan? requestTimeout = null,
        string connectBaseUri = "http://localhost:8083"
    )
    {
        StubHttpMessageHandler handler = new(respond, neverAnswers);
        IHttpClientFactory httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));

        CdcConnectRestAdapter adapter = new(
            httpClientFactory,
            Options.Create(ControlOptions(connectBaseUri, requestTimeout)),
            NullLogger<CdcConnectRestAdapter>.Instance
        );

        return (adapter, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json) };

    private static HttpResponseMessage Status(HttpStatusCode statusCode) => new(statusCode);

    private static CdcControlOptions ControlOptions(string connectBaseUri, TimeSpan? requestTimeout) =>
        new()
        {
            DeploymentKey = "dms-local",
            InstanceKey = "data-store-1",
            TopicPrefix = "edfi.dms",
            Generation = 1,
            PartitionCount = 3,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = connectBaseUri,
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
            Timeouts = new() { ConnectRequest = requestTimeout ?? TimeSpan.FromSeconds(30) },
        };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class StubHttpMessageHandler(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool neverAnswers
    ) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RecordedRequest recorded = new(request.Method, request.RequestUri!, body);
            Requests.Add(recorded);

            if (neverAnswers)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return respond(recorded);
        }
    }
}
