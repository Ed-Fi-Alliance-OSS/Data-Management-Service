// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// A Kafka Connect worker echoes the configuration it was submitted — including connection
/// credentials — in validation messages, error bodies, and task traces. These tests pin the boundary
/// that keeps those values inside the worker: a sentinel secret placed in a submitted configuration,
/// in an error body, in a validation message, in a task trace, and in a live read-back must never
/// reach a diagnostic, an observation, a log entry, or an exception message.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcConnectRestSecretRedaction")]
public class Given_CdcConnectRestSecretRedaction
{
    private const string ConnectorName = "edfi-dms-source";
    private const string ConnectorClass = "io.debezium.connector.postgresql.PostgresConnector";
    private const string OperationId = "operation-1";
    private const string SentinelSecret = "sentinel-connection-secret";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> SecretBearingConfig = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["connector.class"] = ConnectorClass,
        ["tasks.max"] = "1",
        ["database.password"] = SentinelSecret,
    };

    [Test]
    public async Task It_keeps_a_submitted_secret_out_of_a_rejected_registration()
    {
        (ICdcConnectClient client, RecordingLogger logger) = Adapter(_ =>
            Json(
                HttpStatusCode.InternalServerError,
                $$"""{"error_code":500,"message":"failed to configure database.password={{SentinelSecret}}"}"""
            )
        );

        CdcConnectResult result = await client.PutConnectorConfigAsync(
            ConnectorName,
            SecretBearingConfig,
            CancellationToken.None
        );

        using var _ = new AssertionScope();
        result.Failure!.Summary.Should().NotContain(SentinelSecret);
        logger
            .Messages.Should()
            .NotContain(message => message.Contains(SentinelSecret, StringComparison.Ordinal));
    }

    [Test]
    public async Task It_keeps_a_secret_echoed_in_a_validation_message_out_of_the_validation_verdict()
    {
        (ICdcConnectClient client, RecordingLogger logger) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "name": "cls",
                  "error_count": 1,
                  "configs": [
                    {
                      "value": {
                        "name": "database.password",
                        "value": "{{SentinelSecret}}",
                        "errors": ["Invalid value {{SentinelSecret}} for configuration database.password"]
                      }
                    }
                  ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectConfigValidation> result = await client.ValidateConnectorPluginConfigAsync(
            ConnectorClass,
            SecretBearingConfig,
            CancellationToken.None
        );

        using var _ = new AssertionScope();
        result.Value!.ErrorPropertyNames.Should().BeEquivalentTo("database.password");
        JsonSerializer.Serialize(result.Value).Should().NotContain(SentinelSecret);
        logger
            .Messages.Should()
            .NotContain(message => message.Contains(SentinelSecret, StringComparison.Ordinal));
    }

    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.Conflict)]
    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task It_keeps_a_secret_echoed_in_an_error_body_out_of_every_failure_summary(
        HttpStatusCode statusCode
    )
    {
        (ICdcConnectClient client, RecordingLogger logger) = Adapter(_ =>
            Json(statusCode, $$"""{"message":"{{SentinelSecret}}"}""")
        );

        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack = await client.GetConnectorConfigAsync(
            ConnectorName,
            CancellationToken.None
        );
        CdcConnectResult offsets = await client.DeleteConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );

        using var _ = new AssertionScope();
        readBack.Value.Should().BeNull();
        readBack.Failure!.Summary.Should().NotContain(SentinelSecret);
        offsets.Failure!.Summary.Should().NotContain(SentinelSecret);
        logger
            .Messages.Should()
            .NotContain(message => message.Contains(SentinelSecret, StringComparison.Ordinal));
    }

    [Test]
    public async Task It_keeps_a_secret_out_of_the_exception_raised_by_a_transport_failure()
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            throw new HttpRequestException($"connection to database.password={SentinelSecret} refused")
        );

        CdcConnectResult<CdcConnectorStatus> result = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );

        using AssertionScope scope = new();
        result.Outcome.Should().Be(CdcConnectOutcome.Unavailable);
        result.Failure!.Summary.Should().NotContain(SentinelSecret);
    }

    [Test]
    public async Task It_keeps_a_secret_quoted_in_a_task_trace_out_of_the_runtime_observation()
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
                      "trace": "org.apache.kafka.connect.errors.ConnectException: password={{SentinelSecret}}\n\tat io.debezium.Foo"
                    }
                  ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectorStatus> status = await client.GetConnectorStatusAsync(
            ConnectorName,
            CancellationToken.None
        );
        CoreCdc.CdcConnectorRuntimeObservation observation = Mapper()
            .MapRuntime(Context(), Binding(), status, EmptyOffsets());

        using AssertionScope scope = new();
        status.Value!.Tasks[0].ErrorCategory.Should().NotContain(SentinelSecret);
        observation.LastErrorCategory.Should().NotContain(SentinelSecret);
        Serialize(observation).Should().NotContain(SentinelSecret);
    }

    [Test]
    public void It_keeps_a_secret_read_back_from_the_live_configuration_out_of_the_drift_diagnostics()
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();
        ICdcConnectorTemplateService templateService =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = CdcControlTemplateTestData.BuildTemplateRequest(
            CdcProvider.Postgresql
        );

        // The rendered configuration references the credential indirectly; a worker that answers with
        // the resolved literal is drift whose observed value must not be carried onto the observation.
        Dictionary<string, string> readBack = new(
            templateService.Render(request).Config,
            StringComparer.Ordinal
        )
        {
            ["database.password"] = SentinelSecret,
        };

        CoreCdc.CdcConnectorConfigurationObservation observation = new CdcConnectorObservationMapper(
            templateService,
            new FixedTimeProvider(ObservedAt)
        ).MapConfiguration(
            Context(),
            request,
            CdcControlTemplateTestData.BuildFreshProviderSetupEvidence(CdcProvider.Postgresql),
            CdcControlTemplateTestData.BuildSourcePartitionEvidence(request),
            new(CdcConnectOutcome.Succeeded, readBack, null)
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.Diagnostics.Should().NotBeEmpty();
        Serialize(observation).Should().NotContain(SentinelSecret);
    }

    [Test]
    public async Task It_keeps_a_secret_smuggled_into_a_committed_offset_out_of_the_offset_observation()
    {
        (ICdcConnectClient client, _) = Adapter(_ =>
            Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "offsets": [
                    {
                      "partition": { "server": "{{ConnectorNameForBinding()}}" },
                      "offset": { "lsn_proc": 42, "snapshot": false, "password": "{{SentinelSecret}}" }
                    }
                  ]
                }
                """
            )
        );

        CdcConnectResult<CdcConnectorOffsets> offsets = await client.GetConnectorOffsetsAsync(
            ConnectorName,
            CancellationToken.None
        );
        CoreCdc.CdcConnectorOffsetObservation observation = Mapper()
            .MapOffset(Context(), Binding(), sqlServerCatalogName: null, offsets);

        Serialize(observation).Should().NotContain(SentinelSecret);
    }

    private static string Serialize<TObservation>(TObservation observation) =>
        JsonSerializer.Serialize(observation);

    private static ICdcConnectorObservationMapper Mapper() =>
        new CdcConnectorObservationMapper(
            A.Fake<ICdcConnectorTemplateService>(),
            new FixedTimeProvider(ObservedAt)
        );

    private static CdcConnectResult<CdcConnectorOffsets> EmptyOffsets() =>
        new(CdcConnectOutcome.Succeeded, new([]), null);

    private static CdcObservationContext Context() =>
        new(
            OperationId,
            CdcControlTemplateTestData.BuildTargetIdentity(CdcProvider.Postgresql),
            CdcControlTemplateTestData.SourceFingerprint(CdcProvider.Postgresql).Value
        );

    private static CoreCdc.CdcBinding Binding() =>
        CdcControlTemplateTestData.BuildBinding(CdcProvider.Postgresql);

    private static string ConnectorNameForBinding() =>
        CdcControlTemplateTestData.BuildInventory(CdcProvider.Postgresql).ConnectorName;

    private static (ICdcConnectClient Client, RecordingLogger Logger) Adapter(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    )
    {
        StubHttpMessageHandler handler = new(respond);
        IHttpClientFactory httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));

        RecordingLogger logger = new();

        return (
            new CdcConnectRestAdapter(
                httpClientFactory,
                Options.Create(ControlOptions()),
                TimeProvider.System,
                logger
            ),
            logger
        );
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json) };

    private static CdcControlOptions ControlOptions() =>
        new()
        {
            DeploymentKey = "dms-local",
            InstanceKey = "data-store-1",
            TopicPrefix = "edfi.dms",
            Generation = 1,
            PartitionCount = 3,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>Captures every formatted log entry so a leaked secret would fail the assertion.</summary>
    private sealed class RecordingLogger : ILogger<CdcConnectRestAdapter>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages.ToArray();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _messages.Add($"{formatter(state, exception)} {exception?.ToString() ?? string.Empty}");
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
