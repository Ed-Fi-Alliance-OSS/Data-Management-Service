// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Mssql;

public static class MssqlReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddMssqlReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteExceptionClassifier,
                MssqlRelationalWriteExceptionClassifier
            >()
        );

        return services.AddReferenceResolver<
            MssqlReferenceResolverAdapterFactory,
            MssqlRelationalCommandExecutor,
            MssqlRelationalWriteSessionFactory,
            MssqlDocumentHydrator,
            MssqlSessionDocumentHydrator
        >();
    }
}

internal sealed class MssqlReferenceResolverAdapterFactory(IRelationalCommandExecutor commandExecutor)
    : IReferenceResolverAdapterFactory
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public IReferenceResolverAdapter CreateAdapter()
    {
        return new MssqlReferenceResolverAdapter(_commandExecutor);
    }

    public IReferenceResolverAdapter CreateSessionAdapter(DbConnection connection, DbTransaction transaction)
    {
        return new MssqlReferenceResolverAdapter(
            new SessionRelationalCommandExecutor(connection, transaction)
        );
    }
}

internal sealed class MssqlDocumentHydrator : IDocumentHydrator
{
    private readonly Func<CancellationToken, Task<DbConnection>> _openConnectionAsync;

    public MssqlDocumentHydrator(IDmsInstanceSelection dmsInstanceSelection)
        : this(dmsInstanceSelection, connectionString => new SqlConnection(connectionString)) { }

    internal MssqlDocumentHydrator(
        IDmsInstanceSelection dmsInstanceSelection,
        Func<string, DbConnection> createConnection
    )
    {
        ArgumentNullException.ThrowIfNull(dmsInstanceSelection);
        ArgumentNullException.ThrowIfNull(createConnection);

        _openConnectionAsync = async cancellationToken =>
        {
            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();
            var connectionString = selectedInstance.ConnectionString;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Selected DMS instance '{selectedInstance.Id}' does not have a valid connection string."
                );
            }

            var connection = createConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        };
    }

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken ct
    )
    {
        await using var connection = await _openConnectionAsync(ct).ConfigureAwait(false);

        return await HydrationExecutor.ExecuteAsync(connection, plan, keyset, SqlDialect.Mssql, null, ct);
    }
}

internal sealed class MssqlSessionDocumentHydrator : ISessionDocumentHydrator
{
    public Task<HydratedPage> HydrateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken cancellationToken = default
    ) =>
        HydrationExecutor.ExecuteAsync(
            connection,
            plan,
            keyset,
            SqlDialect.Mssql,
            transaction,
            cancellationToken
        );
}
