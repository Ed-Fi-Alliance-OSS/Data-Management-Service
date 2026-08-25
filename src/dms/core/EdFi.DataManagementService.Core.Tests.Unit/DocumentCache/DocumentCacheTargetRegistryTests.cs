// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheTargetRegistry")]
public class DocumentCacheTargetRegistryTests
{
    private const string SensitiveProviderFailure =
        "Server=prod-db.example.com;Database=StudentRecords;Password=Secret123;Host=ProdHost;";

    private static readonly DocumentCacheTargetKey _defaultTargetKey = DocumentCacheTargetKey.Create(null, 1);

    private static readonly DocumentCacheTargetKey _tenantTargetKey = DocumentCacheTargetKey.Create(
        "TenantA",
        7
    );

    private static readonly DocumentCacheTargetEffectiveSettings _effectiveSettings =
        DocumentCacheTargetEffectiveSettings.FromOptions(CreateOptions([("", 1)]));

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly byte[] _resourceKeySeedHash =
    [
        0,
        1,
        2,
        3,
        4,
        5,
        6,
        7,
        8,
        9,
        10,
        11,
        12,
        13,
        14,
        15,
        16,
        17,
        18,
        19,
        20,
        21,
        22,
        23,
        24,
        25,
        26,
        27,
        28,
        29,
        30,
        31,
    ];

