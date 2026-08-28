// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Turns live Kafka Connect evidence into the shared CDC observation contracts. Property comparison
/// itself is never restated here: the connector-template service owns those rules, and this mapper
/// only carries its verdict onto the observation the readiness evaluators consume.
/// </summary>
public interface ICdcConnectorObservationMapper
{
    /// <summary>
    /// Validates a live connector configuration read-back through
    /// <see cref="ICdcConnectorTemplateService.ValidateLiveReadBack"/> and maps the resulting
    /// diagnostics onto <see cref="CdcConnectorConfigurationObservation"/> item states.
    /// </summary>
    /// <remarks>
    /// A read-back that could not be obtained, or that could not be compared at all, reports
    /// <see cref="CdcConnectorConfigurationState.Unknown"/> rather than a match: absent evidence keeps
    /// readiness false instead of passing.
    /// </remarks>
    CdcConnectorConfigurationObservation MapConfiguration(
        CdcObservationContext context,
        CdcConnectorTemplateRequest templateRequest,
        CdcConnectorProviderSetupEvidence providerSetupEvidence,
        CdcConnectorTemplateSourcePartitionEvidence? sourcePartitionEvidence,
        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack
    );

    /// <summary>
    /// Maps a connector status document onto <see cref="CdcConnectorRuntimeObservation"/>. The
    /// committed offsets supply the snapshot evidence, because a Connect status document reports task
    /// state only.
    /// </summary>
    /// <remarks>
    /// The observed task count is reported as it is: the shared contract admits exactly one task, so
    /// any other count is rejected by validation rather than rounded off here.
    /// </remarks>
    CdcConnectorRuntimeObservation MapRuntime(
        CdcObservationContext context,
        CdcBinding binding,
        CdcConnectResult<CdcConnectorStatus> status,
        CdcConnectResult<CdcConnectorOffsets> committedOffsets
    );

    /// <summary>
    /// Maps the connector's committed source offsets onto <see cref="CdcConnectorOffsetObservation"/>,
    /// selecting the entry whose Connect source partition is the binding's.
    /// </summary>
    /// <remarks>
    /// A snapshot offset is reported as one and stays rejected by the shared validation rules: the
    /// provider barrier is only ever satisfied by a committed streaming position.
    /// </remarks>
    /// <param name="sqlServerCatalogName">
    /// Expected SQL Server catalog, which that provider's Connect source partition includes. Not used
    /// for PostgreSQL.
    /// </param>
    CdcConnectorOffsetObservation MapOffset(
        CdcObservationContext context,
        CdcBinding binding,
        string? sqlServerCatalogName,
        CdcConnectResult<CdcConnectorOffsets> committedOffsets
    );
}

