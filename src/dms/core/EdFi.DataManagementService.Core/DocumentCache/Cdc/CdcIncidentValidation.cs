// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public static class CdcIncidentValidator
{
    private const int MaximumIncidentValueLength = 256;
    private const string Sha256Prefix = "sha256:";

    public static CdcContractValidationResult Validate(CdcIncident incident, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(incident);

        CdcDiagnosticCollector diagnostics = new();
        Validate(incident, nowUtc, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcIncident incident,
        CdcBinding binding,
        DateTimeOffset nowUtc
    )
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(binding);

        CdcDiagnosticCollector diagnostics = new();
        Validate(incident, nowUtc, diagnostics);

        if (incident.BindingIdentity != binding.ToCompleteBindingIdentity())
        {
            diagnostics.MalformedPayload(
                "$.bindingIdentity",
                "CDC incident bindingIdentity must match the complete persisted binding identity."
            );
        }

        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        if (artifactNameResult.Inventory is not null && incident.PositionMetadata is not null)
        {
            ValidatePositionMetadataMatchesInventory(
                incident.PositionMetadata,
                artifactNameResult.Inventory,
                diagnostics
            );
        }

        return diagnostics.ToValidationResult();
    }

    private static void Validate(
        CdcIncident incident,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateContractVersion(incident.ContractVersion, "$.contractVersion", diagnostics);
        ValidateIncidentType(incident.IncidentType, diagnostics);
        ValidateTimestamp(incident.LatchedAt, nowUtc, "$.latchedAt", diagnostics);
        ValidateBindingIdentity(incident.BindingIdentity, diagnostics);
        ValidateFailureCategory(incident.FailureCategory, diagnostics);
        ValidatePositionMetadata(incident.PositionMetadata, diagnostics);
    }

    private static void ValidateContractVersion(
        int contractVersion,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (contractVersion != CdcJsonContract.CurrentContractVersion)
        {
            diagnostics.InvalidContractVersion(
                path,
                $"CDC contract version `{contractVersion}` is not supported. Expected `{CdcJsonContract.CurrentContractVersion}`."
            );
        }
    }

    private static void ValidateIncidentType(CdcIncidentType incidentType, CdcDiagnosticCollector diagnostics)
    {
        if (!Enum.IsDefined(incidentType) || incidentType != CdcIncidentType.SourceHistoryContinuityLost)
        {
            diagnostics.InvalidEnumValue(
                "$.incidentType",
                "CDC incidentType must be `sourceHistoryContinuityLost`."
            );
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset timestamp,
        DateTimeOffset nowUtc,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcContractValidationResult result = CdcJsonContract.ValidateNotFutureUtcTimestamp(
            timestamp,
            nowUtc.ToUniversalTime(),
            path
        );

        foreach (CdcDiagnostic diagnostic in result.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }
    }

    private static void ValidateBindingIdentity(
        CdcCompleteBindingIdentity? bindingIdentity,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (bindingIdentity is null)
        {
            diagnostics.MissingRequiredField("$.bindingIdentity", "bindingIdentity");
            return;
        }

        CdcKafkaSafeTokenValidator.Validate(
            bindingIdentity.DeploymentKey,
            "$.bindingIdentity.deploymentKey",
            "deploymentKey",
            diagnostics
        );
        CdcKafkaSafeTokenValidator.Validate(
            bindingIdentity.TenantKey,
            "$.bindingIdentity.tenantKey",
            "tenantKey",
            diagnostics
        );
        ValidateDataStoreId(bindingIdentity.DataStoreId, "$.bindingIdentity.dataStoreId", diagnostics);
        CdcKafkaSafeTokenValidator.Validate(
            bindingIdentity.InstanceKey,
            "$.bindingIdentity.instanceKey",
            "instanceKey",
            diagnostics
        );
        ValidatePositive(
            bindingIdentity.Generation,
            "$.bindingIdentity.generation",
            "generation",
            diagnostics
        );
        ValidateProvider(bindingIdentity.Provider, "$.bindingIdentity.provider", diagnostics);
        ValidateSha256(
            bindingIdentity.PhysicalSourceFingerprint,
            "$.bindingIdentity.physicalSourceFingerprint",
            "physicalSourceFingerprint",
            true,
            diagnostics
        );
        ValidateArtifactName(
            bindingIdentity.ConnectorName,
            "$.bindingIdentity.connectorName",
            "connectorName",
            true,
            diagnostics
        );
        ValidateArtifactName(
            bindingIdentity.TopicName,
            "$.bindingIdentity.topicName",
            "topicName",
            true,
            diagnostics
        );
    }

    private static void ValidateDataStoreId(
        string? dataStoreId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (dataStoreId is null || dataStoreId.Length == 0)
        {
            diagnostics.MissingRequiredField(path, "dataStoreId");
            return;
        }

        if (dataStoreId.Length > 1 && dataStoreId[0] == '0')
        {
            diagnostics.MalformedPayload(path, "CDC dataStoreId must not contain leading zero padding.");
            return;
        }

        if (dataStoreId.Any(character => character is < '0' or > '9'))
        {
            diagnostics.MalformedPayload(
                path,
                "CDC dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
            );
            return;
        }

        if (!long.TryParse(dataStoreId, out long value) || value <= 0)
        {
            diagnostics.MalformedPayload(
                path,
                "CDC dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
            );
        }
    }

    private static void ValidateProvider(
        CdcProvider provider,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(provider))
        {
            diagnostics.InvalidEnumValue(path, "CDC provider must be `postgresql` or `sqlServer`.");
        }
    }

    private static void ValidatePositive(
        long value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value <= 0)
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must be positive.");
        }
    }

    private static void ValidateFailureCategory(
        CdcIncidentFailureCategory failureCategory,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(failureCategory))
        {
            diagnostics.InvalidEnumValue("$.failureCategory", "CDC incident failureCategory is not defined.");
        }
    }

    private static void ValidatePositionMetadata(
        CdcIncidentPositionMetadata? positionMetadata,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (positionMetadata is null)
        {
            diagnostics.MissingRequiredField("$.positionMetadata", "positionMetadata");
            return;
        }

        ValidateArtifactName(
            positionMetadata.ConnectorName,
            "$.positionMetadata.connectorName",
            "connectorName",
            false,
            diagnostics
        );
        ValidateArtifactName(
            positionMetadata.TopicName,
            "$.positionMetadata.topicName",
            "topicName",
            false,
            diagnostics
        );
        ValidateArtifactName(
            positionMetadata.ProgressTopicName,
            "$.positionMetadata.progressTopicName",
            "progressTopicName",
            false,
            diagnostics
        );
        ValidateArtifactName(
            positionMetadata.SchemaHistoryTopicName,
            "$.positionMetadata.schemaHistoryTopicName",
            "schemaHistoryTopicName",
            false,
            diagnostics
        );
        ValidateArtifactName(
            positionMetadata.ProviderArtifactName,
            "$.positionMetadata.providerArtifactName",
            "providerArtifactName",
            false,
            diagnostics
        );
        ValidateSha256(
            positionMetadata.ConnectSourcePartitionHash,
            "$.positionMetadata.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            false,
            diagnostics
        );
        ValidateProviderPosition(
            positionMetadata.LsnProc,
            "$.positionMetadata.lsnProc",
            "lsnProc",
            diagnostics
        );
        ValidateProviderPosition(
            positionMetadata.CommitLsn,
            "$.positionMetadata.commitLsn",
            "commitLsn",
            diagnostics
        );
        ValidateProviderPosition(
            positionMetadata.ChangeLsn,
            "$.positionMetadata.changeLsn",
            "changeLsn",
            diagnostics
        );
        ValidateProviderPosition(
            positionMetadata.RetainedRangeStart,
            "$.positionMetadata.retainedRangeStart",
            "retainedRangeStart",
            diagnostics
        );
        ValidateProviderPosition(
            positionMetadata.RetainedRangeEnd,
            "$.positionMetadata.retainedRangeEnd",
            "retainedRangeEnd",
            diagnostics
        );
        ValidateEventSerialNo(positionMetadata.EventSerialNo, diagnostics);
        ValidateUnavailableFacts(positionMetadata.UnavailableFacts, diagnostics);
    }

    private static void ValidatePositionMetadataMatchesInventory(
        CdcIncidentPositionMetadata positionMetadata,
        CdcArtifactInventory inventory,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateOptionalExactMatch(
            positionMetadata.ConnectorName,
            inventory.ConnectorName,
            "$.positionMetadata.connectorName",
            "connectorName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            positionMetadata.TopicName,
            inventory.TopicName,
            "$.positionMetadata.topicName",
            "topicName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            positionMetadata.ProgressTopicName,
            inventory.ProgressTopicName,
            "$.positionMetadata.progressTopicName",
            "progressTopicName",
            diagnostics
        );

        if (inventory.SchemaHistoryTopicName is null)
        {
            if (positionMetadata.SchemaHistoryTopicName is not null)
            {
                diagnostics.MalformedPayload(
                    "$.positionMetadata.schemaHistoryTopicName",
                    "CDC schemaHistoryTopicName is not applicable for the binding provider."
                );
            }
        }
        else
        {
            ValidateOptionalExactMatch(
                positionMetadata.SchemaHistoryTopicName,
                inventory.SchemaHistoryTopicName,
                "$.positionMetadata.schemaHistoryTopicName",
                "schemaHistoryTopicName",
                diagnostics
            );
        }

        if (positionMetadata.ProviderArtifactName is not null)
        {
            HashSet<string> providerArtifactNames = inventory
                .GovernedArtifacts.Where(artifact => IsProviderArtifact(artifact.Kind))
                .Select(artifact => artifact.Name)
                .ToHashSet(StringComparer.Ordinal);

            if (!providerArtifactNames.Contains(positionMetadata.ProviderArtifactName))
            {
                diagnostics.MalformedPayload(
                    "$.positionMetadata.providerArtifactName",
                    "CDC providerArtifactName must match a binding-derived provider artifact."
                );
            }
        }
    }

    private static bool IsProviderArtifact(CdcGovernedArtifactKind kind) =>
        kind
            is CdcGovernedArtifactKind.PostgresqlPublication
                or CdcGovernedArtifactKind.PostgresqlLogicalSlot
                or CdcGovernedArtifactKind.SqlServerCdcGatingRole
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat;

    private static void ValidateOptionalExactMatch(
        string? value,
        string expected,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null && !string.Equals(value, expected, StringComparison.Ordinal))
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must match the binding-derived artifact.");
        }
    }

    private static void ValidateArtifactName(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (value.Length == 0)
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must not be empty.");
            return;
        }

        CdcKafkaSafeTokenValidator.Validate(value, path, fieldName, diagnostics);
        if (value.Length > CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength)
        {
            diagnostics.MalformedPayload(
                path,
                $"CDC {fieldName} must not exceed {CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength} characters."
            );
        }
    }

    private static void ValidateSha256(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (!IsSha256Fingerprint(value))
        {
            diagnostics.MalformedPayload(
                path,
                $"CDC {fieldName} must be `sha256:` plus 64 lowercase hex characters."
            );
        }
    }

    private static bool IsSha256Fingerprint(string value)
    {
        if (
            value.Length != Sha256Prefix.Length + 64
            || !value.StartsWith(Sha256Prefix, StringComparison.Ordinal)
        )
        {
            return false;
        }

        return value[Sha256Prefix.Length..].All(IsLowercaseHex);
    }

    private static bool IsLowercaseHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void ValidateProviderPosition(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0)
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must not be empty.");
            return;
        }

        if (value.Length > MaximumIncidentValueLength || !IsProviderPosition(value))
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must be a provider-normalized position.");
        }
    }

    private static bool IsProviderPosition(string value) =>
        IsDecimalInteger(value) || IsPostgresqlWalLsn(value) || IsSqlServerLsn(value);

    private static bool IsDecimalInteger(string value) =>
        value.Length != 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsPostgresqlWalLsn(string value)
    {
        int separatorIndex = value.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            return false;
        }

        return IsHex(value[..separatorIndex], 1, 16) && IsHex(value[(separatorIndex + 1)..], 1, 16);
    }

    private static bool IsSqlServerLsn(string value)
    {
        string[] parts = value.Split(':');
        return parts.Length == 3 && IsHex(parts[0], 8, 8) && IsHex(parts[1], 8, 8) && IsHex(parts[2], 4, 4);
    }

    private static bool IsHex(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void ValidateEventSerialNo(long? eventSerialNo, CdcDiagnosticCollector diagnostics)
    {
        if (eventSerialNo < 0)
        {
            diagnostics.MalformedPayload(
                "$.positionMetadata.eventSerialNo",
                "CDC eventSerialNo must be zero or positive."
            );
        }
    }

    private static void ValidateUnavailableFacts(
        IReadOnlyList<CdcIncidentUnavailableFact>? unavailableFacts,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (unavailableFacts is null)
        {
            diagnostics.MissingRequiredField("$.positionMetadata.unavailableFacts", "unavailableFacts");
            return;
        }

        HashSet<CdcIncidentUnavailableFact> facts = [];
        for (int index = 0; index < unavailableFacts.Count; index++)
        {
            CdcIncidentUnavailableFact fact = unavailableFacts[index];
            string path = $"$.positionMetadata.unavailableFacts[{index}]";
            if (!Enum.IsDefined(fact))
            {
                diagnostics.InvalidEnumValue(path, "CDC incident unavailableFact is not defined.");
                continue;
            }

            if (!facts.Add(fact))
            {
                diagnostics.MalformedPayload(
                    path,
                    "CDC incident unavailableFacts must not contain duplicates."
                );
            }
        }
    }
}
