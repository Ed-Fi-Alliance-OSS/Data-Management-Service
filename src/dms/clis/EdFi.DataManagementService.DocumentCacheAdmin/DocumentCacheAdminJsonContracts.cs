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

        if (
            !DocumentCacheAdminMutatingCommandContracts.TryGet(
                commandName,
                out DocumentCacheAdminMutatingCommandContract? contract
            )
        )
        {
            failure = $"Command '{commandName}' does not support JSON request input.";
            return false;
        }

        string[] allowedProperties = contract.RequiresOfflineWriterAdmission
            ? ["targetKey", "confirmation", "expectedPhysicalSourceFingerprint", "offlineWriterAdmission"]
            : ["targetKey", "confirmation", "expectedPhysicalSourceFingerprint"];
        string[] requiredProperties = contract.RequiresOfflineWriterAdmission
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
        if (
            contract.RequiresOfflineWriterAdmission
            && !TryReadOfflineWriterAdmission(
                rootProperties["offlineWriterAdmission"],
                out offlineWriterAdmission,
                out failure
            )
        )
        {
            return false;
        }

        object sharedRequest = contract.CreateRequest(
            validTargetKey,
            expectedPhysicalSourceFingerprint,
            offlineWriterAdmission
        );
        request = new DocumentCacheAdminJsonRequest(
            commandName,
            contract.RequestType,
            sharedRequest,
            contract.ReadTargetKey(sharedRequest).TargetKey
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

    private static bool TryReadOfflineWriterAdmission(
        JsonElement offlineWriterAdmissionElement,
        out DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        out string? failure
    )
    {
        offlineWriterAdmission = null;
        failure = null;

        try
        {
            offlineWriterAdmission = JsonSerializer.Deserialize<DocumentCacheOfflineWriterAdmission>(
                offlineWriterAdmissionElement.GetRawText()
            );
        }
        catch (JsonException exception)
        {
            failure = $"Request JSON property 'offlineWriterAdmission' is invalid: {exception.Message}";
            return false;
        }

        if (offlineWriterAdmission is null)
        {
            failure =
                $"Request JSON property 'offlineWriterAdmission' must be '{DocumentCacheOfflineWriterAdmission.ClosedAndDrainedJsonValue}'.";
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