internal sealed class CdcConnectorObservationMapper(
    ICdcConnectorTemplateService templateService,
    TimeProvider timeProvider
) : ICdcConnectorObservationMapper
{
    internal const string TasksMaxPropertyName = "tasks.max";

    public CdcConnectorConfigurationObservation MapConfiguration(
        CdcObservationContext context,
        CdcConnectorTemplateRequest templateRequest,
        CdcConnectorProviderSetupEvidence providerSetupEvidence,
        CdcConnectorTemplateSourcePartitionEvidence? sourcePartitionEvidence,
        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(providerSetupEvidence);
        ArgumentNullException.ThrowIfNull(readBack);

        DateTimeOffset observedAt = timeProvider.GetUtcNow();

        if (readBack.Value is null || !readBack.Succeeded)
        {
            return Complete(
                context,
                templateRequest,
                observedAt,
                CdcConnectorConfigurationState.Unknown,
                UnknownItems(context.TargetIdentity.Provider),
                taskCount: null,
                [ReadBackUnavailable(templateRequest, observedAt, readBack)]
            );
        }

        int? taskCount = ReadTaskCount(readBack.Value);

        CdcConnectorTemplateEffectiveConfigValidationRequest validationRequest;
        try
        {
            validationRequest = new(
                templateRequest,
                readBack.Value,
                providerSetupEvidence,
                sourcePartitionEvidence
            );
        }
        catch (ArgumentException exception)
        {
            // A read-back the shared contract cannot even accept as a property map is unusable evidence.
            // The exception carries the offending value, so only its type is reported.
            return Complete(
                context,
                templateRequest,
                observedAt,
                CdcConnectorConfigurationState.Unknown,
                UnknownItems(context.TargetIdentity.Provider),
                taskCount,
                [ReadBackUnusable(templateRequest, observedAt, exception.GetType().Name)]
            );
        }

        CdcConnectorTemplateResult validation = templateService.ValidateLiveReadBack(validationRequest);
        IReadOnlyList<CdcDiagnostic> diagnostics =
        [
            .. validation.Diagnostics.Select(diagnostic => ToDiagnostic(diagnostic, observedAt)),
        ];

        // The effective-config validator returns an empty rendered configuration when it rejected its own
        // inputs or the fresh provider-setup evidence before ever comparing the read-back. Nothing was
        // observed about the live configuration in that case, so no item may report a match.
        if (validation.Config.Count == 0)
        {
            return Complete(
                context,
                templateRequest,
                observedAt,
                CdcConnectorConfigurationState.Unknown,
                UnknownItems(context.TargetIdentity.Provider),
                taskCount,
                diagnostics
            );
        }

        HashSet<CdcConnectorConfigurationArea> invalidAreas =
        [
            .. validation
                .Diagnostics.Where(diagnostic =>
                    diagnostic.Severity == CdcConnectorTemplateDiagnosticSeverity.Error
                )
                .Select(diagnostic => AreaFor(diagnostic, context.TargetIdentity.Provider)),
        ];

        return Complete(
            context,
            templateRequest,
            observedAt,
            invalidAreas.Count == 0
                ? CdcConnectorConfigurationState.Matched
                : CdcConnectorConfigurationState.Invalid,
            ComparedItems(invalidAreas, context.TargetIdentity.Provider),
            taskCount,
            diagnostics
        );
    }

    public CdcConnectorRuntimeObservation MapRuntime(
        CdcObservationContext context,
        CdcBinding binding,
        CdcConnectResult<CdcConnectorStatus> status,
        CdcConnectResult<CdcConnectorOffsets> committedOffsets
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(committedOffsets);

        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        string connectorName = ConnectorName(binding);
        CdcConnectorSnapshotState snapshotState = ReadSnapshotState(committedOffsets);
        List<CdcDiagnostic> diagnostics = [];

        if (status.Value is null || !status.Succeeded)
        {
            diagnostics.Add(
                Unavailable(
                    "connectorRuntimeUnavailable",
                    CdcDiagnosticComponent.ConnectorRuntime,
                    "connectorRuntime",
                    connectorName,
                    "connector status",
                    status.Outcome.ToString(),
                    observedAt
                )
            );

            return CompleteRuntime(
                context,
                binding,
                observedAt,
                connectorName,
                CdcConnectorRuntimeState.Unknown,
                taskCount: null,
                runningTaskCount: null,
                CdcConnectorRuntimeState.Unknown,
                snapshotState,
                lastErrorCategory: null,
                diagnostics
            );
        }

        CdcConnectorStatus connectorStatus = status.Value;
        CdcConnectorRuntimeState connectorState = ToRuntimeState(connectorStatus.ConnectorState);

        // Exactly one task is the contract. Any other count is reported as observed, and the sole task
        // state stays unknown because no single task speaks for the connector.
        CdcConnectorRuntimeState soleTaskState =
            connectorStatus.Tasks.Count == 1
                ? ToRuntimeState(connectorStatus.Tasks[0].State)
                : CdcConnectorRuntimeState.Unknown;

        return CompleteRuntime(
            context,
            binding,
            observedAt,
            connectorName,
            connectorState,
            connectorStatus.Tasks.Count,
            connectorStatus.Tasks.Count(task =>
                ToRuntimeState(task.State) == CdcConnectorRuntimeState.Running
            ),
            soleTaskState,
            snapshotState,
            ReadLastErrorCategory(connectorStatus, connectorState, soleTaskState),
            diagnostics
        );
    }

    public CdcConnectorOffsetObservation MapOffset(
        CdcObservationContext context,
        CdcBinding binding,
        string? sqlServerCatalogName,
        CdcConnectResult<CdcConnectorOffsets> committedOffsets
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(committedOffsets);

        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        CdcProvider provider = context.TargetIdentity.Provider;
        string connectorName = ConnectorName(binding);
        List<CdcDiagnostic> diagnostics = [];

        CdcSourcePartitionHashResult expectedHash = CdcSourcePartitionHashCalculator.Compute(
            provider,
            connectorName,
            sqlServerCatalogName
        );
        diagnostics.AddRange(expectedHash.Diagnostics);

        if (committedOffsets.Value is null || !committedOffsets.Succeeded)
        {
            diagnostics.Add(
                Unavailable(
                    "connectorOffsetsUnavailable",
                    CdcDiagnosticComponent.ConnectorRuntime,
                    "connectorOffsets",
                    connectorName,
                    "committed connector offsets",
                    committedOffsets.Outcome.ToString(),
                    observedAt
                )
            );

            return CompleteOffset(
                context,
                binding,
                observedAt,
                connectorName,
                CdcConnectorOffsetMatchResult.Missing,
                expectedHash,
                observedHash: expectedHash,
                isSnapshot: false,
                isNull: false,
                offset: null,
                diagnostics
            );
        }

        IReadOnlyList<CdcConnectorOffsetEntry> entries = committedOffsets.Value.Entries;
        List<CdcConnectorOffsetEntry> matching =
        [
            .. entries.Where(entry =>
                PartitionMatches(entry.Partition, provider, connectorName, sqlServerCatalogName)
            ),
        ];

        CdcConnectorOffsetMatchResult matchResult = matching.Count switch
        {
            1 => CdcConnectorOffsetMatchResult.Exact,
            > 1 => CdcConnectorOffsetMatchResult.Multiple,
            _ => entries.Count == 0
                ? CdcConnectorOffsetMatchResult.Missing
                : CdcConnectorOffsetMatchResult.SourcePartitionMismatch,
        };

        // The observed partition is hashed rather than the expected one, so a connector committing
        // under another source partition is visible as a hash mismatch instead of being asserted away.
        CdcConnectorOffsetEntry? evidence = matching.Count == 1 ? matching[0] : null;
        if (evidence is null && matching.Count == 0 && entries.Count == 1)
        {
            evidence = entries[0];
        }

        CdcSourcePartitionHashResult observedHash = expectedHash;
        if (evidence is { } observedEntry)
        {
            observedHash = ObservedSourcePartitionHash(observedEntry.Partition, provider);
            if (!observedHash.Succeeded)
            {
                diagnostics.AddRange(observedHash.Diagnostics);
                observedHash = expectedHash;
            }
        }

        // Provider positions are read only from the binding's own source partition. An offset committed
        // under a different partition is another source's position, never this binding's.
        JsonElement? offset =
            matchResult == CdcConnectorOffsetMatchResult.Exact && evidence is { } matchedEntry
                ? matchedEntry.Offset
                : null;

        return CompleteOffset(
            context,
            binding,
            observedAt,
            connectorName,
            matchResult,
            expectedHash,
            observedHash,
            offset is { } committedOffset && IsSnapshotOffset(committedOffset),
            offset is { } nullableOffset && IsNullOffset(nullableOffset),
            offset,
            diagnostics
        );
    }

    private static CdcConnectorRuntimeObservation CompleteRuntime(
        CdcObservationContext context,
        CdcBinding binding,
        DateTimeOffset observedAt,
        string connectorName,
        CdcConnectorRuntimeState connectorState,
        int? taskCount,
        int? runningTaskCount,
        CdcConnectorRuntimeState soleTaskState,
        CdcConnectorSnapshotState snapshotState,
        string? lastErrorCategory,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        CdcConnectorRuntimeObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            connectorName,
            connectorState,
            taskCount,
            runningTaskCount,
            soleTaskState,
            snapshotState,
            lastErrorCategory,
            // Kafka Connect reports no time of failure, and observation time is not that time.
            LastErrorObservedAt: null,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );

        CdcContractValidationResult validation = CdcConnectorRuntimeObservationValidator.ValidateForBinding(
            observation,
            binding,
            context.ToValidationContext(observedAt)
        );

        return validation.Succeeded
            ? observation
            : observation with
            {
                Diagnostics = CdcDiagnostic.NormalizeDiagnostics([
                    .. observation.Diagnostics,
                    .. validation.Diagnostics,
                ]),
            };
    }

    private static CdcConnectorOffsetObservation CompleteOffset(
        CdcObservationContext context,
        CdcBinding binding,
        DateTimeOffset observedAt,
        string connectorName,
        CdcConnectorOffsetMatchResult matchResult,
        CdcSourcePartitionHashResult expectedHash,
        CdcSourcePartitionHashResult observedHash,
        bool isSnapshot,
        bool isNull,
        JsonElement? offset,
        List<CdcDiagnostic> diagnostics
    )
    {
        CdcProvider provider = context.TargetIdentity.Provider;
        long? lsnProc = null;
        string? commitLsn = null;
        string? changeLsn = null;
        long? eventSerialNo = null;

        if (offset is { } committedOffset && !isNull)
        {
            if (provider == CdcProvider.Postgresql)
            {
                lsnProc = ReadInt64(committedOffset, "lsn_proc");
                if (lsnProc is null && committedOffset.TryGetProperty("lsn_proc", out _))
                {
                    diagnostics.Add(
                        new CdcDiagnostic(
                            CdcDiagnosticCategory.MalformedPayload,
                            observedAt,
                            "$.lsnProc",
                            "CDC connector offset lsnProc must be a 64-bit integer."
                        )
                    );
                }
            }
            else
            {
                commitLsn = ReadString(committedOffset, "commit_lsn");
                changeLsn = ReadString(committedOffset, "change_lsn");
                eventSerialNo = ReadInt64(committedOffset, "event_serial_no");
            }
        }

        CdcConnectorOffsetObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            provider,
            context.PhysicalSourceFingerprint,
            connectorName,
            // The Connect source partition is keyed by the rendered topic prefix, which is the
            // binding's connector name.
            connectorName,
            matchResult,
            observedHash.Hash ?? string.Empty,
            isSnapshot,
            isNull,
            lsnProc,
            commitLsn,
            changeLsn,
            eventSerialNo,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );

        CdcContractValidationResult validation = CdcConnectorOffsetObservationValidator.ValidateForBinding(
            observation,
            binding,
            context.ToValidationContext(observedAt),
            expectedHash.Hash
        );

        return validation.Succeeded
            ? observation
            : observation with
            {
                Diagnostics = CdcDiagnostic.NormalizeDiagnostics([
                    .. observation.Diagnostics,
                    .. validation.Diagnostics,
                ]),
            };
    }

    /// <summary>
    /// Snapshot progress is not part of a Connect status document, so it is read from the connector's
    /// own committed offset: a snapshot offset means the snapshot is still running, and a committed
    /// streaming offset means it finished.
    /// </summary>
    private static CdcConnectorSnapshotState ReadSnapshotState(
        CdcConnectResult<CdcConnectorOffsets> committedOffsets
    )
    {
        if (committedOffsets.Value is null || !committedOffsets.Succeeded)
        {
            return CdcConnectorSnapshotState.Unknown;
        }

        IReadOnlyList<CdcConnectorOffsetEntry> entries = committedOffsets.Value.Entries;

        return entries.Count switch
        {
            0 => CdcConnectorSnapshotState.NotStarted,
            1 => ReadSnapshotState(entries[0].Offset),
            _ => CdcConnectorSnapshotState.Unknown,
        };
    }

    private static CdcConnectorSnapshotState ReadSnapshotState(JsonElement offset)
    {
        if (IsNullOffset(offset))
        {
            return CdcConnectorSnapshotState.NotStarted;
        }

        return IsSnapshotOffset(offset)
            ? CdcConnectorSnapshotState.Running
            : CdcConnectorSnapshotState.Completed;
    }

    /// <summary>
    /// Reduces the failing task's exception type to the bounded lowercase token the shared contract
    /// admits. A connector or task that failed always reports a category, because the contract requires
    /// one, and an unrecognizable trace reports the unclassified token rather than a fragment of itself.
    /// </summary>
    private static string? ReadLastErrorCategory(
        CdcConnectorStatus status,
        CdcConnectorRuntimeState connectorState,
        CdcConnectorRuntimeState soleTaskState
    )
    {
        string? category = status
            .Tasks.Select(task => task.ErrorCategory)
            .FirstOrDefault(errorCategory => !string.IsNullOrWhiteSpace(errorCategory));

        if (category is null)
        {
            return
                connectorState == CdcConnectorRuntimeState.Failed
                || soleTaskState == CdcConnectorRuntimeState.Failed
                ? CdcConnectRestAdapter.UnclassifiedErrorCategory
                : null;
        }

        CdcDiagnosticCollector unusedDiagnostics = new();

        return CdcKafkaSafeTokenValidator.Validate(
                category.ToLowerInvariant(),
                "$.lastErrorCategory",
                "lastErrorCategory",
                unusedDiagnostics
            ) ?? CdcConnectRestAdapter.UnclassifiedErrorCategory;
    }

    private static CdcConnectorRuntimeState ToRuntimeState(string? state) =>
        state?.ToUpperInvariant() switch
        {
            "RUNNING" => CdcConnectorRuntimeState.Running,
            "PAUSED" => CdcConnectorRuntimeState.Paused,
            "FAILED" => CdcConnectorRuntimeState.Failed,
            "STOPPED" => CdcConnectorRuntimeState.Stopped,
            "UNASSIGNED" => CdcConnectorRuntimeState.Unassigned,
            _ => CdcConnectorRuntimeState.Unknown,
        };

    private static bool PartitionMatches(
        JsonElement partition,
        CdcProvider provider,
        string connectorName,
        string? sqlServerCatalogName
    )
    {
        if (
            partition.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadString(partition, "server"), connectorName, StringComparison.Ordinal)
        )
        {
            return false;
        }

        return provider != CdcProvider.SqlServer
            || (
                !string.IsNullOrEmpty(sqlServerCatalogName)
                && string.Equals(
                    ReadString(partition, "database"),
                    sqlServerCatalogName,
                    StringComparison.Ordinal
                )
            );
    }

    private static CdcSourcePartitionHashResult ObservedSourcePartitionHash(
        JsonElement partition,
        CdcProvider provider
    ) =>
        CdcSourcePartitionHashCalculator.Compute(
            provider,
            ReadString(partition, "server"),
            ReadString(partition, "database")
        );

    /// <summary>
    /// Debezium reports a snapshot offset as a boolean or as a phase token. Only an explicit false, or
    /// the absence of the property, is streaming evidence; anything else is treated as a snapshot.
    /// </summary>
    private static bool IsSnapshotOffset(JsonElement offset)
    {
        if (
            offset.ValueKind != JsonValueKind.Object
            || !offset.TryGetProperty("snapshot", out JsonElement snapshot)
        )
        {
            return false;
        }

        return snapshot.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.Equals(
                snapshot.GetString(),
                "false",
                StringComparison.OrdinalIgnoreCase
            ),
            _ => true,
        };
    }

    private static bool IsNullOffset(JsonElement offset) =>
        offset.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        if (
            element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement property)
        )
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long value))
        {
            return value;
        }

        return
            property.ValueKind == JsonValueKind.String
            && long.TryParse(
                property.GetString(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long parsedValue
            )
            ? parsedValue
            : null;
    }

    private static string ConnectorName(CdcBinding binding) =>
        CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory?.ConnectorName
        ?? throw new ArgumentException(
            "CDC binding must derive a governed artifact inventory.",
            nameof(binding)
        );

    private static CdcDiagnostic Unavailable(
        string code,
        CdcDiagnosticComponent component,
        string artifactKind,
        string artifactName,
        string expected,
        string observed,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            code,
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            component,
            observedAt,
            "CDC connector evidence is unavailable from the Kafka Connect worker.",
            retryable: true,
            artifactKind: artifactKind,
            artifactName: artifactName,
            expected: expected,
            observed: observed
        ).WithPath($"$.{artifactKind}");

    private static CdcConnectorConfigurationObservation Complete(
        CdcObservationContext context,
        CdcConnectorTemplateRequest templateRequest,
        DateTimeOffset observedAt,
        CdcConnectorConfigurationState configurationState,
        CdcConnectorConfigurationItemStates items,
        int? taskCount,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        CdcArtifactInventory inventory = templateRequest.ArtifactInventory;
        CdcConnectorConfigurationObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            inventory.ConnectorName,
            configurationState,
            inventory.TopicPrefix,
            taskCount,
            items.Transform,
            items.Converter,
            items.ProducerOverride,
            items.Heartbeat,
            items.SourceIncludeList,
            items.Offset,
            items.SchemaHistory,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );

        CdcContractValidationResult validationResult =
            CdcConnectorConfigurationObservationValidator.ValidateForBinding(
                observation,
                templateRequest.Binding,
                context.ToValidationContext(observedAt)
            );

        if (validationResult.Succeeded)
        {
            return observation;
        }

        // An observation that cannot pass its own contract is never reported as a match. An already
        // invalid verdict keeps its verdict: a contract failure must not weaken evidence of drift into
        // an absence of evidence.
        return observation with
        {
            ConfigurationState =
                configurationState == CdcConnectorConfigurationState.Matched
                    ? CdcConnectorConfigurationState.Unknown
                    : configurationState,
            Diagnostics = CdcDiagnostic.NormalizeDiagnostics([
                .. observation.Diagnostics,
                .. validationResult.Diagnostics,
            ]),
        };
    }

    private static int? ReadTaskCount(IReadOnlyDictionary<string, string> readBack) =>
        readBack.TryGetValue(TasksMaxPropertyName, out string? tasksMax)
        && int.TryParse(tasksMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out int taskCount)
            ? taskCount
            : null;

    private static CdcConnectorConfigurationItemStates UnknownItems(CdcProvider provider) =>
        new(
            CdcConnectorConfigurationItemState.Unknown,
            CdcConnectorConfigurationItemState.Unknown,
            CdcConnectorConfigurationItemState.Unknown,
            CdcConnectorConfigurationItemState.Unknown,
            CdcConnectorConfigurationItemState.Unknown,
            CdcConnectorConfigurationItemState.Unknown,
            SchemaHistoryState(provider, CdcConnectorConfigurationItemState.Unknown)
        );

    private static CdcConnectorConfigurationItemStates ComparedItems(
        IReadOnlySet<CdcConnectorConfigurationArea> invalidAreas,
        CdcProvider provider
    ) =>
        new(
            State(invalidAreas, CdcConnectorConfigurationArea.Transform),
            State(invalidAreas, CdcConnectorConfigurationArea.Converter),
            State(invalidAreas, CdcConnectorConfigurationArea.ProducerOverride),
            State(invalidAreas, CdcConnectorConfigurationArea.Heartbeat),
            State(invalidAreas, CdcConnectorConfigurationArea.SourceIncludeList),
            State(invalidAreas, CdcConnectorConfigurationArea.Offset),
            SchemaHistoryState(provider, State(invalidAreas, CdcConnectorConfigurationArea.SchemaHistory))
        );

    private static CdcConnectorConfigurationItemState State(
        IReadOnlySet<CdcConnectorConfigurationArea> invalidAreas,
        CdcConnectorConfigurationArea area
    ) =>
        invalidAreas.Contains(area)
            ? CdcConnectorConfigurationItemState.Invalid
            : CdcConnectorConfigurationItemState.Matched;

    /// <summary>
    /// PostgreSQL connectors carry no schema history, and the shared contract requires the item to be
    /// reported as not applicable for that provider.
    /// </summary>
    private static CdcConnectorConfigurationItemState SchemaHistoryState(
        CdcProvider provider,
        CdcConnectorConfigurationItemState state
    ) => provider == CdcProvider.Postgresql ? CdcConnectorConfigurationItemState.NotApplicable : state;

    /// <summary>
    /// Attributes one template diagnostic to the observation item that owns the setting it concerns.
    /// Property names are matched first, because the template's own category is derived from the same
    /// names and is coarser. The offset item is the fallback: a diagnostic no item names means the
    /// registered connector is not the rendered one, so the source stream it reads and the position
    /// identity it commits under can no longer be assumed to be the binding's.
    /// </summary>
    private static CdcConnectorConfigurationArea AreaFor(
        CdcConnectorTemplateDiagnostic diagnostic,
        CdcProvider provider
    )
    {
        CdcConnectorConfigurationArea area = AreaForPropertyName(diagnostic.PropertyName ?? string.Empty);
        if (area != CdcConnectorConfigurationArea.Unattributed)
        {
            return area;
        }

        area = AreaForCategory(diagnostic.Category);
        if (area == CdcConnectorConfigurationArea.Unattributed)
        {
            return CdcConnectorConfigurationArea.Offset;
        }

        // A PostgreSQL read-back has no schema-history item to report through, so a schema-history
        // property there is an unexpected property rather than schema-history drift.
        return area == CdcConnectorConfigurationArea.SchemaHistory && provider == CdcProvider.Postgresql
            ? CdcConnectorConfigurationArea.Offset
            : area;
    }

    private static CdcConnectorConfigurationArea AreaForPropertyName(string propertyName)
    {
        if (
            propertyName is "transforms" or "message.key.columns" or "providerSetup.expectedMessageKeyColumns"
            || propertyName.StartsWith("transforms.", StringComparison.Ordinal)
        )
        {
            return CdcConnectorConfigurationArea.Transform;
        }

        if (
            propertyName is "tombstones.on.delete" or "time.precision.mode" or "unavailable.value.placeholder"
            || propertyName.StartsWith("key.converter", StringComparison.Ordinal)
            || propertyName.StartsWith("value.converter", StringComparison.Ordinal)
            // Connect's errors.* settings govern converter and transform failure handling.
            || propertyName.StartsWith("errors.", StringComparison.Ordinal)
        )
        {
            return CdcConnectorConfigurationArea.Converter;
        }

        if (propertyName.StartsWith("producer.override.", StringComparison.Ordinal))
        {
            return CdcConnectorConfigurationArea.ProducerOverride;
        }

        if (
            propertyName
                is "poll.interval.ms"
                    or "statistics.metrics.enabled"
                    or "providerSetup.heartbeatActionQuery"
            || propertyName.StartsWith("heartbeat.", StringComparison.Ordinal)
            || propertyName.StartsWith("topic.heartbeat.", StringComparison.Ordinal)
        )
        {
            return CdcConnectorConfigurationArea.Heartbeat;
        }

        if (
            propertyName
            is "table.include.list"
                or "column.include.list"
                or "providerSetup.sourceTableInventory"
        )
        {
            return CdcConnectorConfigurationArea.SourceIncludeList;
        }

        if (
            propertyName is "include.schema.changes"
            || propertyName.StartsWith("schema.history.", StringComparison.Ordinal)
        )
        {
            return CdcConnectorConfigurationArea.SchemaHistory;
        }

        if (
            propertyName
                is "topic.prefix"
                    or "connector.class"
                    or "name"
                    or "plugin.name"
                    or "slot.name"
                    or "publication.name"
                    or "publication.autocreate.mode"
                    or "data.query.mode"
            || propertyName.StartsWith("source.partition", StringComparison.Ordinal)
            || propertyName.StartsWith("snapshot.", StringComparison.Ordinal)
            || propertyName.StartsWith("database.", StringComparison.Ordinal)
        )
        {
            return CdcConnectorConfigurationArea.Offset;
        }

        return CdcConnectorConfigurationArea.Unattributed;
    }

    private static CdcConnectorConfigurationArea AreaForCategory(
        CdcConnectorTemplateDiagnosticCategory category
    ) =>
        category switch
        {
            CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation
            or CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
            // Topic naming shapes where records are routed, which the transform chain owns.
            or CdcConnectorTemplateDiagnosticCategory.TopicNamingConfigurationViolation =>
                CdcConnectorConfigurationArea.Transform,
            CdcConnectorTemplateDiagnosticCategory.ConverterConfigurationViolation =>
                CdcConnectorConfigurationArea.Converter,
            CdcConnectorTemplateDiagnosticCategory.ProducerPolicyViolation
            or CdcConnectorTemplateDiagnosticCategory.KafkaSecurityPropertyViolation =>
                CdcConnectorConfigurationArea.ProducerOverride,
            CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation =>
                CdcConnectorConfigurationArea.Heartbeat,
            CdcConnectorTemplateDiagnosticCategory.IncludeListViolation =>
                CdcConnectorConfigurationArea.SourceIncludeList,
            CdcConnectorTemplateDiagnosticCategory.SchemaHistoryConfigurationViolation =>
                CdcConnectorConfigurationArea.SchemaHistory,
            CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
            or CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure =>
                CdcConnectorConfigurationArea.Offset,
            _ => CdcConnectorConfigurationArea.Unattributed,
        };

    /// <summary>
    /// Carries one template diagnostic onto the observation. The template has already bounded and
    /// classified its own text — an unsafe property name arrives hashed and an unsafe value arrives
    /// redacted — and the shared diagnostic sanitizes every field again on construction.
    /// </summary>
    private static CdcDiagnostic ToDiagnostic(
        CdcConnectorTemplateDiagnostic diagnostic,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            diagnostic.Code,
            CdcDiagnosticCategory.ConnectorConfigInvalid,
            ToSeverity(diagnostic.Severity),
            CdcDiagnosticComponent.ConnectorConfig,
            observedAt,
            "CDC connector live configuration does not match the rendered connector template.",
            retryable: false,
            artifactKind: diagnostic.PropertyName,
            artifactName: diagnostic.SafeArtifactOrObjectName?.Value,
            expected: diagnostic.ExpectedValue,
            observed: diagnostic.ObservedValue
        ).WithPath("$.connectorConfig");

    private static CdcDiagnosticSeverity ToSeverity(CdcConnectorTemplateDiagnosticSeverity severity) =>
        severity switch
        {
            CdcConnectorTemplateDiagnosticSeverity.Info => CdcDiagnosticSeverity.Info,
            CdcConnectorTemplateDiagnosticSeverity.Warning => CdcDiagnosticSeverity.Warning,
            _ => CdcDiagnosticSeverity.Error,
        };

    private static CdcDiagnostic ReadBackUnavailable(
        CdcConnectorTemplateRequest templateRequest,
        DateTimeOffset observedAt,
        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack
    ) =>
        new CdcDiagnostic(
            "connectorConfigReadBackUnavailable",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.ConnectorConfig,
            observedAt,
            "CDC connector live configuration read-back is unavailable.",
            retryable: true,
            artifactKind: "connectorConfig",
            artifactName: templateRequest.ConnectorName.Value,
            expected: "connector configuration read-back",
            observed: readBack.Outcome.ToString()
        ).WithPath("$.connectorConfig");

    private static CdcDiagnostic ReadBackUnusable(
        CdcConnectorTemplateRequest templateRequest,
        DateTimeOffset observedAt,
        string rejection
    ) =>
        new CdcDiagnostic(
            "connectorConfigReadBackUnusable",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.ConnectorConfig,
            observedAt,
            "CDC connector live configuration read-back could not be read as a connector property map.",
            retryable: false,
            artifactKind: "connectorConfig",
            artifactName: templateRequest.ConnectorName.Value,
            expected: "connector property map",
            observed: rejection
        ).WithPath("$.connectorConfig");

    /// <summary>The seven configuration areas the shared observation reports an item state for.</summary>
    private enum CdcConnectorConfigurationArea
    {
        Unattributed,
        Transform,
        Converter,
        ProducerOverride,
        Heartbeat,
        SourceIncludeList,
        Offset,
        SchemaHistory,
    }

    private readonly record struct CdcConnectorConfigurationItemStates(
        CdcConnectorConfigurationItemState Transform,
        CdcConnectorConfigurationItemState Converter,
        CdcConnectorConfigurationItemState ProducerOverride,
        CdcConnectorConfigurationItemState Heartbeat,
        CdcConnectorConfigurationItemState SourceIncludeList,
        CdcConnectorConfigurationItemState Offset,
        CdcConnectorConfigurationItemState SchemaHistory
    );
}
