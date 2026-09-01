// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Fingerprint validation and resource-key validation are the first two database touches of any
/// request, and they run against whichever target selection chose. Both must reach that database
/// through the leased data-source cache, because that is the only place a derivative's pool is built,
/// retired, and disposed with its configuration; a connection opened straight from the target's
/// connection string would put the first read of a snapshot or read replica outside every one of those
/// rules.
/// </summary>
/// <remarks>
/// The substituted lifetime builds each data source against a different endpoint than the caller asked
/// for. That is what makes the question answerable without a database: the address a read failed
/// against says where its connection came from, and the lifetime's own open count says whether the
/// connection was taken from the leased data source at all. Neither read can complete, which is the
/// point - what each did on the way is the behavior under test.
/// </remarks>
[TestFixture]
public class Given_A_Postgresql_Effective_Target_Validation_Read
{
    /// <summary>
    /// A loopback port nothing listens on, so a read that opens a real connection is refused
    /// immediately and without a name to resolve.
    /// </summary>
    private const string TargetEndpoint = "127.0.0.1:1";

    /// <summary>
    /// The endpoint the cache's data sources are actually built against. Different from the target's,
    /// so a connection taken from a leased data source and one constructed from the target's own
    /// connection string fail against addresses that can be told apart.
    /// </summary>
    private const string LeasedEndpoint = "127.0.0.1:2";

    private const string SnapshotConnectionString =
        "Host=127.0.0.1;Port=1;Database=dms;Username=u;Password=p;Timeout=1";

    private static readonly EffectiveDataStoreTarget _target = new(
        EffectiveTargetKind.Snapshot,
        SnapshotConnectionString
    );

    private ValidationReadFacts _fingerprintRead = null!;
    private ValidationReadFacts _resourceKeyRead = null!;

    [SetUp]
    public async Task Setup()
    {
        _fingerprintRead = await RunAsync(cache =>
            new PostgresqlDatabaseFingerprintReader(
                cache,
                NullLogger<PostgresqlDatabaseFingerprintReader>.Instance
            ).ReadFingerprintAsync(_target)
        );

        _resourceKeyRead = await RunAsync(cache =>
            new PostgresqlResourceKeyRowReader(
                cache,
                NullLogger<PostgresqlResourceKeyRowReader>.Instance
            ).ReadResourceKeyRowsAsync(_target)
        );
    }

    [Test]
    public void It_leases_the_fingerprint_reads_data_source_from_the_cache()
    {
        _fingerprintRead
            .Requested.Should()
            .Equal(
                [SnapshotConnectionString],
                "the fingerprint read must reach its target through the leased cache, keyed on the "
                    + "configured connection string rather than any realized form of it"
            );
    }

    [Test]
    public void It_leases_the_resource_key_reads_data_source_from_the_cache()
    {
        _resourceKeyRead.Requested.Should().ContainSingle().Which.Should().Be(SnapshotConnectionString);
    }

    /// <summary>
    /// The address proves the connection came from the leased data source. A connection constructed
    /// from the target's connection string would have failed against the target's own endpoint, which
    /// is exactly the reversion this guards.
    /// </summary>
    [Test]
    public void It_opens_the_fingerprint_reads_connection_from_the_leased_data_source()
    {
        _fingerprintRead.Failure!.Message.Should().Contain(LeasedEndpoint);
        _fingerprintRead
            .Failure!.Message.Should()
            .NotContain(
                TargetEndpoint,
                "a connection built from the target's own connection string would name the target's "
                    + "endpoint rather than the leased data source's"
            );
    }

    /// <summary>
    /// The resource-key read takes its connection from the data source rather than constructing one,
    /// so the lifetime's open is the direct observation of the same property.
    /// </summary>
    [Test]
    public void It_opens_the_resource_key_reads_connection_from_the_leased_data_source()
    {
        _resourceKeyRead.Opens.Should().Be(1);
    }

