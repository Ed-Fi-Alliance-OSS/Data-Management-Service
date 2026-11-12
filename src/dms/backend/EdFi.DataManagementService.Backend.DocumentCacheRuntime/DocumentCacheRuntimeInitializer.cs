// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.DocumentCacheRuntime;

public static class DocumentCacheRuntimeInitializer
{
    public static async Task InitializeAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        cancellationToken.ThrowIfCancellationRequested();

        _ = serviceProvider.GetRequiredService<IOptions<DocumentCacheOptions>>().Value;

        // This host has no eager options validation, so resolve the deprecated Configuration Service
        // token cache cap here: an invalid value fails as a configuration error, naming the setting
        // without its value, before schema initialization or any Configuration Service call. A valid
        // explicit cap logs the same deprecation warning the web host does.
        ConfigurationServiceTokenCacheOptions.LogDeprecationIfSet(
            serviceProvider.GetRequiredService<IOptions<ConfigurationServiceTokenCacheOptions>>().Value,
            serviceProvider
                .GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(DocumentCacheRuntimeInitializer))
                ?? NullLogger.Instance
        );

        await serviceProvider
            .GetRequiredService<IEffectiveSchemaBootstrapper>()
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