    private static readonly EffectiveSchemaSet _effectiveSchemaSet = new(
        new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "5.2.0",
            RelationalMappingVersion: "relational-v1",
            EffectiveSchemaHash: "schema-hash",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: _resourceKeySeedHash,
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(
                    ResourceKeyId: 1,
                    Resource: new QualifiedResourceName("Ed-Fi", "Student"),
                    ResourceVersion: "5.2.0",
                    IsAbstractResource: false
                ),
            ]
        ),
        ProjectsInEndpointOrder: []
    );

    private static readonly DatabaseFingerprint _databaseFingerprint = new(
        ApiSchemaFormatVersion: "5.2.0",
        EffectiveSchemaHash: "schema-hash",
        ResourceKeyCount: 1,
        ResourceKeySeedHash: [.. _resourceKeySeedHash]
    );

    private static readonly DocumentCacheLifecycleObservation _trackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheInventoryValidationResult _satisfiedInventory = new(
        DocumentCacheInventoryStatus.Satisfied,
        "Inventory satisfied."
    );

    private static readonly DocumentCacheEnqueueTriggerValidationResult _satisfiedEnqueueTrigger = new(
        DocumentCacheEnqueueTriggerStatus.Satisfied,
        "Enqueue trigger satisfied."
    );

    [TestFixture]
    [Parallelizable]
    public class Given_Startup_Bound_Membership : DocumentCacheTargetRegistryTests
    {
        [Test]
        public void It_exposes_configured_targets_before_any_refresh()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7), ("", 1)]);

            DocumentCacheTargetRegistrySnapshot snapshot = fixture.Registry.CurrentSnapshot;

            snapshot
                .Targets.Select(target => target.TargetKey)
                .Should()
                .Equal(_tenantTargetKey, _defaultTargetKey);
            fixture.Registry.CurrentRuntimeSnapshot.ExecutionContexts.Should().BeEmpty();
            snapshot
                .Targets.Should()
                .AllSatisfy(target =>
                {
                    target.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Configured);
                    target.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.NotEvaluated);
                    target.EffectiveSettings.ProjectorFailureBackoff.Should().Be(TimeSpan.FromSeconds(10));
                });
        }

        [Test]
        public async Task It_does_not_expand_membership_from_runtime_options_mutation()
        {
            DocumentCacheOptions options = CreateOptions([("TenantA", 7)]);
            RegistryFixture fixture = new(options);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            options.Targets.Add(new DocumentCacheTargetOptions { TenantKey = "TenantB", DataStoreId = 8 });

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            snapshot.Targets.Select(target => target.TargetKey).Should().Equal(_tenantTargetKey);
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Explicit_Refresh_Hooks : DocumentCacheTargetRegistryTests
    {
        [Test]
        public async Task It_calls_LoadDataStores_directly_for_a_configured_non_default_tenant()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");

            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.CmsRefreshNotification);

            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            fixture.DataStoreProvider.LoadTenantsCallCount.Should().Be(0);
            fixture.DataStoreProvider.TenantExistsCallCount.Should().Be(0);
            fixture.DataStoreProvider.GetLoadedTenantKeysCallCount.Should().Be(0);
        }

        [Test]
        public async Task It_calls_LoadDataStores_once_per_configured_tenant()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7), ("TenantA", 9), ("", 1)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");
            fixture.DataStoreProvider.QueueLoadResult(null);

            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA", "");
        }

        [Test]
        public async Task It_forwards_the_refresh_cancellation_token_to_direct_load_hooks()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            using var cancellationTokenSource = new CancellationTokenSource();

            await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup,
                cancellationTokenSource.Token
            );

            fixture
                .DataStoreProvider.LoadDataStoreCancellationTokens.Should()
                .ContainSingle()
                .Which.Should()
                .Be(cancellationTokenSource.Token);
        }

        [Test]
        public async Task It_forwards_the_refresh_cancellation_token_to_expiration_refresh_hooks()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueExpirationRefreshResult(
                "TenantA",
                CreateDataStore(7, "connection-a")
            );
            using var cancellationTokenSource = new CancellationTokenSource();

            await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.SupervisorTriggered,
                cancellationTokenSource.Token
            );

            fixture
                .DataStoreProvider.RefreshIfExpiredCancellationTokens.Should()
                .ContainSingle()
                .Which.Should()
                .Be(cancellationTokenSource.Token);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Unresolved_Targets : DocumentCacheTargetRegistryTests
    {
        [Test]
        public async Task It_marks_configured_targets_missing_from_CMS_as_unresolved_with_retry_diagnostics()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Unresolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            observation.Generation.Should().BeNull();
            observation.RetryState!.AttemptCount.Should().Be(1);
            observation
                .RetryState.NextRetryAt.Should()
                .Be(fixture.TimeProvider.GetUtcNow() + TimeSpan.FromSeconds(10));
            observation
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.TargetUnresolved);
            fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey).Should().BeNull();
            fixture.ContextBuilder.BuildCalls.Should().BeEmpty();
        }

        [Test]
        public async Task It_uses_direct_load_on_supervisor_refresh_for_unresolved_targets()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.SupervisorTriggered
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.Generation!.Value.Should().Be(1);
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA", "TenantA");
            fixture.DataStoreProvider.RefreshIfExpiredCalls.Should().BeEmpty();
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_keeps_the_current_generation_when_CMS_refresh_fails_for_a_resolved_target()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            DocumentCacheTargetExecutionContext initialContext =
                fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey)!;
            fixture.DataStoreProvider.QueueLoadFailure("TenantA", new InvalidOperationException("boom"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.Generation!.Value.Should().Be(1);
            observation.RetryState!.AttemptCount.Should().Be(1);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure
                );
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(1)
                )
                .Should()
                .BeSameAs(initialContext);
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_logs_CMS_refresh_failures_without_raw_exception_details()
        {
            RecordingLogger<DocumentCacheTargetRegistry> logger = new();
            RegistryFixture fixture = new(Targets: [("TenantA", 7)], Logger: logger);
            fixture.DataStoreProvider.QueueLoadFailure(
                "TenantA",
                new InvalidOperationException(SensitiveProviderFailure)
            );

            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

            LogRecord record = logger.Records.Single(record =>
                record.Message.Contains("registry refresh failed", StringComparison.Ordinal)
            );
            record.Level.Should().Be(LogLevel.Debug);
            record.Exception.Should().BeNull();
            record
                .Properties["FailureCategory"]
                .Should()
                .Be(DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure);
            record.Properties["ExceptionType"].Should().Be(nameof(InvalidOperationException));
            AssertLogDoesNotContainSensitiveProviderFailure(record);
        }

        private static void AssertLogDoesNotContainSensitiveProviderFailure(LogRecord record)
        {
            string renderedLogText = string.Join(
                "\n",
                [
                    record.Message,
                    .. record.Properties.Values.Select(value => value?.ToString() ?? string.Empty),
                ]
            );
            renderedLogText.Should().NotContain("prod-db.example.com");
            renderedLogText.Should().NotContain("StudentRecords");
            renderedLogText.Should().NotContain("Secret123");
            renderedLogText.Should().NotContain("ProdHost");
            renderedLogText.Should().NotContain("Password");
            renderedLogText.Should().NotContain("Server=");
            renderedLogText.Should().NotContain("Database=");
        }

        [Test]
        public async Task It_reports_unresolved_when_a_successful_refresh_no_longer_resolves_the_key()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Unresolved);
            observation.Generation.Should().BeNull();
            observation
                .Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .NotContain(DocumentCacheTargetDiagnosticCategory.TargetReplaced);
            fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey).Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Resolved_Targets : DocumentCacheTargetRegistryTests
    {
        [Test]
        public async Task It_resolves_a_loaded_target_with_the_first_context_generation()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            observation.Generation!.Value.Should().Be(1);
            DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = fixture.Registry.CurrentRuntimeSnapshot;
            DocumentCacheTargetExecutionContext? context = runtimeSnapshot.GetExecutionContext(
                _tenantTargetKey
            );
            context.Should().NotBeNull();
            context!.Generation.Value.Should().Be(1);
            runtimeSnapshot
                .GetExecutionContext(_tenantTargetKey, new DocumentCacheTargetContextGeneration(1))
                .Should()
                .BeSameAs(context);
            runtimeSnapshot
                .GetExecutionContext(_tenantTargetKey, new DocumentCacheTargetContextGeneration(2))
                .Should()
                .BeNull();
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
            fixture.ContextBuilder.BuildCalls[0].Generation.Value.Should().Be(1);
            fixture
                .ContextBuilder.BuildCalls[0]
                .ResolvedDataStore.ConnectionFactoryInput.Should()
                .Be("connection-a");
        }

        [Test]
        public async Task It_exposes_a_status_snapshot_with_matching_observation_and_runtime_context()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));

            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

            DocumentCacheTargetStatusSnapshot statusSnapshot = fixture.Registry.CurrentStatusSnapshot;

            DocumentCacheTargetObservation observation = statusSnapshot
                .Targets.Should()
                .ContainSingle()
                .Which;
            DocumentCacheTargetExecutionContext executionContext = statusSnapshot.GetExecutionContext(
                observation.TargetKey,
                observation.Generation!
            )!;
            executionContext.Should().NotBeNull();
            executionContext.Generation.Should().Be(observation.Generation);
            executionContext.TargetKey.Should().Be(observation.TargetKey);
            executionContext.ConnectionInput.Value.Should().Be("connection-a");
            statusSnapshot.RegistryObservedAt.Should().Be(fixture.TimeProvider.GetUtcNow());
        }

        [Test]
        public async Task It_builds_a_generation_from_the_same_resolved_data_store_used_for_the_signature()
        {
            RegistryWithRealBuilderFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            fixture.DataStoreProvider.QueueMutationAfterNextGetById(
                "TenantA",
                CreateDataStore(7, "connection-b")
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            snapshot.Targets.Single().Generation!.Value.Should().Be(1);
            fixture.DataStoreProvider.GetByIdCalls.Should().ContainSingle();
            DocumentCacheTargetExecutionContext context =
                fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey)!;
            context.ConnectionInput.Value.Should().Be("connection-a");
            context.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
            fixture.FingerprintReader.ConnectionInputs.Should().Equal("connection-a");
            fixture.LifecycleReader.ConnectionInputs.Should().Equal("connection-a");
            fixture.InventoryValidator.ConnectionInputs.Should().Equal("connection-a");
            fixture.PrerequisiteValidator.ConnectionInputs.Should().Equal("connection-a");
        }

        [Test]
        public async Task It_does_not_expose_a_runtime_context_for_a_resolved_ineligible_target()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.ContextBuilder.MarkIneligible(_tenantTargetKey);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            snapshot
                .Targets.Single()
                .EligibilityState.Should()
                .Be(DocumentCacheTargetEligibilityState.Ineligible);
            fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey).Should().BeNull();
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_reads_runtime_contexts_without_refreshing_or_rebuilding_targets()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey)
                .Should()
                .NotBeNull();
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey)
                .Should()
                .NotBeNull();

            fixture.DataStoreProvider.LoadDataStoreCalls.Should().ContainSingle();
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_uses_expiration_aware_refresh_for_supervisor_refresh_of_resolved_current_targets()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.SupervisorTriggered
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.Generation!.Value.Should().Be(1);
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            fixture.DataStoreProvider.RefreshIfExpiredCalls.Should().Equal("TenantA");
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_detects_replacement_metadata_after_expiration_aware_supervisor_refresh()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueExpirationRefreshResult(
                "TenantA",
                CreateDataStore(7, "connection-b")
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.SupervisorTriggered
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.Generation!.Value.Should().Be(2);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TargetReplaced
                );
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            fixture.DataStoreProvider.RefreshIfExpiredCalls.Should().Equal("TenantA");
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 2);
        }

        [Test]
        public async Task It_does_not_create_a_new_generation_for_display_or_route_context_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", name: "Original", routeContextValue: "2025")
            );
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", name: "Changed", routeContextValue: "2026")
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            snapshot.Targets.Single().Generation!.Value.Should().Be(1);
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(1)
                )
                .Should()
                .NotBeNull();
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [TestCase(
            RelationalProviderMetadataStatus.Missing,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
            RelationalProviderMetadataStatus.Unknown,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown
        )]
        [TestCase(
            RelationalProviderMetadataStatus.Unknown,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
            RelationalProviderMetadataStatus.Missing,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
        )]
        public async Task It_refreshes_provider_metadata_diagnostics_without_replacing_the_generation(
            RelationalProviderMetadataStatus initialStatus,
            DocumentCacheTargetDiagnosticCategory initialCategory,
            RelationalProviderMetadataStatus refreshedStatus,
            DocumentCacheTargetDiagnosticCategory refreshedCategory
        )
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(
                    7,
                    "connection-a",
                    RelationalProviderToken.Postgresql,
                    relationalProviderMetadataStatus: initialStatus
                )
            );
            DocumentCacheTargetRegistrySnapshot initialSnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );
            initialSnapshot
                .Targets.Single()
                .Diagnostics.Should()
                .ContainSingle(diagnostic => diagnostic.Category == initialCategory);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(
                    7,
                    "connection-a",
                    RelationalProviderToken.Postgresql,
                    relationalProviderMetadataStatus: refreshedStatus
                )
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.Generation!.Value.Should().Be(1);
            observation
                .Diagnostics.Should()
                .ContainSingle(diagnostic => diagnostic.Category == refreshedCategory);
            observation
                .Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .NotContain(DocumentCacheTargetDiagnosticCategory.TargetReplaced);
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 1);
        }

        [Test]
        public async Task It_does_not_make_command_preflight_generation_stale_for_provider_metadata_status_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(
                    7,
                    "connection-a",
                    RelationalProviderToken.Postgresql,
                    relationalProviderMetadataStatus: RelationalProviderMetadataStatus.Missing
                )
            );
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(
                    7,
                    "connection-a",
                    RelationalProviderToken.Postgresql,
                    relationalProviderMetadataStatus: RelationalProviderMetadataStatus.Unknown
                )
            );
            DocumentCacheTargetObservation observation = (
                await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.CmsRefreshNotification)
            ).Targets.Single();

            DocumentCacheAdministrativeCommandResult result =
                DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                    new DocumentCacheGuardedNewEmptyActivationRequest(
                        DocumentCacheAdministrativeTargetKey.FromTargetKey(_tenantTargetKey)
                    ),
                    observation,
                    new DocumentCacheGuardedNewEmptyActivationPreflightFacts(
                        new DocumentCacheTargetContextGeneration(1),
                        activationProviderPrerequisites: null,
                        guardedNewEmptyState: null
                    )
                );

            result
                .Classification.Should()
                .NotBe(DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution);
            result.TargetGeneration.Should().Be(1);
        }

        [Test]
        public async Task It_retries_recoverable_SqlServerDocumentCachePrerequisite_failures_without_replacing_the_generation()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.ContextBuilder.QueueProviderPrerequisiteFailure(DocumentCacheLifecycleState.Disabled);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );
            DocumentCacheTargetRegistrySnapshot initialSnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );
            fixture.DataStoreProvider.QueueExpirationRefreshResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );

            DocumentCacheTargetRegistrySnapshot retrySnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.SupervisorTriggered
            );

            initialSnapshot
                .Targets.Single()
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
                );
            DocumentCacheTargetObservation retriedObservation = retrySnapshot.Targets.Single();
            retriedObservation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            retriedObservation.Generation!.Value.Should().Be(1);
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 1);
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(1)
                )
                .Should()
                .NotBeNull();
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            fixture.DataStoreProvider.RefreshIfExpiredCalls.Should().Equal("TenantA");
        }

        [Test]
        public async Task It_retries_recoverable_SqlServerDocumentCachePrerequisite_failures_after_forced_Cms_refresh()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.ContextBuilder.QueueProviderPrerequisiteFailure(DocumentCacheLifecycleState.Disabled);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );
            DocumentCacheTargetRegistrySnapshot initialSnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );

            DocumentCacheTargetRegistrySnapshot cmsRefreshSnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            initialSnapshot
                .Targets.Single()
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
                );
            DocumentCacheTargetObservation observation = cmsRefreshSnapshot.Targets.Single();
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            observation.Generation!.Value.Should().Be(1);
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 1);
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(1)
                )
                .Should()
                .NotBeNull();
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA", "TenantA");
            fixture.DataStoreProvider.RefreshIfExpiredCalls.Should().BeEmpty();
        }

        [Test]
        public async Task It_freezes_unsupported_SqlServerDocumentCachePrerequisite_incidents_for_the_same_generation()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.ContextBuilder.QueueProviderPrerequisiteFailure(DocumentCacheLifecycleState.Tracking);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

            DocumentCacheTargetRegistrySnapshot retrySnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.SupervisorTriggered
            );

            DocumentCacheTargetObservation observation = retrySnapshot.Targets.Single();
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            observation.Generation!.Value.Should().Be(1);
            observation
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.Category
                    == DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident
                );
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
            fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_tenantTargetKey).Should().BeNull();
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            fixture.DataStoreProvider.RefreshIfExpiredCalls.Should().Equal("TenantA");
        }

        [Test]
        public async Task It_allows_replacement_metadata_to_revalidate_after_unsupported_SqlServerDocumentCachePrerequisite_incidents()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.ContextBuilder.QueueProviderPrerequisiteFailure(DocumentCacheLifecycleState.Tracking);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-b", RelationalProviderToken.SqlServer)
            );

            DocumentCacheTargetRegistrySnapshot replacementSnapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = replacementSnapshot.Targets.Single();
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            observation.Generation!.Value.Should().Be(2);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TargetReplaced
                );
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 2);
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(2)
                )
                .Should()
                .NotBeNull();
        }

        [Test]
        public async Task It_creates_a_new_generation_when_the_connection_factory_input_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-b"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.Generation!.Value.Should().Be(2);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TargetReplaced
                );
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 2);
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(1)
                )
                .Should()
                .BeNull();
            fixture
                .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                    _tenantTargetKey,
                    new DocumentCacheTargetContextGeneration(2)
                )
                .Should()
                .NotBeNull();
        }

        [Test]
        public async Task It_creates_a_new_generation_when_the_provider_token_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            snapshot.Targets.Single().Generation!.Value.Should().Be(2);
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 2);
        }
    }

    private static DocumentCacheOptions CreateOptions(
        IReadOnlyList<(string TenantKey, long DataStoreId)> targets
    )
    {
        DocumentCacheOptions options = new()
        {
            Targets = targets
                .Select(target => new DocumentCacheTargetOptions
                {
                    TenantKey = target.TenantKey,
                    DataStoreId = target.DataStoreId,
                })
                .ToList(),
            Projector = new DocumentCacheProjectorOptions { FailureBackoff = TimeSpan.FromSeconds(10) },
        };

        return options;
    }

    private static DataStore CreateDataStore(
        long id,
        string connectionString,
        RelationalProviderToken? relationalProviderToken = null,
        RelationalProviderMetadataStatus relationalProviderMetadataStatus =
            RelationalProviderMetadataStatus.Supported,
        string name = "Display name must not leak",
        string routeContextValue = "2025"
    ) =>
        new(
            id,
            "Operational",
            name,
            connectionString,
            new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("schoolYear")] = new(routeContextValue),
            },
            relationalProviderToken ?? RelationalProviderToken.Postgresql,
            relationalProviderMetadataStatus
        );

    private sealed class RegistryFixture
    {
        public SequencedDataStoreProvider DataStoreProvider { get; } = new();

        public RecordingTargetContextBuilder ContextBuilder { get; } = new();

        public FakeTimeProvider TimeProvider { get; } =
            new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        public DocumentCacheTargetRegistry Registry { get; }

        public RegistryFixture(
            IReadOnlyList<(string TenantKey, long DataStoreId)> Targets,
            ILogger<DocumentCacheTargetRegistry>? Logger = null
        )
            : this(CreateOptions(Targets), Logger) { }

        public RegistryFixture(
            DocumentCacheOptions options,
            ILogger<DocumentCacheTargetRegistry>? Logger = null
        )
        {
            Registry = new DocumentCacheTargetRegistry(
                DataStoreProvider,
                ContextBuilder,
                Options.Create(options),
                TimeProvider,
                Logger ?? NullLogger<DocumentCacheTargetRegistry>.Instance
            );
        }
    }

    private sealed class RegistryWithRealBuilderFixture
    {
        public SequencedDataStoreProvider DataStoreProvider { get; } = new();

        public RecordingFingerprintReader FingerprintReader { get; } = new();

        public RecordingLifecycleReader LifecycleReader { get; } = new();

        public RecordingInventoryValidator InventoryValidator { get; } = new();

        public RecordingDatabaseFingerprintReader DatabaseFingerprintReader { get; } = new();

        public RecordingResourceKeyValidator ResourceKeyValidator { get; } = new();

        public RecordingPrerequisiteValidator PrerequisiteValidator { get; } = new();

        public FakeTimeProvider TimeProvider { get; } =
            new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        public DocumentCacheTargetRegistry Registry { get; }

        public RegistryWithRealBuilderFixture(IReadOnlyList<(string TenantKey, long DataStoreId)> Targets)
        {
            DocumentCacheOptions options = CreateOptions(Targets);
            DocumentCacheTargetContextBuilder contextBuilder = new(
                Options.Create(options),
                new DocumentCacheProcessProviderToken(RelationalProviderToken.Postgresql),
                new StaticEffectiveSchemaSetProvider(_effectiveSchemaSet),
                DatabaseFingerprintReader,
                ResourceKeyValidator,
                FingerprintReader,
                LifecycleReader,
                InventoryValidator,
                PrerequisiteValidator,
                NullLogger<DocumentCacheTargetContextBuilder>.Instance
            );

            Registry = new DocumentCacheTargetRegistry(
                DataStoreProvider,
                contextBuilder,
                Options.Create(options),
                TimeProvider,
                NullLogger<DocumentCacheTargetRegistry>.Instance
            );
        }
    }

    private sealed class RecordingFingerprintReader : IDocumentCachePhysicalSourceFingerprintReader
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public List<string> ConnectionInputs { get; } = [];

        public Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadFingerprintAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            ConnectionInputs.Add(connectionString);
            return Task.FromResult(DocumentCachePhysicalSourceFingerprintReadResult.Success(_fingerprint));
        }
    }

    private sealed class RecordingLifecycleReader : IDocumentCacheLifecycleReader
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public List<string> ConnectionInputs { get; } = [];

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            ConnectionInputs.Add(connectionString);
            return Task.FromResult(DocumentCacheLifecycleReadResult.Success(_trackingLifecycle));
        }
    }

    private sealed class RecordingDatabaseFingerprintReader : IDatabaseFingerprintReader
    {
        public List<string> ConnectionInputs { get; } = [];

        public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString)
        {
            ConnectionInputs.Add(connectionString);
            return Task.FromResult<DatabaseFingerprint?>(_databaseFingerprint);
        }
    }

    private sealed class RecordingResourceKeyValidator : IResourceKeyValidator
    {
        public List<string> ConnectionInputs { get; } = [];

        public Task<ResourceKeyValidationResult> ValidateAsync(
            DatabaseFingerprint dbFingerprint,
            short expectedResourceKeyCount,
            ImmutableArray<byte> expectedResourceKeySeedHash,
            IReadOnlyList<ResourceKeyRow> expectedResourceKeysInIdOrder,
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            ConnectionInputs.Add(connectionString);
            return Task.FromResult<ResourceKeyValidationResult>(
                new ResourceKeyValidationResult.ValidationSuccess()
            );
        }
    }

    private sealed class RecordingInventoryValidator : IDocumentCacheInventoryValidator
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public List<string> ConnectionInputs { get; } = [];

        public Task<DocumentCacheProviderInventoryValidationResult> ValidateInventoryAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            ConnectionInputs.Add(connectionString);
            return Task.FromResult(
                new DocumentCacheProviderInventoryValidationResult(
                    _satisfiedInventory,
                    _satisfiedEnqueueTrigger
                )
            );
        }
    }

    private sealed class RecordingPrerequisiteValidator : IDocumentCacheProviderPrerequisiteValidator
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public List<string> ConnectionInputs { get; } = [];

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateInitializationAsync(
            string connectionString,
            DocumentCacheLifecycleObservation lifecycle,
            CancellationToken cancellationToken = default
        )
        {
            ConnectionInputs.Add(connectionString);
            return Task.FromResult(
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                    lifecycle
                )
            );
        }

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPreflightAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                )
            );
    }

    private sealed class StaticEffectiveSchemaSetProvider(EffectiveSchemaSet effectiveSchemaSet)
        : IEffectiveSchemaSetProvider
    {
        public EffectiveSchemaSet EffectiveSchemaSet { get; } = effectiveSchemaSet;

        public bool IsInitialized => true;

        public void Initialize(EffectiveSchemaSet effectiveSchemaSet) =>
            throw new InvalidOperationException("Static test provider is already initialized.");
    }

    private sealed class RecordingTargetContextBuilder : IDocumentCacheTargetContextBuilder
    {
        private readonly HashSet<DocumentCacheTargetKey> _ineligibleTargetKeys = [];
        private readonly Queue<DocumentCacheLifecycleState> _queuedProviderPrerequisiteFailures = new();

        public List<BuildCall> BuildCalls { get; } = [];

        public void MarkIneligible(DocumentCacheTargetKey targetKey)
        {
            ArgumentNullException.ThrowIfNull(targetKey);

            _ineligibleTargetKeys.Add(targetKey);
        }

        public void QueueProviderPrerequisiteFailure(DocumentCacheLifecycleState lifecycleState) =>
            _queuedProviderPrerequisiteFailures.Enqueue(lifecycleState);

        public Task<DocumentCacheTargetContextBuildResult> BuildAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCacheResolvedTargetDataStore resolvedDataStore,
            DocumentCacheTargetContextGeneration generation,
            CancellationToken cancellationToken = default
        )
        {
            BuildCalls.Add(new BuildCall(targetKey, resolvedDataStore, generation));
            if (
                resolvedDataStore.RelationalProviderMetadataStatus
                != RelationalProviderMetadataStatus.Supported
            )
            {
                return Task.FromResult(
                    CreateProviderMetadataBuildResult(targetKey, resolvedDataStore, generation)
                );
            }

            if (
                _queuedProviderPrerequisiteFailures.TryDequeue(out DocumentCacheLifecycleState lifecycleState)
            )
            {
                return Task.FromResult(
                    CreateProviderPrerequisiteBuildResult(
                        targetKey,
                        resolvedDataStore,
                        generation,
                        lifecycleState
                    )
                );
            }

            if (_ineligibleTargetKeys.Contains(targetKey))
            {
                return Task.FromResult(CreateIneligibleBuildResult(targetKey, resolvedDataStore, generation));
            }

            return Task.FromResult(CreateBuildResult(targetKey, resolvedDataStore, generation));
        }

        private static DocumentCacheTargetContextBuildResult CreateProviderMetadataBuildResult(
            DocumentCacheTargetKey targetKey,
            DocumentCacheResolvedTargetDataStore resolvedDataStore,
            DocumentCacheTargetContextGeneration generation
        )
        {
            DocumentCacheTargetDiagnosticCategory category =
                resolvedDataStore.RelationalProviderMetadataStatus == RelationalProviderMetadataStatus.Missing
                    ? DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
                    : DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown;

            DocumentCacheTargetDiagnostic diagnostic = new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                resolvedDataStore.RelationalProviderToken,
                generation,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                category,
                category == DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
                    ? "Resolved target is missing relational provider metadata."
                    : "Resolved target has unknown relational provider metadata."
            );

            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedIneligible(
                targetKey,
                _effectiveSettings,
                generation,
                resolvedDataStore.RelationalProviderToken,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState: null,
                [diagnostic]
            );

            return new DocumentCacheTargetContextBuildResult(observation, ExecutionContext: null);
        }

        private static DocumentCacheTargetContextBuildResult CreateProviderPrerequisiteBuildResult(
            DocumentCacheTargetKey targetKey,
            DocumentCacheResolvedTargetDataStore resolvedDataStore,
            DocumentCacheTargetContextGeneration generation,
            DocumentCacheLifecycleState lifecycleState
        )
        {
            RelationalProviderToken providerToken =
                resolvedDataStore.RelationalProviderToken ?? RelationalProviderToken.SqlServer;
            DocumentCacheLifecycleObservation lifecycle = new(lifecycleState, false);
            DocumentCacheSqlServerPrerequisiteDetails failedPrerequisites = new(
                new DocumentCacheProviderPrerequisiteResult(
                    DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                    DocumentCacheProviderPrerequisiteStatus.Disabled,
                    "RCSI disabled."
                ),
                new DocumentCacheProviderPrerequisiteResult(
                    DocumentCacheProviderPrerequisiteName.NestedTriggers,
                    DocumentCacheProviderPrerequisiteStatus.Satisfied,
                    "Nested triggers satisfied."
                )
            );
            DocumentCacheProviderPrerequisiteValidationResult prerequisiteFailure =
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    failedPrerequisites,
                    lifecycle
                );
            DocumentCacheTargetDiagnostic diagnostic = new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                providerToken,
                generation,
                _fingerprint,
                lifecycle,
                _satisfiedInventory,
                _satisfiedEnqueueTrigger,
                prerequisiteFailure.SqlServerPrerequisites,
                retryState: null,
                prerequisiteFailure.FailureCategory!.Value,
                prerequisiteFailure.Message
            );
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedIneligible(
                targetKey,
                _effectiveSettings,
                generation,
                providerToken,
                _fingerprint,
                lifecycle,
                _satisfiedInventory,
                _satisfiedEnqueueTrigger,
                prerequisiteFailure.SqlServerPrerequisites,
                retryState: null,
                [diagnostic]
            );

            return new DocumentCacheTargetContextBuildResult(observation, ExecutionContext: null);
        }

        private static DocumentCacheTargetContextBuildResult CreateIneligibleBuildResult(
            DocumentCacheTargetKey targetKey,
            DocumentCacheResolvedTargetDataStore resolvedDataStore,
            DocumentCacheTargetContextGeneration generation
        )
        {
            RelationalProviderToken providerToken =
                resolvedDataStore.RelationalProviderToken ?? RelationalProviderToken.Postgresql;
            DocumentCacheInventoryValidationResult invalidInventory = new(
                DocumentCacheInventoryStatus.Invalid,
                "Inventory invalid."
            );
            DocumentCacheTargetDiagnostic diagnostic = new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                providerToken,
                generation,
                _fingerprint,
                _trackingLifecycle,
                invalidInventory,
                _satisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                retryState: null,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                "Inventory invalid."
            );
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedIneligible(
                targetKey,
                _effectiveSettings,
                generation,
                providerToken,
                _fingerprint,
                _trackingLifecycle,
                invalidInventory,
                _satisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                retryState: null,
                [diagnostic]
            );

            return new DocumentCacheTargetContextBuildResult(observation, ExecutionContext: null);
        }

        private static DocumentCacheTargetContextBuildResult CreateBuildResult(
            DocumentCacheTargetKey targetKey,
            DocumentCacheResolvedTargetDataStore resolvedDataStore,
            DocumentCacheTargetContextGeneration generation
        )
        {
            RelationalProviderToken providerToken =
                resolvedDataStore.RelationalProviderToken ?? RelationalProviderToken.Postgresql;
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedEligible(
                targetKey,
                _effectiveSettings,
                generation,
                providerToken,
                _fingerprint,
                _trackingLifecycle,
                _satisfiedInventory,
                _satisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );
            DocumentCacheTargetExecutionContext executionContext = new(
                targetKey,
                generation,
                _effectiveSettings,
                new DocumentCacheTargetDataStoreMetadata(
                    resolvedDataStore.Id,
                    resolvedDataStore.DataStoreType
                ),
                new DocumentCacheTargetConnectionInput(
                    providerToken,
                    resolvedDataStore.ConnectionFactoryInput ?? "connection"
                ),
                _fingerprint,
                _trackingLifecycle,
                _satisfiedInventory,
                _satisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );

            return new DocumentCacheTargetContextBuildResult(observation, executionContext);
        }
    }

    private sealed record BuildCall(
        DocumentCacheTargetKey TargetKey,
        DocumentCacheResolvedTargetDataStore ResolvedDataStore,
        DocumentCacheTargetContextGeneration Generation
    );

    private sealed class SequencedDataStoreProvider : IDataStoreProvider
    {
        private readonly Dictionary<string, Queue<LoadDataStoresResult>> _queuedLoadResults = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, Queue<LoadDataStoresResult>> _queuedExpirationRefreshResults =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IList<DataStore>> _loadedDataStores = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, Queue<IList<DataStore>>> _queuedGetByIdMutations = new(
            StringComparer.OrdinalIgnoreCase
        );

        public List<string> LoadDataStoreCalls { get; } = [];

        public List<string> RefreshIfExpiredCalls { get; } = [];

        public List<CancellationToken> LoadDataStoreCancellationTokens { get; } = [];

        public List<CancellationToken> RefreshIfExpiredCancellationTokens { get; } = [];

        public List<(long Id, string TenantKey)> GetByIdCalls { get; } = [];

        public int LoadTenantsCallCount { get; private set; }

        public int TenantExistsCallCount { get; private set; }

        public int GetLoadedTenantKeysCallCount { get; private set; }

        public void QueueLoadResult(string? tenant, params DataStore[] dataStores) =>
            GetQueue(tenant).Enqueue(LoadDataStoresResult.Success(dataStores));

        public void QueueLoadFailure(string? tenant, Exception exception) =>
            GetQueue(tenant).Enqueue(LoadDataStoresResult.Failure(exception));

        public void QueueExpirationRefreshResult(string? tenant, params DataStore[] dataStores) =>
            GetExpirationRefreshQueue(tenant).Enqueue(LoadDataStoresResult.Success(dataStores));

        public void QueueMutationAfterNextGetById(string? tenant, params DataStore[] dataStores) =>
            GetGetByIdMutationQueue(tenant).Enqueue(dataStores);

        public Task<IList<DataStore>> LoadDataStores(
            string? tenant = null,
            CancellationToken cancellationToken = default
        )
        {
            string tenantKey = GetTenantKey(tenant);
            LoadDataStoreCalls.Add(tenantKey);
            LoadDataStoreCancellationTokens.Add(cancellationToken);

            Queue<LoadDataStoresResult> queue = GetQueue(tenant);
            if (queue.Count == 0)
            {
                _loadedDataStores[tenantKey] = [];
                return Task.FromResult<IList<DataStore>>([]);
            }

            LoadDataStoresResult result = queue.Dequeue();
            if (result.Exception is not null)
            {
                throw result.Exception;
            }

            _loadedDataStores[tenantKey] = result.DataStores;
            return Task.FromResult(result.DataStores);
        }

        public Task RefreshInstancesIfExpiredAsync(
            string? tenant = null,
            CancellationToken cancellationToken = default
        )
        {
            string tenantKey = GetTenantKey(tenant);
            RefreshIfExpiredCalls.Add(tenantKey);
            RefreshIfExpiredCancellationTokens.Add(cancellationToken);

            Queue<LoadDataStoresResult> queue = GetExpirationRefreshQueue(tenant);
            if (queue.Count == 0)
            {
                return Task.CompletedTask;
            }

            LoadDataStoresResult result = queue.Dequeue();
            if (result.Exception is not null)
            {
                throw result.Exception;
            }

            _loadedDataStores[tenantKey] = result.DataStores;
            return Task.CompletedTask;
        }

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
            _loadedDataStores.TryGetValue(GetTenantKey(tenant), out IList<DataStore>? dataStores)
                ? dataStores.ToList().AsReadOnly()
                : [];

        public DataStore? GetById(long id, string? tenant = null)
        {
            string tenantKey = GetTenantKey(tenant);
            GetByIdCalls.Add((id, tenantKey));

            if (!_loadedDataStores.TryGetValue(tenantKey, out IList<DataStore>? dataStores))
            {
                return null;
            }

            DataStore? dataStore = dataStores.FirstOrDefault(dataStore => dataStore.Id == id);
            if (
                _queuedGetByIdMutations.TryGetValue(tenantKey, out Queue<IList<DataStore>>? queuedMutations)
                && queuedMutations.Count > 0
            )
            {
                _loadedDataStores[tenantKey] = queuedMutations.Dequeue();
            }

            return dataStore;
        }

        public bool IsLoaded(string? tenant = null) => _loadedDataStores.ContainsKey(GetTenantKey(tenant));

        public Task<IList<string>> LoadTenants()
        {
            LoadTenantsCallCount++;
            return Task.FromResult<IList<string>>([]);
        }

        public bool TenantExists(string tenant)
        {
            TenantExistsCallCount++;
            return false;
        }

        public IReadOnlyList<string> GetLoadedTenantKeys()
        {
            GetLoadedTenantKeysCallCount++;
            return [];
        }

        private Queue<LoadDataStoresResult> GetQueue(string? tenant)
        {
            string tenantKey = GetTenantKey(tenant);
            if (!_queuedLoadResults.TryGetValue(tenantKey, out Queue<LoadDataStoresResult>? queue))
            {
                queue = new Queue<LoadDataStoresResult>();
                _queuedLoadResults.Add(tenantKey, queue);
            }

            return queue;
        }

        private Queue<LoadDataStoresResult> GetExpirationRefreshQueue(string? tenant)
        {
            string tenantKey = GetTenantKey(tenant);
            if (
                !_queuedExpirationRefreshResults.TryGetValue(
                    tenantKey,
                    out Queue<LoadDataStoresResult>? queue
                )
            )
            {
                queue = new Queue<LoadDataStoresResult>();
                _queuedExpirationRefreshResults.Add(tenantKey, queue);
            }

            return queue;
        }

        private Queue<IList<DataStore>> GetGetByIdMutationQueue(string? tenant)
        {
            string tenantKey = GetTenantKey(tenant);
            if (!_queuedGetByIdMutations.TryGetValue(tenantKey, out Queue<IList<DataStore>>? queue))
            {
                queue = new Queue<IList<DataStore>>();
                _queuedGetByIdMutations.Add(tenantKey, queue);
            }

            return queue;
        }

        private static string GetTenantKey(string? tenant) => tenant ?? string.Empty;
    }

    private sealed record LoadDataStoresResult(IList<DataStore> DataStores, Exception? Exception)
    {
        public static LoadDataStoresResult Success(IList<DataStore> dataStores) =>
            new(dataStores, Exception: null);

        public static LoadDataStoresResult Failure(Exception exception) => new([], exception);
    }
}
