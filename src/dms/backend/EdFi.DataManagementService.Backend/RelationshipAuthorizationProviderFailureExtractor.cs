// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend;

public sealed record RelationshipAuthorizationProviderFailure(string? ErrorCode, string Message);

public interface IRelationshipAuthorizationProviderFailureExtractor
{
    RelationshipAuthorizationProviderFailure Extract(DbException exception);
}

internal sealed class DefaultRelationshipAuthorizationProviderFailureExtractor
    : IRelationshipAuthorizationProviderFailureExtractor
{
    public static DefaultRelationshipAuthorizationProviderFailureExtractor Instance { get; } = new();

    public RelationshipAuthorizationProviderFailure Extract(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new RelationshipAuthorizationProviderFailure(null, exception.Message);
    }
}
