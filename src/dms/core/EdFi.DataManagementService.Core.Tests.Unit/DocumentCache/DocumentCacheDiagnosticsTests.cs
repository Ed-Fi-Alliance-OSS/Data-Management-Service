// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheDiagnostics")]
public class DocumentCacheDiagnosticsTests
{
    private static readonly DateTimeOffset _observedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly DocumentCacheTargetEffectiveSettings _effectiveSettings = new(
        readAccelerationEnabled: true,
        directFillTimeout: TimeSpan.FromMilliseconds(250),
        projectorPollInterval: TimeSpan.FromSeconds(5),
        projectorPageSize: 100,
        projectorMaxConcurrentTargets: 2,
        projectorFailureBackoff: TimeSpan.FromSeconds(30),
        projectorBaselineHighWaterMark: 1000
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation _trackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheLifecycleObservation _disabledLifecycleWithLatch = new(
        DocumentCacheLifecycleState.Disabled,
        CacheAheadRecoveryRequired: true
    );

    private static readonly DocumentCacheInventoryValidationResult _satisfiedInventory = new(
        DocumentCacheInventoryStatus.Satisfied,
        "Inventory satisfied."
    );

    private static readonly DocumentCacheEnqueueTriggerValidationResult _satisfiedEnqueueTrigger = new(
        DocumentCacheEnqueueTriggerStatus.Satisfied,
        "Enqueue trigger satisfied."
    );

    private static readonly DocumentCacheSqlServerPrerequisiteDetails _notApplicablePrerequisites =
        DocumentCacheSqlServerPrerequisiteDetails.NotApplicable();

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Diagnostic_Snapshots : DocumentCacheDiagnosticsTests
    {
        [Test]
        public void It_includes_every_configured_target_with_successful_and_failed_observations()
        {
            DocumentCacheTargetKey configuredTarget = TargetKey("TenantA", 7);
            DocumentCacheTargetKey eligibleTarget = TargetKey("", 1);
            DocumentCacheTargetKey failedTarget = TargetKey("TenantB", 8);
            DocumentCacheResolutionRetryState retryState = RetryState(
                DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                "CMS refresh failed."
            );
            DocumentCacheTargetDiagnostic failedDiagnostic = Diagnostic(
                failedTarget,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                "Provider prerequisite failed."
            );

            DocumentCacheDiagnosticSnapshot snapshot = DocumentCacheDiagnosticSnapshot.FromRegistrySnapshot(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.Configured(configuredTarget, _effectiveSettings),
                        DocumentCacheTargetObservation.ResolvedEligible(
                            eligibleTarget,
                            _effectiveSettings,
                            Generation(3),
                            RelationalProviderToken.Postgresql,
                            _fingerprint,
                            _trackingLifecycle,
                            _satisfiedInventory,
                            _satisfiedEnqueueTrigger,
                            _notApplicablePrerequisites
                        ),
                        DocumentCacheTargetObservation.ResolvedIneligible(
                            failedTarget,
                            _effectiveSettings,
                            Generation(4),
                            RelationalProviderToken.SqlServer,
                            _fingerprint,
                            _disabledLifecycleWithLatch,
                            new DocumentCacheInventoryValidationResult(
                                DocumentCacheInventoryStatus.Invalid,
                                "Inventory invalid."
                            ),
                            new DocumentCacheEnqueueTriggerValidationResult(
                                DocumentCacheEnqueueTriggerStatus.Disabled,
                                "Enqueue trigger disabled."
                            ),
                            FailedSqlServerPrerequisites(),
                            retryState,
                            [failedDiagnostic]
                        ),
                    ],
                    _observedAt
                )
            );

            snapshot.ObservedAt.Should().Be(_observedAt);
            snapshot
                .Targets.Select(target => target.TargetKey)
                .Should()
                .Equal(configuredTarget, eligibleTarget, failedTarget);

            DocumentCacheTargetDiagnosticSnapshot configured = snapshot.Targets[0];
            configured.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Configured);
            configured.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.NotEvaluated);
            configured.Generation.Should().BeNull();
            configured.ProviderToken.Should().BeNull();
            configured.Diagnostics.Should().BeEmpty();

            DocumentCacheTargetDiagnosticSnapshot eligible = snapshot.Targets[1];
            eligible.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            eligible.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            eligible.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
            eligible.Generation.Should().Be(Generation(3));
            eligible.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            eligible.Lifecycle.Should().Be(_trackingLifecycle);
            eligible.Inventory.Should().Be(_satisfiedInventory);
            eligible.EnqueueTrigger.Should().Be(_satisfiedEnqueueTrigger);
            eligible.SqlServerPrerequisites.Should().Be(_notApplicablePrerequisites);
            eligible.RetryState.Should().BeNull();
            eligible.Diagnostics.Should().BeEmpty();

            DocumentCacheTargetDiagnosticSnapshot failed = snapshot.Targets[2];
            failed.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            failed.Lifecycle!.CacheAheadRecoveryRequired.Should().BeTrue();
            failed.Inventory!.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
            failed.EnqueueTrigger!.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Disabled);
            failed
                .SqlServerPrerequisites!.ReadCommittedSnapshot.Status.Should()
                .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
            failed.RetryState.Should().Be(retryState);
            failed.Diagnostics.Should().ContainSingle().Which.Should().Be(failedDiagnostic);
        }

        [Test]
        public void It_preserves_target_failure_categories_for_later_health_composition()
        {
            DocumentCacheTargetKey targetKey = TargetKey("TenantA", 7);
            DocumentCacheTargetDiagnosticCategory[] categories =
            [
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
                DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
                DocumentCacheTargetDiagnosticCategory.ProviderMismatch,
                DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
            ];

            DocumentCacheDiagnosticSnapshot snapshot = DocumentCacheDiagnosticSnapshot.FromRegistrySnapshot(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.ResolvedIneligible(
                            targetKey,
                            _effectiveSettings,
                            Generation(1),
                            RelationalProviderToken.SqlServer,
                            _fingerprint,
                            _disabledLifecycleWithLatch,
                            new DocumentCacheInventoryValidationResult(
                                DocumentCacheInventoryStatus.Invalid,
                                "Inventory invalid."
                            ),
                            _satisfiedEnqueueTrigger,
                            FailedSqlServerPrerequisites(),
                            RetryState(
                                DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                                "CMS refresh failed."
                            ),
                            categories.Select(category =>
                                Diagnostic(targetKey, category, $"Diagnostic category {category}.")
                            )
                        ),
                    ],
                    _observedAt
                )
            );

            snapshot
                .Targets.Single()
                .Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .Equal(categories);
        }

        [Test]
        public void It_uses_current_registry_snapshot_without_refresh_or_health_decision()
        {
            DocumentCacheTargetKey targetKey = TargetKey("TenantA", 7);
            StaticTargetRegistry targetRegistry = new(
                new DocumentCacheTargetRegistrySnapshot(
                    [DocumentCacheTargetObservation.Configured(targetKey, _effectiveSettings)],
                    _observedAt
                )
            );
            DocumentCacheDiagnosticSnapshotProvider provider = new(targetRegistry);

            DocumentCacheDiagnosticSnapshot snapshot = provider.CurrentSnapshot;

            snapshot.ObservedAt.Should().Be(_observedAt);
            snapshot.Targets.Should().ContainSingle().Which.TargetKey.Should().Be(targetKey);
        }

        [Test]
        public void It_preserves_source_identity_inventory_failures_in_target_snapshots()
        {
            DocumentCacheTargetKey targetKey = TargetKey("TenantA", 7);
            DocumentCacheInventoryValidationResult sourceIdentityInventory = new(
                DocumentCacheInventoryStatus.Missing,
                "Source identity inventory failure."
            );
            DocumentCacheTargetDiagnostic inventoryDiagnostic = new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                RelationalProviderToken.Postgresql,
                Generation(5),
                null,
                _trackingLifecycle,
                sourceIdentityInventory,
                _satisfiedEnqueueTrigger,
                _notApplicablePrerequisites,
                retryState: null,
                DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                "Source identity inventory failure."
            );

            DocumentCacheDiagnosticSnapshot snapshot = DocumentCacheDiagnosticSnapshot.FromRegistrySnapshot(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.ResolvedIneligible(
                            targetKey,
                            _effectiveSettings,
                            Generation(5),
                            RelationalProviderToken.Postgresql,
                            null,
                            _trackingLifecycle,
                            sourceIdentityInventory,
                            _satisfiedEnqueueTrigger,
                            _notApplicablePrerequisites,
                            retryState: null,
                            [inventoryDiagnostic]
                        ),
                    ],
                    _observedAt
                )
            );

            DocumentCacheTargetDiagnosticSnapshot targetSnapshot = snapshot.Targets.Single();
            targetSnapshot.PhysicalSourceFingerprint.Should().BeNull();
            targetSnapshot.Inventory!.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
            targetSnapshot.Inventory.Should().NotBe(_satisfiedInventory);
            targetSnapshot
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.InventoryFailure
                );
        }

        [Test]
        public void It_preserves_bounded_sanitized_failure_messages()
        {
            DocumentCacheTargetKey targetKey = TargetKey("TenantA", 7);
            string longUnsafeMessage = new string('x', 600) + "\r\n";
            DocumentCacheTargetDiagnostic diagnostic = Diagnostic(
                targetKey,
                DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                longUnsafeMessage
            );

            DocumentCacheDiagnosticSnapshot snapshot = DocumentCacheDiagnosticSnapshot.FromRegistrySnapshot(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.Unresolved(
                            targetKey,
                            _effectiveSettings,
                            RetryState(
                                DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                                longUnsafeMessage
                            ),
                            [diagnostic]
                        ),
                    ],
                    _observedAt
                )
            );

            DocumentCacheTargetDiagnostic projectedDiagnostic = snapshot
                .Targets.Single()
                .Diagnostics.Single();
            projectedDiagnostic.Message.Should().HaveLength(512);
            projectedDiagnostic.Message.Should().NotContain("\r").And.NotContain("\n");
            snapshot
                .Targets.Single()
                .RetryState!.LastFailureMessage.Should()
                .HaveLength(512)
                .And.NotContain("\r")
                .And.NotContain("\n");
        }

        private static DocumentCacheTargetKey TargetKey(string? tenantKey, long dataStoreId) =>
            DocumentCacheTargetKey.Create(tenantKey, dataStoreId);

        private static DocumentCacheTargetContextGeneration Generation(long value) => new(value);

        private static DocumentCacheResolutionRetryState RetryState(
            DocumentCacheTargetDiagnosticCategory category,
            string message
        ) =>
            new(
                attemptCount: 2,
                lastAttemptedAt: _observedAt,
                nextRetryAt: _observedAt + TimeSpan.FromSeconds(30),
                category,
                message
            );

        private static DocumentCacheTargetDiagnostic Diagnostic(
            DocumentCacheTargetKey targetKey,
            DocumentCacheTargetDiagnosticCategory category,
            string message
        ) =>
            new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                RelationalProviderToken.SqlServer,
                Generation(4),
                _fingerprint,
                _disabledLifecycleWithLatch,
                new DocumentCacheInventoryValidationResult(DocumentCacheInventoryStatus.Invalid, "Invalid."),
                _satisfiedEnqueueTrigger,
                FailedSqlServerPrerequisites(),
                retryState: null,
                category,
                message
            );

        private static DocumentCacheSqlServerPrerequisiteDetails FailedSqlServerPrerequisites() =>
            new(
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

        private sealed class StaticTargetRegistry(DocumentCacheTargetRegistrySnapshot currentSnapshot)
            : IDocumentCacheTargetRegistry
        {
            public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = currentSnapshot;

            public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
                new([], DateTimeOffset.UtcNow);

            public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
                DocumentCacheTargetRefreshReason reason,
                CancellationToken cancellationToken = default
            ) => throw new AssertionException("Diagnostic snapshots must not refresh target resolution.");
        }
    }
}
