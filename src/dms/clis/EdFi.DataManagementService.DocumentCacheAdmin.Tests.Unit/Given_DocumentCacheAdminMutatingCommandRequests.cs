// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("MutatingCommandRequests")]
public sealed class Given_DocumentCacheAdminMutatingCommandRequests
{
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestCaseSource(nameof(MutatingCommandCases))]
    public void It_builds_the_selected_shared_request_dto_from_options(
        string commandName,
        Type expectedRequestType,
        DocumentCacheAdministrativeCommandConfirmation expectedConfirmation,
        DocumentCacheOfflineWriterAdmissionConfirmation? expectedOfflineWriterAdmission
    )
    {
        ParseResult parseResult = ParseMutatingCommand(
            commandName,
            includeConfirmation: true,
            includeOfflineWriterAdmission: expectedOfflineWriterAdmission is not null,
            includeFingerprint: true
        );

        parseResult.Errors.Should().BeEmpty();
        DocumentCacheAdminInvocationTargetParser
            .TryParse(
                parseResult,
                _ => throw new InvalidOperationException("Options invocation should not read JSON."),
                out DocumentCacheAdminInvocationTarget? invocationTarget,
                out string? targetFailure
            )
            .Should()
            .BeTrue(targetFailure);

        bool built = DocumentCacheAdminMutatingCommandRequestBuilder.TryBuild(
            parseResult,
            invocationTarget!,
            out DocumentCacheAdminMutatingCommandRequest? commandRequest,
            out string? failure
        );

        built.Should().BeTrue(failure);
        commandRequest!.CommandName.Should().Be(commandName);
        commandRequest.RequestType.Should().Be(expectedRequestType);
        commandRequest.Request.Should().BeOfType(expectedRequestType);
        commandRequest.TargetKey.Should().Be(DocumentCacheTargetKey.Create("TenantA", 7));
        RequestConfirmation(commandRequest.Request).Should().Be(expectedConfirmation);
        RequestFingerprint(commandRequest.Request)!.Value.Should().Be(Fingerprint);
        RequestOfflineWriterAdmission(commandRequest.Request)
            ?.Confirmation.Should()
            .Be(expectedOfflineWriterAdmission);
    }

    [TestCaseSource(nameof(MutatingCommandNames))]
    public void It_rejects_missing_confirmation_for_option_based_mutating_commands(string commandName)
    {
        ParseResult parseResult = ParseMutatingCommand(
            commandName,
            includeConfirmation: false,
            includeOfflineWriterAdmission: DocumentCacheAdminCommandSurface.RequiresOfflineWriterAdmission(
                commandName
            ),
            includeFingerprint: false
        );

        parseResult.Errors.Should().Contain(error => error.Message.Contains("--confirm"));
    }

    [TestCase("true")]
    [TestCase("OnlineCacheRebuild")]
    [TestCase("integrityScrub")]
    public void It_rejects_wrong_or_differently_cased_confirmation_tokens(string confirmation)
    {
        ParseResult parseResult = DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                confirmation,
            ]);

        parseResult.Errors.Should().NotBeEmpty();
    }

    [Test]
    public void It_rejects_missing_offline_writer_admission_for_writer_fenced_commands()
    {
        ParseResult parseResult = ParseMutatingCommand(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            includeConfirmation: true,
            includeOfflineWriterAdmission: false,
            includeFingerprint: false
        );

        parseResult.Errors.Should().Contain(error => error.Message.Contains("--offline-writer-admission"));
    }

    [TestCase("true")]
    [TestCase("ClosedAndDrained")]
    [TestCase("offlineActivationWritersClosedAndDrained")]
    public void It_rejects_wrong_or_differently_cased_offline_writer_admission_tokens(string admission)
    {
        ParseResult parseResult = DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([
                DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                DocumentCacheAdminCommandSurface.ExpectedConfirmationOptionValue(
                    DocumentCacheAdminCommandSurface.ActivateOfflineCommandName
                ),
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
                admission,
            ]);

        parseResult.Errors.Should().NotBeEmpty();
    }

    [Test]
    public void It_rejects_malformed_expected_physical_source_fingerprint_as_an_argument_error()
    {
        ParseResult parseResult = DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                DocumentCacheAdminCommandSurface.ExpectedConfirmationOptionValue(
                    DocumentCacheAdminCommandSurface.RebuildOnlineCommandName
                ),
                DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
                "not-a-fingerprint",
            ]);

        parseResult
            .Errors.Should()
            .Contain(error =>
                error.Message.Contains(
                    DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName
                )
            );
    }

    [Test]
    public void It_allows_request_json_to_supply_mutating_command_fields()
    {
        ParseResult parseResult = DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.RequestJsonOptionName,
                "-",
            ]);

        parseResult.Errors.Should().BeEmpty();
        DocumentCacheAdminInvocationTargetParser
            .TryParse(
                parseResult,
                _ =>
                    $$"""
                    {
                      "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                      "confirmation": "onlineCacheRebuild",
                      "expectedPhysicalSourceFingerprint": "{{Fingerprint}}"
                    }
                    """,
                out DocumentCacheAdminInvocationTarget? invocationTarget,
                out string? targetFailure
            )
            .Should()
            .BeTrue(targetFailure);

        DocumentCacheAdminMutatingCommandRequestBuilder
            .TryBuild(
                parseResult,
                invocationTarget!,
                out DocumentCacheAdminMutatingCommandRequest? request,
                out string? failure
            )
            .Should()
            .BeTrue(failure);
        request!.Request.Should().BeOfType<DocumentCacheOnlineCacheRebuildRequest>();
    }

    private static ParseResult ParseMutatingCommand(
        string commandName,
        bool includeConfirmation,
        bool includeOfflineWriterAdmission,
        bool includeFingerprint
    )
    {
        List<string> args =
        [
            commandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "7",
            DocumentCacheAdminCommandSurface.TenantKeyOptionName,
            "TenantA",
        ];

        if (includeConfirmation)
        {
            args.Add(DocumentCacheAdminCommandSurface.ConfirmOptionName);
            args.Add(DocumentCacheAdminCommandSurface.ExpectedConfirmationOptionValue(commandName));
        }

        if (includeOfflineWriterAdmission)
        {
            args.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
            args.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue);
        }

        if (includeFingerprint)
        {
            args.Add(DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName);
            args.Add(Fingerprint);
        }

        return DocumentCacheAdminCommandSurface.CreateRootCommand().Parse(args);
    }

    private static DocumentCacheAdministrativeCommandConfirmation? RequestConfirmation(object request) =>
        request switch
        {
            DocumentCacheGuardedNewEmptyActivationRequest guardedNewEmptyActivationRequest =>
                guardedNewEmptyActivationRequest.Confirmation,
            DocumentCacheOfflineActivationRequest offlineActivationRequest =>
                offlineActivationRequest.Confirmation,
            DocumentCacheOfflineDeactivationRequest offlineDeactivationRequest =>
                offlineDeactivationRequest.Confirmation,
            DocumentCacheOnlineCacheRebuildRequest onlineCacheRebuildRequest =>
                onlineCacheRebuildRequest.Confirmation,
            DocumentCacheExplicitIntegrityScrubRequest explicitIntegrityScrubRequest =>
                explicitIntegrityScrubRequest.Confirmation,
            DocumentCacheInternalOnlyCacheAheadRecoveryRequest cacheAheadRecoveryRequest =>
                cacheAheadRecoveryRequest.Confirmation,
            _ => throw new ArgumentException("Unsupported request type.", nameof(request)),
        };

    private static DocumentCachePhysicalSourceFingerprint? RequestFingerprint(object request) =>
        request switch
        {
            DocumentCacheGuardedNewEmptyActivationRequest guardedNewEmptyActivationRequest =>
                guardedNewEmptyActivationRequest.ExpectedPhysicalSourceFingerprint,
            DocumentCacheOfflineActivationRequest offlineActivationRequest =>
                offlineActivationRequest.ExpectedPhysicalSourceFingerprint,
            DocumentCacheOfflineDeactivationRequest offlineDeactivationRequest =>
                offlineDeactivationRequest.ExpectedPhysicalSourceFingerprint,
            DocumentCacheOnlineCacheRebuildRequest onlineCacheRebuildRequest =>
                onlineCacheRebuildRequest.ExpectedPhysicalSourceFingerprint,
            DocumentCacheExplicitIntegrityScrubRequest explicitIntegrityScrubRequest =>
                explicitIntegrityScrubRequest.ExpectedPhysicalSourceFingerprint,
            DocumentCacheInternalOnlyCacheAheadRecoveryRequest cacheAheadRecoveryRequest =>
                cacheAheadRecoveryRequest.ExpectedPhysicalSourceFingerprint,
            _ => throw new ArgumentException("Unsupported request type.", nameof(request)),
        };

    private static DocumentCacheOfflineWriterAdmission? RequestOfflineWriterAdmission(object request) =>
        request switch
        {
            DocumentCacheOfflineActivationRequest offlineActivationRequest =>
                offlineActivationRequest.OfflineWriterAdmission,
            DocumentCacheOfflineDeactivationRequest offlineDeactivationRequest =>
                offlineDeactivationRequest.OfflineWriterAdmission,
            DocumentCacheInternalOnlyCacheAheadRecoveryRequest cacheAheadRecoveryRequest =>
                cacheAheadRecoveryRequest.OfflineWriterAdmission,
            _ => null,
        };

    private static IEnumerable<TestCaseData> MutatingCommandCases()
    {
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            typeof(DocumentCacheGuardedNewEmptyActivationRequest),
            DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation,
            null
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            typeof(DocumentCacheOfflineActivationRequest),
            DocumentCacheAdministrativeCommandConfirmation.OfflineActivation,
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
            typeof(DocumentCacheOfflineDeactivationRequest),
            DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation,
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            typeof(DocumentCacheOnlineCacheRebuildRequest),
            DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild,
            null
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            typeof(DocumentCacheExplicitIntegrityScrubRequest),
            DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub,
            null
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
            typeof(DocumentCacheInternalOnlyCacheAheadRecoveryRequest),
            DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery,
            DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
        );
    }

    private static IEnumerable<string> MutatingCommandNames() =>
        MutatingCommandCases().Select(testCase => (string)testCase.Arguments[0]!);
}
