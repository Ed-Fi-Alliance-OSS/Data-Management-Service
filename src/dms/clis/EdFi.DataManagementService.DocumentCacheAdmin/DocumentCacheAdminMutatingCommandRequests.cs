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
    Type RequestType,
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
        if (!DocumentCacheAdminCommandSurface.IsMutatingCommand(commandName))
        {
            failure = $"Command '{commandName}' is not a DocumentCache mutating command.";
            return false;
        }

        if (invocationTarget.JsonRequest is { } jsonRequest)
        {
            if (!string.Equals(jsonRequest.CommandName, commandName, StringComparison.Ordinal))
            {
                failure = $"Request JSON command '{jsonRequest.CommandName}' does not match '{commandName}'.";
                return false;
            }

            commandRequest = new(
                commandName,
                jsonRequest.RequestType,
                jsonRequest.Request,
                jsonRequest.TargetKey
            );
            return true;
        }

        if (
            !TryReadExpectedConfirmation(
                parseResult,
                commandName,
                out DocumentCacheAdministrativeCommandConfirmation confirmation,
                out failure
            )
        )
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
                commandName,
                out DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
                out failure
            )
        )
        {
            return false;
        }

        DocumentCacheAdministrativeTargetKey targetKey = DocumentCacheAdministrativeTargetKey.FromTargetKey(
            invocationTarget.TargetKey
        );

        commandRequest = commandName switch
        {
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName => Create(
                commandName,
                new DocumentCacheGuardedNewEmptyActivationRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
            ),
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName => Create(
                commandName,
                new DocumentCacheOfflineActivationRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
            ),
            DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName => Create(
                commandName,
                new DocumentCacheOfflineDeactivationRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
            ),
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName => Create(
                commandName,
                new DocumentCacheOnlineCacheRebuildRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
            ),
            DocumentCacheAdminCommandSurface.ScrubCommandName => Create(
                commandName,
                new DocumentCacheExplicitIntegrityScrubRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
            ),
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName => Create(
                commandName,
                new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
            ),
            _ => throw new InvalidOperationException($"Unsupported mutating command '{commandName}'."),
        };
        return true;
    }

    private static DocumentCacheAdminMutatingCommandRequest Create<TRequest>(
        string commandName,
        TRequest request
    )
        where TRequest : notnull
    {
        DocumentCacheAdministrativeTargetKey targetKey = RequestTargetKey(request);
        return new(commandName, typeof(TRequest), request, targetKey.TargetKey);
    }

    private static DocumentCacheAdministrativeTargetKey RequestTargetKey<TRequest>(TRequest request) =>
        request switch
        {
            DocumentCacheGuardedNewEmptyActivationRequest guardedNewEmptyActivationRequest =>
                guardedNewEmptyActivationRequest.TargetKey,
            DocumentCacheOfflineActivationRequest offlineActivationRequest =>
                offlineActivationRequest.TargetKey,
            DocumentCacheOfflineDeactivationRequest offlineDeactivationRequest =>
                offlineDeactivationRequest.TargetKey,
            DocumentCacheOnlineCacheRebuildRequest onlineCacheRebuildRequest =>
                onlineCacheRebuildRequest.TargetKey,
            DocumentCacheExplicitIntegrityScrubRequest explicitIntegrityScrubRequest =>
                explicitIntegrityScrubRequest.TargetKey,
            DocumentCacheInternalOnlyCacheAheadRecoveryRequest cacheAheadRecoveryRequest =>
                cacheAheadRecoveryRequest.TargetKey,
            _ => throw new ArgumentException(
                $"Unsupported DocumentCache mutating request type '{typeof(TRequest)}'.",
                nameof(request)
            ),
        };

    private static bool TryReadExpectedConfirmation(
        ParseResult parseResult,
        string commandName,
        out DocumentCacheAdministrativeCommandConfirmation confirmation,
        out string? failure
    )
    {
        confirmation = default;
        failure = null;

        if (!DocumentCacheAdminCommandSurface.TryGetExpectedConfirmation(commandName, out confirmation))
        {
            failure = $"Command '{commandName}' does not have an expected confirmation.";
            return false;
        }

        string expectedValue = DocumentCacheAdminCommandSurface.ExpectedConfirmationOptionValue(commandName);
        string? suppliedValue = parseResult.GetValue<string?>(
            DocumentCacheAdminCommandSurface.ConfirmOptionName
        );
        if (!string.Equals(suppliedValue, expectedValue, StringComparison.Ordinal))
        {
            failure =
                $"{DocumentCacheAdminCommandSurface.ConfirmOptionName} must be the exact confirmation token '{expectedValue}'.";
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
        string commandName,
        out DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        out string? failure
    )
    {
        offlineWriterAdmission = null;
        failure = null;

        if (!DocumentCacheAdminCommandSurface.RequiresOfflineWriterAdmission(commandName))
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

        if (
            !DocumentCacheAdminCommandSurface.TryGetExpectedOfflineWriterAdmissionConfirmation(
                commandName,
                out DocumentCacheOfflineWriterAdmissionConfirmation confirmation
            )
        )
        {
            throw new ArgumentException(
                $"Command '{commandName}' does not have an expected offline writer admission.",
                nameof(commandName)
            );
        }

        offlineWriterAdmission = new(confirmed: true, confirmation);
        return true;
    }
}
