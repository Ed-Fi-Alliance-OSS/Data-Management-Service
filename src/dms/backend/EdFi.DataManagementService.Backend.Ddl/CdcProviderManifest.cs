// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Ddl;

internal sealed record CdcProviderManifest(
    string ManifestVersion,
    CdcProvider Provider,
    CdcProviderSetupMode Mode,
    CdcProviderSetupOutcome Outcome,
    bool OptInEnabled,
    CdcSourceFingerprint? ObservedSourceFingerprint,
    IReadOnlyList<CdcSourceTableInventory> SourceTableInventory,
    IReadOnlyList<CdcProviderArtifactObservation> ProviderArtifacts,
    IReadOnlyList<CdcGrantObservation> GrantInventory,
    IReadOnlyList<CdcExpectedMessageKeyColumns> ExpectedMessageKeyColumns,
    CdcHeartbeatActionQuery? HeartbeatActionQuery,
    IReadOnlyList<CdcProviderHistoryObservation> ProviderHistoryObservations,
    IReadOnlyList<CdcProviderDiagnostic> Diagnostics
);

internal static class CdcProviderManifestEmitter
{
    private const string ManifestVersion = "1";
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    public static CdcProviderManifestPayload CreatePayload(
        CdcProviderSetupResult result,
        bool? providerOptInEnabled = null
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CdcProviderManifestPayload(
            new CdcSafeName(FileNameFor(result.Provider)),
            Emit(BuildManifest(result, providerOptInEnabled))
        );
    }

