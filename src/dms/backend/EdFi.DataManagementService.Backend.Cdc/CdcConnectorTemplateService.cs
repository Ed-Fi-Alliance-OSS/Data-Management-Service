// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Cdc;

public interface ICdcConnectorTemplateService
{
    /// <summary>
    /// Reports whether provider setup evidence alone is sufficient for connector-template rendering.
    /// This does not validate binding identity, deployment policy, connection properties, Kafka
    /// security properties, Kafka Connect REST state, provider DDL, broker access rules, source
    /// offsets, or lifecycle operations.
    /// </summary>
    CdcProviderSetupReadiness GetProviderSetupReadiness(CdcProviderSetupResult providerSetupResult);

    CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    );

    CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request);

    CdcConnectorTemplateResult ValidateRegistrationPreflight(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    );

    CdcConnectorTemplateResult ValidateLiveReadBack(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    );
}

internal sealed class CdcConnectorTemplateService(
    ICdcConnectorTemplateInputValidator inputValidator,
    ICdcConnectorTemplateRenderer renderer,
    ICdcConnectorTemplateEffectiveConfigValidator effectiveConfigValidator
) : ICdcConnectorTemplateService
{
    public CdcProviderSetupReadiness GetProviderSetupReadiness(CdcProviderSetupResult providerSetupResult)
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        CdcConnectorTemplateValidationResult validationResult = CdcProviderSetupReadinessRules.Validate(
            providerSetupResult
        );

        return new CdcProviderSetupReadiness(
            provider: providerSetupResult.Provider,
            outcome: providerSetupResult.Outcome,
            canRenderTemplate: validationResult.IsValid,
            diagnostics: validationResult.Diagnostics
        );
    }

    public CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    ) => inputValidator.ValidateRequest(request, sourcePhase);

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request) => renderer.Render(request);

    public CdcConnectorTemplateResult ValidateRegistrationPreflight(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    ) =>
        effectiveConfigValidator.ValidateEffectiveConfig(
            request,
            CdcConnectorTemplateSourcePhase.RegistrationPreflight
        );

    public CdcConnectorTemplateResult ValidateLiveReadBack(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    ) =>
        effectiveConfigValidator.ValidateEffectiveConfig(
            request,
            CdcConnectorTemplateSourcePhase.LiveReadBack
        );
}

public sealed record CdcProviderSetupReadiness
{
    public CdcProviderSetupReadiness(
        CdcProvider provider,
        CdcProviderSetupOutcome outcome,
        bool canRenderTemplate,
        IReadOnlyList<CdcConnectorTemplateDiagnostic>? diagnostics = null
    )
    {
        Provider = provider;
        Outcome = outcome;
        CanRenderTemplate = canRenderTemplate;
        Diagnostics = diagnostics?.ToArray() ?? [];
    }

    public CdcProvider Provider { get; }

    public CdcProviderSetupOutcome Outcome { get; }

    /// <summary>
    /// True only when the provider setup result contains the provider evidence needed before a
    /// connector-template request can render. This is not a combined CDC deployment readiness flag.
    /// </summary>
    public bool CanRenderTemplate { get; }

    public IReadOnlyList<CdcConnectorTemplateDiagnostic> Diagnostics { get; }
}

internal static class CdcProviderSetupReadinessRules
{
    private const string RedactedValue = "[redacted]";

    internal static CdcConnectorTemplateValidationResult Validate(CdcProviderSetupResult providerSetupResult)
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        AddOutcomeDiagnostic(providerSetupResult, diagnostics);
        AddSourceFingerprintDiagnostics(providerSetupResult, diagnostics);
        AddHeartbeatActionQueryDiagnostic(providerSetupResult, diagnostics);
        AddSourceTableInventoryDiagnostics(providerSetupResult, diagnostics);
        AddExpectedMessageKeyColumnsDiagnostics(providerSetupResult, diagnostics);
        AddProviderArtifactDiagnostics(providerSetupResult, diagnostics);

