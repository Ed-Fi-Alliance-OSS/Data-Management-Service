// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcProviderSetupPrerequisiteRules
{
    private const string RedactedValue = "[redacted]";
    private static readonly CdcSafeName SqlServerSnapshotIsolationSafeName = new(
        "sqlserver_snapshot_isolation"
    );

    internal static IReadOnlyList<CdcConnectorTemplateDiagnostic> Validate(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcProviderArtifactNames? expectedArtifactNames = null
    )
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        AddSourceTableInventoryDiagnostics(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddExpectedMessageKeyColumnsDiagnostics(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddProviderArtifactDiagnostics(
            providerSetupResult,
            expectedArtifactNames,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddSqlServerSnapshotIsolationDiagnosticIfNeeded(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddInvalidHeartbeatActionQueryDiagnosticIfNeeded(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );

        return diagnostics;
    }

    private static void AddInvalidHeartbeatActionQueryDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (providerSetupResult.HeartbeatActionQuery is null)
        {
            return;
        }

        string heartbeatSql = providerSetupResult.HeartbeatActionQuery.Sql;
        if (!string.IsNullOrWhiteSpace(heartbeatSql) && !ContainsControlCharacter(heartbeatSql))
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired,
                CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation,
                "providerSetup.heartbeatActionQuery",
                "fresh provider heartbeat action query",
                RedactedValue,
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

    private static void AddSourceTableInventoryDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory =
            providerSetupResult.SourceTableInventory;

        if (
            sourceTableInventory is null
            || !CdcConnectorTemplateSharedRules.HasRequiredSourceTableMembership(sourceTableInventory)
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.sourceTableInventory",
                    "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                    ObservedCountOrMissing(sourceTableInventory),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
            return;
        }

        AddSourceTableNameDiagnostics(
            providerSetupResult,
            sourceTableInventory,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddRequiredSourceInventoryContractDiagnosticIfNeeded(
            providerSetupResult,
            sourceTableInventory,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddSourceColumnShapeDiagnostics(
            providerSetupResult,
            sourceTableInventory,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
    }

    private static void AddRequiredSourceInventoryContractDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (CdcConnectorTemplateSharedRules.HasRequiredSourceInventory(sourceTableInventory))
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                "providerSetup.sourceTableInventory",
                "valid CDC source table contract inventory",
                "malformed",
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddSourceColumnShapeDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        foreach (CdcSourceTableInventory sourceTable in sourceTableInventory)
        {
            if (
                CdcConnectorTemplateSharedRules.OrderedRequiredMessageKeyTableKinds.Contains(
                    sourceTable.TableKind
                )
            )
            {
                continue;
            }

            if (HasMalformedSourceColumns(sourceTable))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                        "providerSetup.sourceTableInventory.columns",
                        $"non-null source column inventory for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "malformed",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
                continue;
            }

            if (!HasDuplicateColumnNames(sourceTable))
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.sourceTableInventory.columns",
                    $"unique source column names for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                    "duplicate",
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static void AddSourceTableNameDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        foreach (
            CdcSourceTableKind tableKind in CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds
        )
        {
            CdcSourceTableInventory sourceTable = sourceTableInventory.Single(table =>
                table.TableKind == tableKind
            );
            var expectedTableName = CdcConnectorTemplateSharedRules.ExpectedSourceTableName(tableKind);
            if (sourceTable.TableName.Equals(expectedTableName))
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.IncludeListViolation,
                    "table.include.list",
                    $"{expectedTableName.Schema.Value}.{expectedTableName.Name}",
                    SanitizePhysicalIdentifier(
                        $"{sourceTable.TableName.Schema.Value}.{sourceTable.TableName.Name}"
                    ),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static void AddExpectedMessageKeyColumnsDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns =
            providerSetupResult.ExpectedMessageKeyColumns;
        if (
            expectedMessageKeyColumns is null
            || !CdcConnectorTemplateSharedRules.HasExpectedMessageKeyColumns(expectedMessageKeyColumns)
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                    "providerSetup.expectedMessageKeyColumns",
                    "DocumentUuid keys for document sources",
                    ObservedCountOrMissing(expectedMessageKeyColumns),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
            return;
        }

        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory =
            providerSetupResult.SourceTableInventory;
        if (
            sourceTableInventory is null
            || !CdcConnectorTemplateSharedRules.HasRequiredSourceTableMembership(sourceTableInventory)
        )
        {
            return;
        }

        foreach (CdcExpectedMessageKeyColumns messageKeyColumns in expectedMessageKeyColumns)
        {
            CdcSourceTableInventory sourceTable = sourceTableInventory.Single(table =>
                table.TableKind == messageKeyColumns.TableKind
            );

            if (HasMalformedSourceColumns(sourceTable))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                        "providerSetup.sourceTableInventory.columns",
                        $"non-null source column inventory for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "malformed",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
                continue;
            }

            if (HasDuplicateColumnNames(sourceTable))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                        "message.key.columns",
                        $"unique source column names for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "duplicate",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
            }

            foreach (DbColumnName keyColumn in messageKeyColumns.KeyColumns)
            {
                if (CountSourceColumns(sourceTable, keyColumn) > 0)
                {
                    continue;
                }

                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                        "message.key.columns",
                        $"source column {keyColumn.Value} for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "missing",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
            }
        }
    }

    private static void AddProviderArtifactDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcProviderArtifactNames? expectedArtifactNames,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (
            AddMalformedArtifactInventoryDiagnosticIfNeeded(
                providerSetupResult,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics
            )
        )
        {
            return;
        }

        if (providerSetupResult.Provider == CdcProvider.Postgresql)
        {
            if (expectedArtifactNames?.Postgresql is { } postgresqlNames)
            {
                AddExpectedArtifactDiagnosticIfNeeded(
                    providerSetupResult,
                    postgresqlNames.PublicationName,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    diagnostics,
                    CdcProviderArtifactKind.PostgresqlPublication,
                    CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
                    "publication.name",
                    "one matched provider setup artifact with the expected binding name"
                );
                AddExpectedArtifactDiagnosticIfNeeded(
                    providerSetupResult,
                    postgresqlNames.ReplicationSlotName,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    diagnostics,
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired,
                    "slot.name",
                    "one matched provider setup artifact with the expected binding name"
                );
                return;
            }

            AddMissingArtifactDiagnosticIfNeeded(
                providerSetupResult,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics,
                CdcProviderArtifactKind.PostgresqlPublication,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
                "publication.name"
            );
            AddMissingArtifactDiagnosticIfNeeded(
                providerSetupResult,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics,
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired,
                "slot.name"
            );
        }
        else if (providerSetupResult.Provider == CdcProvider.SqlServer)
        {
            if (expectedArtifactNames?.SqlServer is { } sqlServerNames)
            {
                AddExpectedArtifactDiagnosticIfNeeded(
                    providerSetupResult,
                    sqlServerNames.GatingRoleName,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    diagnostics,
                    CdcProviderArtifactKind.SqlServerGatingRole,
                    CdcConnectorTemplateDiagnosticCodes.SqlServerGatingRoleMetadataRequired,
                    "providerSetup.artifactInventory.sqlServerGatingRole",
                    "one usable SQL Server gating-role artifact with the expected binding name"
                );
            }

            AddSqlServerCaptureInstanceDiagnostics(
                providerSetupResult,
                expectedArtifactNames?.SqlServer,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics
            );
        }
    }

    private static bool AddMalformedArtifactInventoryDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (
            providerSetupResult.ArtifactInventory is null
            || providerSetupResult.ArtifactInventory.All(artifact => artifact is not null)
        )
        {
            return false;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.ProviderSetupArtifactInventoryMalformed,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                "providerSetup.artifactInventory",
                "non-null provider setup artifacts",
                "malformed",
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );

        return true;
    }

    private static void AddExpectedArtifactDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName expectedArtifactName,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics,
        CdcProviderArtifactKind artifactKind,
        string code,
        string propertyName,
        string expectedValue
    )
    {
        CdcProviderArtifactObservation[] artifacts = ArtifactsOfKind(providerSetupResult, artifactKind);
        string? observedValue = ExpectedArtifactObservedValue(artifacts, expectedArtifactName);
        if (observedValue is null)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                code,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                propertyName,
                expectedValue,
                observedValue,
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddMissingArtifactDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics,
        CdcProviderArtifactKind artifactKind,
        string code,
        string propertyName
    )
    {
        CdcProviderArtifactObservation[] artifacts = MatchingUsableArtifacts(
            providerSetupResult,
            artifactKind
        );
        if (artifacts.Length == 1)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                code,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                propertyName,
                "one matched provider setup artifact",
                artifacts.Length == 0 ? "missing" : artifacts.Length.ToString(),
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static void AddSqlServerCaptureInstanceDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSqlServerProviderArtifactNames? expectedArtifactNames,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        CdcProviderArtifactObservation[] captureInstances = CdcConnectorTemplateSharedRules
            .ArtifactInventory(providerSetupResult)
            .Where(artifact => artifact.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance)
            .ToArray();

        foreach (
            CdcSourceTableKind tableKind in CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds
        )
        {
            CdcProviderArtifactObservation[] tableCaptureInstances = captureInstances
                .Where(artifact =>
                    CdcConnectorTemplateSharedRules.SqlServerCaptureInstanceSourceTableKind(artifact)
                    == tableKind
                )
                .ToArray();
            CdcSafeName? expectedCaptureInstanceName = expectedArtifactNames?.CaptureInstanceNames[tableKind];

            if (
                tableCaptureInstances.Length == 1
                && tableCaptureInstances[0].State
                    is CdcProviderArtifactState.Created
                        or CdcProviderArtifactState.Matched
                && (
                    expectedCaptureInstanceName is null
                    || tableCaptureInstances[0].SafeArtifactName.Equals(expectedCaptureInstanceName)
                )
            )
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.artifactInventory.sqlServerCaptureInstance",
                    $"one usable SQL Server capture-instance artifact for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(tableKind)}",
                    SqlServerCaptureInstanceObservedValue(tableCaptureInstances, expectedCaptureInstanceName),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        int extraCaptureInstanceCount = captureInstances.Count(artifact =>
            CdcConnectorTemplateSharedRules.SqlServerCaptureInstanceSourceTableKind(artifact)
                is not { } tableKind
            || !CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds.Contains(tableKind)
        );
        if (extraCaptureInstanceCount == 0)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                "providerSetup.artifactInventory.sqlServerCaptureInstance",
                "only SQL Server capture-instance artifacts for dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                extraCaptureInstanceCount.ToString(),
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddSqlServerSnapshotIsolationDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (providerSetupResult.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        CdcProviderArtifactObservation[] snapshotIsolationArtifacts = CdcConnectorTemplateSharedRules
            .ArtifactInventory(providerSetupResult)
            .Where(artifact =>
                artifact.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && artifact.SafeArtifactName.Equals(SqlServerSnapshotIsolationSafeName)
            )
            .ToArray();
        string? observedValue = SqlServerSnapshotIsolationObservedValue(snapshotIsolationArtifacts);
        if (observedValue is null)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerSnapshotIsolationMetadataRequired,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                "providerSetup.artifactInventory.sqlServerSnapshotIsolation",
                "one usable SQL Server snapshot-isolation artifact with allow_snapshot_isolation=True",
                observedValue,
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcProvider provider,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            safeArtifactOrObjectName,
            expectedValue,
            observedValue,
            provider,
            sourcePhase,
            redactionClassification
        );

    private static CdcProviderArtifactObservation[] MatchingUsableArtifacts(
        CdcProviderSetupResult providerSetupResult,
        CdcProviderArtifactKind artifactKind
    ) =>
        CdcConnectorTemplateSharedRules
            .ArtifactInventory(providerSetupResult)
            .Where(artifact =>
                artifact.ArtifactKind == artifactKind
                && artifact.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
            )
            .ToArray();

    private static CdcProviderArtifactObservation[] ArtifactsOfKind(
        CdcProviderSetupResult providerSetupResult,
        CdcProviderArtifactKind artifactKind
    ) =>
        CdcConnectorTemplateSharedRules
            .ArtifactInventory(providerSetupResult)
            .Where(artifact => artifact.ArtifactKind == artifactKind)
            .ToArray();

    private static string? ExpectedArtifactObservedValue(
        IReadOnlyList<CdcProviderArtifactObservation> artifacts,
        CdcSafeName expectedArtifactName
    )
    {
        if (artifacts.Count == 0)
        {
            return "missing";
        }

        if (artifacts.Count > 1)
        {
            return artifacts.Count.ToString();
        }

        CdcProviderArtifactObservation artifact = artifacts[0];
        if (!artifact.SafeArtifactName.Equals(expectedArtifactName))
        {
            return "unexpected-name";
        }

        return artifact.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
            ? null
            : artifact.State.ToString();
    }

    private static bool HasMalformedSourceColumns(CdcSourceTableInventory sourceTable) =>
        sourceTable.Columns is null || sourceTable.Columns.Any(column => column is null);

    private static bool HasDuplicateColumnNames(CdcSourceTableInventory sourceTable) =>
        sourceTable
            .Columns.GroupBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private static int CountSourceColumns(CdcSourceTableInventory sourceTable, DbColumnName columnName) =>
        sourceTable.Columns.Count(column =>
            string.Equals(column.ColumnName.Value, columnName.Value, StringComparison.Ordinal)
        );

    private static string SqlServerCaptureInstanceObservedValue(
        IReadOnlyList<CdcProviderArtifactObservation> tableCaptureInstances,
        CdcSafeName? expectedCaptureInstanceName
    )
    {
        if (tableCaptureInstances.Count == 0)
        {
            return "missing";
        }

        if (tableCaptureInstances.Count > 1)
        {
            return tableCaptureInstances.Count.ToString();
        }

        CdcProviderArtifactObservation captureInstance = tableCaptureInstances[0];
        if (
            expectedCaptureInstanceName is not null
            && !captureInstance.SafeArtifactName.Equals(expectedCaptureInstanceName)
        )
        {
            return "unexpected-name";
        }

        return captureInstance.State.ToString();
    }

    private static string? SqlServerSnapshotIsolationObservedValue(
        IReadOnlyList<CdcProviderArtifactObservation> artifacts
    )
    {
        if (artifacts.Count == 0)
        {
            return "missing";
        }

        if (artifacts.Count > 1)
        {
            return artifacts.Count.ToString();
        }

        CdcProviderArtifactObservation artifact = artifacts[0];
        if (artifact.State is not (CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched))
        {
            return artifact.State.ToString();
        }

        if (
            artifact.SafeObservedValues is null
            || !artifact.SafeObservedValues.TryGetValue("allow_snapshot_isolation", out string? value)
            || string.IsNullOrWhiteSpace(value)
        )
        {
            return "missing-allow_snapshot_isolation";
        }

        return string.Equals(value, bool.TrueString, StringComparison.Ordinal)
            ? null
            : $"allow_snapshot_isolation={value}";
    }

    private static string ObservedCountOrMissing<T>(IReadOnlyList<T>? values) =>
        values is null || values.Count == 0 ? "missing" : values.Count.ToString();

    private static string SanitizePhysicalIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RedactedValue;
        }

        return new string(
            value
                .Select(character =>
                    char.IsLetterOrDigit(character) || character is '_' or '.' ? character : '_'
                )
                .ToArray()
        );
    }
}
