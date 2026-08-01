// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCachePrerequisite")]
public class DocumentCachePrerequisiteTests
{
    private static readonly DocumentCacheLifecycleObservation _disabledLifecycle = new(
        DocumentCacheLifecycleState.Disabled,
        CacheAheadRecoveryRequired: false
    );

    private static DocumentCacheSqlServerPrerequisiteDetails SatisfiedSqlServerPrerequisites =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server READ_COMMITTED_SNAPSHOT is enabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server nested triggers are enabled."
            )
        );

    private static DocumentCacheSqlServerPrerequisiteDetails DisabledReadCommittedSnapshot =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                "SQL Server READ_COMMITTED_SNAPSHOT is disabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server nested triggers are enabled."
            )
        );

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Provider_Prerequisite_Initialization : DocumentCachePrerequisiteTests
    {
        [Test]
        public void It_treats_satisfied_sqlserver_prerequisites_as_successful()
        {
            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    SatisfiedSqlServerPrerequisites,
                    new DocumentCacheLifecycleObservation(
                        DocumentCacheLifecycleState.Tracking,
                        CacheAheadRecoveryRequired: false
                    )
                );

            result.IsSatisfied.Should().BeTrue();
            result.FailureCategory.Should().BeNull();
            result.SqlServerPrerequisites.HasFailure.Should().BeFalse();
        }

        [Test]
        public void It_treats_postgresql_not_applicable_prerequisites_as_successful()
        {
            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                    new DocumentCacheLifecycleObservation(
                        DocumentCacheLifecycleState.Rebuilding,
                        CacheAheadRecoveryRequired: false
                    )
                );

            result.IsSatisfied.Should().BeTrue();
            result
                .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
                .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
            result
                .SqlServerPrerequisites.NestedTriggers.Status.Should()
                .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
        }

        [Test]
        public void It_classifies_disabled_lifecycle_failures_as_recoverable_provider_prerequisite_failures()
        {
            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    DisabledReadCommittedSnapshot,
                    _disabledLifecycle
                );

            result.IsSatisfied.Should().BeFalse();
            result
                .FailureCategory.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
            result.Message.Should().Contain("retried");
        }

        [TestCase(DocumentCacheLifecycleState.Tracking)]
        [TestCase(DocumentCacheLifecycleState.Rebuilding)]
        [TestCase(DocumentCacheLifecycleState.Resetting)]
        public void It_classifies_non_disabled_lifecycle_failures_as_unsupported_incidents(
            DocumentCacheLifecycleState lifecycleState
        )
        {
            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    DisabledReadCommittedSnapshot,
                    new DocumentCacheLifecycleObservation(lifecycleState, CacheAheadRecoveryRequired: false)
                );

            result.IsSatisfied.Should().BeFalse();
            result
                .FailureCategory.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Provider_Prerequisite_Activation_Preflight
        : DocumentCachePrerequisiteTests
    {
        [Test]
        public void It_classifies_command_local_failures_as_retryable_provider_prerequisite_failures()
        {
            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    DisabledReadCommittedSnapshot
                );

            result.IsSatisfied.Should().BeFalse();
            result
                .FailureCategory.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
            result.Message.Should().Contain("retry");
        }

        [Test]
        public void It_does_not_require_lifecycle_input_for_activation_preflight_success()
        {
            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    SatisfiedSqlServerPrerequisites
                );

            result.IsSatisfied.Should().BeTrue();
            result.FailureCategory.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Provider_Prerequisite_Contracts : DocumentCachePrerequisiteTests
    {
        [Test]
        public void It_rejects_mismatched_sqlserver_prerequisite_names()
        {
            Action create = () =>
                _ = new DocumentCacheSqlServerPrerequisiteDetails(
                    new DocumentCacheProviderPrerequisiteResult(
                        DocumentCacheProviderPrerequisiteName.NestedTriggers,
                        DocumentCacheProviderPrerequisiteStatus.Satisfied,
                        "Wrong slot."
                    ),
                    new DocumentCacheProviderPrerequisiteResult(
                        DocumentCacheProviderPrerequisiteName.NestedTriggers,
                        DocumentCacheProviderPrerequisiteStatus.Satisfied,
                        "Nested triggers are enabled."
                    )
                );

            create.Should().Throw<ArgumentException>();
        }

        [Test]
        public void It_sanitizes_and_bounds_validation_messages()
        {
            DocumentCacheSqlServerPrerequisiteDetails prerequisites = new(
                new DocumentCacheProviderPrerequisiteResult(
                    DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                    DocumentCacheProviderPrerequisiteStatus.Unreadable,
                    "Unreadable."
                ),
                new DocumentCacheProviderPrerequisiteResult(
                    DocumentCacheProviderPrerequisiteName.NestedTriggers,
                    DocumentCacheProviderPrerequisiteStatus.Satisfied,
                    "Satisfied."
                )
            );

            DocumentCacheProviderPrerequisiteValidationResult result =
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(prerequisites);

            result.Message.Should().NotContain("\r").And.NotContain("\n");
            result.Message.Length.Should().BeLessThanOrEqualTo(512);
        }
    }
}
