// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Diagnostics;
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
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationshipAuthorizationProviderFailureExtractor,
                PostgresqlRelationshipAuthorizationProviderFailureExtractor
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

    public static IServiceCollection AddPostgresqlRelationalTokenInfoEducationOrganizationLookup(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(
            ServiceDescriptor.Scoped<
                IRelationalTokenInfoEducationOrganizationLookup,
                PostgresqlTokenInfoEducationOrganizationLookup
            >()
        );

        return services;
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
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        long openStart = Stopwatch.GetTimestamp();
        await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(ct);
        RequestTimingContext.Current?.Record(RequestTimingRegistry.DbPhases.OpenConnection, openStart);

        return await HydrationExecutor.ExecuteAsync(
            connection,
            plan,
            keyset,
            SqlDialect.Pgsql,
            transaction: null,
            executionOptions,
            ct
        );
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
