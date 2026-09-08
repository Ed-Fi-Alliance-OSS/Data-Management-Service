// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Removal of the provider-side capture artifacts one binding generation governs. Only artifacts the
/// binding's own inventory names are touched, an artifact that is already gone is reported as not found
/// rather than as a failure, and nothing is removed that was not first observed.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcProviderArtifactTeardown")]
public class Given_CdcProviderArtifactTeardown
{
    [Test]
    public async Task It_drops_the_postgresql_publication_and_logical_slot_it_observed()
    {
        CdcArtifactInventory inventory = CdcSetupControllerHarness.Inventory();
        ICdcProviderDatabaseExecutor executor = Executor(present: true);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            CoreCdc.CdcProvider.Postgresql,
            inventory,
            executor
        );

        using var _ = new AssertionScope();
        Artifact(artifacts, CdcGovernedArtifactKind.PostgresqlPublication)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        Artifact(artifacts, CdcGovernedArtifactKind.PostgresqlLogicalSlot)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        Executed(executor)
            .Should()
            .ContainSingle(sql =>
                sql.StartsWith("DROP PUBLICATION", StringComparison.Ordinal)
                && sql.Contains(inventory.PostgresqlPublicationName!, StringComparison.Ordinal)
            );
        Executed(executor)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("pg_drop_replication_slot", StringComparison.Ordinal)
                && sql.Contains(inventory.PostgresqlLogicalSlotName!, StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// An artifact the provider does not report is not removed and not a failure: a retried retirement
    /// finds what the first one left and reports the rest as already gone.
    /// </summary>
    [Test]
    public async Task It_reports_absent_postgresql_artifacts_without_issuing_a_drop()
    {
        ICdcProviderDatabaseExecutor executor = Executor(present: false);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            CoreCdc.CdcProvider.Postgresql,
            CdcSetupControllerHarness.Inventory(),
            executor
        );

        using var _ = new AssertionScope();
        artifacts.Should().OnlyContain(artifact => artifact.CleanupState == CdcCleanupState.NotFound);
        Executed(executor).Should().BeEmpty();
    }

    /// <summary>
    /// SQL Server disables a capture instance by naming the source table it was created for, which the
    /// deployed schema supplies rather than the control plane guessing it.
    /// </summary>
    [Test]
    public async Task It_disables_every_sql_server_capture_instance_against_its_own_source_table()
    {
        CdcArtifactInventory inventory = CdcSetupControllerHarness.Inventory(CoreCdc.CdcProvider.SqlServer);
        ICdcProviderDatabaseExecutor executor = Executor(present: true);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            CoreCdc.CdcProvider.SqlServer,
            inventory,
            executor
        );

