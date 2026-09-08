// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend.Postgresql;

public sealed class PostgresqlDocumentCacheProviderPrerequisiteValidator
    : IDocumentCacheProviderPrerequisiteValidator
{
    public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateInitializationAsync(
        string connectionString,
        DocumentCacheLifecycleObservation lifecycle,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        return Task.FromResult(
            DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                lifecycle
            )
        );
    }

    public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPreflightAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            )
        );
}
