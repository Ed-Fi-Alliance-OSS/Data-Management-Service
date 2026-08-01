// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
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
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IDocumentCacheMaterializationDataStore,
                PostgresqlDocumentCacheMaterializationDataStore
            >()
        );
        services.TryAdd(ServiceDescriptor.Scoped<IDocumentCacheWriter, PostgresqlDocumentCacheWriter>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheSessionBoundWriter, PostgresqlDocumentCacheWriter>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentProjectionWorkPager, PostgresqlDocumentProjectionWorkPager>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativeMutex,
                PostgresqlDocumentCacheAdministrativeMutex
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativePrimitives,
                PostgresqlDocumentCacheAdministrativePrimitives
            >()
        );

        services.AddReferenceResolver<
            PostgresqlReferenceResolverAdapterFactory,
            PostgresqlRelationalCommandExecutor,
            PostgresqlRelationalWriteSessionFactory,
            PostgresqlDocumentHydrator,
            PostgresqlSessionDocumentHydrator
        >();
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCacheProjectionDrainPageProcessor,
                DocumentCacheProjectionDrainPageProcessor
            >()
        );

        return services;
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

    public IReferenceResolverAdapter CreateSessionAdapter(IRelationalCommandExecutor commandExecutor)
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        return new PostgresqlReferenceResolverAdapter(commandExecutor);
    }

    public RelationalCommand? TryBuildSessionLookupCommand(ReferenceLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The PostgreSQL lookup is always one statement binding one array parameter, so every
        // request is embeddable.
        return PostgresqlReferenceLookupCommandBuilder.Build(request);
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
        await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(ct);

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
        IRelationalWriteSession writeSession,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writeSession);

        return HydrationExecutor.ExecuteAsync(
            batchSql => writeSession.CreateCommand(new RelationalCommand(batchSql)),
            plan,
            keyset,
            SqlDialect.Pgsql,
            executionOptions,
            cancellationToken
        );
    }
}
