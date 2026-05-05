// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
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
    private static readonly string _descriptorVerificationPrefix =
        ReferenceLookupVerificationSupport.BuildExpectedVerificationIdentityKey(
            new DocumentIdentity([
                new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, string.Empty),
            ]),
            normalizeDescriptorValues: true
        );

    private const string EmptyVerificationIdentitySql = """
        SELECT
            CAST(NULL AS bigint) AS "DocumentId",
            CAST(NULL AS smallint) AS "ResourceKeyId",
            CAST(NULL AS text) AS "VerificationIdentityKey"
        WHERE FALSE
        """;

    public static RelationalCommand Build(ReferenceLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RelationalCommand(
            BuildCommandText(request),
            [
                new RelationalParameter(
                    ReferentialIdsParameterName,
                    request.ReferentialIds.Select(static referentialId => referentialId.Value).ToArray(),
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

    private static string BuildCommandText(ReferenceLookupRequest request)
    {
        var descriptorVerificationPrefix = EscapeSqlLiteral(_descriptorVerificationPrefix);
        var verificationIdentitySql = BuildVerificationIdentitySql(request);

        return $$"""
            WITH "LookupInput"("ReferentialId", "Ordinal") AS (
                SELECT input."ReferentialId", input."Ordinal"
                FROM unnest(@referentialIds::uuid[]) WITH ORDINALITY AS input("ReferentialId", "Ordinal")
            ),
            "VerificationIdentity"("DocumentId", "ResourceKeyId", "VerificationIdentityKey") AS (
                {{verificationIdentitySql}}
            )
            SELECT
                lookupInput."ReferentialId" AS "ReferentialId",
                referentialIdentity."DocumentId" AS "DocumentId",
                document."ResourceKeyId" AS "ResourceKeyId",
                referentialIdentity."ResourceKeyId" AS "ReferentialIdentityResourceKeyId",
                descriptor."DocumentId" IS NOT NULL AS "IsDescriptor",
                CASE
                    WHEN descriptor."DocumentId" IS NOT NULL THEN '{{descriptorVerificationPrefix}}' || lower(descriptor."Uri")
                    ELSE verificationIdentity."VerificationIdentityKey"
                END AS "VerificationIdentityKey"
            FROM "LookupInput" lookupInput
            INNER JOIN dms."ReferentialIdentity" referentialIdentity
                ON referentialIdentity."ReferentialId" = lookupInput."ReferentialId"
            INNER JOIN dms."Document" document
                ON document."DocumentId" = referentialIdentity."DocumentId"
            LEFT JOIN dms."Descriptor" descriptor
                ON descriptor."DocumentId" = document."DocumentId"
            LEFT JOIN "VerificationIdentity" verificationIdentity
                ON verificationIdentity."DocumentId" = document."DocumentId"
                AND verificationIdentity."ResourceKeyId" = referentialIdentity."ResourceKeyId"
            ORDER BY lookupInput."Ordinal"
            """;
    }

    private static string BuildVerificationIdentitySql(ReferenceLookupRequest request)
    {
        var projections = ReferenceLookupVerificationSupport.BuildProjections(request);

        return projections.Count == 0
            ? EmptyVerificationIdentitySql
            : string.Join(
                Environment.NewLine + "UNION ALL" + Environment.NewLine,
                projections.Select(BuildVerificationIdentityProjectionSql)
            );
    }

    private static string BuildVerificationIdentityProjectionSql(
        ReferenceLookupVerificationProjection projection
    )
    {
        return $$"""
            SELECT
                source."DocumentId" AS "DocumentId",
                {{projection.ResourceKeyId.ToString(CultureInfo.InvariantCulture)}} AS "ResourceKeyId",
                {{BuildVerificationIdentityExpression(
                "source",
                projection.IdentityElements
            )}} AS "VerificationIdentityKey"
            FROM {{QuoteTableName(projection.SourceTable)}} source
            """;
    }

    private static string BuildVerificationIdentityExpression(
        string sourceAlias,
        IReadOnlyList<ReferenceLookupVerificationElement> identityElements
    )
    {
        StringBuilder builder = new();

        for (var index = 0; index < identityElements.Count; index++)
        {
            var identityElement = identityElements[index];

            if (index > 0)
            {
                builder.Append(" || '#' || ");
            }

            builder.Append('\'');
            builder.Append(EscapeSqlLiteral($"{identityElement.IdentityJsonPath}="));
            builder.Append("' || ");
            builder.Append(BuildColumnToTextExpression(sourceAlias, identityElement));
        }

        return builder.ToString();
    }

    private static string BuildColumnToTextExpression(
        string sourceAlias,
        ReferenceLookupVerificationElement identityElement
    )
    {
        var columnExpression = $"{sourceAlias}.{QuoteIdentifier(identityElement.Column.Value)}";
        return DialectIdentityTextFormatter.PgsqlColumnToText(columnExpression, identityElement.ScalarType);
    }

    private static string QuoteTableName(DbTableName tableName) =>
        $"{QuoteIdentifier(tableName.Schema.Value)}.{QuoteIdentifier(tableName.Name)}";

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
