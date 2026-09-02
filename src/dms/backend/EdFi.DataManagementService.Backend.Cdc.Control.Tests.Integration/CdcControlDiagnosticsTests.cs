// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Confluent.Kafka;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration;

/// <summary>
/// The redaction boundary every CDC control-plane adapter sits behind, swept across all of them at
/// once.
/// </summary>
/// <remarks>
/// <para>
/// Each adapter is handed a deployment configuration seeded with sentinel values of exactly the kinds
/// the contract forbids in evidence — a database password, a bearer token, a Kafka SASL secret, a
/// tenant display name, a physical source identity, and a whole connection string — and is then driven
/// against endpoints that are not there. Failure is the point: a diagnostic is written precisely when
/// something went wrong, which is when an adapter is most likely to quote the request, the endpoint, or
/// the provider's own error text back into its evidence.
/// </para>
/// <para>
/// Nothing here needs a broker, a worker, or a database, because nothing here asserts what those
/// answer. The assertion is on what the adapter says when they do not answer: no sentinel may appear in
/// a returned observation, in a diagnostic, in a log entry, or in an exception message. The shared
/// contract enforces the same rule structurally when a diagnostic is constructed; this sweep is the
/// behavioral half, and it covers the log and exception paths the contract cannot see.
/// </para>
/// </remarks>
[TestFixture]
[Parallelizable]
[Category("CdcControlDiagnostics")]
public class Given_CdcControlDiagnosticBoundaries
{
    private const string DatabasePasswordSentinel = "sentinel-database-password";
    private const string BearerTokenSentinel = "sentinel-bearer-token";
    private const string SaslSecretSentinel = "sentinel-sasl-secret";
    private const string TenantDisplaySentinel = "sentinel-tenant-display-name";

    /// <summary>
    /// The binding fingerprint the observation envelope legitimately carries. It is a derived value the
    /// contract requires in every observation, so it is deliberately not a sentinel: the raw source
    /// identity it is derived from is what must never appear, and no adapter is given one.
    /// </summary>
    private const string PhysicalSourceFingerprint = "fingerprint-1";

    private const string OperationId = "operation-1";
    private const string DeploymentKey = "dms";
    private const string InstanceKey = "instance";
    private const string TopicPrefix = "edfi.documents";
    private const long BindingGeneration = 1;

    /// <summary>
    /// A port nothing is listening on, so every transport fails and every adapter takes its
    /// unavailable-evidence path.
    /// </summary>
    private const int UnreachablePort = 1;

    private static readonly string[] Sentinels =
    [
        DatabasePasswordSentinel,
        BearerTokenSentinel,
        SaslSecretSentinel,
        TenantDisplaySentinel,
    ];

    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private CapturingLoggerProvider _logs = null!;
    private ServiceProvider _services = null!;

