// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Fail-fast initializer registered by core until a datastore composition root supplies backend mapping initialization.
/// </summary>
internal class MissingBackendMappingInitializer : IBackendMappingInitializer
{
    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Backend mapping initialization is not configured. "
                + "Register a datastore-specific IBackendMappingInitializer after AddDmsDefaultConfiguration."
        );
}
