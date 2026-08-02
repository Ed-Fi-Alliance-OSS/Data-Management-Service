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
    /// Retained as a named alias of
    /// <see cref="PostgresqlReferenceResolverServiceCollectionExtensions.AddPostgresqlReferenceResolver" />,
    /// which now registers the natural-key resolver itself — there is no second resolver arm to choose
    /// between, so the two entry points compose exactly the same graph.
    /// </remarks>
    public static IServiceCollection AddPostgresqlNaturalKeyReferenceResolver(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

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
