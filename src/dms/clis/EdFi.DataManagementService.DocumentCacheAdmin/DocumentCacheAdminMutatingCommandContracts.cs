// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal sealed class DocumentCacheAdminMutatingCommandContract
{
    private readonly Func<
        DocumentCacheAdministrativeTargetKey,
        DocumentCachePhysicalSourceFingerprint?,
        DocumentCacheAdministrativeCommandConfirmation,
        DocumentCacheOfflineWriterAdmission?,
        object
    > _requestFactory;

    public DocumentCacheAdminMutatingCommandContract(
        string commandName,
        Type requestType,
        DocumentCacheAdministrativeCommand administrativeCommand,
        Func<
            DocumentCacheAdministrativeTargetKey,
            DocumentCachePhysicalSourceFingerprint?,
            DocumentCacheAdministrativeCommandConfirmation,
            DocumentCacheOfflineWriterAdmission?,
            object
        > requestFactory
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(requestFactory);

        CommandName = commandName;
        RequestType = requestType;
        AdministrativeCommand = administrativeCommand;
        SharedContract = DocumentCacheAdministrativeCommandContracts.Get(administrativeCommand);
        _requestFactory = requestFactory;
    }

    public string CommandName { get; }

    public Type RequestType { get; }

    public DocumentCacheAdministrativeCommand AdministrativeCommand { get; }

    public DocumentCacheAdministrativeCommandContract SharedContract { get; }

    public DocumentCacheAdministrativeCommandConfirmation ExpectedConfirmation =>
        SharedContract.ExpectedConfirmation;

    public DocumentCacheOfflineWriterAdmissionConfirmation? ExpectedOfflineWriterAdmissionConfirmation =>
        SharedContract.ExpectedOfflineWriterAdmissionConfirmation;

    public bool RequiresOfflineWriterAdmission => SharedContract.RequiresOfflineWriterAdmission;

    public string ExpectedConfirmationJsonValue =>
        JsonNamingPolicy.CamelCase.ConvertName(ExpectedConfirmation.ToString());

    public DocumentCacheOfflineWriterAdmission CreateOfflineWriterAdmission()
    {
        if (ExpectedOfflineWriterAdmissionConfirmation is not { } confirmation)
        {
            throw new InvalidOperationException(
                $"Command '{CommandName}' does not require offline writer admission."
            );
        }

        return new(confirmed: true, confirmation);
    }

    public object CreateRequest(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return _requestFactory(
            DocumentCacheAdministrativeTargetKey.FromTargetKey(targetKey),
            expectedPhysicalSourceFingerprint,
            ExpectedConfirmation,
            offlineWriterAdmission
        );
    }

    public DocumentCacheAdministrativeTargetKey ReadTargetKey(object request)
    {
        return ReadRequest(request).TargetKey;
    }

    public DocumentCacheAdministrativeCommandConfirmation? ReadConfirmation(object request)
    {
        return ReadRequest(request).Confirmation;
    }

    public DocumentCacheOfflineWriterAdmission? ReadOfflineWriterAdmission(object request)
    {
        IDocumentCacheAdministrativeRequest administrativeRequest = ReadRequest(request);
        return
            administrativeRequest is IDocumentCacheOfflineWriterAdmissionRequest offlineWriterAdmissionRequest
            ? offlineWriterAdmissionRequest.OfflineWriterAdmission
            : null;
    }

    private IDocumentCacheAdministrativeRequest ReadRequest(object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RequestType.IsInstanceOfType(request))
        {
            throw new ArgumentException(
                $"Request must be a '{RequestType.FullName}' instance for command '{CommandName}'.",
                nameof(request)
            );
        }

        return request is IDocumentCacheAdministrativeRequest administrativeRequest
            ? administrativeRequest
            : throw new ArgumentException(
                $"Request must implement '{typeof(IDocumentCacheAdministrativeRequest).FullName}'.",
                nameof(request)
            );
    }

    public DocumentCacheAdminMutatingCommandRequest CreateCommandRequest(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission
    )
    {
        object request = CreateRequest(targetKey, expectedPhysicalSourceFingerprint, offlineWriterAdmission);
        return new(CommandName, RequestType, request, ReadTargetKey(request).TargetKey);
    }
}

internal static class DocumentCacheAdminMutatingCommandContracts
{
    private static readonly IReadOnlyDictionary<
        string,
        DocumentCacheAdminMutatingCommandContract
    > MutatingContracts = new Dictionary<string, DocumentCacheAdminMutatingCommandContract>(
        StringComparer.Ordinal
    )
    {
        [DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName] = Create(
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheGuardedNewEmptyActivationRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
        ),
        [DocumentCacheAdminCommandSurface.ActivateOfflineCommandName] = Create(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            DocumentCacheAdministrativeCommand.OfflineActivation,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheOfflineActivationRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
        ),
        [DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName] = Create(
            DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheOfflineDeactivationRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
        ),
        [DocumentCacheAdminCommandSurface.RebuildOnlineCommandName] = Create(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheOnlineCacheRebuildRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
        ),
        [DocumentCacheAdminCommandSurface.ScrubCommandName] = Create(
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheExplicitIntegrityScrubRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
        ),
        [DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName] = Create(
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                )
        ),
    };

    public static bool TryGet(
        string commandName,
        [NotNullWhen(true)] out DocumentCacheAdminMutatingCommandContract? contract
    ) => MutatingContracts.TryGetValue(commandName, out contract);

    private static DocumentCacheAdminMutatingCommandContract Create<TRequest>(
        string commandName,
        DocumentCacheAdministrativeCommand administrativeCommand,
        Func<
            DocumentCacheAdministrativeTargetKey,
            DocumentCachePhysicalSourceFingerprint?,
            DocumentCacheAdministrativeCommandConfirmation,
            DocumentCacheOfflineWriterAdmission?,
            TRequest
        > requestFactory
    )
        where TRequest : notnull, IDocumentCacheAdministrativeRequest
    {
        return new(
            commandName,
            typeof(TRequest),
            administrativeCommand,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                requestFactory(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation,
                    offlineWriterAdmission
                )
        );
    }
}