    [Test]
    public void It_releases_the_fingerprint_reads_lease_even_though_the_read_failed()
    {
        _fingerprintRead
            .DisposalsOfTheBuiltDataSource.Should()
            .Be(
                1,
                "releasing the last lease on an unconfigured target disposes its data source, so a "
                    + "disposal here is the proof the lease was given back rather than stranded"
            );
    }

    [Test]
    public void It_releases_the_resource_key_reads_lease_even_though_the_read_failed()
    {
        _resourceKeyRead.DisposalsOfTheBuiltDataSource.Should().Be(1);
    }

    [Test]
    public void It_reaches_a_database_that_is_not_there_rather_than_succeeding_vacuously()
    {
        _fingerprintRead
            .Failure.Should()
            .NotBeNull("a read that completed would mean it never tried to use the connection");
        _resourceKeyRead.Failure.Should().NotBeNull();
    }

    [Test]
    public void It_does_no_provider_work_under_the_caches_state_lock()
    {
        _fingerprintRead.LockViolations.Should().BeEmpty();
        _resourceKeyRead.LockViolations.Should().BeEmpty();
    }

    /// <summary>
    /// What one validation read did to the cache: the connection strings it asked the cache for,
    /// whether the data source built for it was disposed - measured before the cache itself is
    /// disposed, so a stranded lease cannot be mistaken for a released one - how many connections it
    /// took from that data source, any lock violation, and what the read threw.
    /// </summary>
    private sealed record ValidationReadFacts(
        IReadOnlyList<string> Requested,
        int DisposalsOfTheBuiltDataSource,
        int Opens,
        IReadOnlyCollection<string> LockViolations,
        Exception? Failure
    );

    private static async Task<ValidationReadFacts> RunAsync(Func<NpgsqlDataSourceCache, Task> read)
    {
        List<string> requested = [];
        int opens = 0;

        using GatedNpgsqlDataSourceLifetime inner = new()
        {
            OnOpen = (_, _) =>
            {
                opens++;
                return Task.CompletedTask;
            },
        };

        RedirectingLifetime lifetime = new(inner, requested);
        NpgsqlDataSourceCache cache = new(
            NullLogger<NpgsqlDataSourceCache>.Instance,
            lifetime,
            inner.ReceiveStateLockProbe
        );

        Exception? failure = null;

        try
        {
            await read(cache);
        }
        catch (Exception exception)
        {
            // Nothing is behind the substituted lifetime, so the read cannot complete. The exception is
            // kept rather than swallowed, because a read that did not fail would not have used its
            // connection at all - and because the address it names is the evidence.
            failure = exception;
        }

        // Read before the cache is disposed. Disposing it would dispose every entry, including one a
        // stranded lease is still holding, which is exactly the failure this measurement must catch.
        int disposals = inner.Built.Count == 0 ? 0 : inner.DisposeCountOf(inner.Built[0]);

        ValidationReadFacts facts = new(requested, disposals, opens, inner.LockViolations, failure);

        cache.Dispose();

        return facts;
    }

    /// <summary>
    /// Records what the cache was asked to build and then builds it against a different endpoint, so
    /// the two possible sources of a connection - the leased data source and the target's own
    /// connection string - can be told apart by the address a failed read names.
    /// </summary>
    private sealed class RedirectingLifetime(INpgsqlDataSourceLifetime inner, List<string> requested)
        : INpgsqlDataSourceLifetime
    {
        public NpgsqlDataSource Build(string connectionString)
        {
            requested.Add(connectionString);

            return inner.Build(connectionString.Replace("Port=1", "Port=2", StringComparison.Ordinal));
        }

        public Task<NpgsqlConnection> OpenConnectionAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken
        ) => inner.OpenConnectionAsync(dataSource, cancellationToken);

        public void DisposeDataSource(NpgsqlDataSource dataSource) => inner.DisposeDataSource(dataSource);
    }
}
