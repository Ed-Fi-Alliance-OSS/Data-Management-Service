// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheProviderCommandTimeoutClassifier
{
    bool IsProviderCommandTimeout(Exception exception);
}

internal sealed class NoOpDocumentCacheProviderCommandTimeoutClassifier
    : IDocumentCacheProviderCommandTimeoutClassifier
{
    public static NoOpDocumentCacheProviderCommandTimeoutClassifier Instance { get; } = new();

    private NoOpDocumentCacheProviderCommandTimeoutClassifier() { }

    public bool IsProviderCommandTimeout(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }
}