        return new CdcConnectorTemplateValidationResult(diagnostics);
    }

    private static void AddOutcomeDiagnostic(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (
            providerSetupResult.Outcome
            is CdcProviderSetupOutcome.CreatedOrMatched
                or CdcProviderSetupOutcome.ExactMatch
        )
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.ProviderSetupResultNotReady,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                "providerSetup.outcome",
                "CreatedOrMatched or ExactMatch",
                providerSetupResult.Outcome.ToString(),
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static void AddSourceFingerprintDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        bool boundFingerprintIsValid = IsValidSourceFingerprint(
            providerSetupResult.BoundPhysicalSourceFingerprint
        );
        if (!boundFingerprintIsValid)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceFingerprintEvidenceRequired,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                    "providerSetup.boundPhysicalSourceFingerprint",
                    "valid bound physical-source fingerprint",
                    RedactedValue,
                    providerSetupResult.Provider,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (
            providerSetupResult.ObservedSourceFingerprint is not null
            && IsValidSourceFingerprint(providerSetupResult.ObservedSourceFingerprint)
            && boundFingerprintIsValid
            && providerSetupResult.ObservedSourceFingerprint.Equals(
                providerSetupResult.BoundPhysicalSourceFingerprint
            )
        )
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SourceFingerprintEvidenceRequired,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                "providerSetup.observedPhysicalSourceFingerprint",
                "matching observed physical-source fingerprint",
                providerSetupResult.ObservedSourceFingerprint is null ? "missing" : RedactedValue,
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddHeartbeatActionQueryDiagnostic(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (providerSetupResult.HeartbeatActionQuery is not null)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired,
                CdcConnectorTemplateDiagnosticCategory.Heartbeat,
                "providerSetup.heartbeatActionQuery",
                "fresh provider heartbeat action query",
                "missing",
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddSourceTableInventoryDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory =
            providerSetupResult.SourceTableInventory;

        if (HasRequiredSourceInventory(sourceTableInventory))
        {
            AddSourceTableNameDiagnostics(providerSetupResult, sourceTableInventory, diagnostics);
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                "providerSetup.sourceTableInventory",
                "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                ObservedCountOrMissing(sourceTableInventory),
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddSourceTableNameDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory,
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
                    CdcConnectorTemplateDiagnosticCategory.IncludeList,
                    "table.include.list",
                    $"{expectedTableName.Schema.Value}.{expectedTableName.Name}",
                    SanitizePhysicalIdentifier(
                        $"{sourceTable.TableName.Schema.Value}.{sourceTable.TableName.Name}"
                    ),
                    providerSetupResult.Provider,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static void AddExpectedMessageKeyColumnsDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns =
            providerSetupResult.ExpectedMessageKeyColumns;
        if (!HasExpectedMessageKeyColumns(expectedMessageKeyColumns))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.MessageKey,
                    "providerSetup.expectedMessageKeyColumns",
                    "DocumentUuid keys for document sources",
                    ObservedCountOrMissing(expectedMessageKeyColumns),
                    providerSetupResult.Provider,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
            return;
        }

        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory =
            providerSetupResult.SourceTableInventory;
        if (!HasRequiredSourceInventory(sourceTableInventory))
        {
            return;
        }

        foreach (CdcExpectedMessageKeyColumns messageKeyColumns in expectedMessageKeyColumns)
        {
            CdcSourceTableInventory sourceTable = sourceTableInventory.Single(table =>
                table.TableKind == messageKeyColumns.TableKind
            );

            if (HasDuplicateColumnNames(sourceTable))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKey,
                        "message.key.columns",
                        $"unique source column names for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "duplicate",
                        providerSetupResult.Provider,
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
                        CdcConnectorTemplateDiagnosticCategory.MessageKey,
                        "message.key.columns",
                        $"source column {keyColumn.Value} for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "missing",
                        providerSetupResult.Provider,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
            }
        }
    }

    private static void AddProviderArtifactDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (providerSetupResult.Provider == CdcProvider.Postgresql)
        {
            AddMissingArtifactDiagnosticIfNeeded(
                providerSetupResult,
                diagnostics,
                CdcProviderArtifactKind.PostgresqlPublication,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
                "publication.name"
            );
            AddMissingArtifactDiagnosticIfNeeded(
                providerSetupResult,
                diagnostics,
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired,
                "slot.name"
            );
        }
        else if (providerSetupResult.Provider == CdcProvider.SqlServer)
        {
            AddSqlServerCaptureInstanceDiagnostics(providerSetupResult, diagnostics);
        }
    }

    private static void AddMissingArtifactDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
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
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                propertyName,
                "one matched provider setup artifact",
                artifacts.Length == 0 ? "missing" : artifacts.Length.ToString(),
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static void AddSqlServerCaptureInstanceDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        CdcProviderArtifactObservation[] captureInstances = ArtifactInventory(providerSetupResult)
            .Where(artifact => artifact.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance)
            .ToArray();

        foreach (
            CdcSourceTableKind tableKind in CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds
        )
        {
            CdcProviderArtifactObservation[] tableCaptureInstances = captureInstances
                .Where(artifact => SqlServerCaptureInstanceSourceTableKind(artifact) == tableKind)
                .ToArray();

            if (
                tableCaptureInstances.Length == 1
                && tableCaptureInstances[0].State
                    is CdcProviderArtifactState.Created
                        or CdcProviderArtifactState.Matched
            )
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                    "providerSetup.artifactInventory.sqlServerCaptureInstance",
                    $"one usable SQL Server capture-instance artifact for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(tableKind)}",
                    SqlServerCaptureInstanceObservedValue(tableCaptureInstances),
                    providerSetupResult.Provider,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        int extraCaptureInstanceCount = captureInstances.Count(artifact =>
            SqlServerCaptureInstanceSourceTableKind(artifact) is not { } tableKind
            || !CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds.Contains(tableKind)
        );
        if (extraCaptureInstanceCount == 0)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                "providerSetup.artifactInventory.sqlServerCaptureInstance",
                "only SQL Server capture-instance artifacts for dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                extraCaptureInstanceCount.ToString(),
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
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
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            safeArtifactOrObjectName: null,
            expectedValue,
            observedValue,
            provider,
            CdcConnectorTemplateSourcePhase.RequestValidation,
            redactionClassification
        );

    private static bool HasRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory
    ) =>
        sourceTableInventory is not null
        && CdcConnectorTemplateSharedRules.HasRequiredSourceInventory(sourceTableInventory);

    private static bool HasExpectedMessageKeyColumns(
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns
    ) =>
        expectedMessageKeyColumns is not null
        && CdcConnectorTemplateSharedRules.HasExpectedMessageKeyColumns(expectedMessageKeyColumns);

    private static bool HasDuplicateColumnNames(CdcSourceTableInventory sourceTable) =>
        sourceTable
            .Columns.GroupBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private static int CountSourceColumns(CdcSourceTableInventory sourceTable, DbColumnName columnName) =>
        sourceTable.Columns.Count(column =>
            string.Equals(column.ColumnName.Value, columnName.Value, StringComparison.Ordinal)
        );

    private static CdcProviderArtifactObservation[] MatchingUsableArtifacts(
        CdcProviderSetupResult providerSetupResult,
        CdcProviderArtifactKind artifactKind
    ) =>
        ArtifactInventory(providerSetupResult)
            .Where(artifact =>
                artifact.ArtifactKind == artifactKind
                && artifact.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
            )
            .ToArray();

    private static CdcProviderArtifactObservation[] ArtifactInventory(
        CdcProviderSetupResult providerSetupResult
    ) => providerSetupResult.ArtifactInventory is null ? [] : providerSetupResult.ArtifactInventory.ToArray();

    private static CdcSourceTableKind? SqlServerCaptureInstanceSourceTableKind(
        CdcProviderArtifactObservation artifact
    )
    {
        if (
            artifact.SafeObservedValues is null
            || !artifact.SafeObservedValues.TryGetValue("source_table_kind", out string? sourceTableKind)
        )
        {
            return null;
        }

        return sourceTableKind switch
        {
            "document_cache" => CdcSourceTableKind.DocumentCache,
            "document" => CdcSourceTableKind.Document,
            "cdc_heartbeat" => CdcSourceTableKind.CdcHeartbeat,
            _ => null,
        };
    }

    private static string SqlServerCaptureInstanceObservedValue(
        IReadOnlyList<CdcProviderArtifactObservation> tableCaptureInstances
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

        return tableCaptureInstances[0].State.ToString();
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

    private static bool IsValidSourceFingerprint(CdcSourceFingerprint? sourceFingerprint)
    {
        if (sourceFingerprint is null)
        {
            return false;
        }

        try
        {
            CdcConnectorTemplateContractValidation.ValidateSourceFingerprint(
                sourceFingerprint,
                nameof(sourceFingerprint)
            );
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class CdcConnectorTemplateServiceCollectionExtensions
{
    public static IServiceCollection AddCdcConnectorTemplates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(
            ServiceDescriptor.Scoped<
                ICdcConnectorTemplateInputValidator,
                CdcConnectorTemplateInputValidator
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<ICdcConnectorTemplateRenderer, CdcConnectorTemplateRenderer>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                ICdcConnectorTemplateEffectiveConfigValidator,
                CdcConnectorTemplateEffectiveConfigValidator
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<ICdcConnectorTemplateService, CdcConnectorTemplateService>()
        );

        return services;
    }
}
