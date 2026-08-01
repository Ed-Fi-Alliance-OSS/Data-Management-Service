// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_A_DocumentCacheSessionBoundWriterResult
{
    [Test]
    public void It_maps_retry_budget_exhaustion_before_command_mutation_to_failed_no_mutation()
    {
        DocumentCacheSessionBoundWriterResult result = DocumentCacheSessionBoundWriterResult.FromWriterResult(
            new DocumentCacheWriterResult.RetryBudgetExhausted(attemptCount: 3),
            commandExecutionMutated: false
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.WriterRetryBudgetExhausted);
        result
            .DiagnosticCategory.Should()
            .Be(DocumentCacheAdministrativeDiagnosticCategory.WriterRetryBudgetExhausted);
        result.Mutated.Should().BeFalse();
        result.WriterResult.Should().BeOfType<DocumentCacheWriterResult.RetryBudgetExhausted>();
    }

    [Test]
    public void It_maps_retry_budget_exhaustion_after_command_mutation_to_incomplete_retryable()
    {
        DocumentCacheSessionBoundWriterResult result = DocumentCacheSessionBoundWriterResult.FromWriterResult(
            new DocumentCacheWriterResult.RetryBudgetExhausted(attemptCount: 3),
            commandExecutionMutated: true
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.WriterRetryBudgetExhausted);
        result.Mutated.Should().BeTrue();
    }

    [Test]
    public void It_maps_delete_race_retry_exhaustion_before_command_mutation_to_failed_no_mutation()
    {
        DocumentCacheSessionBoundWriterResult result = DocumentCacheSessionBoundWriterResult.FromWriterResult(
            new DocumentCacheWriterResult.DeleteRaceRetryExhausted(attemptCount: 3),
            commandExecutionMutated: false
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.WriterRetryBudgetExhausted);
        result
            .DiagnosticCategory.Should()
            .Be(DocumentCacheAdministrativeDiagnosticCategory.WriterRetryBudgetExhausted);
        result.Mutated.Should().BeFalse();
        result.WriterResult.Should().BeOfType<DocumentCacheWriterResult.DeleteRaceRetryExhausted>();
    }

    [Test]
    public void It_maps_delete_race_retry_exhaustion_after_command_mutation_to_incomplete_retryable()
    {
        DocumentCacheSessionBoundWriterResult result = DocumentCacheSessionBoundWriterResult.FromWriterResult(
            new DocumentCacheWriterResult.DeleteRaceRetryExhausted(attemptCount: 3),
            commandExecutionMutated: true
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.WriterRetryBudgetExhausted);
        result
            .DiagnosticCategory.Should()
            .Be(DocumentCacheAdministrativeDiagnosticCategory.WriterRetryBudgetExhausted);
        result.Mutated.Should().BeTrue();
    }

    [Test]
    public void It_classifies_session_loss_from_the_current_command_mutation_flag()
    {
        DocumentCacheSessionBoundWriterResult result = DocumentCacheSessionBoundWriterResult.SessionLoss(
            commandExecutionMutated: true,
            "session closed"
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        result.DiagnosticCategory.Should().Be(DocumentCacheAdministrativeDiagnosticCategory.SessionLoss);
        result.Mutated.Should().BeTrue();
        result.HasWriterResult.Should().BeFalse();
    }

    [Test]
    public void It_classifies_provider_command_timeout_without_prior_mutation_as_failed_no_mutation()
    {
        DocumentCacheSessionBoundWriterResult result =
            DocumentCacheSessionBoundWriterResult.ProviderCommandTimeout(
                commandExecutionMutated: false,
                "timeout"
            );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result
            .DiagnosticCategory.Should()
            .Be(DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout);
        result.Mutated.Should().BeFalse();
    }

    [Test]
    public void It_marks_successful_writer_acknowledgement_as_mutated()
    {
        DocumentCacheSessionBoundWriterResult result = DocumentCacheSessionBoundWriterResult.FromWriterResult(
            new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(acknowledgedContentVersion: 12),
            commandExecutionMutated: false
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.DiagnosticCategory.Should().BeNull();
        result.Mutated.Should().BeTrue();
    }
}
