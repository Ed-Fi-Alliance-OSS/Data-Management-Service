// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Old.Postgresql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Postgresql;

public static class PostgresqlReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresqlReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteExceptionClassifier,
                PostgresqlRelationalWriteExceptionClassifier
            >()
        );

        return services.AddReferenceResolver<
            PostgresqlReferenceResolverAdapterFactory,
            PostgresqlRelationalCommandExecutor,
            PostgresqlRelationalWriteSessionFactory,
            PostgresqlDocumentHydrator,
            PostgresqlSessionDocumentHydrator
        >();
    }
}

internal sealed class PostgresqlReferenceResolverAdapterFactory(IRelationalCommandExecutor commandExecutor)
    : IReferenceResolverAdapterFactory
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public IReferenceResolverAdapter CreateAdapter()
    {
        return new PostgresqlReferenceResolverAdapter(_commandExecutor);
    }

    public IReferenceResolverAdapter CreateSessionAdapter(DbConnection connection, DbTransaction transaction)
    {
        return new PostgresqlReferenceResolverAdapter(
            new SessionRelationalCommandExecutor(connection, transaction)
        );
    }
}

internal sealed class PostgresqlDocumentHydrator(NpgsqlDataSourceProvider dataSourceProvider)
    : IDocumentHydrator
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken ct
    )
    {
        await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(ct);

        return await HydrationExecutor.ExecuteAsync(connection, plan, keyset, SqlDialect.Pgsql, null, ct);
    }
}

internal sealed class PostgresqlSessionDocumentHydrator : ISessionDocumentHydrator
{
    public Task<HydratedPage> HydrateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken cancellationToken = default
    ) =>
        HydrationExecutor.ExecuteAsync(
            connection,
            plan,
            keyset,
            SqlDialect.Pgsql,
            transaction,
            executionOptions,
            cancellationToken
        );
}
