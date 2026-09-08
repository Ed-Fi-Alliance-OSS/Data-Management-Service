// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Every baseline read-path seam classifies connection acquisition, and classifies it the same way.
/// This is the exhaustive per-seam evidence: the integration tests prove the end-to-end 404, but every
/// seam produces the same one, so a response cannot say which seam raised it.
/// </summary>
/// <remarks>
/// Cancellation coverage lives here rather than end to end, because no request-scoped token reaches
/// these seams today. The fingerprint seam is absent from the cancellation cases by design: it takes no
/// token at all, so no cancellation there is attributable to a caller - what it must still guarantee is
/// that a cancellation-shaped failure is not translated, which the classification cases below cover.
/// </remarks>
[TestFixture]
[Parallelizable]
public class SeamConnectionGuardTests
{
    /// <summary>
    /// Syntactically valid and pointed at a port nothing listens on, so nothing here can reach a
    /// database even if a double stopped refusing.
    /// </summary>
    private const string PostgresqlConnectionString =
        "Host=127.0.0.1;Port=1;Database=dms;Username=u;Password=p;Timeout=1";

    private const string MssqlConnectionString =
        "Server=127.0.0.1,1;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;Connect Timeout=1";

    private static readonly EffectiveTargetKind[] _kindsThatMustNotTranslate =
    [
        EffectiveTargetKind.Primary,
        EffectiveTargetKind.ReadReplica,
    ];

    /// <summary>
    /// One seam, exercised end to end from its own entry point: given a target kind and a token, it
    /// reaches its acquisition and fails there.
    /// </summary>
    public sealed record Seam(string Name, Func<EffectiveTargetKind, CancellationToken, Task> ExerciseAsync)
    {
        public override string ToString() => Name;
    }

    // ---- PostgreSQL -------------------------------------------------------------------------

    /// <summary>
    /// Fails every build with an expected provider failure, so a seam's acquisition fails where the
    /// connection string is parsed - which is the half of the boundary a wrap around the open alone
    /// would miss. When a token is supplied and already cancelled, the open reports that instead.
    /// </summary>
    private sealed class RefusingNpgsqlLifetime(Exception? buildFailure) : INpgsqlDataSourceLifetime
    {
        public NpgsqlDataSource Build(string connectionString) =>
            buildFailure is null ? new NpgsqlDataSourceBuilder(connectionString).Build() : throw buildFailure;

        public Task<NpgsqlConnection> OpenConnectionAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromException<NpgsqlConnection>(new NpgsqlException("connection refused"));
        }

