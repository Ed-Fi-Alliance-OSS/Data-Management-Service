// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlReferenceLookupSmallListStrategy(IRelationalCommandExecutor commandExecutor)
{
    internal const int BulkLookupThreshold = 2000;

    private const string EmptyLookupInputSql = """
        SELECT CAST(NULL AS uniqueidentifier) AS [ReferentialId], CAST(NULL AS int) AS [Ordinal]
        WHERE 1 = 0
        """;

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public static bool CanResolve(int referentialIdCount) => referentialIdCount < BulkLookupThreshold;

    public static bool CanResolve(IReadOnlyList<ReferentialId> referentialIds)
    {
        ArgumentNullException.ThrowIfNull(referentialIds);

        return CanResolve(referentialIds.Count);
    }

    public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
        ReferenceLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return _commandExecutor.ExecuteReaderAsync(
            BuildCommand(request.ReferentialIds),
            ReferenceLookupResultReader.ReadAsync,
            cancellationToken
        );
    }

    internal static RelationalCommand BuildCommand(IReadOnlyList<ReferentialId> referentialIds)
    {
        ArgumentNullException.ThrowIfNull(referentialIds);

        EnsureSupportedLookupSize(referentialIds.Count);

        return new RelationalCommand(BuildCommandText(referentialIds.Count), BuildParameters(referentialIds));
    }

    private static IReadOnlyList<RelationalParameter> BuildParameters(
        IReadOnlyList<ReferentialId> referentialIds
    )
    {
        List<RelationalParameter> parameters = new(referentialIds.Count);

        for (var index = 0; index < referentialIds.Count; index++)
        {
            parameters.Add(
                new RelationalParameter(
                    CreateParameterName(index),
                    referentialIds[index].Value,
                    ConfigureReferentialIdParameter
                )
            );
        }

        return parameters;
    }

    private static string BuildCommandText(int referentialIdCount)
    {
        var lookupInputSql =
            referentialIdCount == 0 ? EmptyLookupInputSql : BuildLookupInputSql(referentialIdCount);

        return $$"""
            WITH [LookupInput]([ReferentialId], [Ordinal]) AS (
                {{lookupInputSql}}
            )
            SELECT
                lookupInput.[ReferentialId] AS [ReferentialId],
                referentialIdentity.[DocumentId] AS [DocumentId],
                document.[ResourceKeyId] AS [ResourceKeyId],
                referentialIdentity.[ResourceKeyId] AS [ReferentialIdentityResourceKeyId],
                CASE
                    WHEN descriptor.[DocumentId] IS NULL THEN CAST(0 AS bit)
                    ELSE CAST(1 AS bit)
                END AS [IsDescriptor]
            FROM [LookupInput] lookupInput
            INNER JOIN [dms].[ReferentialIdentity] referentialIdentity
                ON referentialIdentity.[ReferentialId] = lookupInput.[ReferentialId]
            INNER JOIN [dms].[Document] document
                ON document.[DocumentId] = referentialIdentity.[DocumentId]
            LEFT JOIN [dms].[Descriptor] descriptor
                ON descriptor.[DocumentId] = document.[DocumentId]
            ORDER BY lookupInput.[Ordinal]
            """;
    }

    private static string BuildLookupInputSql(int referentialIdCount)
    {
        StringBuilder sqlBuilder = new();
        sqlBuilder.AppendLine("SELECT lookupInput.[ReferentialId], lookupInput.[Ordinal]");
        sqlBuilder.AppendLine("FROM (VALUES");

        for (var index = 0; index < referentialIdCount; index++)
        {
            sqlBuilder.Append("    (");
            sqlBuilder.Append(CreateParameterName(index));
            sqlBuilder.Append(", ");
            sqlBuilder.Append(index);
            sqlBuilder.Append(')');

            if (index < referentialIdCount - 1)
            {
                sqlBuilder.AppendLine(",");
                continue;
            }

            sqlBuilder.AppendLine();
        }

        sqlBuilder.Append(") AS lookupInput([ReferentialId], [Ordinal])");

        return sqlBuilder.ToString();
    }

    private static string CreateParameterName(int index) => $"@p{index}";

    private static void ConfigureReferentialIdParameter(DbParameter parameter)
    {
        parameter.DbType = DbType.Guid;

        if (parameter is SqlParameter sqlParameter)
        {
            sqlParameter.SqlDbType = SqlDbType.UniqueIdentifier;
        }
    }

    private static void EnsureSupportedLookupSize(int referentialIdCount)
    {
        if (CanResolve(referentialIdCount))
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(referentialIdCount),
            referentialIdCount,
            $"SQL Server small-list reference lookup supports fewer than {BulkLookupThreshold} referential ids. Use the bulk strategy for larger sets."
        );
    }
}
