// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcConnectorTemplateSharedRules
{
    internal const long DefaultHeartbeatIntervalMilliseconds = 5000;

    private static readonly IReadOnlyList<CdcSourceTableKind> OrderedRequiredSourceTableKindsValue =
        Array.AsReadOnly(
            new[]
            {
                CdcSourceTableKind.DocumentCache,
                CdcSourceTableKind.Document,
                CdcSourceTableKind.CdcHeartbeat,
            }
        );

    private static readonly IReadOnlyList<CdcSourceTableKind> OrderedRequiredMessageKeyTableKindsValue =
        Array.AsReadOnly(new[] { CdcSourceTableKind.DocumentCache, CdcSourceTableKind.Document });

    internal static IReadOnlyList<CdcSourceTableKind> OrderedRequiredSourceTableKinds =>
        OrderedRequiredSourceTableKindsValue;

    internal static IReadOnlyList<CdcSourceTableKind> OrderedRequiredMessageKeyTableKinds =>
        OrderedRequiredMessageKeyTableKindsValue;

    internal static IReadOnlyList<CdcSourceTableInventory> OrderedSourceTables(
        CdcConnectorTemplateRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return OrderedRequiredSourceTableKinds.Select(tableKind => SourceTable(request, tableKind)).ToArray();
    }

    internal static CdcSourceTableInventory SourceTable(
        CdcConnectorTemplateRequest request,
        CdcSourceTableKind tableKind
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ProviderSetupEvidence.Result.SourceTableInventory.Single(table =>
            table.TableKind == tableKind
        );
    }

    internal static IReadOnlyList<CdcExpectedMessageKeyColumns> OrderedMessageKeyColumns(
        CdcConnectorTemplateRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return OrderedRequiredMessageKeyTableKinds
            .Select(tableKind => MessageKeyColumns(request, tableKind))
            .ToArray();
    }

    internal static CdcExpectedMessageKeyColumns MessageKeyColumns(
        CdcConnectorTemplateRequest request,
        CdcSourceTableKind tableKind
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ProviderSetupEvidence.Result.ExpectedMessageKeyColumns.Single(columns =>
            columns.TableKind == tableKind
        );
    }

    internal static DbTableName ExpectedSourceTableName(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.DocumentCache => new(new DbSchemaName("dms"), "DocumentCache"),
            CdcSourceTableKind.Document => new(new DbSchemaName("dms"), "Document"),
            CdcSourceTableKind.CdcHeartbeat => new(new DbSchemaName("dms"), "CdcHeartbeat"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    internal static bool HasRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory
    )
    {
        ArgumentNullException.ThrowIfNull(sourceTableInventory);

        CdcSourceTableKind[] observedKinds = sourceTableInventory.Select(table => table.TableKind).ToArray();

        return sourceTableInventory.Count == OrderedRequiredSourceTableKinds.Count
            && !OrderedRequiredSourceTableKinds.Except(observedKinds).Any()
            && !observedKinds.Except(OrderedRequiredSourceTableKinds).Any()
            && !observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1);
    }

    internal static bool HasExpectedMessageKeyColumns(
        IReadOnlyList<CdcExpectedMessageKeyColumns> expectedMessageKeyColumns
    )
    {
        ArgumentNullException.ThrowIfNull(expectedMessageKeyColumns);

        CdcSourceTableKind[] observedKinds = expectedMessageKeyColumns
            .Select(columns => columns.TableKind)
            .ToArray();

        return expectedMessageKeyColumns.Count == OrderedRequiredMessageKeyTableKinds.Count
            && !OrderedRequiredMessageKeyTableKinds.Except(observedKinds).Any()
            && !observedKinds.Except(OrderedRequiredMessageKeyTableKinds).Any()
            && !observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1)
            && expectedMessageKeyColumns.All(columns =>
                columns.KeyColumns.Count == 1
                && string.Equals(columns.KeyColumns[0].Value, "DocumentUuid", StringComparison.Ordinal)
            );
    }

    internal static long HeartbeatIntervalMilliseconds(CdcConnectorTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.DeploymentPolicy.HeartbeatInterval is null
            ? DefaultHeartbeatIntervalMilliseconds
            : PositiveIntervalMilliseconds(
                request.DeploymentPolicy.HeartbeatInterval.Value,
                nameof(request),
                "CDC connector template heartbeat interval must render to a positive millisecond value."
            );
    }

    internal static long PollIntervalMilliseconds(CdcConnectorTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.DeploymentPolicy.SqlServerPollInterval is null
            ? throw new InvalidOperationException(
                "CDC connector template SQL Server poll interval was not supplied."
            )
            : PositiveIntervalMilliseconds(
                request.DeploymentPolicy.SqlServerPollInterval.Value,
                nameof(request),
                "CDC connector template SQL Server poll interval must render to a positive millisecond value."
            );
    }

    private static long PositiveIntervalMilliseconds(
        TimeSpan interval,
        string parameterName,
        string exceptionMessage
    )
    {
        double milliseconds = Math.Ceiling(interval.TotalMilliseconds);
        if (milliseconds is < 1 or > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, exceptionMessage);
        }

        return Convert.ToInt64(milliseconds);
    }
}
