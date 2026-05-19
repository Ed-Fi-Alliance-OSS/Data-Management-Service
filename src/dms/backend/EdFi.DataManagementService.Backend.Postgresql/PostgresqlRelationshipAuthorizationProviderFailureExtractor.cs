// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlRelationshipAuthorizationProviderFailureExtractor
    : IRelationshipAuthorizationProviderFailureExtractor
{
    public RelationshipAuthorizationProviderFailure Extract(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is PostgresException postgresException
            ? new RelationshipAuthorizationProviderFailure(
                postgresException.SqlState,
                postgresException.MessageText
            )
            : new RelationshipAuthorizationProviderFailure(null, exception.Message);
    }
}
