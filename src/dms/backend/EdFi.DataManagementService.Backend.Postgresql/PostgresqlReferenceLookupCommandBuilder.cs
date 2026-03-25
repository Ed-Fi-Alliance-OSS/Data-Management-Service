// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Builds the PostgreSQL bulk reference-lookup command used by relational write prerequisites.
/// </summary>
internal static class PostgresqlReferenceLookupCommandBuilder
{
    private const string ReferentialIdsParameterName = "@referentialIds";
    private static readonly NpgsqlDbType ReferentialIdsParameterDbType = (NpgsqlDbType)(
        (int)NpgsqlDbType.Array | (int)NpgsqlDbType.Uuid
    );

    private const string LookupCommandText = """
        WITH "LookupInput"("ReferentialId", "Ordinal") AS (
            SELECT input."ReferentialId", input."Ordinal"
            FROM unnest(@referentialIds::uuid[]) WITH ORDINALITY AS input("ReferentialId", "Ordinal")
        )
        SELECT
            lookupInput."ReferentialId" AS "ReferentialId",
            referentialIdentity."DocumentId" AS "DocumentId",
            document."ResourceKeyId" AS "ResourceKeyId",
            referentialIdentity."ResourceKeyId" AS "ReferentialIdentityResourceKeyId",
            descriptor."DocumentId" IS NOT NULL AS "IsDescriptor"
        FROM "LookupInput" lookupInput
        INNER JOIN dms."ReferentialIdentity" referentialIdentity
            ON referentialIdentity."ReferentialId" = lookupInput."ReferentialId"
        INNER JOIN dms."Document" document
            ON document."DocumentId" = referentialIdentity."DocumentId"
        LEFT JOIN dms."Descriptor" descriptor
            ON descriptor."DocumentId" = document."DocumentId"
        ORDER BY lookupInput."Ordinal"
        """;

    public static RelationalCommand Build(IReadOnlyList<ReferentialId> referentialIds)
    {
        ArgumentNullException.ThrowIfNull(referentialIds);

        return new RelationalCommand(
            LookupCommandText,
            [
                new RelationalParameter(
                    ReferentialIdsParameterName,
                    referentialIds.Select(static referentialId => referentialId.Value).ToArray(),
                    static parameter =>
                    {
                        if (parameter is not NpgsqlParameter npgsqlParameter)
                        {
                            throw new InvalidOperationException(
                                "PostgreSQL reference lookup parameter configuration requires an NpgsqlParameter instance."
                            );
                        }

                        npgsqlParameter.NpgsqlDbType = ReferentialIdsParameterDbType;
                    }
                ),
            ]
        );
    }
}
