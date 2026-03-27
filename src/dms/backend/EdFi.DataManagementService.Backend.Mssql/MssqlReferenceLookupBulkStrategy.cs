// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Resolves large SQL Server reference lookup sets through a TVP-backed command.
/// </summary>
internal sealed class MssqlReferenceLookupBulkStrategy(IRelationalCommandExecutor commandExecutor)
{
    private const string ReferentialIdsParameterName = "@referentialIds";
    private const string ReferentialIdColumnName = "Id";
    private const string UniqueIdentifierTableTypeName = "dms.UniqueIdentifierTable";

    private const string LookupInputSql = """
        SELECT
            lookupInput.[Id] AS [ReferentialId],
            ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS [Ordinal]
        FROM @referentialIds lookupInput
        """;

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public static bool CanResolve(int referentialIdCount) =>
        !MssqlReferenceLookupSmallListStrategy.CanResolve(referentialIdCount);

    public static bool CanResolve(IReadOnlyList<ReferentialId> referentialIds)
    {
        ArgumentNullException.ThrowIfNull(referentialIds);

        return CanResolve(referentialIds.Count);
    }

    public async Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
        ReferenceLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var lookupResults = await _commandExecutor
            .ExecuteReaderAsync(
                BuildCommand(request),
                ReferenceLookupResultReader.ReadAsync,
                cancellationToken
            )
            .ConfigureAwait(false);

        return ReorderResultsInRequestOrder(request.ReferentialIds, lookupResults);
    }

    internal static RelationalCommand BuildCommand(ReferenceLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RelationalCommand(
            MssqlReferenceLookupSmallListStrategy.BuildCommandText(request, LookupInputSql),
            [
                new RelationalParameter(
                    ReferentialIdsParameterName,
                    CreateReferentialIdTable(request.ReferentialIds),
                    ConfigureReferentialIdsParameter
                ),
            ]
        );
    }

    private static DataTable CreateReferentialIdTable(IReadOnlyList<ReferentialId> referentialIds)
    {
        DataTable referentialIdTable = new();
        referentialIdTable.Columns.Add(ReferentialIdColumnName, typeof(Guid));

        foreach (var referentialId in referentialIds)
        {
            referentialIdTable.Rows.Add(referentialId.Value);
        }

        return referentialIdTable;
    }

    private static void ConfigureReferentialIdsParameter(System.Data.Common.DbParameter parameter)
    {
        if (parameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server bulk reference lookup parameter configuration requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = SqlDbType.Structured;
        sqlParameter.TypeName = UniqueIdentifierTableTypeName;
    }

    private static IReadOnlyList<ReferenceLookupResult> ReorderResultsInRequestOrder(
        IReadOnlyList<ReferentialId> requestedReferentialIds,
        IReadOnlyList<ReferenceLookupResult> lookupResults
    )
    {
        Dictionary<ReferentialId, ReferenceLookupResult> lookupResultByReferentialId = [];
        HashSet<ReferentialId> requestedReferentialIdSet = [.. requestedReferentialIds];

        foreach (var lookupResult in lookupResults)
        {
            if (!requestedReferentialIdSet.Contains(lookupResult.ReferentialId))
            {
                throw new InvalidOperationException(
                    $"SQL Server bulk reference lookup returned an unexpected referential id '{lookupResult.ReferentialId.Value}'."
                );
            }

            if (!lookupResultByReferentialId.TryAdd(lookupResult.ReferentialId, lookupResult))
            {
                throw new InvalidOperationException(
                    $"SQL Server bulk reference lookup returned multiple rows for referential id '{lookupResult.ReferentialId.Value}'."
                );
            }
        }

        List<ReferenceLookupResult> orderedLookupResults = [];

        foreach (var requestedReferentialId in requestedReferentialIds)
        {
            if (lookupResultByReferentialId.TryGetValue(requestedReferentialId, out var lookupResult))
            {
                orderedLookupResults.Add(lookupResult);
            }
        }

        return orderedLookupResults;
    }
}
