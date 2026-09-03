// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Records every connection string a provider was asked to realize - to build a data source for, or to
/// take a lease against.
/// </summary>
/// <remarks>
/// A configured derivative that is never used must cost nothing: no pool, no data source, no
/// acquisition. Asserting that a request did not <em>open</em> one is weaker, because a provider may
/// build a data source lazily and never connect. Recording the build and lease calls themselves is what
/// distinguishes "never opened" from "never created".
/// </remarks>
public sealed class DerivativeRealizationRecorder
{
    private readonly ConcurrentQueue<string> _realized = new();

    public IReadOnlyList<string> Realized => [.. _realized];

    public int CountFor(string connectionString) =>
        Realized.Count(realized => string.Equals(realized, connectionString, StringComparison.Ordinal));

    internal void Record(string connectionString) => _realized.Enqueue(connectionString);
}

internal sealed class RecordingNpgsqlDataSourceLifetime(DerivativeRealizationRecorder recorder)
    : INpgsqlDataSourceLifetime
{
    private readonly INpgsqlDataSourceLifetime _inner = NpgsqlDataSourceLifetime.Instance;

    public NpgsqlDataSource Build(string connectionString)
    {
        recorder.Record(connectionString);
        return _inner.Build(connectionString);
    }

    public Task<NpgsqlConnection> OpenConnectionAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken
    ) => _inner.OpenConnectionAsync(dataSource, cancellationToken);

    public void DisposeDataSource(NpgsqlDataSource dataSource) => _inner.DisposeDataSource(dataSource);
}

internal sealed class RecordingMssqlConnectionAcquisition(
    IMssqlConnectionAcquisition inner,
    DerivativeRealizationRecorder recorder
) : IMssqlConnectionAcquisition
{
    public Task<MssqlConnectionLease> AcquireLeaseAsync(
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken = default
    )
    {
        recorder.Record(target.ConnectionString);
        return inner.AcquireLeaseAsync(target, cancellationToken);
    }
}

public static class DerivativeRealizationRecorderServiceCollectionExtensions
{
    /// <summary>
    /// Substitutes the PostgreSQL data-source lifetime with a recording one, keeping the cache the very
    /// same singleton the reconciler is registered against.
    /// </summary>
    internal static void RecordPostgresqlRealization(
        this IServiceCollection services,
        DerivativeRealizationRecorder recorder
    )
    {
        services.AddSingleton(recorder);
        services.RemoveAll<NpgsqlDataSourceCache>();
        services.AddSingleton(_ => new NpgsqlDataSourceCache(
            NullLogger<NpgsqlDataSourceCache>.Instance,
            new RecordingNpgsqlDataSourceLifetime(recorder)
        ));
    }

    /// <summary>Wraps the SQL Server acquisition boundary so every lease is recorded.</summary>
    internal static void RecordMssqlRealization(
        this IServiceCollection services,
        DerivativeRealizationRecorder recorder
    )
    {
        ServiceDescriptor descriptor =
            services.LastOrDefault(service => service.ServiceType == typeof(IMssqlConnectionAcquisition))
            ?? throw new InvalidOperationException("No SQL Server acquisition boundary to record.");

        services.AddSingleton(recorder);
        services.Remove(descriptor);
        services.AddSingleton<IMssqlConnectionAcquisition>(
            serviceProvider => new RecordingMssqlConnectionAcquisition(
                (IMssqlConnectionAcquisition)(
                    descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(serviceProvider)
                    ?? ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!)
                ),
                recorder
            )
        );
    }
}