        public void DisposeDataSource(NpgsqlDataSource dataSource) => dataSource.Dispose();
    }

    private static NpgsqlDataSourceCache PostgresqlCache(Exception? buildFailure) =>
        new(NullLogger<NpgsqlDataSourceCache>.Instance, new RefusingNpgsqlLifetime(buildFailure));

    private static NpgsqlDataSourceProvider PostgresqlProvider(
        NpgsqlDataSourceCache cache,
        EffectiveTargetKind targetKind
    ) =>
        new(
            SelectionOf(new EffectiveDataStoreTarget(targetKind, PostgresqlConnectionString)),
            cache,
            NullLogger<NpgsqlDataSourceProvider>.Instance
        );

    private static Seam PostgresqlFingerprintReader(Exception? buildFailure) =>
        new(
            "PostgreSQL fingerprint reader",
            (targetKind, _) =>
                new PostgresqlDatabaseFingerprintReader(
                    PostgresqlCache(buildFailure),
                    NullLogger<PostgresqlDatabaseFingerprintReader>.Instance
                ).ReadFingerprintAsync(new EffectiveDataStoreTarget(targetKind, PostgresqlConnectionString))
        );

    private static Seam PostgresqlResourceKeyRowReader(Exception? buildFailure) =>
        new(
            "PostgreSQL resource-key row reader",
            (targetKind, cancellationToken) =>
                new PostgresqlResourceKeyRowReader(
                    PostgresqlCache(buildFailure),
                    NullLogger<PostgresqlResourceKeyRowReader>.Instance
                ).ReadResourceKeyRowsAsync(
                    new EffectiveDataStoreTarget(targetKind, PostgresqlConnectionString),
                    cancellationToken
                )
        );

    private static Seam PostgresqlCommandExecutor(Exception? buildFailure) =>
        new(
            "PostgreSQL relational command executor",
            (targetKind, cancellationToken) =>
                new PostgresqlRelationalCommandExecutor(
                    PostgresqlProvider(PostgresqlCache(buildFailure), targetKind),
                    NullLogger<PostgresqlRelationalCommandExecutor>.Instance
                ).ExecuteReaderAsync(
                    new RelationalCommand("select 1", []),
                    (_, _) => Task.FromResult(0),
                    cancellationToken
                )
        );

    private static Seam PostgresqlDocumentHydrator(Exception? buildFailure) =>
        new(
            "PostgreSQL document hydrator",
            (targetKind, cancellationToken) =>
                PostgresqlReferenceResolverTestAccess.HydrateAsync(
                    PostgresqlProvider(PostgresqlCache(buildFailure), targetKind),
                    cancellationToken
                )
        );

    // ---- SQL Server -------------------------------------------------------------------------

    /// <summary>
    /// Refuses to produce a lease, with an expected provider failure. Acquisition is where SQL Server
    /// realizes the derivative's effective connection string, so this is the parsing half of the
    /// boundary as well as the open.
    /// </summary>
    private sealed class RefusingMssqlAcquisition : IMssqlConnectionAcquisition
    {
        public Task<MssqlConnectionLease> AcquireLeaseAsync(
            EffectiveDataStoreTarget target,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromException<MssqlConnectionLease>(new StubDbException("login failed"));
        }
    }

    private static Seam MssqlFingerprintReader() =>
        new(
            "SQL Server fingerprint reader",
            (targetKind, _) =>
                new MssqlDatabaseFingerprintReader(
                    new RefusingMssqlAcquisition(),
                    NullLogger<MssqlDatabaseFingerprintReader>.Instance
                ).ReadFingerprintAsync(new EffectiveDataStoreTarget(targetKind, MssqlConnectionString))
        );

    private static Seam MssqlResourceKeyRowReader() =>
        new(
            "SQL Server resource-key row reader",
            (targetKind, cancellationToken) =>
                new MssqlResourceKeyRowReader(
                    new RefusingMssqlAcquisition(),
                    NullLogger<MssqlResourceKeyRowReader>.Instance
                ).ReadResourceKeyRowsAsync(
                    new EffectiveDataStoreTarget(targetKind, MssqlConnectionString),
                    cancellationToken
                )
        );

    private static Seam MssqlCommandExecutor() =>
        new(
            "SQL Server relational command executor",
            (targetKind, cancellationToken) =>
                new MssqlRelationalCommandExecutor(
                    SelectionOf(new EffectiveDataStoreTarget(targetKind, MssqlConnectionString)),
                    new RefusingMssqlAcquisition(),
                    NullLogger<MssqlRelationalCommandExecutor>.Instance
                ).ExecuteReaderAsync(
                    new RelationalCommand("select 1", []),
                    (_, _) => Task.FromResult(0),
                    cancellationToken
                )
        );

    private static Seam MssqlDocumentHydrator() =>
        new(
            "SQL Server document hydrator",
            (targetKind, cancellationToken) =>
                MssqlReferenceResolverTestAccess.HydrateAsync(
                    SelectionOf(new EffectiveDataStoreTarget(targetKind, MssqlConnectionString)),
                    new RefusingMssqlAcquisition(),
                    cancellationToken
                )
        );

    // ---- The seam sets ----------------------------------------------------------------------

    /// <summary>All seven baseline seams, each failing where its connection string is parsed.</summary>
    private static IEnumerable<Seam> AllSeams()
    {
        yield return PostgresqlFingerprintReader(new NpgsqlException("unknown keyword"));
        yield return PostgresqlResourceKeyRowReader(new NpgsqlException("unknown keyword"));
        yield return PostgresqlCommandExecutor(new NpgsqlException("unknown keyword"));
        yield return PostgresqlDocumentHydrator(new NpgsqlException("unknown keyword"));
        yield return MssqlFingerprintReader();
        yield return MssqlResourceKeyRowReader();
        yield return MssqlCommandExecutor();
        yield return MssqlDocumentHydrator();
    }

    /// <summary>
    /// The seams that accept a token. The PostgreSQL builds succeed here so acquisition reaches the
    /// open, which is where a supplied token is observed.
    /// </summary>
    private static IEnumerable<Seam> SeamsThatAcceptAToken()
    {
        yield return PostgresqlResourceKeyRowReader(buildFailure: null);
        yield return PostgresqlCommandExecutor(buildFailure: null);
        yield return PostgresqlDocumentHydrator(buildFailure: null);
        yield return MssqlResourceKeyRowReader();
        yield return MssqlCommandExecutor();
        yield return MssqlDocumentHydrator();
    }

    [TestCaseSource(nameof(AllSeams))]
    public async Task It_reports_an_unavailable_snapshot(Seam seam)
    {
        Func<Task> exercise = () => seam.ExerciseAsync(EffectiveTargetKind.Snapshot, CancellationToken.None);

        var thrown = await exercise.Should().ThrowAsync<DatabaseConnectionUnavailableException>();

        thrown.Which.TargetKind.Should().Be(EffectiveTargetKind.Snapshot);
        thrown.Which.InnerException.Should().NotBeNull("the provider failure is retained for diagnostics");
    }

    /// <summary>
    /// The same failure on the two kinds whose contracts must not move propagates as the provider
    /// raised it. For SQL Server that means it is still a DbException, which is what keeps the
    /// write-failure mapper and the custom-view wrappers firing.
    /// </summary>
    [Test]
    public async Task It_propagates_the_same_failure_on_a_primary_or_a_read_replica()
    {
        foreach (Seam seam in AllSeams())
        {
            foreach (EffectiveTargetKind targetKind in _kindsThatMustNotTranslate)
            {
                Func<Task> exercise = () => seam.ExerciseAsync(targetKind, CancellationToken.None);

                (await exercise.Should().ThrowAsync<Exception>())
                    .Which.Should()
                    .NotBeOfType<DatabaseConnectionUnavailableException>(
                        $"{seam} must not translate a {targetKind} failure"
                    );
            }
        }
    }

    /// <summary>
    /// A cancellation the caller asked for is not evidence of a missing snapshot, so it propagates as
    /// itself for every kind alike - including the snapshot, where translation would otherwise apply.
    /// </summary>
    [TestCaseSource(nameof(SeamsThatAcceptAToken))]
    public async Task It_propagates_a_cancellation_for_every_kind(Seam seam)
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        foreach (
            EffectiveTargetKind targetKind in (EffectiveTargetKind[])
                [EffectiveTargetKind.Primary, EffectiveTargetKind.ReadReplica, EffectiveTargetKind.Snapshot]
        )
        {
            Func<Task> exercise = () => seam.ExerciseAsync(targetKind, cancelled.Token);

            await exercise
                .Should()
                .ThrowAsync<OperationCanceledException>($"{seam} on a {targetKind} target");
        }
    }

    private static IDataStoreSelection SelectionOf(EffectiveDataStoreTarget target)
    {
        var selection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => selection.GetEffectiveTarget()).Returns(target);
        return selection;
    }

    private sealed class StubDbException(string message) : DbException(message);
}

/// <summary>
/// The PostgreSQL document hydrator is constructed by an internal factory rather than exposed
/// directly, so this reaches it the same way the reference-resolver registration does. The plan and
/// keyset are null because the connection is opened before either is read.
/// </summary>
internal static class PostgresqlReferenceResolverTestAccess
{
    public static Task HydrateAsync(
        NpgsqlDataSourceProvider dataSourceProvider,
        CancellationToken cancellationToken
    ) =>
        new PostgresqlDocumentHydrator(
            dataSourceProvider,
            NullLogger<PostgresqlDocumentHydrator>.Instance
        ).HydrateAsync(null!, null!, new HydrationExecutionOptions(), cancellationToken);
}
