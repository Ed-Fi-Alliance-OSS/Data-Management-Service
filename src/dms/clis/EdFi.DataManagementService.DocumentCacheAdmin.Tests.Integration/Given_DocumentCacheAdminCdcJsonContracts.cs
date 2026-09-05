// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// Proves the cdc verbs put exactly one shared CDC contract on stdout in <c>--json</c> mode, that the
/// document round-trips through the CDC contract reader, and that nothing an operator supplied in
/// confidence appears in either output mode.
/// </summary>
[TestFixture]
[Category("CdcJsonContract")]
public sealed class Given_DocumentCacheAdminCdcJsonContracts
{
    private const string ConnectionStringSentinel =
        "Host=cdc-db.internal;Username=dms;Password=sentinel-secret-value";
    private const string TenantDisplayNameSentinel = "Sentinel Independent School District";
    private const string BearerTokenSentinel = "sentinel-bearer-token";

    private const string DeploymentKey = "deployment";
    private const string InstanceKey = "instance";
    private const string ConnectorName = "edfi-documents-instance-1";
    private const string TopicName = "edfi.documents.instance.1";
    private const string ProgressTopicName = "edfi.documents.instance.1.progress";
    private const string SchemaHistoryTopicName = "edfi.documents.instance.1.schema-history";

