// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal sealed record DocumentCacheAdminStatusRequest(DocumentCacheTargetKey TargetKey);

internal sealed record DocumentCacheAdminJsonRequest(
    string CommandName,
    Type RequestType,
    object Request,
    DocumentCacheTargetKey TargetKey
);

internal static class DocumentCacheAdminJsonSerializer
{
    public static string SerializeContract(object contract, Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(contractType);

        return JsonSerializer.Serialize(contract, contractType);
    }
}

internal static class DocumentCacheAdminJsonRequestParser
{
    private sealed record MutatingCommandContract(
        Type RequestType,
        DocumentCacheAdministrativeCommandConfirmation ExpectedConfirmation,
        DocumentCacheOfflineWriterAdmissionConfirmation? ExpectedOfflineWriterAdmissionConfirmation
    )
    {
        public bool SupportsOfflineWriterAdmission => ExpectedOfflineWriterAdmissionConfirmation is not null;
    }

    private static readonly IReadOnlyDictionary<string, MutatingCommandContract> MutatingContracts =
        new Dictionary<string, MutatingCommandContract>(StringComparer.Ordinal)
        {
            [DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName] = new(
                typeof(DocumentCacheGuardedNewEmptyActivationRequest),
                DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation,
                ExpectedOfflineWriterAdmissionConfirmation: null
            ),
            [DocumentCacheAdminCommandSurface.ActivateOfflineCommandName] = new(
                typeof(DocumentCacheOfflineActivationRequest),
                DocumentCacheAdministrativeCommandConfirmation.OfflineActivation,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            ),
            [DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName] = new(
                typeof(DocumentCacheOfflineDeactivationRequest),
                DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            ),
            [DocumentCacheAdminCommandSurface.RebuildOnlineCommandName] = new(
                typeof(DocumentCacheOnlineCacheRebuildRequest),
                DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild,
                ExpectedOfflineWriterAdmissionConfirmation: null
            ),
            [DocumentCacheAdminCommandSurface.ScrubCommandName] = new(
                typeof(DocumentCacheExplicitIntegrityScrubRequest),
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub,
                ExpectedOfflineWriterAdmissionConfirmation: null
            ),
            [DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName] = new(
                typeof(DocumentCacheInternalOnlyCacheAheadRecoveryRequest),
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery,
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
            ),
        };

    public static bool TryParse(
        string commandName,
        string requestJson,
        out DocumentCacheAdminJsonRequest? request,
        out string? failure
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(requestJson);

        request = null;
        failure = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                requestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
        }
        catch (JsonException exception)
        {
            failure = $"Request JSON is malformed: {exception.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failure = "Request JSON must be an object.";
                return false;
            }

