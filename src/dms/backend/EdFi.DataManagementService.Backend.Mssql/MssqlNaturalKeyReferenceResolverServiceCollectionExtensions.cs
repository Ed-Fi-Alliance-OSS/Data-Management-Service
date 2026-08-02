// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.Mssql;

public static class MssqlNaturalKeyReferenceResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server relational composition surface with the natural-key reference resolver as
    /// the request-scoped <see cref="IReferenceResolver" />.
    /// </summary>
    /// <remarks>
    /// This is the composition production hosts register. A sibling of
    /// <see cref="MssqlReferenceResolverServiceCollectionExtensions.AddMssqlReferenceResolver" />, not a
    /// replacement: the natural-key registration is added first so it wins the shared surface's
    /// <c>TryAdd</c> for <see cref="IReferenceResolver" />, and everything else — command executor, write
    /// session factory, hydrators, write executor — is the same composition. The referential-id resolver's
    /// adapter factory stays registered but has no production consumer; only the differential and canary
    /// integration suites still resolve through it, and both it and this delegation die in Phase 4.
    /// </remarks>
    public static IServiceCollection AddMssqlNaturalKeyReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddNaturalKeyReferenceResolver<MssqlNaturalKeyLookupAdapterFactory>();

        return services.AddMssqlReferenceResolver();
    }
}

internal sealed class MssqlNaturalKeyLookupAdapterFactory(IRelationalCommandExecutor commandExecutor)
    : INaturalKeyLookupAdapterFactory
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public INaturalKeyLookupAdapter CreateAdapter()
    {
        return new MssqlNaturalKeyLookupAdapter(_commandExecutor);
    }

    public INaturalKeyLookupAdapter CreateSessionAdapter(DbConnection connection, DbTransaction transaction)
    {
        return new MssqlNaturalKeyLookupAdapter(
            new SessionRelationalCommandExecutor(connection, transaction)
        );
    }
}
