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
    private readonly Func<object, DocumentCacheAdministrativeTargetKey> _targetKeyAccessor;

    public DocumentCacheAdminMutatingCommandContract(
        string commandName,
        Type requestType,
        DocumentCacheAdministrativeCommand administrativeCommand,
        DocumentCacheAdministrativeCommandConfirmation expectedConfirmation,
        DocumentCacheOfflineWriterAdmissionConfirmation? expectedOfflineWriterAdmissionConfirmation,
        Func<
            DocumentCacheAdministrativeTargetKey,
            DocumentCachePhysicalSourceFingerprint?,
            DocumentCacheAdministrativeCommandConfirmation,
            DocumentCacheOfflineWriterAdmission?,
            object
        > requestFactory,
        Func<object, DocumentCacheAdministrativeTargetKey> targetKeyAccessor
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(targetKeyAccessor);

        CommandName = commandName;
        RequestType = requestType;
        AdministrativeCommand = administrativeCommand;
        ExpectedConfirmation = expectedConfirmation;
        ExpectedOfflineWriterAdmissionConfirmation = expectedOfflineWriterAdmissionConfirmation;
        _requestFactory = requestFactory;
        _targetKeyAccessor = targetKeyAccessor;
    }

    public string CommandName { get; }

    public Type RequestType { get; }

    public DocumentCacheAdministrativeCommand AdministrativeCommand { get; }

    public DocumentCacheAdministrativeCommandConfirmation ExpectedConfirmation { get; }

    public DocumentCacheOfflineWriterAdmissionConfirmation? ExpectedOfflineWriterAdmissionConfirmation { get; }

    public bool RequiresOfflineWriterAdmission => ExpectedOfflineWriterAdmissionConfirmation is not null;

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
        ArgumentNullException.ThrowIfNull(request);

        if (!RequestType.IsInstanceOfType(request))
        {
            throw new ArgumentException(
                $"Request must be a '{RequestType.FullName}' instance for command '{CommandName}'.",
                nameof(request)
            );
        }

        return _targetKeyAccessor(request);
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
            DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation,
            expectedOfflineWriterAdmissionConfirmation: null,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheGuardedNewEmptyActivationRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            request => request.TargetKey
        ),
        [DocumentCacheAdminCommandSurface.ActivateOfflineCommandName] = Create(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            DocumentCacheAdministrativeCommand.OfflineActivation,
            DocumentCacheAdministrativeCommandConfirmation.OfflineActivation,
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheOfflineActivationRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            request => request.TargetKey
        ),
        [DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName] = Create(
            DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation,
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheOfflineDeactivationRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            request => request.TargetKey
        ),
        [DocumentCacheAdminCommandSurface.RebuildOnlineCommandName] = Create(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild,
            expectedOfflineWriterAdmissionConfirmation: null,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheOnlineCacheRebuildRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            request => request.TargetKey
        ),
        [DocumentCacheAdminCommandSurface.ScrubCommandName] = Create(
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub,
            expectedOfflineWriterAdmissionConfirmation: null,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheExplicitIntegrityScrubRequest(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            request => request.TargetKey
        ),
        [DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName] = Create(
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery,
            DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                    targetKey,
                    offlineWriterAdmission,
                    expectedPhysicalSourceFingerprint,
                    confirmation
                ),
            request => request.TargetKey
        ),
    };

    public static IEnumerable<string> CommandNames => MutatingContracts.Keys;

    public static bool TryGet(
        string commandName,
        [NotNullWhen(true)] out DocumentCacheAdminMutatingCommandContract? contract
    ) => MutatingContracts.TryGetValue(commandName, out contract);

    private static DocumentCacheAdminMutatingCommandContract Create<TRequest>(
        string commandName,
        DocumentCacheAdministrativeCommand administrativeCommand,
        DocumentCacheAdministrativeCommandConfirmation expectedConfirmation,
        DocumentCacheOfflineWriterAdmissionConfirmation? expectedOfflineWriterAdmissionConfirmation,
        Func<
            DocumentCacheAdministrativeTargetKey,
            DocumentCachePhysicalSourceFingerprint?,
            DocumentCacheAdministrativeCommandConfirmation,
            DocumentCacheOfflineWriterAdmission?,
            TRequest
        > requestFactory,
        Func<TRequest, DocumentCacheAdministrativeTargetKey> targetKeyAccessor
    )
        where TRequest : notnull
    {
        return new(
            commandName,
            typeof(TRequest),
            administrativeCommand,
            expectedConfirmation,
            expectedOfflineWriterAdmissionConfirmation,
            (targetKey, expectedPhysicalSourceFingerprint, confirmation, offlineWriterAdmission) =>
                requestFactory(
                    targetKey,
                    expectedPhysicalSourceFingerprint,
                    confirmation,
                    offlineWriterAdmission
                ),
            request => targetKeyAccessor((TRequest)request)
        );
    }
}
