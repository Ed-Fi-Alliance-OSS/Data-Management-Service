// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc;

internal interface ICdcConnectorTemplateEffectiveConfigValidator
{
    CdcConnectorTemplateResult ValidateEffectiveConfig(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase
    );
}

internal sealed class CdcConnectorTemplateEffectiveConfigValidator(ICdcConnectorTemplateRenderer renderer)
    : ICdcConnectorTemplateEffectiveConfigValidator
{
    private const string RedactedValue = "[redacted]";
    private const string TopicHeartbeatName = "topic.heartbeat.name";

    public CdcConnectorTemplateResult ValidateEffectiveConfig(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (
            sourcePhase
            is not (CdcConnectorTemplateSourcePhase.Preflight or CdcConnectorTemplateSourcePhase.LiveReadBack)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourcePhase),
                sourcePhase,
                "CDC connector template effective-config validation supports only registration preflight or live read-back phases."
            );
        }

        CdcConnectorTemplateRequest templateRequest = request.TemplateRequest;
        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        AddProviderSetupEvidenceDiagnostics(request, sourcePhase, diagnostics);
        if (HasErrors(diagnostics))
        {
            return BuildResult(
                templateRequest.BindingIdentity,
                CdcConnectorTemplateOutcome.ValidationFailed,
                new SortedDictionary<string, string>(StringComparer.Ordinal),
                registrationPayload: null,
                configSha256: null,
                diagnostics
            );
        }

        CdcConnectorTemplateRequest expectedTemplateRequest = BuildExpectedTemplateRequest(
            templateRequest,
            request.ProviderSetupEvidence
        );
        CdcConnectorTemplateResult expectedResult = renderer.Render(expectedTemplateRequest);
        if (expectedResult.Outcome == CdcConnectorTemplateOutcome.ValidationFailed)
        {
            return BuildResult(
                templateRequest.BindingIdentity,
                CdcConnectorTemplateOutcome.ValidationFailed,
                expectedResult.Config,
                expectedResult.RegistrationPayload,
                expectedResult.ConfigSha256,
                expectedResult.Diagnostics.Select(diagnostic => WithSourcePhase(diagnostic, sourcePhase))
            );
        }

        AddEffectiveConfigDiagnostics(request, expectedResult.Config, sourcePhase, diagnostics);
        AddSourcePartitionDiagnostics(request, expectedResult.Config, sourcePhase, diagnostics);

        if (!HasErrors(diagnostics))
        {
            return expectedResult;
        }

        return BuildResult(
            templateRequest.BindingIdentity,
            CdcConnectorTemplateOutcome.ValidationFailed,
            expectedResult.Config,
            expectedResult.RegistrationPayload,
            expectedResult.ConfigSha256,
            diagnostics
        );
    }

    private static CdcConnectorTemplateRequest BuildExpectedTemplateRequest(
        CdcConnectorTemplateRequest templateRequest,
        CdcConnectorProviderSetupEvidence providerSetupEvidence
    ) =>
        new(
            templateRequest.BindingIdentity,
            providerSetupEvidence,
            templateRequest.DeploymentPolicy,
            templateRequest.ProviderConnectionProperties,
            templateRequest.KafkaClientSecurityProperties
        );

    private static void AddProviderSetupEvidenceDiagnostics(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        CdcConnectorTemplateBindingIdentity bindingIdentity = request.TemplateRequest.BindingIdentity;
        CdcConnectorProviderSetupEvidence providerSetupEvidence = request.ProviderSetupEvidence;
        CdcProviderSetupResult result = providerSetupEvidence.Result;

        if (result.Provider != bindingIdentity.Provider)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.provider",
                    bindingIdentity.Provider.ToString(),
                    result.Provider.ToString(),
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (
            result.Outcome
            is not (CdcProviderSetupOutcome.CreatedOrMatched or CdcProviderSetupOutcome.ExactMatch)
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.outcome",
                    "CreatedOrMatched or ExactMatch",
                    result.Outcome.ToString(),
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (providerSetupEvidence.BindingGeneration != bindingIdentity.BindingGeneration)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.bindingGeneration",
                    bindingIdentity.BindingGeneration.ToString(),
                    providerSetupEvidence.BindingGeneration.ToString(),
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (!result.BoundPhysicalSourceFingerprint.Equals(bindingIdentity.BoundPhysicalSourceFingerprint))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.boundPhysicalSourceFingerprint",
                    "binding physical-source fingerprint",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (
            result.ObservedSourceFingerprint is null
            || !result.ObservedSourceFingerprint.Equals(bindingIdentity.BoundPhysicalSourceFingerprint)
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.observedPhysicalSourceFingerprint",
                    "binding physical-source fingerprint",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (!CdcConnectorTemplateSharedRules.HasRequiredSourceInventory(result.SourceTableInventory))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.sourceTableInventory",
                    "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (!CdcConnectorTemplateSharedRules.HasExpectedMessageKeyColumns(result.ExpectedMessageKeyColumns))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.expectedMessageKeyColumns",
                    "DocumentUuid keys for document sources",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (result.HeartbeatActionQuery is null)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.heartbeatActionQuery",
                    "fresh provider heartbeat action query",
                    null,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (HasErrors(diagnostics) || HasProviderPrerequisiteErrors(request, sourcePhase))
        {
            return;
        }

        AddProviderSetupEvidenceDriftDiagnostics(request, sourcePhase, diagnostics);
    }

    private static bool HasProviderPrerequisiteErrors(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase
    ) =>
        CdcProviderSetupPrerequisiteRules
            .Validate(
                request.ProviderSetupEvidence.Result,
                request.TemplateRequest.ConnectorName,
                sourcePhase
            )
            .Any(diagnostic => diagnostic.Severity == CdcConnectorTemplateDiagnosticSeverity.Error);

    private static void AddProviderSetupEvidenceDriftDiagnostics(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        CdcProviderSetupResult renderedResult = request.TemplateRequest.ProviderSetupEvidence.Result;
        CdcProviderSetupResult freshResult = request.ProviderSetupEvidence.Result;

        if (
            !SourceTableInventoriesMatch(
                renderedResult.SourceTableInventory,
                freshResult.SourceTableInventory
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.sourceTableInventory",
                    "rendered request source-table inventory",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (
            !MessageKeyInventoriesMatch(
                renderedResult.ExpectedMessageKeyColumns,
                freshResult.ExpectedMessageKeyColumns
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.expectedMessageKeyColumns",
                    "rendered request message-key inventory",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (
            !HeartbeatActionQueriesMatch(
                renderedResult.HeartbeatActionQuery,
                freshResult.HeartbeatActionQuery
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.heartbeatActionQuery",
                    "rendered request heartbeat action query",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static bool SourceTableInventoriesMatch(
        IReadOnlyList<CdcSourceTableInventory>? expectedInventory,
        IReadOnlyList<CdcSourceTableInventory>? observedInventory
    )
    {
        if (
            expectedInventory is null
            || observedInventory is null
            || expectedInventory.Count != observedInventory.Count
        )
        {
            return false;
        }

        IReadOnlyDictionary<CdcSourceTableKind, CdcSourceTableInventory>? expectedTablesByKind =
            SourceTablesByKind(expectedInventory);
        IReadOnlyDictionary<CdcSourceTableKind, CdcSourceTableInventory>? observedTablesByKind =
            SourceTablesByKind(observedInventory);
        if (
            expectedTablesByKind is null
            || observedTablesByKind is null
            || expectedTablesByKind.Count != observedTablesByKind.Count
        )
        {
            return false;
        }

        foreach (
            CdcSourceTableKind tableKind in expectedTablesByKind
                .Keys.OrderBy(CdcSourceInventoryContract.RequiredSourceTableOrdinal)
                .ThenBy(kind => kind.ToString(), StringComparer.Ordinal)
        )
        {
            if (
                !observedTablesByKind.TryGetValue(tableKind, out CdcSourceTableInventory? observedTable)
                || !SourceTablesMatch(expectedTablesByKind[tableKind], observedTable)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<CdcSourceTableKind, CdcSourceTableInventory>? SourceTablesByKind(
        IReadOnlyList<CdcSourceTableInventory> inventory
    )
    {
        Dictionary<CdcSourceTableKind, CdcSourceTableInventory> tablesByKind = [];

        foreach (CdcSourceTableInventory? table in inventory)
        {
            if (table is null || !tablesByKind.TryAdd(table.TableKind, table))
            {
                return null;
            }
        }

        return tablesByKind;
    }

    private static bool SourceTablesMatch(
        CdcSourceTableInventory? expectedTable,
        CdcSourceTableInventory? observedTable
    ) =>
        expectedTable is not null
        && observedTable is not null
        && expectedTable.TableKind == observedTable.TableKind
        && expectedTable.TableName.Equals(observedTable.TableName)
        && string.Equals(
            expectedTable.EmittedQuotedTableName,
            observedTable.EmittedQuotedTableName,
            StringComparison.Ordinal
        )
        && SourceColumnsMatch(expectedTable.Columns, observedTable.Columns);

    private static bool SourceColumnsMatch(
        IReadOnlyList<CdcSourceColumnInventory>? expectedColumns,
        IReadOnlyList<CdcSourceColumnInventory>? observedColumns
    )
    {
        if (
            expectedColumns is null
            || observedColumns is null
            || expectedColumns.Count != observedColumns.Count
        )
        {
            return false;
        }

        IReadOnlyDictionary<string, CdcSourceColumnInventory>? expectedColumnsByName = SourceColumnsByName(
            expectedColumns
        );
        IReadOnlyDictionary<string, CdcSourceColumnInventory>? observedColumnsByName = SourceColumnsByName(
            observedColumns
        );
        if (
            expectedColumnsByName is null
            || observedColumnsByName is null
            || expectedColumnsByName.Count != observedColumnsByName.Count
        )
        {
            return false;
        }

        foreach (string columnName in expectedColumnsByName.Keys.Order(StringComparer.Ordinal))
        {
            if (
                !observedColumnsByName.TryGetValue(columnName, out CdcSourceColumnInventory? observedColumn)
                || !SourceColumnMatches(expectedColumnsByName[columnName], observedColumn)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, CdcSourceColumnInventory>? SourceColumnsByName(
        IReadOnlyList<CdcSourceColumnInventory> columns
    )
    {
        Dictionary<string, CdcSourceColumnInventory> columnsByName = new(StringComparer.Ordinal);

        foreach (CdcSourceColumnInventory? column in columns)
        {
            if (column is null || !columnsByName.TryAdd(column.ColumnName.Value, column))
            {
                return null;
            }
        }

        return columnsByName;
    }

    private static bool SourceColumnMatches(
        CdcSourceColumnInventory? expectedColumn,
        CdcSourceColumnInventory? observedColumn
    ) =>
        expectedColumn is not null
        && observedColumn is not null
        && string.Equals(
            expectedColumn.ColumnName.Value,
            observedColumn.ColumnName.Value,
            StringComparison.Ordinal
        )
        && string.Equals(
            expectedColumn.EmittedQuotedColumnName,
            observedColumn.EmittedQuotedColumnName,
            StringComparison.Ordinal
        )
        && expectedColumn.Ordinal == observedColumn.Ordinal
        && string.Equals(
            expectedColumn.ProviderDataType,
            observedColumn.ProviderDataType,
            StringComparison.Ordinal
        )
        && expectedColumn.IsNullable == observedColumn.IsNullable;

    private static bool MessageKeyInventoriesMatch(
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedInventory,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? observedInventory
    )
    {
        if (
            expectedInventory is null
            || observedInventory is null
            || expectedInventory.Count != observedInventory.Count
        )
        {
            return false;
        }

        IReadOnlyDictionary<CdcSourceTableKind, CdcExpectedMessageKeyColumns>? expectedColumnsByKind =
            MessageKeyColumnsByKind(expectedInventory);
        IReadOnlyDictionary<CdcSourceTableKind, CdcExpectedMessageKeyColumns>? observedColumnsByKind =
            MessageKeyColumnsByKind(observedInventory);
        if (
            expectedColumnsByKind is null
            || observedColumnsByKind is null
            || expectedColumnsByKind.Count != observedColumnsByKind.Count
        )
        {
            return false;
        }

        foreach (
            CdcSourceTableKind tableKind in expectedColumnsByKind
                .Keys.OrderBy(CdcSourceInventoryContract.RequiredSourceTableOrdinal)
                .ThenBy(kind => kind.ToString(), StringComparer.Ordinal)
        )
        {
            if (
                !observedColumnsByKind.TryGetValue(
                    tableKind,
                    out CdcExpectedMessageKeyColumns? observedColumns
                ) || !MessageKeyColumnsMatch(expectedColumnsByKind[tableKind], observedColumns)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<
        CdcSourceTableKind,
        CdcExpectedMessageKeyColumns
    >? MessageKeyColumnsByKind(IReadOnlyList<CdcExpectedMessageKeyColumns> inventory)
    {
        Dictionary<CdcSourceTableKind, CdcExpectedMessageKeyColumns> columnsByKind = [];

        foreach (CdcExpectedMessageKeyColumns? columns in inventory)
        {
            if (columns is null || !columnsByKind.TryAdd(columns.TableKind, columns))
            {
                return null;
            }
        }

        return columnsByKind;
    }

    private static bool MessageKeyColumnsMatch(
        CdcExpectedMessageKeyColumns? expectedColumns,
        CdcExpectedMessageKeyColumns? observedColumns
    ) =>
        expectedColumns is not null
        && observedColumns is not null
        && expectedColumns.TableKind == observedColumns.TableKind
        && ColumnNamesMatch(expectedColumns.KeyColumns, observedColumns.KeyColumns);

    private static bool ColumnNamesMatch(
        IReadOnlyList<DbColumnName>? expectedColumns,
        IReadOnlyList<DbColumnName>? observedColumns
    )
    {
        if (
            expectedColumns is null
            || observedColumns is null
            || expectedColumns.Count != observedColumns.Count
        )
        {
            return false;
        }

        for (int index = 0; index < expectedColumns.Count; index++)
        {
            if (
                !string.Equals(
                    expectedColumns[index].Value,
                    observedColumns[index].Value,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool HeartbeatActionQueriesMatch(
        CdcHeartbeatActionQuery? expectedQuery,
        CdcHeartbeatActionQuery? observedQuery
    )
    {
        if (expectedQuery is null || observedQuery is null)
        {
            return expectedQuery is null && observedQuery is null;
        }

        return string.Equals(expectedQuery.Sql, observedQuery.Sql, StringComparison.Ordinal)
            && string.Equals(expectedQuery.Sha256Hash, observedQuery.Sha256Hash, StringComparison.Ordinal);
    }

    private static void AddEffectiveConfigDiagnostics(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        IReadOnlyDictionary<string, string> expectedConfig,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        foreach (var expectedProperty in expectedConfig)
        {
            if (!request.EffectiveConfig.TryGetValue(expectedProperty.Key, out string? observedValue))
            {
                diagnostics.Add(
                    BuildPropertyDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMissing,
                        expectedProperty.Key,
                        expectedProperty.Value,
                        null,
                        request.TemplateRequest,
                        sourcePhase
                    )
                );
                continue;
            }

            if (CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(expectedProperty.Key))
            {
                if (IsAcceptedSecretReadBack(expectedProperty.Value, observedValue, sourcePhase))
                {
                    continue;
                }

                diagnostics.Add(
                    BuildSecretDiagnostic(
                        expectedProperty.Key,
                        request.TemplateRequest,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.SecretValue
                    )
                );
                continue;
            }

            if (!string.Equals(expectedProperty.Value, observedValue, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    BuildPropertyDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMismatch,
                        expectedProperty.Key,
                        expectedProperty.Value,
                        observedValue,
                        request.TemplateRequest,
                        sourcePhase
                    )
                );
            }
        }

        foreach (var observedProperty in request.EffectiveConfig)
        {
            if (expectedConfig.ContainsKey(observedProperty.Key))
            {
                continue;
            }

            if (observedProperty.Key == TopicHeartbeatName && observedProperty.Value.Length == 0)
            {
                continue;
            }

            diagnostics.Add(
                BuildUnexpectedPropertyDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty,
                    observedProperty.Key,
                    expectedValue: "absent",
                    observedProperty.Value,
                    request.TemplateRequest,
                    sourcePhase
                )
            );
        }
    }

    private static bool IsAcceptedSecretReadBack(
        string expectedValue,
        string observedValue,
        CdcConnectorTemplateSourcePhase sourcePhase
    ) =>
        observedValue.Length > 0
        && (
            string.Equals(expectedValue, observedValue, StringComparison.Ordinal)
            || (
                sourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && (
                    string.Equals(observedValue, "[hidden]", StringComparison.Ordinal)
                    || observedValue.All(character => character == '*')
                )
            )
        );

    private static void AddSourcePartitionDiagnostics(
        CdcConnectorTemplateEffectiveConfigValidationRequest request,
        IReadOnlyDictionary<string, string> expectedConfig,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (request.SourcePartitionEvidence is null)
        {
            if (sourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack)
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch,
                        CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                        "source.partition",
                        "actual connector source partition evidence",
                        null,
                        request.TemplateRequest,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.Safe
                    )
                );
            }

            return;
        }

        IReadOnlyDictionary<string, string> sourcePartition = request.SourcePartitionEvidence.Properties;
        IReadOnlyDictionary<string, string> expectedSourcePartition = BuildExpectedSourcePartition(
            request.TemplateRequest.Provider,
            expectedConfig
        );

        foreach (var expectedProperty in expectedSourcePartition)
        {
            string propertyName = $"source.partition.{expectedProperty.Key}";
            if (!sourcePartition.TryGetValue(expectedProperty.Key, out string? observedValue))
            {
                diagnostics.Add(
                    BuildSourcePartitionDiagnostic(
                        propertyName,
                        expectedProperty.Value,
                        null,
                        request.TemplateRequest,
                        sourcePhase
                    )
                );
                continue;
            }

            if (!string.Equals(expectedProperty.Value, observedValue, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    BuildSourcePartitionDiagnostic(
                        propertyName,
                        expectedProperty.Value,
                        observedValue,
                        request.TemplateRequest,
                        sourcePhase
                    )
                );
            }
        }

        foreach (
            string observedKey in sourcePartition.Keys.Except(
                expectedSourcePartition.Keys,
                StringComparer.Ordinal
            )
        )
        {
            diagnostics.Add(
                BuildSourcePartitionDiagnostic(
                    $"source.partition.{observedKey}",
                    expectedValue: "absent",
                    observedValue: sourcePartition[observedKey],
                    request.TemplateRequest,
                    sourcePhase
                )
            );
        }
    }

    private static IReadOnlyDictionary<string, string> BuildExpectedSourcePartition(
        CdcProvider provider,
        IReadOnlyDictionary<string, string> expectedConfig
    )
    {
        var sourcePartition = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["server"] = expectedConfig["topic.prefix"],
        };

        if (provider == CdcProvider.SqlServer)
        {
            sourcePartition["database"] = expectedConfig["database.names"];
        }

        return sourcePartition;
    }

    private static CdcConnectorTemplateDiagnostic BuildPropertyDiagnostic(
        string code,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase
    )
    {
        CdcConnectorTemplateRedactionClassification redactionClassification =
            RedactionClassificationForProperty(propertyName);

        return BuildDiagnostic(
            code,
            CategoryForProperty(propertyName),
            propertyName,
            RedactValueForDiagnostic(expectedValue, redactionClassification),
            RedactValueForDiagnostic(observedValue, redactionClassification),
            request,
            sourcePhase,
            redactionClassification
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildUnexpectedPropertyDiagnostic(
        string code,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase
    )
    {
        CdcConnectorTemplateRedactionClassification redactionClassification =
            RedactionClassificationForUnexpectedEffectiveConfigProperty(propertyName);

        return BuildDiagnostic(
            code,
            CategoryForProperty(propertyName),
            propertyName,
            RedactUnexpectedExpectedValueForDiagnostic(expectedValue, redactionClassification),
            RedactValueForDiagnostic(observedValue, redactionClassification),
            request,
            sourcePhase,
            redactionClassification
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildSecretDiagnostic(
        string propertyName,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        BuildDiagnostic(
            CdcConnectorTemplateDiagnosticCodes.LiveReadBackSecretMismatch,
            CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation,
            propertyName,
            RedactedValue,
            RedactedValue,
            request,
            sourcePhase,
            redactionClassification
        );

    private static CdcConnectorTemplateDiagnostic BuildSourcePartitionDiagnostic(
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase
    )
    {
        CdcConnectorTemplateRedactionClassification redactionClassification =
            RedactionClassificationForSourcePartitionProperty(propertyName, expectedValue == "absent");

        return BuildDiagnostic(
            CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch,
            CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
            propertyName,
            RedactUnexpectedExpectedValueForDiagnostic(expectedValue, redactionClassification),
            RedactValueForDiagnostic(observedValue, redactionClassification),
            request,
            sourcePhase,
            redactionClassification
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            request.ConnectorName,
            expectedValue,
            observedValue,
            request.Provider,
            sourcePhase,
            redactionClassification
        );

    private static CdcConnectorTemplateDiagnostic WithSourcePhase(
        CdcConnectorTemplateDiagnostic diagnostic,
        CdcConnectorTemplateSourcePhase sourcePhase
    ) =>
        new(
            diagnostic.Code,
            diagnostic.Category,
            diagnostic.Severity,
            diagnostic.PropertyName,
            diagnostic.SafeArtifactOrObjectName,
            diagnostic.ExpectedValue,
            diagnostic.ObservedValue,
            diagnostic.Provider,
            sourcePhase,
            diagnostic.RedactionClassification
        );

    private static CdcConnectorTemplateResult BuildResult(
        CdcConnectorTemplateBindingIdentity bindingIdentity,
        CdcConnectorTemplateOutcome outcome,
        IReadOnlyDictionary<string, string> config,
        CdcKafkaConnectRegistrationPayload? registrationPayload,
        string? configSha256,
        IEnumerable<CdcConnectorTemplateDiagnostic> diagnostics
    ) =>
        new(
            bindingIdentity,
            outcome,
            config,
            registrationPayload,
            redactedArtifactPayload: null,
            configSha256,
            diagnostics.ToArray()
        );

    private static bool HasErrors(IEnumerable<CdcConnectorTemplateDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic => diagnostic.Severity == CdcConnectorTemplateDiagnosticSeverity.Error);

    private static CdcConnectorTemplateDiagnosticCategory CategoryForProperty(string propertyName)
    {
        if (propertyName == "table.include.list")
        {
            return CdcConnectorTemplateDiagnosticCategory.IncludeListViolation;
        }

        if (propertyName == "message.key.columns")
        {
            return CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation;
        }

        if (propertyName == "transforms" || propertyName.StartsWith("transforms.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation;
        }

        if (
            propertyName
            is "key.converter"
                or "key.converter.schemas.enable"
                or "value.converter"
                or "value.converter.schemas.enable"
                or "value.converter.decimal.format"
                or "tombstones.on.delete"
        )
        {
            return CdcConnectorTemplateDiagnosticCategory.ConverterConfigurationViolation;
        }

        if (propertyName.StartsWith("topic.", StringComparison.Ordinal) || propertyName == "topic.prefix")
        {
            return CdcConnectorTemplateDiagnosticCategory.TopicNamingConfigurationViolation;
        }

        if (
            propertyName.StartsWith("heartbeat.", StringComparison.Ordinal)
            || propertyName == "poll.interval.ms"
        )
        {
            return CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation;
        }

        if (propertyName.StartsWith("schema.history.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.SchemaHistoryConfigurationViolation;
        }

        if (propertyName.StartsWith("producer.override.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.ProducerPolicyViolation;
        }

        if (
            propertyName.StartsWith("database.", StringComparison.Ordinal)
            || CdcConnectorTemplateInputValidator.IsSqlServerDriverConnectionProperty(propertyName)
        )
        {
            return CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation;
        }

        if (CdcConnectorTemplateInputValidator.IsKafkaClientSecurityProperty(propertyName))
        {
            return CdcConnectorTemplateDiagnosticCategory.KafkaSecurityPropertyViolation;
        }

        return CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch;
    }

    private static CdcConnectorTemplateRedactionClassification RedactionClassificationForUnexpectedEffectiveConfigProperty(
        string propertyName
    )
    {
        if (IsSecretBearingUnexpectedProperty(propertyName))
        {
            return CdcConnectorTemplateRedactionClassification.SecretValue;
        }

        return CdcConnectorTemplateRedactionClassification.PhysicalIdentifier;
    }

    private static CdcConnectorTemplateRedactionClassification RedactionClassificationForProperty(
        string propertyName
    )
    {
        if (CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(propertyName))
        {
            return CdcConnectorTemplateRedactionClassification.SecretValue;
        }

        if (
            propertyName.StartsWith("database.", StringComparison.Ordinal)
            || propertyName.StartsWith("driver.", StringComparison.Ordinal)
            || CdcConnectorTemplateInputValidator.IsKafkaSecurityMaterialRenderedProperty(propertyName)
            || propertyName
                is "table.include.list"
                    or "message.key.columns"
                    or "heartbeat.action.query"
                    or "publication.name"
                    or "slot.name"
                    or "topic.prefix"
                    or "transforms.documentState.target.topic"
                    or "transforms.documentState.progress.topic"
                    or "schema.history.internal.kafka.bootstrap.servers"
                    or "schema.history.internal.kafka.topic"
        )
        {
            return CdcConnectorTemplateRedactionClassification.PhysicalIdentifier;
        }

        return CdcConnectorTemplateRedactionClassification.Safe;
    }

    private static CdcConnectorTemplateRedactionClassification RedactionClassificationForSourcePartitionProperty(
        string propertyName,
        bool isUnexpectedProperty
    )
    {
        string partitionKey = propertyName.StartsWith("source.partition.", StringComparison.Ordinal)
            ? propertyName["source.partition.".Length..]
            : propertyName;

        if (IsSecretBearingUnexpectedProperty(partitionKey))
        {
            return CdcConnectorTemplateRedactionClassification.SecretValue;
        }

        if (partitionKey is "server" or "database")
        {
            return CdcConnectorTemplateRedactionClassification.PhysicalIdentifier;
        }

        if (isUnexpectedProperty)
        {
            return CdcConnectorTemplateRedactionClassification.PhysicalIdentifier;
        }

        return CdcConnectorTemplateRedactionClassification.Safe;
    }

    private static bool IsSecretBearingUnexpectedProperty(string propertyName) =>
        CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(propertyName)
        || propertyName.EndsWith(".password", StringComparison.Ordinal)
        || propertyName is "sasl.jaas.config" or "ssl.keystore.key";

    private static string? RedactUnexpectedExpectedValueForDiagnostic(
        string? value,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) => value == "absent" ? value : RedactValueForDiagnostic(value, redactionClassification);

    private static string? RedactValueForDiagnostic(
        string? value,
        CdcConnectorTemplateRedactionClassification redactionClassification
    )
    {
        if (value is null)
        {
            return null;
        }

        return
            redactionClassification
                is CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    or CdcConnectorTemplateRedactionClassification.SecretValue
                    or CdcConnectorTemplateRedactionClassification.MaskedSecret
            ? RedactedValue
            : value;
    }
}
