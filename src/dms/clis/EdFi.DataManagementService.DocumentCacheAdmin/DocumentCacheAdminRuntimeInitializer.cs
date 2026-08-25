// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminRuntimeInitializer
{
    public static async Task InitializeAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        cancellationToken.ThrowIfCancellationRequested();

        await serviceProvider
            .GetRequiredService<IEffectiveSchemaBootstrapper>()
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);

        IRuntimeMappingSetCompiler runtimeCompiler =
            serviceProvider.GetServices<IRuntimeMappingSetCompiler>().SingleOrDefault()
            ?? throw new InvalidOperationException("No runtime mapping-set compiler is configured.");

        MappingSetKey mappingSetKey = runtimeCompiler.GetCurrentKey();
        _ = await serviceProvider
            .GetRequiredService<IMappingSetProvider>()
            .GetOrCreateAsync(mappingSetKey, cancellationToken)
            .ConfigureAwait(false);
    }
}
