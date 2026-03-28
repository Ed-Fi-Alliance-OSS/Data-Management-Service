// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Postgresql;

public static class PostgresqlReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresqlReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<PostgresqlDataSourceCache>();
        services.TryAddScoped<IPostgresqlDbConnectionProvider, PostgresqlRequestDbConnectionProvider>();

        return services.AddReferenceResolver<
            PostgresqlReferenceResolverAdapterFactory,
            PostgresqlRelationalCommandExecutor
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
}
