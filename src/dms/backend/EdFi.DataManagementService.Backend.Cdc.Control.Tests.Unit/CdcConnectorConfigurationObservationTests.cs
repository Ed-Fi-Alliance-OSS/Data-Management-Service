// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Live connector configuration read-back mapped onto the shared configuration observation. Property
/// comparison belongs to the connector-template service; these tests prove the mapper carries its
/// verdict faithfully, localizes each drift to the item that owns it, and never reports a match it
/// did not observe.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcConnectorConfigurationObservation")]
public class Given_CdcConnectorConfigurationObservationMapping
{
    private const string OperationId = "operation-1";
    private const string SentinelSecret = "sentinel-database-password";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void It_reports_a_conforming_postgresql_read_back_as_matched()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(CdcProvider.Postgresql);

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Matched);
        observation.Diagnostics.Should().BeEmpty();
        observation.TaskCount.Should().Be(1);
        observation.ConnectorName.Should().Be(Inventory(CdcProvider.Postgresql).ConnectorName);
        observation.TopicPrefix.Should().Be(Inventory(CdcProvider.Postgresql).TopicPrefix);
        observation.TransformState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        observation.ConverterState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        observation.ProducerOverrideState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        observation.HeartbeatState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        observation.SourceIncludeListState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        observation.OffsetState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        observation.SchemaHistoryState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.NotApplicable);
        Validate(observation, CdcProvider.Postgresql).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_a_conforming_sql_server_read_back_as_matched()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(CdcProvider.SqlServer);

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Matched);
        observation.Diagnostics.Should().BeEmpty();
        observation.SchemaHistoryState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Matched);
        Validate(observation, CdcProvider.SqlServer).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_task_count_drift_as_invalid_and_carries_the_observed_count()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["tasks.max"] = "2"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.TaskCount.Should().Be(2);
        observation.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public void It_reports_error_tolerance_drift_as_invalid_converter_handling()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["errors.tolerance"] = "all"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.ConverterState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
        observation.TaskCount.Should().Be(1);
        Diagnostic(observation, "errors.tolerance").Should().NotBeNull();

        // A localized drift must not make the observation fail its own contract, or the readiness
        // evaluator would report an internally inconsistent observation instead of the drift.
        Validate(observation, CdcProvider.Postgresql).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_producer_override_drift_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["producer.override.acks"] = "1"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.ProducerOverrideState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
        Diagnostic(observation, "producer.override.acks").Should().NotBeNull();
        Validate(observation, CdcProvider.Postgresql).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_heartbeat_action_query_drift_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["heartbeat.action.query"] = "select 2"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.HeartbeatState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
        Validate(observation, CdcProvider.Postgresql).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_an_unexpected_heartbeat_topic_name_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["topic.heartbeat.name"] = "edfi.heartbeat"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.HeartbeatState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
        Diagnostic(observation, "topic.heartbeat.name").Should().NotBeNull();
    }

    [Test]
    public void It_accepts_the_empty_heartbeat_topic_name_kafka_connect_reports()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["topic.heartbeat.name"] = string.Empty
        );

        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Matched);
    }

    [Test]
    public void It_reports_sql_server_schema_history_drift_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.SqlServer,
            config => config["schema.history.internal.kafka.topic"] = "some.other.topic"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.SchemaHistoryState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
        Validate(observation, CdcProvider.SqlServer).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_source_include_list_drift_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["table.include.list"] = "\"dms\".\"Document\""
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.SourceIncludeListState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
    }

    [Test]
    public void It_reports_transform_drift_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["transforms.documentState.target.topic"] = "someone.elses.topic"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.TransformState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
    }

    [Test]
    public void It_reports_a_missing_rendered_property_as_invalid()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config.Remove("value.converter")
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.ConverterState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
    }

    [Test]
    public void It_reports_a_source_partition_mismatch_as_an_invalid_offset_identity()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            sourcePartitionServer: "some-other-connector"
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.OffsetState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
        Validate(observation, CdcProvider.Postgresql).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_absent_source_partition_evidence_as_an_invalid_offset_identity()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            omitSourcePartitionEvidence: true
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation.OffsetState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Invalid);
    }

    [Test]
    public void It_keeps_a_drifted_secret_out_of_every_diagnostic()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["database.password"] = SentinelSecret
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Invalid);
        observation
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                !diagnostic.Message.Contains(SentinelSecret, StringComparison.Ordinal)
                && (diagnostic.Expected ?? string.Empty) != SentinelSecret
                && (diagnostic.Observed ?? string.Empty) != SentinelSecret
                && (diagnostic.ArtifactKind ?? string.Empty) != SentinelSecret
            );
    }

    [Test]
    public void It_accepts_the_hidden_secret_placeholder_kafka_connect_returns()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            config => config["database.password"] = "[hidden]"
        );

        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Matched);
    }

    [Test]
    public void It_reports_an_unavailable_read_back_as_unknown_rather_than_a_match()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = MapUnavailable(
            CdcProvider.Postgresql,
            new(CdcConnectOutcome.NotFound, null, new(404, "Kafka Connect answered 404.", false))
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Unknown);
        observation.TaskCount.Should().BeNull();
        observation.TransformState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.ConverterState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.ProducerOverrideState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.HeartbeatState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.SourceIncludeListState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.OffsetState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.SchemaHistoryState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.NotApplicable);
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "connectorConfigReadBackUnavailable");
    }

    [Test]
    public void It_reports_a_read_back_that_was_never_compared_as_unknown()
    {
        // Live read-back validation accepts only fresh validate-only evidence, and rejects the request
        // before it compares any property when the evidence was produced by a provisioning run.
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            mode: CdcProviderSetupMode.InitialCreateOrExactMatch
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Unknown);
        observation.TransformState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.OffsetState.Should().Be(CoreCdc.CdcConnectorConfigurationItemState.Unknown);
        observation.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public void It_reports_provider_setup_evidence_that_is_not_an_exact_match_as_unknown()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(
            CdcProvider.Postgresql,
            outcome: CdcProviderSetupOutcome.CreatedOrMatched
        );

        using var _ = new AssertionScope();
        observation.ConfigurationState.Should().Be(CoreCdc.CdcConnectorConfigurationState.Unknown);
        observation.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public void It_carries_the_operation_envelope_onto_the_observation()
    {
        CoreCdc.CdcConnectorConfigurationObservation observation = Map(CdcProvider.SqlServer);

        using var _ = new AssertionScope();
        observation.ContractVersion.Should().Be(CoreCdc.CdcJsonContract.CurrentContractVersion);
        observation.OperationId.Should().Be(OperationId);
        observation.ObservedAt.Should().Be(ObservedAt);
        observation.TargetIdentity.Should().Be(TargetIdentity(CdcProvider.SqlServer));
        observation.Provider.Should().Be(CoreCdc.CdcProvider.SqlServer);
        observation
            .PhysicalSourceFingerprint.Should()
            .Be(CdcControlTemplateTestData.SourceFingerprint(CdcProvider.SqlServer).Value);
    }

    [Test]
    public void It_rejects_a_missing_read_back_result()
    {
        Action mapping = () =>
            Mapper(CdcProvider.Postgresql, out CdcConnectorTemplateRequest request)
                .MapConfiguration(
                    Context(CdcProvider.Postgresql),
                    request,
                    CdcControlTemplateTestData.BuildFreshProviderSetupEvidence(CdcProvider.Postgresql),
                    CdcControlTemplateTestData.BuildSourcePartitionEvidence(request),
                    null!
                );

        mapping.Should().Throw<ArgumentNullException>();
    }

    private static CoreCdc.CdcConnectorConfigurationObservation Map(
        CdcProvider provider,
        Action<Dictionary<string, string>>? drift = null,
        string? sourcePartitionServer = null,
        bool omitSourcePartitionEvidence = false,
        CdcProviderSetupMode mode = CdcProviderSetupMode.ValidateOnly,
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.ExactMatch
    )
    {
        using ServiceProvider serviceProvider = BuildTemplateServiceProvider();
        ICdcConnectorTemplateService templateService =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = CdcControlTemplateTestData.BuildTemplateRequest(provider);
        Dictionary<string, string> readBack = new(
            templateService.Render(request).Config,
            StringComparer.Ordinal
        );
        drift?.Invoke(readBack);

        return new CdcConnectorObservationMapper(
            templateService,
            new FixedTimeProvider(ObservedAt)
        ).MapConfiguration(
            Context(provider),
            request,
            CdcControlTemplateTestData.BuildFreshProviderSetupEvidence(provider, mode, outcome),
            omitSourcePartitionEvidence
                ? null
                : CdcControlTemplateTestData.BuildSourcePartitionEvidence(request, sourcePartitionServer),
            new(CdcConnectOutcome.Succeeded, readBack, null)
        );
    }

    private static CoreCdc.CdcConnectorConfigurationObservation MapUnavailable(
        CdcProvider provider,
        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack
    )
    {
        using ServiceProvider serviceProvider = BuildTemplateServiceProvider();

        return new CdcConnectorObservationMapper(
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>(),
            new FixedTimeProvider(ObservedAt)
        ).MapConfiguration(
            Context(provider),
            CdcControlTemplateTestData.BuildTemplateRequest(provider),
            CdcControlTemplateTestData.BuildFreshProviderSetupEvidence(provider),
            null,
            readBack
        );
    }

    private static ICdcConnectorObservationMapper Mapper(
        CdcProvider provider,
        out CdcConnectorTemplateRequest request
    )
    {
        ServiceProvider serviceProvider = BuildTemplateServiceProvider();
        request = CdcControlTemplateTestData.BuildTemplateRequest(provider);

        return new CdcConnectorObservationMapper(
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>(),
            new FixedTimeProvider(ObservedAt)
        );
    }

    private static ServiceProvider BuildTemplateServiceProvider() =>
        new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

    private static CdcObservationContext Context(CdcProvider provider) =>
        new(
            OperationId,
            TargetIdentity(provider),
            CdcControlTemplateTestData.SourceFingerprint(provider).Value
        );

    private static CoreCdc.CdcTargetIdentity TargetIdentity(CdcProvider provider) =>
        CdcControlTemplateTestData.BuildTargetIdentity(provider);

    private static CoreCdc.CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcControlTemplateTestData.BuildInventory(provider);

    private static CoreCdc.CdcContractValidationResult Validate(
        CoreCdc.CdcConnectorConfigurationObservation observation,
        CdcProvider provider
    ) =>
        CoreCdc.CdcConnectorConfigurationObservationValidator.ValidateForBinding(
            observation,
            CdcControlTemplateTestData.BuildBinding(provider),
            new(
                OperationId,
                TargetIdentity(provider),
                CdcControlTemplateTestData.SourceFingerprint(provider).Value,
                ObservedAt.AddMinutes(1)
            )
        );

    private static CoreCdc.CdcDiagnostic? Diagnostic(
        CoreCdc.CdcConnectorConfigurationObservation observation,
        string artifactKind
    ) =>
        observation.Diagnostics.SingleOrDefault(diagnostic =>
            string.Equals(diagnostic.ArtifactKind, artifactKind, StringComparison.Ordinal)
        );

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
