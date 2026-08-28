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
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Mssql;

public static class MssqlReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddMssqlReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered here as well as in AddMssqlDatastore because this method is also used on its own.
        // The seams below take the boundary by constructor injection, so it has to be present wherever
        // they are registered, or they would resolve nothing and the single acquisition identity would
        // not be single at all.
        services.TryAddSingleton<ISqlServerPoolClearing, SqlClientPoolClearing>();

        // Registered as the concrete type so both the acquisition boundary and the ownership
        // reconciler resolve the very same singleton. A second instance would hold its own realization
        // memo and pool state, and only one of the two would ever be reconciled.
        services.TryAddSingleton<MssqlConnectionAcquisition>();
        services.TryAddSingleton<IMssqlConnectionAcquisition>(provider =>
            provider.GetRequiredService<MssqlConnectionAcquisition>()
        );

        // Both type arguments are supplied deliberately: TryAddEnumerable identifies a factory
        // registration by the factory's own return type, so a factory typed to the interface is
        // indistinguishable from every other reconciler and is rejected outright.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDataStoreOwnershipReconciler, MssqlConnectionAcquisition>(provider =>
                provider.GetRequiredService<MssqlConnectionAcquisition>()
            )
        );

        services.TryAdd(
            ServiceDescriptor.Singleton<
                IRelationalWriteExceptionClassifier,
                MssqlRelationalWriteExceptionClassifier
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheProviderCommandTimeoutClassifier,
                MssqlDocumentCacheProviderCommandTimeoutClassifier
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
        services.TryAdd(ServiceDescriptor.Scoped<MssqlDocumentCacheWriter, MssqlDocumentCacheWriter>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheWriter>(serviceProvider =>
                serviceProvider.GetRequiredService<MssqlDocumentCacheWriter>()
            )
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheSessionBoundWriter>(serviceProvider =>
                serviceProvider.GetRequiredService<MssqlDocumentCacheWriter>()
            )
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentProjectionWorkPager, MssqlDocumentProjectionWorkPager>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IDocumentCacheStatusCurrentSourceObserver,
                MssqlDocumentCacheStatusCurrentSourceObserver
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativeMutex,
                MssqlDocumentCacheAdministrativeMutex
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheAdministrativePrimitives>(serviceProvider =>
                DocumentCacheAdministrativePrimitives.ForSqlServer(
                    serviceProvider.GetRequiredService<IDocumentCacheProviderCommandTimeoutClassifier>()
                )
            )
        );
        services.AddReferenceResolver<
            MssqlReferenceResolverAdapterFactory,
            MssqlRelationalCommandExecutor,
            MssqlRelationalWriteSessionFactory,
            MssqlDocumentHydrator,
            MssqlSessionDocumentHydrator
        >();
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentCacheReadLookupAdapter, MssqlDocumentCacheReadLookupAdapter>()
        );
        services.AddDocumentCacheReadAccelerationCoordinator();
        return services;
    }

    public static IServiceCollection AddMssqlDmsCdcControlPlane(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDmsCdcControlPlane();
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheProviderCommandTimeoutClassifier,
                MssqlDocumentCacheProviderCommandTimeoutClassifier
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<ICdcProviderSourcePositionAdapter, MssqlCdcSourcePositionAdapter>()
        );

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
    private readonly Func<CancellationToken, Task<MssqlLeasedConnection>> _openConnectionAsync;

    public MssqlDocumentHydrator(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition
    )
    {
        ArgumentNullException.ThrowIfNull(dataStoreSelection);
        ArgumentNullException.ThrowIfNull(acquisition);

        _openConnectionAsync = cancellationToken =>
            MssqlSeamConnection.OpenAsync(dataStoreSelection, acquisition, cancellationToken);
    }

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        await using var leased = await _openConnectionAsync(ct).ConfigureAwait(false);
        DbConnection connection = leased.Connection;

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