        using var _ = new AssertionScope();
        Artifact(artifacts, CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument)
            .ArtifactName.Should()
            .Be(inventory.SqlServerCaptureInstanceDocumentName);
        Artifact(artifacts, CdcGovernedArtifactKind.SqlServerCdcGatingRole)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);

        foreach (CdcSourceTableInventory source in SourceInventory())
        {
            Executed(executor)
                .Should()
                .ContainSingle(sql =>
                    sql.Contains("sp_cdc_disable_table", StringComparison.Ordinal)
                    && sql.Contains($"@source_name = N'{source.TableName.Name}'", StringComparison.Ordinal)
                );
        }

        // The role is emptied and dropped by one command: SQL Server refuses to drop a role that still
        // has members, and setup made the connector principal one of them.
        Executed(executor)
            .Should()
            .ContainSingle(sql =>
                sql.Contains("DROP MEMBER", StringComparison.Ordinal)
                && sql.Contains(
                    $"DROP ROLE [{inventory.SqlServerCdcGatingRoleName}]",
                    StringComparison.Ordinal
                )
            );
    }

    [Test]
    public async Task It_reports_absent_sql_server_artifacts_without_disabling_anything()
    {
        ICdcProviderDatabaseExecutor executor = Executor(present: false);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            CoreCdc.CdcProvider.SqlServer,
            CdcSetupControllerHarness.Inventory(CoreCdc.CdcProvider.SqlServer),
            executor
        );

        using var _ = new AssertionScope();
        artifacts.Should().HaveCount(4);
        artifacts.Should().OnlyContain(artifact => artifact.CleanupState == CdcCleanupState.NotFound);
        Executed(executor).Should().BeEmpty();
    }

    /// <summary>
    /// The binding record is made durable before the provider create pass runs, so an enablement
    /// interrupted between them leaves a record whose database never had CDC enabled and therefore has
    /// no <c>cdc</c> schema. SQL Server resolves a batch's names before running it, so querying the
    /// capture catalog there is a compile error rather than an empty result — and that error would end
    /// every retirement of such a binding in a provider failure, keeping the record forever.
    /// </summary>
    [Test]
    public async Task It_reports_the_capture_instances_absent_when_the_database_has_no_cdc_schema()
    {
        ICdcProviderDatabaseExecutor executor = Executor(present: true);
        A.CallTo(() =>
                executor.QueryAsync(
                    A<string>.That.Matches(sql => sql.Contains("is_cdc_enabled", StringComparison.Ordinal)),
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, string?>>>([]));

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            CoreCdc.CdcProvider.SqlServer,
            CdcSetupControllerHarness.Inventory(CoreCdc.CdcProvider.SqlServer),
            executor
        );

        using var _ = new AssertionScope();
        Artifact(artifacts, CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        Artifact(artifacts, CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        Artifact(artifacts, CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        Queried(executor)
            .Should()
            .NotContain(sql => sql.Contains("cdc.change_tables", StringComparison.Ordinal));
        Executed(executor)
            .Should()
            .NotContain(sql => sql.Contains("sp_cdc_disable_table", StringComparison.Ordinal));

        // The gating role is a database principal rather than a capture artifact, so it lives in a
        // catalog that is always there and is still asked about — and here it is still present.
        Artifact(artifacts, CdcGovernedArtifactKind.SqlServerCdcGatingRole)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
    }

    /// <summary>
    /// A capture instance whose source table the caller did not supply cannot be disabled, and the
    /// retirement fails rather than reporting an artifact it did not remove.
    /// </summary>
    [Test]
    public async Task It_refuses_a_capture_instance_whose_source_table_was_not_supplied()
    {
        ICdcProviderDatabaseExecutor executor = Executor(present: true);

        Func<Task> teardown = () =>
            RunAsync(
                CoreCdc.CdcProvider.SqlServer,
                CdcSetupControllerHarness.Inventory(CoreCdc.CdcProvider.SqlServer),
                executor,
                sourceInventory: []
            );

        await teardown.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// A provider that refuses a removal propagates: a partial teardown must leave the binding record
    /// intact rather than reporting an artifact as gone.
    /// </summary>
    [Test]
    public async Task It_propagates_a_provider_failure_rather_than_reporting_a_removal()
    {
        ICdcProviderDatabaseExecutor executor = Executor(present: true);
        A.CallTo(() => executor.ExecuteNonQueryAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("the replication slot is active"));

        Func<Task> teardown = () =>
            RunAsync(CoreCdc.CdcProvider.Postgresql, CdcSetupControllerHarness.Inventory(), executor);

        await teardown.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Task<IReadOnlyList<CdcGovernedArtifact>> RunAsync(
        CoreCdc.CdcProvider provider,
        CdcArtifactInventory inventory,
        ICdcProviderDatabaseExecutor executor,
        IReadOnlyList<CdcSourceTableInventory>? sourceInventory = null
    ) =>
        new CdcProviderArtifactTeardown(
            provider,
            NullLogger<CdcProviderArtifactTeardown>.Instance
        ).DeleteAsync(new(inventory, sourceInventory ?? SourceInventory(), executor), CancellationToken.None);

    private static IReadOnlyList<CdcSourceTableInventory> SourceInventory() =>
        CdcControlTemplateTestData.BuildSourceTableInventory(Ddl.CdcProvider.SqlServer);

    /// <summary>An executor whose existence queries all report the artifact present, or all absent.</summary>
    private static ICdcProviderDatabaseExecutor Executor(bool present)
    {
        ICdcProviderDatabaseExecutor executor = A.Fake<ICdcProviderDatabaseExecutor>();
        A.CallTo(() => executor.QueryAsync(A<string>._, A<CancellationToken>._))
            .Returns(
                Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, string?>>>(
                    present
                        ? [new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = "present" }]
                        : []
                )
            );

        return executor;
    }

    private static IReadOnlyList<string> Executed(ICdcProviderDatabaseExecutor executor) =>
        CommandText(executor, nameof(ICdcProviderDatabaseExecutor.ExecuteNonQueryAsync));

    private static IReadOnlyList<string> Queried(ICdcProviderDatabaseExecutor executor) =>
        CommandText(executor, nameof(ICdcProviderDatabaseExecutor.QueryAsync));

    private static IReadOnlyList<string> CommandText(
        ICdcProviderDatabaseExecutor executor,
        string methodName
    ) =>
        [
            .. Fake.GetCalls(executor)
                .Where(call => string.Equals(call.Method.Name, methodName, StringComparison.Ordinal))
                .Select(call => (string)call.Arguments[0]!),
        ];

    private static CdcGovernedArtifact Artifact(
        IReadOnlyList<CdcGovernedArtifact> artifacts,
        CdcGovernedArtifactKind kind
    ) => artifacts.Should().ContainSingle(artifact => artifact.ArtifactKind == kind).Subject;
}
