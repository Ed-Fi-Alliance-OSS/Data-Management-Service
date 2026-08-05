// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Data.SqlClient;
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
    public async Task It_rejects_numeric_lifecycle_text_through_the_mutex_session_executor()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("ProjectionLifecycleState", "0"),
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

        result.Status.Should().Be(DocumentCacheLifecycleReadStatus.Invalid);
        result.Lifecycle.Should().BeNull();
        executor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_propagates_provider_command_timeout_from_lifecycle_read()
    {
        var exception = new TimeoutException("provider command timed out");
        var executor = new ThrowingRelationalCommandExecutor(exception);
        var session = new InMemoryAdministrativeSession(executor);

        Func<Task> act = () =>
            DocumentCacheAdministrativePrimitivesSupport.ReadLifecycleAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql),
                DocumentCacheAdministrativeStateLockMode.Shared
            );

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("provider command timed out");
        executor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_keeps_non_timeout_lifecycle_read_failures_as_unreadable()
    {
        var executor = new ThrowingRelationalCommandExecutor(new InvalidOperationException("boom"));
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheLifecycleReadResult result =
            await DocumentCacheAdministrativePrimitivesSupport.ReadLifecycleAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql),
                DocumentCacheAdministrativeStateLockMode.Shared
            );

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(DocumentCacheLifecycleReadStatus.Unreadable);
        result.Message.Should().Contain("unreadable");
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
    public async Task It_propagates_sql_server_provider_command_timeout_from_activation_prerequisite_validation()
    {
        DocumentCacheAdministrativePrimitiveCommands commands =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);
        var executor = new ThrowingRelationalCommandExecutor(
            CreateSqlException(-2, "Execution Timeout Expired."),
            SqlDialect.Mssql
        );
        var session = new InMemoryAdministrativeSession(executor);

        Func<Task> act = () =>
            DocumentCacheAdministrativePrimitivesSupport.ValidateActivationPrerequisitesAsync(
                session,
                commands
            );

        await act.Should().ThrowAsync<SqlException>().Where(exception => exception.Number == -2);
        executor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_keeps_non_timeout_activation_prerequisite_failures_as_unreadable()
    {
        DocumentCacheAdministrativePrimitiveCommands commands =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);
        var executor = new ThrowingRelationalCommandExecutor(new InvalidOperationException("boom"));
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
            .Be(DocumentCacheProviderPrerequisiteStatus.Unreadable);
        result
            .SqlServerPrerequisites.NestedTriggers.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Unreadable);
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

    [Test]
    public void It_renders_bounded_ordered_clear_commands_without_unbounded_delete_or_truncate()
    {
        DocumentCacheAdministrativePrimitiveCommands pgsql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);
        DocumentCacheAdministrativePrimitiveCommands mssql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);

        pgsql.ClearDocumentCacheBatchCommandText.Should().Contain("ORDER BY \"DocumentId\"");
        pgsql.ClearDocumentCacheBatchCommandText.Should().Contain("LIMIT @pageSize");
        pgsql.ClearDocumentCacheBatchCommandText.Should().Contain("DELETE FROM \"dms\".\"DocumentCache\"");
        pgsql
            .ClearDocumentProjectionWorkBatchCommandText.Should()
            .Contain("DELETE FROM \"dms\".\"DocumentProjectionWork\"");
        pgsql.ClearDocumentCacheBatchCommandText.Should().NotContain("TRUNCATE");

        mssql.ClearDocumentCacheBatchCommandText.Should().Contain("SELECT TOP (@pageSize) [DocumentId]");
        mssql.ClearDocumentCacheBatchCommandText.Should().Contain("ORDER BY [DocumentId]");
        mssql.ClearDocumentCacheBatchCommandText.Should().Contain("DELETE target");
        mssql
            .ClearDocumentProjectionWorkBatchCommandText.Should()
            .Contain("FROM [dms].[DocumentProjectionWork] AS target");
        mssql.ClearDocumentCacheBatchCommandText.Should().NotContain("TRUNCATE");
    }

    [Test]
    public void It_renders_baseline_boundary_high_water_and_page_seeding_commands()
    {
        DocumentCacheAdministrativePrimitiveCommands pgsql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);
        DocumentCacheAdministrativePrimitiveCommands mssql =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);

        pgsql.CaptureBaselineBoundaryCommandText.Should().Contain("MAX(\"DocumentId\")");
        mssql.CaptureBaselineBoundaryCommandText.Should().Contain("MAX([DocumentId])");

        pgsql.ObserveWorkHighWaterCommandText.Should().Contain("LIMIT @highWaterPlusOne");
        pgsql
            .ObserveWorkHighWaterCommandText.Should()
            .Contain("ORDER BY \"FirstEnqueuedAt\", \"DocumentId\"");
        mssql.ObserveWorkHighWaterCommandText.Should().Contain("SELECT TOP (@highWaterPlusOne)");
        mssql.ObserveWorkHighWaterCommandText.Should().Contain("ORDER BY [FirstEnqueuedAt], [DocumentId]");
        pgsql.ObserveWorkHighWaterCommandText.Should().NotContain("COUNT");
        mssql.ObserveWorkHighWaterCommandText.Should().NotContain("COUNT");

        pgsql.SeedBaselinePageCommandText.Should().Contain("FOR SHARE");
        pgsql.SeedBaselinePageCommandText.Should().Contain("ON CONFLICT (\"DocumentId\") DO UPDATE");
        pgsql
            .SeedBaselinePageCommandText.Should()
            .Contain("WHEN work.\"RequiredContentVersion\" < EXCLUDED.\"RequiredContentVersion\"");
        pgsql
            .SeedBaselinePageCommandText.Should()
            .Contain("work.\"RequiredContentVersion\" = candidate.\"PreviousRequiredContentVersion\"");
        pgsql
            .SeedBaselinePageCommandText.Should()
            .Contain(
                "observed.\"PreviousRequiredContentVersion\" IS DISTINCT FROM observed.\"SourceContentVersion\""
            );
        pgsql.SeedBaselinePageCommandText.Should().Contain("ELSE work.\"LastEnqueuedAt\"");
        pgsql.SeedBaselinePageCommandText.Should().NotContain("COUNT");

        mssql.SeedBaselinePageCommandText.Should().Contain("FROM [dms].[Document] AS source WITH (HOLDLOCK)");
        mssql
            .SeedBaselinePageCommandText.Should()
            .Contain("LEFT JOIN [dms].[DocumentProjectionWork] AS work WITH (UPDLOCK, HOLDLOCK)");
        mssql
            .SeedBaselinePageCommandText.Should()
            .Contain("work.[RequiredContentVersion] = observed.[PreviousRequiredContentVersion]");
        mssql.SeedBaselinePageCommandText.Should().Contain("ELSE work.[LastEnqueuedAt]");
        mssql.SeedBaselinePageCommandText.Should().NotContain("COUNT");
    }

    [Test]
    public async Task It_clears_a_bounded_cache_batch_through_the_mutex_session_executor()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(("DocumentId", 9L)),
                    RelationalAccessTestData.CreateRow(("DocumentId", 3L))
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheAdministrativeClearBatchResult result =
            await DocumentCacheAdministrativePrimitivesSupport.ClearDocumentCacheBatchAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql),
                new DocumentCacheAdministrativeClearBatchRequest(pageSize: 2)
            );

        result.Target.Should().Be(DocumentCacheAdministrativeClearTarget.DocumentCache);
        result.RowsCleared.Should().Be(2);
        result.FilledBatch.Should().BeTrue();
        result.Mutated.Should().BeTrue();
        result.ClearedDocumentIds.Should().Equal(3L, 9L);
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].Parameters.Should().ContainSingle();
        executor.Commands[0].Parameters[0].Value.Should().Be(2);
    }

    [Test]
    public async Task It_reads_baseline_boundary_high_water_and_seed_page_results()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(("BoundaryDocumentId", 25L))
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(("DocumentId", 3L)),
                    RelationalAccessTestData.CreateRow(("DocumentId", 7L))
                ),
            ]),
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 3L),
                        ("SourceContentVersion", 10L),
                        ("PreviousRequiredContentVersion", null),
                        ("MutationKind", "Inserted")
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 7L),
                        ("SourceContentVersion", 12L),
                        ("PreviousRequiredContentVersion", 15L),
                        ("MutationKind", "Lowered")
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);
        DocumentCacheAdministrativePrimitiveCommands commands =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);

        DocumentCacheAdministrativeBaselineBoundaryResult boundary =
            await DocumentCacheAdministrativePrimitivesSupport.CaptureBaselineBoundaryAsync(
                session,
                commands
            );
        DocumentCacheAdministrativeWorkHighWaterObservationResult highWater =
            await DocumentCacheAdministrativePrimitivesSupport.ObserveWorkHighWaterAsync(
                session,
                commands,
                new DocumentCacheAdministrativeWorkHighWaterObservationRequest(
                    highWaterMark: 2,
                    diagnosticCapacity: 1
                )
            );
        DocumentCacheAdministrativeBaselineSeedPageResult page =
            await DocumentCacheAdministrativePrimitivesSupport.SeedBaselinePageAsync(
                session,
                commands,
                new DocumentCacheAdministrativeBaselineSeedPageRequest(
                    boundaryDocumentId: 25,
                    afterDocumentId: 0,
                    pageSize: 2
                )
            );

        boundary.BoundaryDocumentId.Should().Be(25);
        highWater.ObservedWorkRows.Should().Be(2);
        highWater.IsAtOrAboveHighWater.Should().BeTrue();
        highWater.DiagnosticDocumentIds.Should().Equal(3L);
        page.Status.Should().Be(DocumentCacheAdministrativeBaselineSeedPageStatus.PageSeeded);
        page.Mutated.Should().BeTrue();
        page.WorkMutationCount.Should().Be(2);
        page.Documents.Select(document => document.MutationKind)
            .Should()
            .Equal(
                DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted,
                DocumentCacheAdministrativeBaselineWorkMutationKind.Lowered
            );
        executor.Commands[1].Parameters[0].Value.Should().Be(3);
        executor.Commands[2].Parameters.Select(parameter => parameter.Value).Should().Equal(25L, 0L, 2);
    }

    [Test]
    public async Task It_observes_projected_state_emptiness_without_exact_counts()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentCacheEmpty", false),
                        ("DocumentProjectionWorkEmpty", true)
                    )
                ),
            ]),
        ]);
        var session = new InMemoryAdministrativeSession(executor);

        DocumentCacheAdministrativeProjectedStateEmptinessResult result =
            await DocumentCacheAdministrativePrimitivesSupport.ReadProjectedStateEmptinessAsync(
                session,
                DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql)
            );

        result.DocumentCacheEmpty.Should().BeFalse();
        result.DocumentProjectionWorkEmpty.Should().BeTrue();
        result.CacheAndWorkEmpty.Should().BeFalse();
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("NOT EXISTS");
        executor.Commands[0].CommandText.Should().NotContain("COUNT");
    }

    [Test]
    public void It_requires_internal_only_proof_and_matching_offline_admission_before_work_clearing()
    {
        DocumentCacheAdministrativeWorkClearance valid = DocumentCacheAdministrativeWorkClearance.Require(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
        );

        valid.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineDeactivation);

        Action activePublication = () =>
            DocumentCacheAdministrativeWorkClearance.Require(
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                DocumentCacheDownstreamPublicationStatus.Active,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            );
        Action wrongConfirmation = () =>
            DocumentCacheAdministrativeWorkClearance.Require(
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            );
        Action onlineRebuild = () =>
            DocumentCacheAdministrativeWorkClearance.Require(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            );

        activePublication.Should().Throw<InvalidOperationException>();
        wrongConfirmation.Should().Throw<InvalidOperationException>();
        onlineRebuild.Should().Throw<InvalidOperationException>();
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

    private sealed class ThrowingRelationalCommandExecutor(
        Exception exception,
        SqlDialect dialect = SqlDialect.Pgsql
    ) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public List<RelationalCommand> Commands { get; } = [];

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            _ = readAsync;
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromException<TResult>(exception);
        }
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, message);

        var errorList = new List<object> { sqlError };
        var errorCollection = (SqlErrorCollection)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errorCollection, errorList);

        var sqlException = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, errorCollection);

        return sqlException;
    }
}
