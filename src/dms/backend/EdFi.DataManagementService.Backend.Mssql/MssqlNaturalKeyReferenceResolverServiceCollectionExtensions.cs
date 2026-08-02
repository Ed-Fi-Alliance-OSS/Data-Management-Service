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
    /// Retained as a named alias of
    /// <see cref="MssqlReferenceResolverServiceCollectionExtensions.AddMssqlReferenceResolver" />, which now
    /// registers the natural-key resolver itself — there is no second resolver arm to choose between, so the
    /// two entry points compose exactly the same graph.
    /// </remarks>
    public static IServiceCollection AddMssqlNaturalKeyReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

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