            return string.Equals(
                commandName,
                DocumentCacheAdminCommandSurface.StatusCommandName,
                StringComparison.Ordinal
            )
                ? TryParseStatusRequest(document.RootElement, out request, out failure)
                : TryParseMutatingRequest(commandName, document.RootElement, out request, out failure);
        }
    }

    private static bool TryParseStatusRequest(
        JsonElement rootElement,
        out DocumentCacheAdminJsonRequest? request,
        out string? failure
    )
    {
        request = null;
        failure = null;

        if (
            !TryReadProperties(
                rootElement,
                "request",
                requiredProperties: ["targetKey"],
                allowedProperties: ["targetKey"],
                out Dictionary<string, JsonElement> rootProperties,
                out failure
            )
        )
        {
            return false;
        }

        if (
            !TryReadTargetKey(rootProperties["targetKey"], out DocumentCacheTargetKey? targetKey, out failure)
        )
        {
            return false;
        }

        DocumentCacheTargetKey validTargetKey =
            targetKey
            ?? throw new InvalidOperationException("Target key validation succeeded without a target.");
        request = new DocumentCacheAdminJsonRequest(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            typeof(DocumentCacheAdminStatusRequest),
            new DocumentCacheAdminStatusRequest(validTargetKey),
            validTargetKey
        );
        return true;
    }

    private static bool TryParseMutatingRequest(
        string commandName,
        JsonElement rootElement,
        out DocumentCacheAdminJsonRequest? request,
        out string? failure
    )
    {
        request = null;
        failure = null;

        if (!MutatingContracts.TryGetValue(commandName, out MutatingCommandContract? contract))
        {
            failure = $"Command '{commandName}' does not support JSON request input.";
            return false;
        }

        string[] allowedProperties = contract.SupportsOfflineWriterAdmission
            ? ["targetKey", "confirmation", "expectedPhysicalSourceFingerprint", "offlineWriterAdmission"]
            : ["targetKey", "confirmation", "expectedPhysicalSourceFingerprint"];
        string[] requiredProperties = contract.SupportsOfflineWriterAdmission
            ? ["targetKey", "confirmation", "offlineWriterAdmission"]
            : ["targetKey", "confirmation"];

        if (
            !TryReadProperties(
                rootElement,
                "request",
                requiredProperties,
                allowedProperties,
                out Dictionary<string, JsonElement> rootProperties,
                out failure
            )
        )
        {
            return false;
        }

        if (
            !TryReadTargetKey(rootProperties["targetKey"], out DocumentCacheTargetKey? targetKey, out failure)
        )
        {
            return false;
        }

        DocumentCacheTargetKey validTargetKey =
            targetKey
            ?? throw new InvalidOperationException("Target key validation succeeded without a target.");
        if (
            !TryReadExpectedConfirmation(
                rootProperties["confirmation"],
                contract.ExpectedConfirmation,
                out failure
            )
        )
        {
            return false;
        }

        if (
            !TryReadExpectedPhysicalSourceFingerprint(
                rootProperties,
                out DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
                out failure
            )
        )
        {
            return false;
        }

        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission = null;
        if (contract.SupportsOfflineWriterAdmission)
        {
            if (!TryReadExpectedOfflineWriterAdmission(rootProperties["offlineWriterAdmission"], out failure))
            {
                return false;
            }

            offlineWriterAdmission = new(
                confirmed: true,
                contract.ExpectedOfflineWriterAdmissionConfirmation!.Value
            );
        }

        object sharedRequest = CreateSharedMutatingRequest(
            commandName,
            validTargetKey,
            expectedPhysicalSourceFingerprint,
            contract.ExpectedConfirmation,
            offlineWriterAdmission
        );
        request = new DocumentCacheAdminJsonRequest(
            commandName,
            contract.RequestType,
            sharedRequest,
            validTargetKey
        );
        return true;
    }

    private static bool TryReadExpectedConfirmation(
        JsonElement confirmationElement,
        DocumentCacheAdministrativeCommandConfirmation expectedConfirmation,
        out string? failure
    )
    {
        failure = null;

        if (confirmationElement.ValueKind != JsonValueKind.String)
        {
            failure = "Request JSON property 'confirmation' must be a string.";
            return false;
        }

        string expectedConfirmationName = JsonNamingPolicy.CamelCase.ConvertName(
            expectedConfirmation.ToString()
        );
        string? suppliedConfirmationName = confirmationElement.GetString();
        if (!string.Equals(suppliedConfirmationName, expectedConfirmationName, StringComparison.Ordinal))
        {
            failure =
                $"Request JSON confirmation '{suppliedConfirmationName}' does not match command confirmation '{expectedConfirmationName}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadExpectedOfflineWriterAdmission(
        JsonElement offlineWriterAdmissionElement,
        out string? failure
    )
    {
        failure = null;

        if (offlineWriterAdmissionElement.ValueKind != JsonValueKind.String)
        {
            failure = "Request JSON property 'offlineWriterAdmission' must be a string.";
            return false;
        }

        string? suppliedAdmission = offlineWriterAdmissionElement.GetString();
        if (
            !string.Equals(
                suppliedAdmission,
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue,
                StringComparison.Ordinal
            )
        )
        {
            failure =
                $"Request JSON offlineWriterAdmission '{suppliedAdmission}' does not match required acknowledgement '{DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadExpectedPhysicalSourceFingerprint(
        IReadOnlyDictionary<string, JsonElement> rootProperties,
        out DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
        out string? failure
    )
    {
        expectedPhysicalSourceFingerprint = null;
        failure = null;

        if (
            !rootProperties.TryGetValue(
                "expectedPhysicalSourceFingerprint",
                out JsonElement fingerprintElement
            )
        )
        {
            return true;
        }

        if (fingerprintElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (fingerprintElement.ValueKind != JsonValueKind.String)
        {
            failure = "Request JSON property 'expectedPhysicalSourceFingerprint' must be a string.";
            return false;
        }

        try
        {
            expectedPhysicalSourceFingerprint = new DocumentCachePhysicalSourceFingerprint(
                fingerprintElement.GetString() ?? string.Empty
            );
            return true;
        }
        catch (ArgumentException exception)
        {
            failure =
                $"Request JSON property 'expectedPhysicalSourceFingerprint' is invalid: {exception.Message}";
            return false;
        }
    }

    private static object CreateSharedMutatingRequest(
        string commandName,
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
        DocumentCacheAdministrativeCommandConfirmation confirmation,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission
    )
    {
        DocumentCacheAdministrativeTargetKey administrativeTargetKey =
            DocumentCacheAdministrativeTargetKey.FromTargetKey(targetKey);

        return commandName switch
        {
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName =>
                new DocumentCacheGuardedNewEmptyActivationRequest(
                    administrativeTargetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName =>
                new DocumentCacheOfflineActivationRequest(
                    administrativeTargetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName =>
                new DocumentCacheOfflineDeactivationRequest(
                    administrativeTargetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName =>
                new DocumentCacheOnlineCacheRebuildRequest(
                    administrativeTargetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            DocumentCacheAdminCommandSurface.ScrubCommandName =>
                new DocumentCacheExplicitIntegrityScrubRequest(
                    administrativeTargetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName =>
                new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                    administrativeTargetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            _ => throw new InvalidOperationException($"Unsupported mutating command '{commandName}'."),
        };
    }

    private static bool TryReadTargetKey(
        JsonElement targetKeyElement,
        out DocumentCacheTargetKey? targetKey,
        out string? failure
    )
    {
        targetKey = null;
        failure = null;

        if (targetKeyElement.ValueKind != JsonValueKind.Object)
        {
            failure = "Request JSON property 'targetKey' must be an object.";
            return false;
        }

        if (
            !TryReadProperties(
                targetKeyElement,
                "targetKey",
                requiredProperties: ["tenantKey", "dataStoreId"],
                allowedProperties: ["tenantKey", "dataStoreId"],
                out Dictionary<string, JsonElement> targetProperties,
                out failure
            )
        )
        {
            return false;
        }

        JsonElement tenantKeyElement = targetProperties["tenantKey"];
        if (tenantKeyElement.ValueKind != JsonValueKind.String)
        {
            failure = "Request JSON property 'targetKey.tenantKey' must be a string.";
            return false;
        }

        JsonElement dataStoreIdElement = targetProperties["dataStoreId"];
        if (
            dataStoreIdElement.ValueKind != JsonValueKind.Number
            || !dataStoreIdElement.TryGetInt64(out long dataStoreId)
        )
        {
            failure = "Request JSON property 'targetKey.dataStoreId' must be an integer.";
            return false;
        }

        if (
            !DocumentCacheTargetKey.TryCreate(
                tenantKeyElement.GetString(),
                dataStoreId,
                out targetKey,
                out string? validationFailure
            )
        )
        {
            failure = validationFailure;
            return false;
        }

        return true;
    }

    private static bool TryReadProperties(
        JsonElement jsonObject,
        string objectName,
        string[] requiredProperties,
        string[] allowedProperties,
        out Dictionary<string, JsonElement> properties,
        out string? failure
    )
    {
        properties = [];
        failure = null;

        foreach (JsonProperty property in jsonObject.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                failure = $"Request JSON property '{property.Name}' is not supported in {objectName}.";
                return false;
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                failure = $"Request JSON property '{property.Name}' is duplicated in {objectName}.";
                return false;
            }
        }

        foreach (string requiredProperty in requiredProperties)
        {
            if (!properties.ContainsKey(requiredProperty))
            {
                failure = $"Request JSON property '{requiredProperty}' is required in {objectName}.";
                return false;
            }
        }

        return true;
    }
}
