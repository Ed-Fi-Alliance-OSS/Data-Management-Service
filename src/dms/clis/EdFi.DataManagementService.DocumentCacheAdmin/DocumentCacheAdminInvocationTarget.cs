// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal enum DocumentCacheAdminInvocationTargetSource
{
    Options,
    RequestJson,
}

internal sealed record DocumentCacheAdminInvocationTarget(
    DocumentCacheTargetKey TargetKey,
    DocumentCacheAdminInvocationTargetSource Source
);

internal static class DocumentCacheAdminInvocationTargetParser
{
    public static bool TryParse(
        ParseResult parseResult,
        TextReader standardInput,
        out DocumentCacheAdminInvocationTarget? invocationTarget,
        out string? failure
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(standardInput);

        return TryParse(
            parseResult,
            requestJsonPath =>
                string.Equals(requestJsonPath, "-", StringComparison.Ordinal)
                    ? standardInput.ReadToEnd()
                    : File.ReadAllText(requestJsonPath),
            out invocationTarget,
            out failure
        );
    }

    public static bool TryParse(
        ParseResult parseResult,
        Func<string, string> requestJsonLoader,
        out DocumentCacheAdminInvocationTarget? invocationTarget,
        out string? failure
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(requestJsonLoader);

        invocationTarget = null;
        failure = null;

        OptionResult? requestJsonResult = GetSpecifiedOption(
            parseResult,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName
        );
        if (requestJsonResult is not null)
        {
            return TryParseRequestJsonTarget(
                parseResult,
                requestJsonResult,
                requestJsonLoader,
                out invocationTarget,
                out failure
            );
        }

        return TryParseOptionTarget(parseResult, out invocationTarget, out failure);
    }

    private static bool TryParseOptionTarget(
        ParseResult parseResult,
        out DocumentCacheAdminInvocationTarget? invocationTarget,
        out string? failure
    )
    {
        invocationTarget = null;
        failure = null;

        if (GetSpecifiedOption(parseResult, DocumentCacheAdminCommandSurface.DataStoreIdOptionName) is null)
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.DataStoreIdOptionName} is required when {DocumentCacheAdminCommandSurface.RequestJsonOptionName} is not supplied.";
            return false;
        }

        long? dataStoreId = parseResult.GetValue<long?>(
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName
        );
        string? tenantKey = parseResult.GetValue<string?>(
            DocumentCacheAdminCommandSurface.TenantKeyOptionName
        );

        if (dataStoreId is null)
        {
            failure = $"{DocumentCacheAdminCommandSurface.DataStoreIdOptionName} is required.";
            return false;
        }

        return TryCreateInvocationTarget(
            tenantKey,
            dataStoreId.Value,
            DocumentCacheAdminInvocationTargetSource.Options,
            out invocationTarget,
            out failure
        );
    }

    private static bool TryParseRequestJsonTarget(
        ParseResult parseResult,
        OptionResult requestJsonResult,
        Func<string, string> requestJsonLoader,
        out DocumentCacheAdminInvocationTarget? invocationTarget,
        out string? failure
    )
    {
        invocationTarget = null;
        failure = null;

        if (
            GetSpecifiedOption(parseResult, DocumentCacheAdminCommandSurface.DataStoreIdOptionName)
            is not null
        )
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.DataStoreIdOptionName} cannot be supplied with {DocumentCacheAdminCommandSurface.RequestJsonOptionName}.";
            return false;
        }

        if (GetSpecifiedOption(parseResult, DocumentCacheAdminCommandSurface.TenantKeyOptionName) is not null)
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.TenantKeyOptionName} cannot be supplied with {DocumentCacheAdminCommandSurface.RequestJsonOptionName}.";
            return false;
        }

        string? requestJsonPath = requestJsonResult.GetValueOrDefault<string?>();
        if (string.IsNullOrWhiteSpace(requestJsonPath))
        {
            failure = $"{DocumentCacheAdminCommandSurface.RequestJsonOptionName} requires a path or '-'.";
            return false;
        }

        string requestJson;
        try
        {
            requestJson = requestJsonLoader(requestJsonPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure =
                $"Unable to read {DocumentCacheAdminCommandSurface.RequestJsonOptionName} input: {exception.Message}";
            return false;
        }

        return TryParseTargetJson(requestJson, out invocationTarget, out failure);
    }

    private static bool TryParseTargetJson(
        string requestJson,
        out DocumentCacheAdminInvocationTarget? invocationTarget,
        out string? failure
    )
    {
        invocationTarget = null;
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

            if (
                !TryReadExactProperties(
                    document.RootElement,
                    "request",
                    ["targetKey"],
                    out Dictionary<string, JsonElement> rootProperties,
                    out failure
                )
            )
            {
                return false;
            }

            JsonElement targetKeyElement = rootProperties["targetKey"];
            if (targetKeyElement.ValueKind != JsonValueKind.Object)
            {
                failure = "Request JSON property 'targetKey' must be an object.";
                return false;
            }

            if (
                !TryReadExactProperties(
                    targetKeyElement,
                    "targetKey",
                    ["tenantKey", "dataStoreId"],
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

            return TryCreateInvocationTarget(
                tenantKeyElement.GetString(),
                dataStoreId,
                DocumentCacheAdminInvocationTargetSource.RequestJson,
                out invocationTarget,
                out failure
            );
        }
    }

    private static bool TryReadExactProperties(
        JsonElement jsonObject,
        string objectName,
        string[] requiredProperties,
        out Dictionary<string, JsonElement> properties,
        out string? failure
    )
    {
        properties = [];
        failure = null;

        foreach (JsonProperty property in jsonObject.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name, StringComparer.Ordinal))
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

    private static bool TryCreateInvocationTarget(
        string? tenantKey,
        long dataStoreId,
        DocumentCacheAdminInvocationTargetSource source,
        out DocumentCacheAdminInvocationTarget? invocationTarget,
        out string? failure
    )
    {
        invocationTarget = null;
        failure = null;

        if (
            !DocumentCacheTargetKey.TryCreate(
                tenantKey,
                dataStoreId,
                out DocumentCacheTargetKey? targetKey,
                out string? validationFailure
            )
        )
        {
            failure = validationFailure;
            return false;
        }

        invocationTarget = new DocumentCacheAdminInvocationTarget(targetKey, source);
        return true;
    }

    private static OptionResult? GetSpecifiedOption(ParseResult parseResult, string optionName)
    {
        OptionResult? optionResult = parseResult.GetResult(optionName) as OptionResult;
        return optionResult is { Implicit: false } ? optionResult : null;
    }
}
