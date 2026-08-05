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
            ServiceDescriptor.Singleton<
                IRelationalWriteExceptionClassifier,
                MssqlRelationalWriteExceptionClassifier
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalParameterConfigurator, MssqlRelationalParameterConfigurator>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IDocumentCacheMaterializationDataStore,
                MssqlDocumentCacheMaterializationDataStore
            >()
        );
        services.TryAdd(ServiceDescriptor.Scoped<IDocumentCacheWriter, MssqlDocumentCacheWriter>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheSessionBoundWriter, MssqlDocumentCacheWriter>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentProjectionWorkPager, MssqlDocumentProjectionWorkPager>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativeMutex,
                MssqlDocumentCacheAdministrativeMutex
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheAdministrativePrimitives>(
                DocumentCacheAdministrativePrimitives.Mssql()
            )
        );

        services.AddReferenceResolver<
            MssqlReferenceResolverAdapterFactory,
            MssqlRelationalCommandExecutor,
            MssqlRelationalWriteSessionFactory,
            MssqlDocumentHydrator,
            MssqlSessionDocumentHydrator
        >();

        return services;
    }

    public static IServiceCollection AddMssqlRelationalTokenInfoEducationOrganizationLookup(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(
            ServiceDescriptor.Scoped<
                IRelationalTokenInfoEducationOrganizationLookup,
                MssqlTokenInfoEducationOrganizationLookup
            >()
        );

        return services;
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

    public IReferenceResolverAdapter CreateSessionAdapter(IRelationalCommandExecutor commandExecutor)
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        return new MssqlReferenceResolverAdapter(commandExecutor);
    }

    public RelationalCommand? TryBuildSessionLookupCommand(ReferenceLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The bulk strategy binds a table-valued parameter, which cannot be renamed into a composite
        // command's allocator-owned parameter set; those requests fall back to the standalone adapter.
        return MssqlReferenceLookupSmallListStrategy.CanResolve(request.ReferentialIds)
            ? MssqlReferenceLookupSmallListStrategy.BuildCommand(request)
            : null;
    }
}

internal sealed class MssqlDocumentHydrator : IDocumentHydrator
{
    private readonly Func<CancellationToken, Task<DbConnection>> _openConnectionAsync;

    public MssqlDocumentHydrator(IDataStoreSelection dataStoreSelection)
        : this(dataStoreSelection, connectionString => new SqlConnection(connectionString)) { }

    internal MssqlDocumentHydrator(
        IDataStoreSelection dataStoreSelection,
        Func<string, DbConnection> createConnection
    )
    {
        ArgumentNullException.ThrowIfNull(dataStoreSelection);
        ArgumentNullException.ThrowIfNull(createConnection);

        _openConnectionAsync = async cancellationToken =>
        {
            var selectedInstance = dataStoreSelection.GetSelectedDataStore();
            var connectionString = selectedInstance.ConnectionString;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Selected data store '{selectedInstance.Id}' does not have a valid connection string."
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
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        await using var connection = await _openConnectionAsync(ct).ConfigureAwait(false);

        return await HydrationExecutor.ExecuteAsync(
            connection,
            plan,
            keyset,
            SqlDialect.Mssql,
            transaction: null,
            executionOptions,
            ct
        );
    }
}

internal sealed class MssqlSessionDocumentHydrator : ISessionDocumentHydrator
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
            SqlDialect.Mssql,
            executionOptions,
            cancellationToken
        );
    }
}