    [Test]
    public async Task It_round_trips_the_admission_contract_for_enable()
    {
        (int exitCode, string stdout, string stderr) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcEnableVerbName,
            ContractResult(
                DocumentCacheAdminCommandSurface.CdcEnableVerbName,
                Admission(CdcAdmissionState.Admitted),
                DocumentCacheAdminExitCodes.Success,
                "admitted"
            ),
            jsonOutput: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        stderr.Should().BeEmpty();

        CdcContractReadResult<CdcAdmission> read = CdcJsonContract.Deserialize<CdcAdmission>(stdout);

        read.Succeeded.Should().BeTrue(string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        read.Contract!.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        read.Contract.TargetIdentity.InstanceKey.Should().Be(InstanceKey);
        AssertNoSecrets(stdout);
    }

    [Test]
    public async Task It_round_trips_the_status_contract_for_status()
    {
        (int exitCode, string stdout, string stderr) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcStatusVerbName,
            ContractResult(
                DocumentCacheAdminCommandSurface.CdcStatusVerbName,
                Status(CdcReadiness.NotReady),
                DocumentCacheAdminExitCodes.Success,
                "notReady"
            ),
            jsonOutput: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        stderr.Should().BeEmpty();

        CdcContractReadResult<CdcStatus> read = CdcJsonContract.Deserialize<CdcStatus>(stdout);

        read.Succeeded.Should().BeTrue(string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        read.Contract!.Readiness.Should().Be(CdcReadiness.NotReady);
        AssertNoSecrets(stdout);
    }

    [Test]
    public async Task It_round_trips_the_adoption_proof_for_adopt()
    {
        (int exitCode, string stdout, string stderr) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
            ContractResult(
                DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
                AdoptionProof(),
                DocumentCacheAdminExitCodes.Success,
                "completed"
            ),
            jsonOutput: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        stderr.Should().BeEmpty();

        CdcContractReadResult<CdcAdoptionProof> read = CdcJsonContract.Deserialize<CdcAdoptionProof>(stdout);

        read.Succeeded.Should().BeTrue(string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        read.Contract!.VerificationResults.Should()
            .HaveCount(Enum.GetValues<CdcAdoptionVerificationKind>().Length);
        read.Contract.Binding.ConnectorName.Should().Be(ConnectorName);
        AssertNoSecrets(stdout);
    }

    [Test]
    public async Task It_round_trips_the_cleanup_proof_for_retire()
    {
        (int exitCode, string stdout, string stderr) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcRetireVerbName,
            ContractResult(
                DocumentCacheAdminCommandSurface.CdcRetireVerbName,
                CleanupProof(),
                DocumentCacheAdminExitCodes.Success,
                "completed"
            ),
            jsonOutput: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        stderr.Should().BeEmpty();

        CdcContractReadResult<CdcCleanupProof> read = CdcJsonContract.Deserialize<CdcCleanupProof>(stdout);

        read.Succeeded.Should().BeTrue(string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        read.Contract!.CleanupMode.Should().Be(CdcCleanupMode.RetireBindingGeneration);
        read.Contract.GovernedArtifacts.Should()
            .Contain(artifact => artifact.ArtifactKind == CdcGovernedArtifactKind.PublicTopic);
        AssertNoSecrets(stdout);
    }

    [Test]
    public async Task It_writes_exactly_one_json_document_to_stdout()
    {
        (_, string stdout, _) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcStatusVerbName,
            ContractResult(
                DocumentCacheAdminCommandSurface.CdcStatusVerbName,
                Status(CdcReadiness.Ready),
                DocumentCacheAdminExitCodes.Success,
                "ready"
            ),
            jsonOutput: true
        );

        stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should()
            .HaveCount(1);
    }

    [Test]
    public async Task It_prints_the_governed_names_without_secrets_in_human_mode()
    {
        (int exitCode, string stdout, string stderr) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcStatusVerbName,
            ContractResult(
                DocumentCacheAdminCommandSurface.CdcStatusVerbName,
                Status(CdcReadiness.Ready),
                DocumentCacheAdminExitCodes.Success,
                "ready"
            ),
            jsonOutput: false
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        stderr.Should().BeEmpty();
        stdout
            .Should()
            .ContainAll(
                $"connector={ConnectorName}",
                "provider=postgresql",
                "dataStoreId=1",
                $"instanceKey={InstanceKey}",
                $"topic={TopicName}",
                $"progressTopic={ProgressTopicName}",
                $"schemaHistoryTopic={SchemaHistoryTopicName}"
            );
        AssertNoSecrets(stdout);
    }

    [Test]
    public async Task It_reports_diagnostics_on_stderr_when_no_contract_was_produced_in_json_mode()
    {
        (int exitCode, string stdout, string stderr) = await ExecuteAsync(
            DocumentCacheAdminCommandSurface.CdcRetireVerbName,
            DocumentCacheAdminCdcCommandResult.Refused(
                DocumentCacheAdminCommandSurface.CdcRetireVerbName,
                DocumentCacheAdminExitCodes.IncompleteRetryable,
                "incompleteRetryable",
                "cdcRetire",
                [
                    new CdcDiagnostic(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        DateTimeOffset.UnixEpoch,
                        "$.governedArtifacts",
                        "CDC retirement left the binding record intact."
                    ),
                ]
            ),
            jsonOutput: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("CDC retirement left the binding record intact.");
    }

    private static void AssertNoSecrets(string output)
    {
        output.Should().NotContain("Password=");
        output.Should().NotContain("sentinel-secret-value");
        output.Should().NotContain(ConnectionStringSentinel);
        output.Should().NotContain(TenantDisplayNameSentinel);
        output.Should().NotContain(BearerTokenSentinel);
        output.Should().NotContain("Host=");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string verbName,
        DocumentCacheAdminCdcCommandResult result,
        bool jsonOutput
    )
    {
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminCdcCommandDispatcher>(new ReturningCdcDispatcher(result))
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(CdcJsonContract.Serialize(Binding()));

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCdcCommand(verbName, jsonOutput),
            new DocumentCacheAdminInvocationTarget(DocumentCacheTargetKey.Create("", 1)),
            serviceProvider,
            stdout,
            stderr,
            CancellationToken.None,
            stdin
        );

        return (exitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private static ParseResult ParseCdcCommand(string verbName, bool jsonOutput)
    {
        List<string> args =
        [
            DocumentCacheAdminCommandSurface.CdcCommandName,
            verbName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
        ];

        if (DocumentCacheAdminCommandSurface.RequiresCdcProvisioningEvidence(verbName))
        {
            args.AddRange([
                DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
                DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
                DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue,
            ]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)
        {
            args.AddRange([DocumentCacheAdminCommandSurface.PreviousGenerationOptionName, "1"]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcAdoptVerbName)
        {
            // The stub dispatcher never reads it, but the surface requires the option, and a test that
            // bypassed that requirement would not be exercising the real parse surface.
            args.AddRange([DocumentCacheAdminCommandSurface.BindingJsonOptionName, "-"]);
        }

        if (DocumentCacheAdminCommandSurface.ExpectedCdcConfirmationOptionValue(verbName) is { } confirmation)
        {
            args.AddRange([DocumentCacheAdminCommandSurface.ConfirmOptionName, confirmation]);
        }

        if (jsonOutput)
        {
            args.Add(DocumentCacheAdminCommandSurface.JsonOptionName);
        }

        ParseResult parseResult = DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([.. args]);
        parseResult.Errors.Should().BeEmpty(string.Join("; ", parseResult.Errors.Select(e => e.Message)));
        return parseResult;
    }

    private static DocumentCacheAdminCdcCommandResult ContractResult<TContract>(
        string verbName,
        TContract contract,
        int exitCode,
        string outcome
    )
        where TContract : notnull =>
        DocumentCacheAdminCdcCommandResult.ForContract(
            verbName,
            contract,
            exitCode,
            outcome,
            $"cdc{verbName}",
            new DocumentCacheAdminCdcGovernedNames(
                ConnectorName,
                "postgresql",
                "1",
                InstanceKey,
                TopicName,
                ProgressTopicName,
                SchemaHistoryTopicName
            )
        );

    private static CdcTargetIdentity TargetIdentity() =>
        new(DeploymentKey, "", "1", InstanceKey, 1, CdcProvider.Postgresql);

    private static CdcAdmission Admission(CdcAdmissionState admissionState) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            DateTimeOffset.UnixEpoch,
            TargetIdentity(),
            admissionState,
            CdcBlockingCategory.None,
            AdmissionSteps(),
            []
        );

    private static CdcAdmissionSteps AdmissionSteps()
    {
        CdcComponent component = new(
            CdcComponentState.Satisfied,
            CdcBlockingCategory.None,
            DateTimeOffset.UnixEpoch,
            null
        );

        return new(
            component,
            component,
            component,
            component,
            component,
            component,
            component,
            component,
            component
        );
    }

    private static CdcStatus Status(CdcReadiness readiness) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            DateTimeOffset.UnixEpoch,
            readiness,
            readiness == CdcReadiness.Ready
                ? CdcBlockingCategory.None
                : CdcBlockingCategory.ProjectionBacklog,
            []
        );

    private static CdcBinding Binding() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            "",
            "1",
            InstanceKey,
            1,
            CdcProvider.Postgresql,
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ConnectorName,
            TopicName,
            3,
            "murmur2",
            CdcJsonContract.CurrentContractVersion
        );

    private static CdcAdoptionProof AdoptionProof() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            DateTimeOffset.UnixEpoch,
            Binding(),
            [
                .. Enum.GetValues<CdcAdoptionVerificationKind>()
                    .Select(kind => new CdcAdoptionVerificationResult(
                        kind,
                        CdcAdoptionVerificationState.ExactMatch,
                        "verified"
                    )),
            ]
        );

    private static CdcCleanupProof CleanupProof() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            DateTimeOffset.UnixEpoch,
            Binding().ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            [
                new CdcGovernedArtifact(
                    CdcGovernedArtifactKind.KafkaConnectConnector,
                    ConnectorName,
                    CdcCleanupState.Deleted,
                    "deleted"
                ),
                new CdcGovernedArtifact(
                    CdcGovernedArtifactKind.PublicTopic,
                    TopicName,
                    CdcCleanupState.Deleted,
                    "deleted"
                ),
                new CdcGovernedArtifact(
                    CdcGovernedArtifactKind.ProgressTopic,
                    ProgressTopicName,
                    CdcCleanupState.NotFound,
                    "not found"
                ),
            ]
        );

    private sealed class ReturningCdcDispatcher(DocumentCacheAdminCdcCommandResult result)
        : IDocumentCacheAdminCdcCommandDispatcher
    {
        Task<DocumentCacheAdminCdcCommandResult> IDocumentCacheAdminCdcCommandDispatcher.ExecuteAsync(
            DocumentCacheAdminCdcCommandRequest request,
            CancellationToken cancellationToken
        ) => Task.FromResult(result);
    }
}
