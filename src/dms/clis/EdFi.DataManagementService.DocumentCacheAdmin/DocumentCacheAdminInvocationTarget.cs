// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal enum DocumentCacheAdminInvocationTargetSource
{
    Options,
    RequestJson,
}

internal sealed record DocumentCacheAdminInvocationTarget(
    DocumentCacheTargetKey TargetKey,
    DocumentCacheAdminInvocationTargetSource Source,
    DocumentCacheAdminJsonRequest? JsonRequest = null
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

        string? duplicateRequestOption = Array.Find(
            new[]
            {
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
                DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
            },
            optionName => GetSpecifiedOption(parseResult, optionName) is not null
        );
        if (duplicateRequestOption is not null)
        {
            failure =
                $"{duplicateRequestOption} cannot be supplied with {DocumentCacheAdminCommandSurface.RequestJsonOptionName}.";
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
        catch (Exception exception) when (IsExpectedRequestJsonInputFailure(exception))
        {
            failure =
                $"Unable to read {DocumentCacheAdminCommandSurface.RequestJsonOptionName} input: {exception.Message}";
            return false;
        }

        if (
            !DocumentCacheAdminJsonRequestParser.TryParse(
                parseResult.CommandResult.Command.Name,
                requestJson,
                out DocumentCacheAdminJsonRequest? jsonRequest,
                out failure
            )
        )
        {
            return false;
        }

        invocationTarget = new DocumentCacheAdminInvocationTarget(
            jsonRequest!.TargetKey,
            DocumentCacheAdminInvocationTargetSource.RequestJson,
            jsonRequest
        );
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

    private static bool IsExpectedRequestJsonInputFailure(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ObjectDisposedException
        || exception is ArgumentException and not ArgumentNullException and not ArgumentOutOfRangeException;
}