    [SetUp]
    public void SetUp()
    {
        _logs = new CapturingLoggerProvider();
        _services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(_logs);
            })
            .BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        _logs?.Dispose();
    }

    [Test]
    public async Task It_keeps_secrets_out_of_the_connect_registration_boundary()
    {
        CdcConnectRestAdapter adapter = new(
            _services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(ControlOptions()),
            TimeProvider.System,
            _services.GetRequiredService<ILogger<CdcConnectRestAdapter>>()
        );

        CdcConnectResult validation = await adapter.PutConnectorConfigAsync(
            Inventory().ConnectorName,
            SecretBearingConnectorConfig(),
            CancellationToken.None
        );

        using AssertionScope _ = new();
        validation.Succeeded.Should().BeFalse("nothing is listening on the configured Connect endpoint");
        AssertNoSentinel(validation.Failure?.Summary);
        AssertNoSentinelInLogs();
    }

    [Test]
    public async Task It_keeps_secrets_out_of_the_source_lag_boundary()
    {
        CdcConnectorJolokiaLagReader reader = new(
            _services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(ControlOptions()),
            _services.GetRequiredService<ILogger<CdcConnectorJolokiaLagReader>>()
        );

        CdcConnectorLagReadResult reading = await reader.ReadAsync(
            CdcProvider.Postgresql,
            TopicPrefix,
            CancellationToken.None
        );

        using AssertionScope _ = new();
        reading.Succeeded.Should().BeFalse("nothing is listening on the configured metrics bridge");
        AssertNoSentinel(reading.Summary);
        AssertNoSentinelInLogs();
    }

    [Test]
    public async Task It_keeps_secrets_out_of_the_projection_status_boundary()
    {
        CdcProjectionCorrelationCollector collector = new(
            _services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(ControlOptions()),
            TimeProvider.System,
            _services.GetRequiredService<ILogger<CdcProjectionCorrelationCollector>>()
        );

        CdcProjectionCorrelationObservation observation = await collector.CollectAsync(
            Context(),
            CancellationToken.None
        );

        using AssertionScope _ = new();

        // The bearer token travels on this request and the endpoint is unreachable, so this is the
        // boundary most likely to echo an authorization header into a failure summary.
        AssertNoSentinel(CdcJsonContract.Serialize(observation));
        AssertNoSentinelInLogs();
    }

    [Test]
    public async Task It_keeps_secrets_out_of_the_kafka_policy_boundary()
    {
        using IAdminClient adminClient = UnreachableAdminClient();
        CdcKafkaAdminAdapter adapter = new(
            adminClient,
            Options.Create(ControlOptions()),
            TimeProvider.System,
            _services.GetRequiredService<ILogger<CdcKafkaAdminAdapter>>()
        );

        CdcKafkaPolicyObservation observation = await adapter.DescribeBindingKafkaPolicyAsync(
            Context(),
            Inventory(),
            CancellationToken.None
        );

        using AssertionScope _ = new();
        observation.PolicyState.Should().NotBe(CdcKafkaPolicyState.Satisfied);

        // The client security properties carry the SASL secret, and librdkafka's own error text quotes
        // the bootstrap configuration back, so both have to stop here.
        AssertNoSentinel(CdcJsonContract.Serialize(observation));
        AssertNoSentinelInLogs();
    }

    [Test]
    public async Task It_keeps_secrets_out_of_the_connect_offset_store_boundary()
    {
        using IAdminClient adminClient = UnreachableAdminClient();
        CdcKafkaAdminAdapter adapter = new(
            adminClient,
            Options.Create(ControlOptions()),
            TimeProvider.System,
            _services.GetRequiredService<ILogger<CdcKafkaAdminAdapter>>()
        );

        CdcConnectOffsetStorePolicyObservation observation = await adapter.EnsureConnectOffsetStoreAsync(
            Context(),
            CancellationToken.None
        );

        using AssertionScope _ = new();
        observation.PolicyState.Should().NotBe(CdcConnectOffsetStorePolicyState.Satisfied);
        AssertNoSentinel(CdcJsonContract.Serialize(observation));
        AssertNoSentinelInLogs();
    }

    [Test]
    public async Task It_keeps_the_instance_connection_string_out_of_the_eligibility_boundary()
    {
        CdcEligibilityProbe probe = new(
            CdcProvider.Postgresql,
            TimeProvider.System,
            _services.GetRequiredService<ILogger<CdcEligibilityProbe>>()
        );

        string observed;
        try
        {
            InitialCdcEligibilityObservation observation = await probe.ProbeAsync(
                new(Context(), Proof(), UnreachableConnectionString())
                {
                    CommandTimeout = TimeSpan.FromSeconds(2),
                },
                CancellationToken.None
            );
            observed = CdcJsonContract.Serialize(observation);
        }
        catch (Exception exception)
        {
            // A probe that cannot open the instance database may report or throw; either way the
            // connection string it was handed must not travel with the outcome.
            observed = exception.ToString();
        }

        using AssertionScope _ = new();
        AssertNoSentinel(observed);
        AssertNoSentinelInLogs();
    }

    [Test]
    public void It_keeps_secrets_out_of_the_explicit_projection_target_boundary()
    {
        CdcExplicitProjectionTargetProof proof = new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["DataManagement:DocumentCache:Targets:0:TenantKey"] = TenantDisplaySentinel,
                        ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "99",
                    }
                )
                .Build()
        );

        // The proof is asked about a pair the operator never configured, so it refuses and reports the
        // refusal. The tenant key it was configured with is a display name, and naming it back would put
        // it in the evidence.
        CdcExplicitProjectionTargetProofResult result = proof.Prove(ValidatedTarget(), ObservedAt);

        using AssertionScope _ = new();
        result.Succeeded.Should().BeFalse();
        AssertNoSentinel(string.Join(" ", result.Diagnostics.Select(Describe)));
        AssertNoSentinelInLogs();
    }

    private static CdcValidatedTarget ValidatedTarget() =>
        CdcTargetValidator
            .Validate(
                new CdcTargetInput(
                    DeploymentKey,
                    CdcTargetValidator.DefaultBindingTenantKey,
                    "1",
                    InstanceKey,
                    CdcProvider.Postgresql,
                    TopicPrefix,
                    BindingGeneration,
                    1,
                    CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm
                )
            )
            .Target!;

    private static string Describe(CdcDiagnostic diagnostic) =>
        $"{diagnostic.Code} {diagnostic.Path} {diagnostic.Message} {diagnostic.Expected} {diagnostic.Observed} {diagnostic.ArtifactName}";

    private void AssertNoSentinelInLogs() => AssertNoSentinel(_logs.Text);

    private static void AssertNoSentinel(string? observed)
    {
        if (string.IsNullOrEmpty(observed))
        {
            return;
        }

        foreach (string sentinel in Sentinels)
        {
            observed
                .Should()
                .NotContain(sentinel, "no CDC control-plane boundary may carry a secret into its evidence");
        }
    }

    /// <summary>An admin client pointed at a port nothing answers on.</summary>
    private static IAdminClient UnreachableAdminClient() =>
        new AdminClientBuilder(
            new AdminClientConfig
            {
                BootstrapServers = $"127.0.0.1:{UnreachablePort}",
                SocketTimeoutMs = 1000,
            }
        ).Build();

    private static string UnreachableConnectionString() =>
        $"Host=127.0.0.1;Port={UnreachablePort};Username=postgres;Password={DatabasePasswordSentinel};Database=edfi_datastore;Timeout=2;Command Timeout=2";

    private static IReadOnlyDictionary<string, string> SecretBearingConnectorConfig() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.class"] = "io.debezium.connector.postgresql.PostgresConnector",
            ["database.password"] = DatabasePasswordSentinel,
            ["database.hostname"] = "127.0.0.1",
        };

    private static CdcControlOptions ControlOptions() =>
        new()
        {
            DeploymentKey = DeploymentKey,
            InstanceKey = InstanceKey,
            TopicPrefix = TopicPrefix,
            Generation = BindingGeneration,
            PartitionCount = 1,
            KafkaBootstrapServers = $"127.0.0.1:{UnreachablePort}",
            ConnectBaseUri = $"http://127.0.0.1:{UnreachablePort}",
            ConnectMetricsBaseUri = $"http://127.0.0.1:{UnreachablePort}",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 1_048_576,
            AclsEnabled = false,
            SetupPrincipal = "postgres",
            DmsBaseUrl = $"http://127.0.0.1:{UnreachablePort}",
            DmsBearerToken = BearerTokenSentinel,
            ProviderConnectionProperties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database.password"] = DatabasePasswordSentinel,
            },
            KafkaClientSecurityProperties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sasl.password"] = SaslSecretSentinel,
            },
            Timeouts = new()
            {
                EligibilityProbe = TimeSpan.FromSeconds(2),
                KafkaAdmin = TimeSpan.FromSeconds(2),
                ConnectRequest = TimeSpan.FromSeconds(2),
                StatusEndpoint = TimeSpan.FromSeconds(2),
            },
        };

    private static CdcObservationContext Context() =>
        new(OperationId, TargetIdentity(), PhysicalSourceFingerprint);

    private static CdcTargetIdentity TargetIdentity() =>
        new(
            DeploymentKey,
            CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            BindingGeneration,
            CdcProvider.Postgresql
        );

    private static InitialCdcProvisioningProof Proof() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "proof-1",
            OperationId,
            TargetIdentity(),
            CdcProvider.Postgresql,
            "run-1",
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            ObservedAt
        );

    private static CdcArtifactInventory Inventory() =>
        CdcArtifactNameGenerator
            .Render(
                new CdcArtifactNameInput(
                    DeploymentKey,
                    TopicPrefix,
                    InstanceKey,
                    BindingGeneration,
                    CdcProvider.Postgresql
                )
            )
            .Inventory!;

    /// <summary>Collects everything every boundary logs, so the log path is swept alongside the evidence.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string Text => string.Join("\n", _entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

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
                entries.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }
    }
}
