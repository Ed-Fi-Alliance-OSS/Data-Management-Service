// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

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
            is not (
                CdcConnectorTemplateSourcePhase.RegistrationPreflight
                or CdcConnectorTemplateSourcePhase.LiveReadBack
            )
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
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
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
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
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
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
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
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
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
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                    "providerSetup.observedPhysicalSourceFingerprint",
                    "binding physical-source fingerprint",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (!HasRequiredSourceInventory(result.SourceTableInventory))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                    "providerSetup.sourceTableInventory",
                    "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                    RedactedValue,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (!HasExpectedMessageKeyColumns(result.ExpectedMessageKeyColumns))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
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
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                    "providerSetup.heartbeatActionQuery",
                    "fresh provider heartbeat action query",
                    null,
                    request.TemplateRequest,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static bool HasRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory
    )
    {
        CdcSourceTableKind[] requiredKinds =
        [
            CdcSourceTableKind.DocumentCache,
            CdcSourceTableKind.Document,
            CdcSourceTableKind.CdcHeartbeat,
        ];
        CdcSourceTableKind[] observedKinds = sourceTableInventory.Select(table => table.TableKind).ToArray();

        return sourceTableInventory.Count == requiredKinds.Length
            && !requiredKinds.Except(observedKinds).Any()
            && !observedKinds.Except(requiredKinds).Any()
            && !observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1);
    }

    private static bool HasExpectedMessageKeyColumns(
        IReadOnlyList<CdcExpectedMessageKeyColumns> expectedMessageKeyColumns
    )
    {
        CdcSourceTableKind[] requiredKinds = [CdcSourceTableKind.DocumentCache, CdcSourceTableKind.Document];
        CdcSourceTableKind[] observedKinds = expectedMessageKeyColumns
            .Select(columns => columns.TableKind)
            .ToArray();

        return expectedMessageKeyColumns.Count == requiredKinds.Length
            && !requiredKinds.Except(observedKinds).Any()
            && !observedKinds.Except(requiredKinds).Any()
            && !observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1)
            && expectedMessageKeyColumns.All(columns =>
                columns.KeyColumns.Count == 1
                && string.Equals(columns.KeyColumns[0].Value, "DocumentUuid", StringComparison.Ordinal)
            );
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
                if (IsAcceptedSecretReadBack(expectedProperty.Value, observedValue))
                {
                    continue;
                }

                diagnostics.Add(
                    BuildSecretDiagnostic(
                        expectedProperty.Key,
                        request.TemplateRequest,
                        sourcePhase,
                        observedValue.Length == 0
                            ? CdcConnectorTemplateRedactionClassification.MaskedSecret
                            : CdcConnectorTemplateRedactionClassification.SecretValue
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

            if (!IsUnexpectedContractProperty(observedProperty.Key))
            {
                continue;
            }

            diagnostics.Add(
                BuildPropertyDiagnostic(
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

    private static bool IsUnexpectedContractProperty(string propertyName) =>
        CdcConnectorTemplateInputValidator.IsReservedKey(propertyName)
        || CdcConnectorTemplateInputValidator.IsKafkaClientSecurityProperty(propertyName)
        || CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(propertyName)
        || propertyName.StartsWith("database.", StringComparison.Ordinal);

    private static bool IsAcceptedSecretReadBack(string expectedValue, string observedValue) =>
        observedValue.Length > 0
        && (
            string.Equals(expectedValue, observedValue, StringComparison.Ordinal)
            || string.Equals(observedValue, "[hidden]", StringComparison.Ordinal)
            || observedValue.All(character => character == '*')
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

    private static CdcConnectorTemplateDiagnostic BuildSecretDiagnostic(
        string propertyName,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        BuildDiagnostic(
            CdcConnectorTemplateDiagnosticCodes.LiveReadBackSecretMismatch,
            CdcConnectorTemplateDiagnosticCategory.SecretRedactionFailure,
            propertyName,
            "exact externalized reference or masked secret evidence",
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
            propertyName == "source.partition.database"
                ? CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                : CdcConnectorTemplateRedactionClassification.Safe;

        return BuildDiagnostic(
            CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch,
            CdcConnectorTemplateDiagnosticCategory.LiveReadBack,
            propertyName,
            RedactValueForDiagnostic(expectedValue, redactionClassification),
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
            return CdcConnectorTemplateDiagnosticCategory.IncludeList;
        }

        if (propertyName == "message.key.columns")
        {
            return CdcConnectorTemplateDiagnosticCategory.MessageKey;
        }

        if (propertyName == "transforms" || propertyName.StartsWith("transforms.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.Transform;
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
            return CdcConnectorTemplateDiagnosticCategory.Converter;
        }

        if (propertyName.StartsWith("topic.", StringComparison.Ordinal) || propertyName == "topic.prefix")
        {
            return CdcConnectorTemplateDiagnosticCategory.TopicNaming;
        }

        if (
            propertyName.StartsWith("heartbeat.", StringComparison.Ordinal)
            || propertyName == "poll.interval.ms"
        )
        {
            return CdcConnectorTemplateDiagnosticCategory.Heartbeat;
        }

        if (propertyName.StartsWith("schema.history.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.SchemaHistory;
        }

        if (propertyName.StartsWith("producer.override.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.ProducerPolicy;
        }

        if (propertyName.StartsWith("database.", StringComparison.Ordinal))
        {
            return CdcConnectorTemplateDiagnosticCategory.ConnectionProperty;
        }

        if (CdcConnectorTemplateInputValidator.IsKafkaClientSecurityProperty(propertyName))
        {
            return CdcConnectorTemplateDiagnosticCategory.KafkaSecurityProperty;
        }

        return CdcConnectorTemplateDiagnosticCategory.LiveReadBack;
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
            || propertyName
                is "table.include.list"
                    or "message.key.columns"
                    or "heartbeat.action.query"
                    or "schema.history.internal.kafka.bootstrap.servers"
        )
        {
            return CdcConnectorTemplateRedactionClassification.PhysicalIdentifier;
        }

        return CdcConnectorTemplateRedactionClassification.Safe;
    }

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
