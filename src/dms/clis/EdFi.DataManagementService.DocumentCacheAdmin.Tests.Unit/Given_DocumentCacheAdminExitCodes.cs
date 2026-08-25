// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("ExitCode")]
public sealed class Given_DocumentCacheAdminExitCodes
{
    [Test]
    public void It_publishes_the_stable_story_exit_code_values()
    {
        DocumentCacheAdminExitCodes.Success.Should().Be(0);
        DocumentCacheAdminExitCodes.UnexpectedFailure.Should().Be(1);
        DocumentCacheAdminExitCodes.RejectedNoMutation.Should().Be(10);
        DocumentCacheAdminExitCodes.FailedNoMutation.Should().Be(11);
        DocumentCacheAdminExitCodes.IncompleteRetryable.Should().Be(12);
        DocumentCacheAdminExitCodes.ArgumentError.Should().Be(64);
        DocumentCacheAdminExitCodes.ConfigurationError.Should().Be(78);
    }

    [TestCase(
        DocumentCacheAdministrativeCommandStatus.Completed,
        DocumentCacheAdministrativeCommandClassification.Succeeded,
        false,
        DocumentCacheAdminExitCodes.Success
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
        false,
        DocumentCacheAdminExitCodes.RejectedNoMutation
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
        DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
        false,
        DocumentCacheAdminExitCodes.FailedNoMutation
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
        DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled,
        false,
        DocumentCacheAdminExitCodes.FailedNoMutation
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
        DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout,
        false,
        DocumentCacheAdminExitCodes.FailedNoMutation
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
        DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation,
        true,
        DocumentCacheAdminExitCodes.IncompleteRetryable
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
        DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation,
        true,
        DocumentCacheAdminExitCodes.IncompleteRetryable
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
        DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
        true,
        DocumentCacheAdminExitCodes.IncompleteRetryable
    )]
    public void It_maps_typed_administrative_results_to_stable_exit_codes(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated,
        int expectedExitCode
    )
    {
        DocumentCacheAdministrativeCommandResult result = Result(status, classification, mutated);

        DocumentCacheAdminExitCodeMapper.ForAdministrativeCommandResult(result).Should().Be(expectedExitCode);
    }

    [Test]
    public void It_maps_unclassified_administrative_statuses_to_unexpected_failure()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            (DocumentCacheAdministrativeCommandStatus)999,
            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
            mutated: false
        );

        DocumentCacheAdminExitCodeMapper
            .ForAdministrativeCommandResult(result)
            .Should()
            .Be(DocumentCacheAdminExitCodes.UnexpectedFailure);
    }

    [Test]
    public void It_uses_the_typed_status_without_parsing_classification_message_text()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
            mutated: true
        );

        DocumentCacheAdminExitCodeMapper
            .ForAdministrativeCommandResult(result)
            .Should()
            .Be(DocumentCacheAdminExitCodes.Success);
    }

    private static DocumentCacheAdministrativeCommandResult Result(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated
    ) =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey("", 1),
            status,
            classification,
            mutated
        );
}