    public static string FileNameFor(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "cdc-provider.pgsql.manifest.json",
            CdcProvider.SqlServer => "cdc-provider.mssql.manifest.json",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    internal static CdcProviderManifest BuildManifest(
        CdcProviderSetupResult result,
        bool? providerOptInEnabled = null
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CdcProviderManifest(
            ManifestVersion: ManifestVersion,
            Provider: result.Provider,
            Mode: result.Mode,
            Outcome: result.Outcome,
            OptInEnabled: providerOptInEnabled ?? result.Outcome != CdcProviderSetupOutcome.Failed,
            ObservedSourceFingerprint: result.ObservedSourceFingerprint,
            SourceTableInventory: SortSourceTables(result.SourceTableInventory),
            ProviderArtifacts: SortArtifactObservations(result.ArtifactInventory),
            GrantInventory: SortGrantObservations(result.GrantInventory),
            ExpectedMessageKeyColumns: SortExpectedMessageKeyColumns(result.ExpectedMessageKeyColumns),
            HeartbeatActionQuery: result.HeartbeatActionQuery,
            ProviderHistoryObservations: SortProviderHistoryObservations(result.ProviderHistoryObservations),
            Diagnostics: SortDiagnostics(result.Diagnostics)
        );
    }

    public static string Emit(CdcProviderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("manifest_version", manifest.ManifestVersion);
            writer.WriteString("provider", ProviderToken(manifest.Provider));
            writer.WriteString("mode", SetupModeToken(manifest.Mode));
            writer.WriteString("outcome", SetupOutcomeToken(manifest.Outcome));
            writer.WriteString("opt_in_status", manifest.OptInEnabled ? "enabled" : "validation_failed");
            WriteSourceFingerprint(writer, manifest.ObservedSourceFingerprint);
            WriteSourceTableInventory(writer, manifest.SourceTableInventory);
            WriteProviderArtifacts(writer, manifest.ProviderArtifacts);
            WriteGrantInventory(writer, manifest.GrantInventory);
            WriteExpectedMessageKeyColumns(writer, manifest.ExpectedMessageKeyColumns);
            WriteHeartbeatActionQuery(writer, manifest.HeartbeatActionQuery);
            WriteProviderHistoryObservations(writer, manifest.ProviderHistoryObservations);
            WriteDiagnostics(writer, manifest.Diagnostics);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteSourceFingerprint(Utf8JsonWriter writer, CdcSourceFingerprint? fingerprint)
    {
        writer.WritePropertyName("observed_source_fingerprint");
        if (fingerprint is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("version", fingerprint.Version);
        writer.WriteString("value", fingerprint.Value);
        writer.WriteEndObject();
    }

    private static void WriteSourceTableInventory(
        Utf8JsonWriter writer,
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory
    )
    {
        writer.WritePropertyName("source_table_inventory");
        writer.WriteStartArray();
        foreach (var table in sourceTableInventory)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "table_kind",
                CdcSourceInventoryContract.SourceTableKindToken(table.TableKind)
            );
            writer.WriteString("schema_name", table.TableName.Schema.Value);
            writer.WriteString("table_name", table.TableName.Name);
            writer.WriteString("emitted_quoted_table_name", table.EmittedQuotedTableName);
            writer.WritePropertyName("columns");
            writer.WriteStartArray();
            foreach (var column in table.Columns.OrderBy(column => column.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("column_name", column.ColumnName.Value);
                writer.WriteString("emitted_quoted_column_name", column.EmittedQuotedColumnName);
                writer.WriteNumber("ordinal", column.Ordinal);
                writer.WriteString("provider_data_type", column.ProviderDataType);
                writer.WriteBoolean("is_nullable", column.IsNullable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteProviderArtifacts(
        Utf8JsonWriter writer,
        IReadOnlyList<CdcProviderArtifactObservation> providerArtifacts
    )
    {
        writer.WritePropertyName("provider_artifacts");
        writer.WriteStartArray();
        foreach (var artifact in providerArtifacts)
        {
            writer.WriteStartObject();
            writer.WriteString("artifact_kind", ArtifactKindToken(artifact.ArtifactKind));
            writer.WriteString("artifact_name", artifact.SafeArtifactName.Value);
            writer.WriteString("state", ArtifactStateToken(artifact.State));
            WriteObservedValues(writer, artifact.SafeObservedValues);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteGrantInventory(
        Utf8JsonWriter writer,
        IReadOnlyList<CdcGrantObservation> grantInventory
    )
    {
        writer.WritePropertyName("grant_inventory");
        writer.WriteStartArray();
        foreach (var grant in grantInventory)
        {
            writer.WriteStartObject();
            writer.WriteString("principal_kind", PrincipalKindToken(grant.PrincipalKind));
            writer.WriteString("principal_name", grant.SafePrincipalName.Value);
            writer.WriteString("artifact_kind", ArtifactKindToken(grant.ArtifactKind));
            writer.WriteString("object_name", grant.SafeObjectName.Value);
            WriteStringArray(writer, "privileges", grant.Privileges.Order(StringComparer.Ordinal));
            WriteStringArray(
                writer,
                "columns",
                grant.Columns.Select(column => column.Value).Order(StringComparer.Ordinal)
            );
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteExpectedMessageKeyColumns(
        Utf8JsonWriter writer,
        IReadOnlyList<CdcExpectedMessageKeyColumns> expectedMessageKeyColumns
    )
    {
        writer.WritePropertyName("expected_message_key_columns");
        writer.WriteStartArray();
        foreach (var key in expectedMessageKeyColumns)
        {
            writer.WriteStartObject();
            writer.WriteString("table_kind", CdcSourceInventoryContract.SourceTableKindToken(key.TableKind));
            WriteStringArray(writer, "columns", key.KeyColumns.Select(column => column.Value));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteHeartbeatActionQuery(
        Utf8JsonWriter writer,
        CdcHeartbeatActionQuery? heartbeatActionQuery
    )
    {
        writer.WritePropertyName("heartbeat_action_query");
        if (heartbeatActionQuery is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("sha256_hash", heartbeatActionQuery.Sha256Hash);
        writer.WriteString("sql", NormalizeSqlLiteral(heartbeatActionQuery.Sql));
        writer.WriteEndObject();
    }

    private static void WriteProviderHistoryObservations(
        Utf8JsonWriter writer,
        IReadOnlyList<CdcProviderHistoryObservation> providerHistoryObservations
    )
    {
        writer.WritePropertyName("provider_history_observations");
        writer.WriteStartArray();
        foreach (var observation in providerHistoryObservations)
        {
            writer.WriteStartObject();
            writer.WriteString("artifact_kind", ArtifactKindToken(observation.ArtifactKind));
            writer.WriteString("artifact_name", observation.SafeArtifactName.Value);
            WriteObservedValues(writer, observation.SafeObservedValues);
            writer.WriteString("classification", ClassificationToken(observation.Classification));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteDiagnostics(
        Utf8JsonWriter writer,
        IReadOnlyList<CdcProviderDiagnostic> diagnostics
    )
    {
        writer.WritePropertyName("validation_diagnostics");
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("category", DiagnosticCategoryToken(diagnostic.Category));
            writer.WriteString("severity", DiagnosticSeverityToken(diagnostic.Severity));
            writer.WriteString("principal_kind", PrincipalKindToken(diagnostic.PrincipalKind));
            writer.WriteString("artifact_kind", ArtifactKindToken(diagnostic.ArtifactKind));
            writer.WriteString("safe_name", diagnostic.SafeName.Value);
            WriteNullableString(writer, "expected_value", diagnostic.ExpectedValue);
            WriteNullableString(writer, "observed_value", diagnostic.ObservedValue);
            WriteNullableString(writer, "provider_error_class", diagnostic.ProviderErrorClass);
            WriteOptionalString(writer, "provider_error_code", diagnostic.ProviderErrorCode);
            WriteOptionalString(writer, "provider_error_state", diagnostic.ProviderErrorState);
            writer.WriteString("classification", ClassificationToken(diagnostic.Classification));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteObservedValues(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> observedValues
    )
    {
        writer.WritePropertyName("observed_values");
        writer.WriteStartObject();
        foreach (var observedValue in observedValues.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            writer.WriteString(observedValue.Key, observedValue.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values
    )
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static IReadOnlyList<CdcSourceTableInventory> SortSourceTables(
        IReadOnlyList<CdcSourceTableInventory> sourceTables
    )
    {
        return sourceTables
            .OrderBy(table => CdcSourceInventoryContract.RequiredSourceTableOrdinal(table.TableKind))
            .ThenBy(table => table.TableName.Schema.Value, StringComparer.Ordinal)
            .ThenBy(table => table.TableName.Name, StringComparer.Ordinal)
            .Select(table => new CdcSourceTableInventory(
                table.TableKind,
                table.TableName,
                table.EmittedQuotedTableName,
                table.Columns.OrderBy(column => column.Ordinal).ToArray()
            ))
            .ToArray();
    }

    private static IReadOnlyList<CdcProviderArtifactObservation> SortArtifactObservations(
        IReadOnlyList<CdcProviderArtifactObservation> observations
    ) =>
        observations
            .OrderBy(observation => ArtifactKindToken(observation.ArtifactKind), StringComparer.Ordinal)
            .ThenBy(observation => observation.SafeArtifactName.Value, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<CdcGrantObservation> SortGrantObservations(
        IReadOnlyList<CdcGrantObservation> grants
    ) =>
        grants
            .OrderBy(grant => PrincipalKindToken(grant.PrincipalKind), StringComparer.Ordinal)
            .ThenBy(grant => grant.SafePrincipalName.Value, StringComparer.Ordinal)
            .ThenBy(grant => ArtifactKindToken(grant.ArtifactKind), StringComparer.Ordinal)
            .ThenBy(grant => grant.SafeObjectName.Value, StringComparer.Ordinal)
            .Select(grant => new CdcGrantObservation(
                grant.PrincipalKind,
                grant.SafePrincipalName,
                grant.ArtifactKind,
                grant.SafeObjectName,
                grant.Privileges.Order(StringComparer.Ordinal).ToArray(),
                grant.Columns.OrderBy(column => column.Value, StringComparer.Ordinal).ToArray()
            ))
            .ToArray();

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> SortExpectedMessageKeyColumns(
        IReadOnlyList<CdcExpectedMessageKeyColumns> keyColumns
    )
    {
        return keyColumns
            .OrderBy(key => CdcSourceInventoryContract.RequiredSourceTableOrdinal(key.TableKind))
            .ThenBy(
                key => CdcSourceInventoryContract.SourceTableKindToken(key.TableKind),
                StringComparer.Ordinal
            )
            .ToArray();
    }

    private static IReadOnlyList<CdcProviderHistoryObservation> SortProviderHistoryObservations(
        IReadOnlyList<CdcProviderHistoryObservation> observations
    ) =>
        observations
            .OrderBy(observation => ArtifactKindToken(observation.ArtifactKind), StringComparer.Ordinal)
            .ThenBy(observation => observation.SafeArtifactName.Value, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<CdcProviderDiagnostic> SortDiagnostics(
        IReadOnlyList<CdcProviderDiagnostic> diagnostics
    ) =>
        diagnostics
            .OrderBy(diagnostic => DiagnosticSeverityToken(diagnostic.Severity), StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => DiagnosticCategoryToken(diagnostic.Category), StringComparer.Ordinal)
            .ThenBy(diagnostic => ArtifactKindToken(diagnostic.ArtifactKind), StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.SafeName.Value, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeSqlLiteral(string sql) => sql.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string ProviderToken(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "postgresql",
            CdcProvider.SqlServer => "sqlserver",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static string SetupModeToken(CdcProviderSetupMode mode) =>
        mode switch
        {
            CdcProviderSetupMode.InitialCreateOrExactMatch => "initial_create_or_exact_match",
            CdcProviderSetupMode.ValidateOnly => "validate_only",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported CDC setup mode."),
        };

    private static string SetupOutcomeToken(CdcProviderSetupOutcome outcome) =>
        outcome switch
        {
            CdcProviderSetupOutcome.CreatedOrMatched => "created_or_matched",
            CdcProviderSetupOutcome.ExactMatch => "exact_match",
            CdcProviderSetupOutcome.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unsupported CDC setup outcome."
            ),
        };

    private static string PrincipalKindToken(CdcPrincipalKind kind) =>
        kind switch
        {
            CdcPrincipalKind.None => "none",
            CdcPrincipalKind.SetupPrincipal => "setup_principal",
            CdcPrincipalKind.ConnectorPrincipal => "connector_principal",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported CDC principal kind."),
        };

    private static string ArtifactKindToken(CdcProviderArtifactKind kind) =>
        kind switch
        {
            CdcProviderArtifactKind.None => "none",
            CdcProviderArtifactKind.SourceTable => "source_table",
            CdcProviderArtifactKind.SourceColumn => "source_column",
            CdcProviderArtifactKind.HeartbeatTable => "heartbeat_table",
            CdcProviderArtifactKind.HeartbeatActionQuery => "heartbeat_action_query",
            CdcProviderArtifactKind.PostgresqlPublication => "postgresql_publication",
            CdcProviderArtifactKind.PostgresqlReplicaIdentity => "postgresql_replica_identity",
            CdcProviderArtifactKind.PostgresqlReplicationSlot => "postgresql_replication_slot",
            CdcProviderArtifactKind.SqlServerCaptureInstance => "sqlserver_capture_instance",
            CdcProviderArtifactKind.SqlServerGatingRole => "sqlserver_gating_role",
            CdcProviderArtifactKind.Grant => "grant",
            CdcProviderArtifactKind.SourceFingerprint => "source_fingerprint",
            CdcProviderArtifactKind.ProviderHistory => "provider_history",
            CdcProviderArtifactKind.SetupPrincipalIdentity => "setup_principal_identity",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported CDC artifact kind."),
        };

    private static string ArtifactStateToken(CdcProviderArtifactState state) =>
        state switch
        {
            CdcProviderArtifactState.Created => "created",
            CdcProviderArtifactState.Matched => "matched",
            CdcProviderArtifactState.Missing => "missing",
            CdcProviderArtifactState.Mismatched => "mismatched",
            CdcProviderArtifactState.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Unsupported CDC artifact state."
            ),
        };

    private static string DiagnosticCategoryToken(CdcProviderDiagnosticCategory category) =>
        category switch
        {
            CdcProviderDiagnosticCategory.SetupPrincipalFailure => "setup_principal_failure",
            CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure =>
                "connector_principal_privilege_failure",
            CdcProviderDiagnosticCategory.MissingRequiredSourceObject => "missing_required_source_object",
            CdcProviderDiagnosticCategory.WorkTableCaptureViolation => "work_table_capture_violation",
            CdcProviderDiagnosticCategory.WorkTableGrantViolation => "work_table_grant_violation",
            CdcProviderDiagnosticCategory.ProviderHistoryUnavailable => "provider_history_unavailable",
            CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence => "provider_history_loss_evidence",
            CdcProviderDiagnosticCategory.ValidationMismatch => "validation_mismatch",
            _ => throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                "Unsupported CDC diagnostic category."
            ),
        };

    private static string DiagnosticSeverityToken(CdcProviderDiagnosticSeverity severity) =>
        severity switch
        {
            CdcProviderDiagnosticSeverity.Info => "info",
            CdcProviderDiagnosticSeverity.Warning => "warning",
            CdcProviderDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(
                nameof(severity),
                severity,
                "Unsupported CDC diagnostic severity."
            ),
        };

    private static string ClassificationToken(CdcProviderRetryContinuityClassification classification) =>
        classification switch
        {
            CdcProviderRetryContinuityClassification.None => "none",
            CdcProviderRetryContinuityClassification.Retryable => "retryable",
            CdcProviderRetryContinuityClassification.FailClosed => "fail_closed",
            CdcProviderRetryContinuityClassification.SourceHistoryUnknown => "source_history_unknown",
            CdcProviderRetryContinuityClassification.SourceHistoryLost => "source_history_lost",
            _ => throw new ArgumentOutOfRangeException(
                nameof(classification),
                classification,
                "Unsupported CDC retry/continuity classification."
            ),
        };
}
