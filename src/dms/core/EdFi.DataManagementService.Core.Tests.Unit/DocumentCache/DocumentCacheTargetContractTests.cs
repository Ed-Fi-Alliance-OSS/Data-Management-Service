// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
public class DocumentCacheTargetContractTests
{
    private static DocumentCacheTargetKey TargetKey => DocumentCacheTargetKey.Create("TenantA", 7);

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 100,
            projectorMaxConcurrentTargets: 2,
            projectorFailureBackoff: TimeSpan.FromSeconds(30),
            projectorBaselineHighWaterMark: 1000
        );

    private static DocumentCacheInventoryValidationResult SatisfiedInventory =>
        new(DocumentCacheInventoryStatus.Satisfied, "Inventory satisfied.");

    private static DocumentCacheEnqueueTriggerValidationResult SatisfiedEnqueueTrigger =>
        new(DocumentCacheEnqueueTriggerStatus.Satisfied, "Enqueue trigger satisfied.");

    private static DocumentCacheLifecycleObservation TrackingLifecycle =>
        new(DocumentCacheLifecycleState.Tracking, CacheAheadRecoveryRequired: false);

    private static DocumentCachePhysicalSourceFingerprint Fingerprint => new("sha256:0123456789abcdef");

    private static DocumentCacheTargetContextGeneration Generation(long value) => new(value);

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCacheTarget_Observation_States : DocumentCacheTargetContractTests
    {
        [Test]
        public void It_should_represent_configured_targets_before_resolution()
        {
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.Configured(
                TargetKey,
                EffectiveSettings
            );

            observation.TargetKey.Should().Be(TargetKey);
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Configured);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.NotEvaluated);
            observation.Generation.Should().BeNull();
            observation.Diagnostics.Should().BeEmpty();
        }

        [Test]
        public void It_should_represent_unresolved_targets_with_retry_observations()
        {
            DocumentCacheResolutionRetryState retryState = new(
                attemptCount: 2,
                lastAttemptedAt: new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
                nextRetryAt: new DateTimeOffset(2026, 7, 29, 12, 0, 30, TimeSpan.Zero),
                lastFailureCategory: DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                lastFailureMessage: "CMS target not returned."
            );
            DocumentCacheTargetDiagnostic diagnostic = new(
                TargetKey,
                DocumentCacheTargetResolutionState.Unresolved,
                providerToken: null,
                generation: null,
                physicalSourceFingerprint: null,
                lifecycle: null,
                inventory: null,
                enqueueTrigger: null,
                sqlServerPrerequisites: null,
                retryState,
                DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
                "Target still unresolved."
            );

            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.Unresolved(
                TargetKey,
                EffectiveSettings,
                retryState,
                [diagnostic]
            );

            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Unresolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            observation.RetryState.Should().Be(retryState);
            observation.Diagnostics.Should().ContainSingle().Which.Should().Be(diagnostic);
        }

        [Test]
        public void It_should_represent_resolved_eligible_targets()
        {
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedEligible(
                TargetKey,
                EffectiveSettings,
                Generation(3),
                DocumentCacheRelationalProviderToken.Postgresql,
                Fingerprint,
                TrackingLifecycle,
                SatisfiedInventory,
                SatisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );

            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            observation.Generation.Should().Be(Generation(3));
            observation.ProviderToken.Should().Be(DocumentCacheRelationalProviderToken.Postgresql);
            observation.PhysicalSourceFingerprint.Should().Be(Fingerprint);
            observation.Lifecycle.Should().Be(TrackingLifecycle);
            observation.Inventory.Should().Be(SatisfiedInventory);
            observation.EnqueueTrigger.Should().Be(SatisfiedEnqueueTrigger);
            observation
                .SqlServerPrerequisites!.ReadCommittedSnapshot.Status.Should()
                .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
        }

        [Test]
        public void It_should_represent_resolved_ineligible_targets()
        {
            DocumentCacheTargetDiagnostic diagnostic = new(
                TargetKey,
                DocumentCacheTargetResolutionState.Resolved,
                DocumentCacheRelationalProviderToken.SqlServer,
                Generation(5),
                Fingerprint,
                new DocumentCacheLifecycleObservation(
                    DocumentCacheLifecycleState.Disabled,
                    CacheAheadRecoveryRequired: true
                ),
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Invalid,
                    "Inventory invalid."
                ),
                new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Disabled,
                    "Enqueue trigger disabled."
                ),
                new DocumentCacheSqlServerPrerequisiteDetails(
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
                ),
                retryState: null,
                DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                "Provider prerequisite failed."
            );

            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedIneligible(
                TargetKey,
                EffectiveSettings,
                Generation(5),
                DocumentCacheRelationalProviderToken.SqlServer,
                Fingerprint,
                diagnostic.Lifecycle,
                diagnostic.Inventory,
                diagnostic.EnqueueTrigger,
                diagnostic.SqlServerPrerequisites,
                retryState: null,
                [diagnostic]
            );

            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            observation
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
            observation.Lifecycle!.CacheAheadRecoveryRequired.Should().BeTrue();
        }

        [Test]
        public void It_should_represent_replaced_target_context_generations()
        {
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ReplacedGeneration(
                TargetKey,
                EffectiveSettings,
                Generation(4),
                replacedByGeneration: Generation(5),
                DocumentCacheRelationalProviderToken.Postgresql,
                Fingerprint,
                TrackingLifecycle,
                SatisfiedInventory,
                SatisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                diagnostics: []
            );

            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.ReplacedGeneration);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            observation.Generation.Should().Be(Generation(4));
            observation.ReplacedByGeneration.Should().Be(Generation(5));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCacheTarget_Value_Objects : DocumentCacheTargetContractTests
    {
        [Test]
        public void It_should_create_effective_settings_from_options()
        {
            DocumentCacheOptions options = new()
            {
                ReadAcceleration = new DocumentCacheReadAccelerationOptions
                {
                    Enabled = true,
                    DirectFillTimeout = TimeSpan.FromMilliseconds(125),
                },
                Projector = new DocumentCacheProjectorOptions
                {
                    PollInterval = TimeSpan.FromSeconds(2),
                    PageSize = 25,
                    MaxConcurrentTargets = 4,
                    FailureBackoff = TimeSpan.FromSeconds(15),
                    BaselineHighWaterMark = 250,
                },
            };

            DocumentCacheTargetEffectiveSettings settings = DocumentCacheTargetEffectiveSettings.FromOptions(
                options
            );

            settings.ReadAccelerationEnabled.Should().BeTrue();
            settings.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(125));
            settings.ProjectorPollInterval.Should().Be(TimeSpan.FromSeconds(2));
            settings.ProjectorPageSize.Should().Be(25);
            settings.ProjectorMaxConcurrentTargets.Should().Be(4);
            settings.ProjectorFailureBackoff.Should().Be(TimeSpan.FromSeconds(15));
            settings.ProjectorBaselineHighWaterMark.Should().Be(250);
        }

        [TestCase("postgresql", "postgresql")]
        [TestCase("POSTGRESQL", "postgresql")]
        [TestCase("sqlserver", "sqlserver")]
        [TestCase("SQLSERVER", "sqlserver")]
        public void It_should_normalize_supported_provider_tokens(string providerToken, string expectedValue)
        {
            bool normalized = DocumentCacheRelationalProviderToken.TryNormalize(
                providerToken,
                out DocumentCacheRelationalProviderToken? token
            );

            normalized.Should().BeTrue();
            token!.Value.Should().Be(expectedValue);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" postgresql ")]
        [TestCase("mysql")]
        public void It_should_reject_missing_blank_or_unknown_provider_tokens(string? providerToken)
        {
            bool normalized = DocumentCacheRelationalProviderToken.TryNormalize(
                providerToken,
                out DocumentCacheRelationalProviderToken? token
            );

            normalized.Should().BeFalse();
            token.Should().BeNull();
        }

        [Test]
        public void It_should_reject_nonpositive_generations()
        {
            Action createGeneration = () => _ = new DocumentCacheTargetContextGeneration(0);

            createGeneration.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCacheTarget_Lifecycle_And_Prerequisites : DocumentCacheTargetContractTests
    {
        [Test]
        public void It_should_include_all_lifecycle_states_and_keep_the_cache_ahead_latch_independent()
        {
            Enum.GetValues<DocumentCacheLifecycleState>()
                .Should()
                .BeEquivalentTo([
                    DocumentCacheLifecycleState.Disabled,
                    DocumentCacheLifecycleState.Resetting,
                    DocumentCacheLifecycleState.Rebuilding,
                    DocumentCacheLifecycleState.Tracking,
                ]);

            DocumentCacheLifecycleObservation observation = new(
                DocumentCacheLifecycleState.Tracking,
                CacheAheadRecoveryRequired: true
            );

            observation.State.Should().Be(DocumentCacheLifecycleState.Tracking);
            observation.CacheAheadRecoveryRequired.Should().BeTrue();
        }

        [Test]
        public void It_should_distinguish_provider_prerequisite_result_statuses()
        {
            Enum.GetValues<DocumentCacheProviderPrerequisiteStatus>()
                .Should()
                .BeEquivalentTo([
                    DocumentCacheProviderPrerequisiteStatus.Satisfied,
                    DocumentCacheProviderPrerequisiteStatus.Disabled,
                    DocumentCacheProviderPrerequisiteStatus.Unreadable,
                    DocumentCacheProviderPrerequisiteStatus.NotApplicable,
                ]);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCacheTarget_Diagnostics : DocumentCacheTargetContractTests
    {
        [Test]
        public void It_should_include_target_context_observations_and_sanitized_bounded_messages()
        {
            string longUnsafeMessage = new string('x', 600) + "\r\n";
            DocumentCacheResolutionRetryState retryState = new(
                attemptCount: 1,
                lastAttemptedAt: null,
                nextRetryAt: null,
                lastFailureCategory: DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                lastFailureMessage: longUnsafeMessage
            );
            DocumentCacheSqlServerPrerequisiteDetails prerequisites = new(
                new DocumentCacheProviderPrerequisiteResult(
                    DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                    DocumentCacheProviderPrerequisiteStatus.Unreadable,
                    "Unreadable\r\n"
                ),
                new DocumentCacheProviderPrerequisiteResult(
                    DocumentCacheProviderPrerequisiteName.NestedTriggers,
                    DocumentCacheProviderPrerequisiteStatus.Disabled,
                    "Disabled\r\n"
                )
            );

            DocumentCacheTargetDiagnostic diagnostic = new(
                TargetKey,
                DocumentCacheTargetResolutionState.Resolved,
                DocumentCacheRelationalProviderToken.SqlServer,
                Generation(9),
                Fingerprint,
                TrackingLifecycle,
                SatisfiedInventory,
                SatisfiedEnqueueTrigger,
                prerequisites,
                retryState,
                DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                longUnsafeMessage
            );

            diagnostic.TargetKey.Should().Be(TargetKey);
            diagnostic.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            diagnostic.ProviderToken.Should().Be(DocumentCacheRelationalProviderToken.SqlServer);
            diagnostic.Generation.Should().Be(Generation(9));
            diagnostic.PhysicalSourceFingerprint.Should().Be(Fingerprint);
            diagnostic.Lifecycle.Should().Be(TrackingLifecycle);
            diagnostic.Inventory.Should().Be(SatisfiedInventory);
            diagnostic.EnqueueTrigger.Should().Be(SatisfiedEnqueueTrigger);
            diagnostic.SqlServerPrerequisites.Should().Be(prerequisites);
            diagnostic.RetryState.Should().Be(retryState);
            diagnostic.Message.Should().HaveLength(512);
            diagnostic.Message.Should().NotContain("\r").And.NotContain("\n");
            diagnostic.RetryState!.LastFailureMessage.Should().HaveLength(512);
        }

        [Test]
        public void It_should_not_expose_raw_physical_detail_fields_in_contract_symbols()
        {
            string[] disallowedTerms =
            [
                "ConnectionString",
                "Credential",
                "Password",
                "TenantDisplayName",
                "Host",
                "Database",
                "DocumentBody",
            ];
            Type[] contractTypes = typeof(DocumentCacheTargetObservation)
                .Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(DocumentCacheTargetObservation).Namespace)
                .ToArray();

            string[] symbols = contractTypes
                .Select(type => type.Name)
                .Concat(
                    contractTypes.SelectMany(type =>
                        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                            .Select(property => property.Name)
                    )
                )
                .Concat(
                    contractTypes.SelectMany(type =>
                        type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                            .Select(field => field.Name)
                    )
                )
                .ToArray();

            symbols
                .Should()
                .NotContain(symbol =>
                    disallowedTerms.Any(term => symbol.Contains(term, StringComparison.OrdinalIgnoreCase))
                );
        }
    }
}
