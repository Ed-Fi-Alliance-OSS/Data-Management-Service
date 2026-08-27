// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal sealed record DocumentCacheAdminMutatingCommandRequest(
    string CommandName,
    object Request,
    DocumentCacheTargetKey TargetKey
);

internal static class DocumentCacheAdminMutatingCommandRequestBuilder
{
    public static bool TryBuild(
        ParseResult parseResult,
        DocumentCacheAdminInvocationTarget invocationTarget,
        out DocumentCacheAdminMutatingCommandRequest? commandRequest,
        out string? failure
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(invocationTarget);

        commandRequest = null;
        failure = null;

        string commandName = parseResult.CommandResult.Command.Name;
        if (
            !DocumentCacheAdminMutatingCommandContracts.TryGet(
                commandName,
                out DocumentCacheAdminMutatingCommandContract? contract
            )
        )
        {
            failure = $"Command '{commandName}' is not a DocumentCache mutating command.";
            return false;
        }

        if (invocationTarget.JsonRequest is { } jsonRequest)
        {
            return contract.TryCreateCommandRequest(
                jsonRequest.SharedRequest,
                out commandRequest,
                out failure
            );
        }

        if (!TryReadExpectedConfirmation(parseResult, contract, out failure))
        {
            return false;
        }

        if (
            !TryReadExpectedPhysicalSourceFingerprint(
                parseResult,
                out DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
                out failure
            )
        )
        {
            return false;
        }

        if (
            !TryReadOfflineWriterAdmission(
                parseResult,
                contract,
                out DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
                out failure
            )
        )
        {
            return false;
        }

        commandRequest = contract.CreateCommandRequest(
            invocationTarget.TargetKey,
            expectedPhysicalSourceFingerprint,
            offlineWriterAdmission
        );
        return true;
    }

    private static bool TryReadExpectedConfirmation(
        ParseResult parseResult,
        DocumentCacheAdminMutatingCommandContract contract,
        out string? failure
    )
    {
        failure = null;

        string? suppliedValue = parseResult.GetValue<string?>(
            DocumentCacheAdminCommandSurface.ConfirmOptionName
        );
        if (!string.Equals(suppliedValue, contract.ExpectedConfirmationJsonValue, StringComparison.Ordinal))
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.ConfirmOptionName} must be the exact confirmation token '{contract.ExpectedConfirmationJsonValue}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadExpectedPhysicalSourceFingerprint(
        ParseResult parseResult,
        out DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
        out string? failure
    )
    {
        expectedPhysicalSourceFingerprint = null;
        failure = null;

        OptionResult? fingerprintOptionResult =
            parseResult.GetResult(
                DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName
            ) as OptionResult;
        if (fingerprintOptionResult is not { Implicit: false })
        {
            return true;
        }

        string? fingerprint = fingerprintOptionResult.GetValueOrDefault<string?>();
        try
        {
            expectedPhysicalSourceFingerprint = new DocumentCachePhysicalSourceFingerprint(
                fingerprint ?? string.Empty
            );
            return true;
        }
        catch (ArgumentException exception)
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName} is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadOfflineWriterAdmission(
        ParseResult parseResult,
        DocumentCacheAdminMutatingCommandContract contract,
        out DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        out string? failure
    )
    {
        offlineWriterAdmission = null;
        failure = null;

        if (!contract.RequiresOfflineWriterAdmission)
        {
            return true;
        }

        string? suppliedValue = parseResult.GetValue<string?>(
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName
        );
        if (
            !string.Equals(
                suppliedValue,
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue,
                StringComparison.Ordinal
            )
        )
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName} must be the exact offline writer admission acknowledgement '{DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue}'.";
            return false;
        }

        offlineWriterAdmission = contract.CreateOfflineWriterAdmission();
        return true;
    }
}
