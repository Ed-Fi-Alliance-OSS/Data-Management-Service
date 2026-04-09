// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
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
