// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.Mssql;

public static class MssqlReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddMssqlReferenceResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddReferenceResolver<
            MssqlReferenceResolverAdapterFactory,
            MssqlRelationalCommandExecutor
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
}
