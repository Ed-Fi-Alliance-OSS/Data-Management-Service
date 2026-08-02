// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.Postgresql;

public static class PostgresqlNaturalKeyReferenceResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL relational composition surface with the natural-key reference resolver as
    /// the request-scoped <see cref="IReferenceResolver" />.
    /// </summary>
    /// <remarks>
    /// This is the composition production hosts register. A sibling of
    /// <see cref="PostgresqlReferenceResolverServiceCollectionExtensions.AddPostgresqlReferenceResolver" />,
    /// not a replacement: the natural-key registration is added first so it wins the shared surface's
    /// <c>TryAdd</c> for <see cref="IReferenceResolver" />, and everything else — command executor, write
    /// session factory, hydrators, write executor — is the same composition. The referential-id resolver's
    /// adapter factory stays registered but has no production consumer; only the differential and canary
    /// integration suites still resolve through it, and both it and this delegation die in Phase 4.
    /// </remarks>
    public static IServiceCollection AddPostgresqlNaturalKeyReferenceResolver(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddNaturalKeyReferenceResolver<PostgresqlNaturalKeyLookupAdapterFactory>();

        return services.AddPostgresqlReferenceResolver();
    }
}

internal sealed class PostgresqlNaturalKeyLookupAdapterFactory(IRelationalCommandExecutor commandExecutor)
    : INaturalKeyLookupAdapterFactory
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public INaturalKeyLookupAdapter CreateAdapter()
    {
        return new PostgresqlNaturalKeyLookupAdapter(_commandExecutor);
    }

    public INaturalKeyLookupAdapter CreateSessionAdapter(DbConnection connection, DbTransaction transaction)
    {
        return new PostgresqlNaturalKeyLookupAdapter(
            new SessionRelationalCommandExecutor(connection, transaction)
        );
    }
}
