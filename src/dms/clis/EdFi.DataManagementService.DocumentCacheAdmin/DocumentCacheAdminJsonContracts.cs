// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal sealed record DocumentCacheAdminJsonRequest(object SharedRequest);

internal static class DocumentCacheAdminJsonSerializer
{
    public static string SerializeContract(object contract, Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(contractType);

        return JsonSerializer.Serialize(contract, contractType);
    }

    /// <summary>
    /// Serializes a CDC contract with the CDC contract's own serializer options rather than the
    /// DocumentCache defaults, so the lower-camel enum tokens and required contract version the shared
    /// CDC contracts define round-trip exactly.
    /// </summary>
    public static string SerializeCdcContract(object contract, Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(contractType);

        return JsonSerializer.Serialize(contract, contractType, CdcJsonContract.SerializerOptions);
    }
}

internal static class DocumentCacheAdminJsonRequestParser
{
    private static readonly JsonSerializerOptions _mutatingRequestJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static bool TryParse(
        string commandName,
        string requestJson,
        out DocumentCacheTargetKey? targetKey,
        out DocumentCacheAdminJsonRequest? request,
        out string? failure
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(requestJson);

        targetKey = null;
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
                ? TryParseStatusRequest(document.RootElement, out targetKey, out request, out failure)
                : TryParseMutatingRequest(
                    commandName,
                    document.RootElement,
                    out targetKey,
                    out request,
                    out failure
                );
        }
    }

    private static bool TryParseStatusRequest(
        JsonElement rootElement,
        out DocumentCacheTargetKey? targetKey,
        out DocumentCacheAdminJsonRequest? request,
        out string? failure
    )
    {
        targetKey = null;
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
            !TryReadTargetKey(
                rootProperties["targetKey"],
                out DocumentCacheTargetKey? parsedTargetKey,
                out failure
            )
        )
        {
            return false;
        }

        targetKey =
            parsedTargetKey
            ?? throw new InvalidOperationException("Target key validation succeeded without a target.");
        return true;
    }

    private static bool TryParseMutatingRequest(
        string commandName,
        JsonElement rootElement,
        out DocumentCacheTargetKey? targetKey,
        out DocumentCacheAdminJsonRequest? request,
        out string? failure
    )
    {
        targetKey = null;
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

        if (!TryRejectDuplicateProperties(rootElement, "request", out failure))
        {
            return false;
        }

        if (!TryValidateRawConfirmationToken(rootElement, contract, out failure))
        {
            return false;
        }

        object? sharedRequest;
        try
        {
            sharedRequest = rootElement.Deserialize(
                contract.SharedRequestClrType,
                _mutatingRequestJsonOptions
            );
        }
        catch (JsonException exception)
        {
            failure = $"Request JSON is not supported by '{commandName}': {exception.Message}";
            return false;
        }
        catch (ArgumentException exception)
        {
            failure = $"Request JSON is not valid for '{commandName}': {exception.Message}";
            return false;
        }

        if (sharedRequest is null)
        {
            failure = $"Request JSON could not be deserialized as '{contract.SharedRequestClrType.Name}'.";
            return false;
        }

        if (!TryValidateMutatingRequest(contract, sharedRequest, out failure))
        {
            return false;
        }

        targetKey = contract.ReadTargetKey(sharedRequest).TargetKey;
        request = new DocumentCacheAdminJsonRequest(sharedRequest);
        return true;
    }

    private static bool TryValidateMutatingRequest(
        DocumentCacheAdminMutatingCommandContract contract,
        object sharedRequest,
        out string? failure
    )
    {
        failure = null;

        DocumentCacheAdministrativeCommandConfirmation? confirmation = contract.ReadConfirmation(
            sharedRequest
        );
        if (confirmation is null)
        {
            failure =
                $"Request JSON property 'confirmation' is required in request and must be '{contract.ExpectedConfirmationJsonValue}'.";
            return false;
        }

        if (!Enum.IsDefined(confirmation.Value) || confirmation.Value != contract.ExpectedConfirmation)
        {
            failure =
                $"Request JSON confirmation '{JsonNamingPolicy.CamelCase.ConvertName(confirmation.Value.ToString())}' does not match command confirmation '{contract.ExpectedConfirmationJsonValue}'.";
            return false;
        }

        if (!contract.RequiresOfflineWriterAdmission)
        {
            return true;
        }

        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission = contract.ReadOfflineWriterAdmission(
            sharedRequest
        );
        if (offlineWriterAdmission is null)
        {
            failure =
                $"Request JSON property 'offlineWriterAdmission' is required in request and must be '{DocumentCacheOfflineWriterAdmission.ClosedAndDrainedJsonValue}'.";
            return false;
        }

        if (
            !offlineWriterAdmission.Confirmed
            || offlineWriterAdmission.HasUnrecognizedConfirmation
            || offlineWriterAdmission.Confirmation != contract.ExpectedOfflineWriterAdmissionConfirmation
        )
        {
            failure =
                $"Request JSON property 'offlineWriterAdmission' must be '{DocumentCacheOfflineWriterAdmission.ClosedAndDrainedJsonValue}'.";
            return false;
        }

        return true;
    }

    private static bool TryValidateRawConfirmationToken(
        JsonElement rootElement,
        DocumentCacheAdminMutatingCommandContract contract,
        out string? failure
    )
    {
        failure = null;

        if (!rootElement.TryGetProperty("confirmation", out JsonElement confirmationElement))
        {
            failure =
                $"Request JSON property 'confirmation' is required in request and must be '{contract.ExpectedConfirmationJsonValue}'.";
            return false;
        }

        if (confirmationElement.ValueKind != JsonValueKind.String)
        {
            failure =
                $"Request JSON property 'confirmation' must be the string value '{contract.ExpectedConfirmationJsonValue}'.";
            return false;
        }

        string? confirmation = confirmationElement.GetString();
        if (!string.Equals(confirmation, contract.ExpectedConfirmationJsonValue, StringComparison.Ordinal))
        {
            failure =
                $"Request JSON confirmation '{confirmation}' does not match command confirmation '{contract.ExpectedConfirmationJsonValue}'.";
            return false;
        }

        return true;
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

    private static bool TryRejectDuplicateProperties(
        JsonElement element,
        string objectName,
        out string? failure
    )
    {
        failure = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> propertyNames = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    failure = $"Request JSON property '{property.Name}' is duplicated in {objectName}.";
                    return false;
                }

                string childObjectName = string.Equals(objectName, "request", StringComparison.Ordinal)
                    ? property.Name
                    : $"{objectName}.{property.Name}";
                if (!TryRejectDuplicateProperties(property.Value, childObjectName, out failure))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (!TryRejectDuplicateProperties(item, $"{objectName}[{index}]", out failure))
                {
                    return false;
                }

                index++;
            }
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
