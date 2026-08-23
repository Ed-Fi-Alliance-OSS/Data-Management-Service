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
        CdcSourceInventoryContract.RequiredSourceTableKinds;

    private static readonly IReadOnlyList<CdcSourceTableKind> OrderedRequiredMessageKeyTableKindsValue =
        CdcSourceInventoryContract.RequiredMessageKeyTableKinds;

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
        CdcSourceInventoryContract.RequiredSourceTableName(tableKind);

    internal static bool HasRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory
    ) => TryValidateRequiredSourceInventory(sourceTableInventory);

    internal static bool HasRequiredSourceTableMembership(
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory
    )
    {
        if (sourceTableInventory is null)
        {
            return false;
        }

        if (sourceTableInventory.Any(table => table is null))
        {
            return false;
        }

        CdcSourceTableKind[] observedKinds = sourceTableInventory.Select(table => table.TableKind).ToArray();

        return sourceTableInventory.Count == OrderedRequiredSourceTableKinds.Count
            && !OrderedRequiredSourceTableKinds.Except(observedKinds).Any()
            && !observedKinds.Except(OrderedRequiredSourceTableKinds).Any()
            && !observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1);
    }

    private static bool TryValidateRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory
    )
    {
        if (sourceTableInventory is null)
        {
            return false;
        }

        try
        {
            CdcSourceInventoryContract.ValidateRequiredSourceInventory(
                sourceTableInventory,
                nameof(sourceTableInventory)
            );
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool HasExpectedMessageKeyColumns(
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns
    )
    {
        if (expectedMessageKeyColumns is null)
        {
            return false;
        }

        try
        {
            CdcSourceInventoryContract.ValidateRequiredMessageKeyColumns(
                expectedMessageKeyColumns,
                nameof(expectedMessageKeyColumns)
            );
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static CdcProviderArtifactObservation[] ArtifactInventory(
        CdcProviderSetupResult providerSetupResult
    )
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        return providerSetupResult.ArtifactInventory is null
            ? []
            : providerSetupResult.ArtifactInventory.Where(artifact => artifact is not null).ToArray();
    }

    internal static CdcSourceTableKind? SqlServerCaptureInstanceSourceTableKind(
        CdcProviderArtifactObservation artifact
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (
            artifact.SafeObservedValues is null
            || !artifact.SafeObservedValues.TryGetValue("source_table_kind", out string? sourceTableKind)
        )
        {
            return null;
        }

        return CdcSourceInventoryContract.TryParseSourceTableKindToken(sourceTableKind, out var tableKind)
            ? tableKind
            : null;
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
