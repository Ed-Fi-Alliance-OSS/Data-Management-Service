// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheAdministrativePrimitives")]
public class Given_DocumentCacheAdministrativePrimitives
{
    [Test]
    public void It_renders_provider_equivalent_writer_blocking_document_locks()
    {
        DocumentCacheAdministrativePrimitiveCommands pgsql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);
        DocumentCacheAdministrativePrimitiveCommands mssql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);

        pgsql
            .GuardedActivationDocumentLockCommandText.Should()
            .Be("""LOCK TABLE "dms"."Document" IN SHARE MODE;""");

        mssql
            .GuardedActivationDocumentLockCommandText.Should()
            .Contain("FROM [dms].[Document] WITH (TABLOCK, HOLDLOCK)");
    }

    [Test]
    public void It_renders_exclusive_state_row_transition_commands_with_expected_lifecycle_and_latch_guards()
    {
        DocumentCacheAdministrativePrimitiveCommands pgsql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);
        DocumentCacheAdministrativePrimitiveCommands mssql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);

        pgsql.ExclusiveLifecycleObservationCommandText.Should().Contain("FOR UPDATE");
        pgsql.TransitionLifecycleCommandText.Should().Contain("WHERE \"StateId\" = 1");
        pgsql
            .TransitionLifecycleCommandText.Should()
            .Contain("\"ProjectionLifecycleState\" = @expectedLifecycle");
        pgsql
            .TransitionLifecycleCommandText.Should()
            .Contain("\"CacheAheadRecoveryRequired\" = @expectedCacheAheadRecoveryRequired");
        pgsql.TransitionLifecycleCommandText.Should().Contain("RETURNING");

        mssql.ExclusiveLifecycleObservationCommandText.Should().Contain("WITH (XLOCK, HOLDLOCK)");
        mssql
            .TransitionLifecycleCommandText.Should()
            .Contain("UPDATE [dms].[DocumentCacheState] WITH (XLOCK, HOLDLOCK)");
        mssql.TransitionLifecycleCommandText.Should().Contain("OUTPUT inserted.[ProjectionLifecycleState]");
        mssql
            .TransitionLifecycleCommandText.Should()
            .Contain("[ProjectionLifecycleState] = @expectedLifecycle");
        mssql
            .TransitionLifecycleCommandText.Should()
            .Contain("[CacheAheadRecoveryRequired] = @expectedCacheAheadRecoveryRequired");
    }

    [Test]
    public async Task It_observes_lifecycle_through_the_mutex_session_executor()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ProjectionLifecycleState", "Tracking"),
                        ("CacheAheadRecoveryRequired", false)
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheLifecycleReadResult result =
            await DocumentCacheAdministrativePrimitivesSupport.ReadLifecycleAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql),
                DocumentCacheAdministrativeStateLockMode.Shared
            );

        result.Succeeded.Should().BeTrue();
        result
            .Lifecycle.Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("FOR SHARE");
    }

    [Test]
    public async Task It_reads_guarded_new_empty_state_without_using_a_second_result_shape()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("CanonicalDocumentsEmpty", true),
                        ("DocumentCacheEmpty", false),
                        ("DocumentProjectionWorkEmpty", true)
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheGuardedNewEmptyActivationState result =
            await DocumentCacheAdministrativePrimitivesSupport.ReadGuardedNewEmptyActivationStateAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql)
            );

        result.CanonicalDocumentsEmpty.Should().BeTrue();
        result.DocumentCacheEmpty.Should().BeFalse();
        result.DocumentProjectionWorkEmpty.Should().BeTrue();
        result.IsEmpty.Should().BeFalse();
        result.Message.Should().Contain("Guarded new-empty activation requires empty");
    }

    [Test]
    public async Task It_transitions_lifecycle_only_when_expected_lifecycle_and_latch_match()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ProjectionLifecycleState", "Tracking"),
                        ("CacheAheadRecoveryRequired", false)
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheAdministrativeLifecycleTransitionResult result =
            await DocumentCacheAdministrativePrimitivesSupport.TryTransitionLifecycleAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql),
                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                    DocumentCacheLifecycleState.Disabled,
                    expectedCacheAheadRecoveryRequired: false,
                    DocumentCacheLifecycleState.Tracking,
                    nextCacheAheadRecoveryRequired: false
                )
            );

        result.Mutated.Should().BeTrue();
        result
            .LifecycleReadResult.Lifecycle.Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
        executor.Commands.Should().ContainSingle();
        executor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal("Disabled", false, "Tracking", false);
    }

    [Test]
    public async Task It_rereads_current_exclusive_state_when_transition_guard_does_not_match()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ProjectionLifecycleState", "Resetting"),
                        ("CacheAheadRecoveryRequired", true)
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheAdministrativeLifecycleTransitionResult result =
            await DocumentCacheAdministrativePrimitivesSupport.TryTransitionLifecycleAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql),
                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                    DocumentCacheLifecycleState.Disabled,
                    expectedCacheAheadRecoveryRequired: false,
                    DocumentCacheLifecycleState.Tracking,
                    nextCacheAheadRecoveryRequired: false
                )
            );

        result.Mutated.Should().BeFalse();
        result
            .LifecycleReadResult.Lifecycle.Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, true));
        executor.Commands.Should().HaveCount(2);
        executor.Commands[1].CommandText.Should().Contain("WITH (XLOCK, HOLDLOCK)");
    }

    [Test]
    public async Task It_revalidates_sql_server_activation_prerequisites_on_the_mutex_session()
    {
        DocumentCacheAdministrativePrimitiveCommands commands =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(("ReadCommittedSnapshot", 0), ("NestedTriggers", 1))
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheProviderPrerequisiteValidationResult result =
            await DocumentCacheAdministrativePrimitivesSupport.ValidateActivationPrerequisitesAsync(
                session,
                commands
            );

        result.IsSatisfied.Should().BeFalse();
        result.FailureCategory.Should().Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
        result
            .SqlServerPrerequisites.NestedTriggers.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Satisfied);
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("FROM [sys].[databases]");
        executor.Commands[0].CommandText.Should().Contain("FROM [sys].[configurations]");
    }

    [Test]
    public async Task It_does_not_issue_activation_lifecycle_transition_when_sql_server_prerequisites_fail()
    {
        DocumentCacheAdministrativePrimitiveCommands commands =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(("ReadCommittedSnapshot", 0), ("NestedTriggers", 1))
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ProjectionLifecycleState", "Disabled"),
                        ("CacheAheadRecoveryRequired", false)
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheAdministrativeActivationTransitionResult result =
            await DocumentCacheAdministrativePrimitivesSupport.TryTransitionLifecycleAfterActivationPrerequisitesAsync(
                session,
                commands,
                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                    DocumentCacheLifecycleState.Disabled,
                    expectedCacheAheadRecoveryRequired: false,
                    DocumentCacheLifecycleState.Tracking,
                    nextCacheAheadRecoveryRequired: false
                )
            );

        result.Mutated.Should().BeFalse();
        result.ActivationPrerequisites.IsSatisfied.Should().BeFalse();
        executor.Commands.Should().HaveCount(2);
        executor.Commands[0].CommandText.Should().Contain("FROM [sys].[databases]");
        executor.Commands[1].CommandText.Should().Contain("WITH (XLOCK, HOLDLOCK)");
        executor
            .Commands.Should()
            .NotContain(command => command.CommandText.Contains("UPDATE [dms].[DocumentCacheState]"));
    }

    [Test]
    public async Task It_treats_postgresql_activation_prerequisites_as_not_applicable_without_sql()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheProviderPrerequisiteValidationResult result =
            await DocumentCacheAdministrativePrimitivesSupport.ValidateActivationPrerequisitesAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql)
            );

        result.IsSatisfied.Should().BeTrue();
        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
        executor.Commands.Should().BeEmpty();
    }

    private sealed class InMemoryAdministrativeSession(IRelationalCommandExecutor executor)
        : IRelationalWriteSession
    {
        private readonly IRelationalCommandExecutor _executor =
            executor ?? throw new ArgumentNullException(nameof(executor));

        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public IRelationalCommandExecutor CreateCommandExecutor() => _executor;

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
