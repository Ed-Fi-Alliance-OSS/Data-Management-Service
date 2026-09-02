// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Category("CdcDispatch")]
public sealed class Given_DocumentCacheAdminCdcCommandDispatcher
{
    private const string ConnectionString = "Host=cdc-db.internal;Username=dms;Password=not-published";
    private const string SetupPrincipal = "setup_principal";
    private const string ConnectorPrincipal = "connector_principal";

    private ICdcSetupController _controller = null!;
    private ICdcProviderSetupInputsFactory _setupInputsFactory = null!;
    private ICdcProviderSourcePositionAdapter _sourcePositions = null!;
    private IConnectionStringProvider _connectionStrings = null!;

    [SetUp]
    public void Setup()
    {
        _controller = A.Fake<ICdcSetupController>();
        _setupInputsFactory = A.Fake<ICdcProviderSetupInputsFactory>();
        _sourcePositions = A.Fake<ICdcProviderSourcePositionAdapter>();
        _connectionStrings = A.Fake<IConnectionStringProvider>();

        A.CallTo(() => _sourcePositions.Provider).Returns(CoreCdc.CdcProvider.Postgresql);
        A.CallTo(() => _connectionStrings.GetConnectionString(A<long>._, A<string?>._))
            .Returns(ConnectionString);
        A.CallTo(() => _setupInputsFactory.CreateAsync(A<CoreCdc.CdcProvider>._, A<CancellationToken>._))
            .Returns(CdcContractReadResult<CdcProviderSetupInputs>.Success(SetupInputs()));
    }

    [Test]
    public async Task It_enables_with_the_operator_evidence_verbatim()
    {
        A.CallTo(() => _controller.EnableAsync(A<CdcEnableRequest>._, A<CancellationToken>._))
            .Returns(Admission(CdcAdmissionState.Admitted));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcEnableVerbName,
                databaseCreationMode: DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
                writeAdmission: DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue
            )
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        result.Contract.Should().BeOfType<CdcAdmission>();

        CdcEnableRequest enableRequest = CapturedEnableRequest();
        enableRequest
            .ProvisioningEvidence.DatabaseCreationMode.Should()
            .Be(
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue
            );
        enableRequest
            .ProvisioningEvidence.WriteAdmissionState.Should()
            .Be(DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue);
        enableRequest.ConnectionString.Should().Be(ConnectionString);
        enableRequest.ProviderSetup.SetupPrincipal.Should().Be(SetupPrincipal);
    }

    /// <summary>
    /// A near-miss evidence token is passed through rather than corrected, so the proof factory stays the
    /// one place that decides whether the operator made the assertion.
    /// </summary>
    [Test]
    public async Task It_does_not_correct_an_unrecognized_evidence_token()
    {
        A.CallTo(() => _controller.EnableAsync(A<CdcEnableRequest>._, A<CancellationToken>._))
            .Returns(Admission(CdcAdmissionState.NotAdmitted));

        await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcEnableVerbName,
                databaseCreationMode: "createdForInitialCdcProvisioning",
                writeAdmission: null
            )
        );

        CdcEnableRequest enableRequest = CapturedEnableRequest();
        enableRequest
            .ProvisioningEvidence.DatabaseCreationMode.Should()
            .Be("createdForInitialCdcProvisioning");
        enableRequest.ProvisioningEvidence.WriteAdmissionState.Should().BeNull();
    }

    [Test]
    public async Task It_reports_an_unadmitted_enablement_as_incomplete_and_retryable()
    {
        A.CallTo(() => _controller.EnableAsync(A<CdcEnableRequest>._, A<CancellationToken>._))
            .Returns(Admission(CdcAdmissionState.NotAdmitted));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcEnableVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        result.Outcome.Should().Be("notAdmitted");
    }

    [Test]
    public async Task It_maps_status_and_restart_to_their_own_controller_operations()
    {
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.Ready));
        A.CallTo(() => _controller.RestartAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.NotReady));

        DocumentCacheAdminCdcCommandResult status = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );
        DocumentCacheAdminCdcCommandResult restart = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRestartVerbName)
        );

        status.Outcome.Should().Be("ready");
        restart.Outcome.Should().Be("notReady");
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _controller.RestartAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_adopts_under_the_operator_supplied_binding_record()
    {
        A.CallTo(() => _controller.AdoptAsync(A<CdcAdoptRequest>._, A<CancellationToken>._))
            .Returns(CdcContractReadResult<CdcAdoptionProof>.Success(AdoptionProof()));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
                bindingJson: CdcJsonContract.Serialize(Binding())
            )
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);

        CdcAdoptRequest adoptRequest = CapturedRequest<CdcAdoptRequest>(
            nameof(ICdcSetupController.AdoptAsync)
        );
        adoptRequest.Binding.ConnectorName.Should().Be(Binding().ConnectorName);
        adoptRequest.Binding.InstanceKey.Should().Be(Binding().InstanceKey);
    }

    /// <summary>
    /// Adoption operates on the binding record's own artifact identity, not the configured one, so the
    /// names it reports are recovered from the record. A rendering of the configured identity would
    /// name artifacts the adoption never touched while the JSON contract carried the right ones.
    /// </summary>
    [Test]
    public async Task It_reports_the_governed_names_of_the_adopted_binding_rather_than_the_configured_identity()
    {
        CdcBinding adopted = AdoptedBinding("other-instance", generation: 7);
        A.CallTo(() => _controller.AdoptAsync(A<CdcAdoptRequest>._, A<CancellationToken>._))
            .Returns(CdcContractReadResult<CdcAdoptionProof>.Success(AdoptionProof(adopted)));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
                bindingJson: CdcJsonContract.Serialize(adopted)
            )
        );

        using var _ = new AssertionScope();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        result.GovernedNames.Should().NotBeNull();
        result.GovernedNames!.InstanceKey.Should().Be(adopted.InstanceKey);
        result.GovernedNames.ConnectorName.Should().Be(adopted.ConnectorName);
        result.GovernedNames.TopicName.Should().Be(adopted.TopicName);
        result.GovernedNames.DataStoreId.Should().Be(adopted.DataStoreId);
    }

    [Test]
    public async Task It_refuses_an_adoption_whose_binding_record_is_not_a_readable_contract()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcAdoptVerbName, bindingJson: "{ not json")
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result.Contract.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
        A.CallTo(() => _controller.AdoptAsync(A<CdcAdoptRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_refuses_an_adoption_with_no_binding_record_at_all()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcAdoptVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        A.CallTo(() => _controller.AdoptAsync(A<CdcAdoptRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_names_the_replaced_generation_from_the_operator_option()
    {
        A.CallTo(() => _controller.ReplaceSourceAsync(A<CdcReplaceSourceRequest>._, A<CancellationToken>._))
            .Returns(Admission(CdcAdmissionState.Admitted));

        await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName, previousGeneration: 4)
        );

        CapturedRequest<CdcReplaceSourceRequest>(nameof(ICdcSetupController.ReplaceSourceAsync))
            .PreviousGeneration.Should()
            .Be(4);
    }

    [Test]
    public async Task It_refuses_a_source_replacement_that_does_not_name_the_generation_it_supersedes()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        A.CallTo(() => _controller.ReplaceSourceAsync(A<CdcReplaceSourceRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_carries_the_operator_s_connector_already_absent_assertion_to_the_controller()
    {
        A.CallTo(() => _controller.RetireAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(CdcContractReadResult<CdcCleanupProof>.Success(CleanupProof()));

        await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRetireVerbName, connectorAlreadyAbsent: true)
        );

        CapturedRequest<CdcTargetOperationRequest>(nameof(ICdcSetupController.RetireAsync))
            .ConnectorAlreadyAbsent.Should()
            .BeTrue();
    }

    [Test]
    public async Task It_reports_a_partial_retirement_as_incomplete_and_retryable()
    {
        A.CallTo(() => _controller.RetireAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(
                CdcContractReadResult<CdcCleanupProof>.Failure([
                    new CdcDiagnostic(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        DateTimeOffset.UnixEpoch,
                        "$.governedArtifacts",
                        "CDC retirement could not remove every governed artifact."
                    ),
                ])
            );

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRetireVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        result.Contract.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public async Task It_refuses_before_reaching_the_controller_when_the_instance_database_is_unresolved()
    {
        A.CallTo(() => _connectionStrings.GetConnectionString(A<long>._, A<string?>._)).Returns(null);

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result.Diagnostics.Should().NotBeEmpty();
        A.CallTo(_controller).MustNotHaveHappened();
    }

    [Test]
    public async Task It_refuses_before_reaching_the_controller_when_the_provider_setup_inputs_are_unavailable()
    {
        A.CallTo(() => _setupInputsFactory.CreateAsync(A<CoreCdc.CdcProvider>._, A<CancellationToken>._))
            .Returns(
                CdcContractReadResult<CdcProviderSetupInputs>.Failure([
                    new CdcDiagnostic(
                        CdcDiagnosticCategory.ProviderSetupInvalid,
                        DateTimeOffset.UnixEpoch,
                        "$.setupPrincipal",
                        "CDC provider setup inputs require a configured principal."
                    ),
                ])
            );

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ConfigurationError);
        A.CallTo(_controller).MustNotHaveHappened();
    }

    /// <summary>
    /// The provider comes from the registered source-position adapter rather than from re-reading the
    /// datastore setting, so a verb can never run against a provider the control plane did not register.
    /// </summary>
    [Test]
    public async Task It_derives_the_provider_setup_inputs_for_the_registered_provider()
    {
        A.CallTo(() => _sourcePositions.Provider).Returns(CoreCdc.CdcProvider.SqlServer);
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.Ready));

        await ExecuteAsync(Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName));

        A.CallTo(() => _setupInputsFactory.CreateAsync(CoreCdc.CdcProvider.SqlServer, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_reports_the_governed_names_derived_from_the_configured_artifact_identity()
    {
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.Ready));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        result.GovernedNames.Should().NotBeNull();
        result.GovernedNames!.Provider.Should().Be("postgresql");
        result.GovernedNames.DataStoreId.Should().Be("1");
        result.GovernedNames.InstanceKey.Should().Be("instance");
        result.GovernedNames.ConnectorName.Should().NotBeNullOrWhiteSpace();
        result.GovernedNames.TopicName.Should().NotBeNullOrWhiteSpace();
        result.GovernedNames.ProgressTopicName.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>The connection string reaches the controller and never the reported result.</summary>
    [Test]
    public async Task It_never_reports_the_instance_connection_string()
    {
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.Ready));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        string rendered = string.Join(
            '\n',
            [
                result.Outcome,
                result.Category,
                result.GovernedNames?.ConnectorName ?? "",
                result.GovernedNames?.TopicName ?? "",
                result.GovernedNames?.ProgressTopicName ?? "",
                result.GovernedNames?.SchemaHistoryTopicName ?? "",
                .. result.Diagnostics.Select(diagnostic => diagnostic.Message),
            ]
        );

        rendered.Should().NotContain("Password=");
        rendered.Should().NotContain("not-published");
    }

    [Test]
    public void It_rejects_a_command_name_that_is_not_a_cdc_verb()
    {
        Func<Task> execute = () => ExecuteAsync(Request("scrub"));

        execute.Should().ThrowAsync<InvalidOperationException>();
    }

    private Task<DocumentCacheAdminCdcCommandResult> ExecuteAsync(
        DocumentCacheAdminCdcCommandRequest request
    ) =>
        new DocumentCacheAdminCdcCommandDispatcher(
            _controller,
            _setupInputsFactory,
            _sourcePositions,
            _connectionStrings,
            Options.Create(ControlOptions()),
            TimeProvider.System
        ).ExecuteAsync(request);

    private CdcEnableRequest CapturedEnableRequest() =>
        CapturedRequest<CdcEnableRequest>(nameof(ICdcSetupController.EnableAsync));

    private TRequest CapturedRequest<TRequest>(string methodName)
        where TRequest : class =>
        Fake.GetCalls(_controller)
            .Single(call => string.Equals(call.Method.Name, methodName, StringComparison.Ordinal))
            .Arguments.Get<TRequest>(0)!;

    private static DocumentCacheAdminCdcCommandRequest Request(
        string verbName,
        string? databaseCreationMode = null,
        string? writeAdmission = null,
        long? previousGeneration = null,
        string? bindingJson = null,
        bool connectorAlreadyAbsent = false
    ) =>
        new(
            verbName,
            DocumentCacheTargetKey.Create("", 1),
            databaseCreationMode,
            writeAdmission,
            previousGeneration,
            bindingJson,
            connectorAlreadyAbsent
        );

    private static CdcControlOptions ControlOptions() =>
        new()
        {
            DeploymentKey = "deployment",
            InstanceKey = "instance",
            TopicPrefix = "edfi.documents.instance",
            Generation = 1,
            PartitionCount = 3,
            SetupPrincipal = SetupPrincipal,
            ConnectorPrincipal = ConnectorPrincipal,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private static CdcProviderSetupInputs SetupInputs() =>
        new(
            SetupPrincipal,
            ConnectorPrincipal,
            [
                new CdcSourceTableInventory(
                    CdcSourceTableKind.Document,
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    "\"dms\".\"Document\"",
                    [
                        new CdcSourceColumnInventory(
                            new DbColumnName("DocumentUuid"),
                            "\"DocumentUuid\"",
                            1,
                            "uuid",
                            IsNullable: false
                        ),
                    ]
                ),
            ],
            [
                new CdcDmsManagedTableInventory(
                    CdcDmsManagedTableKind.Core,
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    "\"dms\".\"Document\""
                ),
            ]
        );

    private static CdcTargetIdentity TargetIdentity() =>
        new("deployment", "", "1", "instance", 1, CoreCdc.CdcProvider.Postgresql);

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
            CdcBlockingCategory.None,
            []
        );

    private static CdcBinding Binding() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "deployment",
            "",
            "1",
            "instance",
            1,
            CoreCdc.CdcProvider.Postgresql,
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "edfi-documents-instance-1",
            "edfi.documents.instance.1",
            3,
            "murmur2",
            CdcJsonContract.CurrentContractVersion
        );

    private static CdcCleanupProof CleanupProof() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            DateTimeOffset.UnixEpoch,
            Binding().ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            []
        );

    /// <summary>
    /// A binding whose governed names are the record's own rather than the configured identity's. The
    /// names come from the shared generator so the record is internally consistent and recoverable.
    /// </summary>
    private static CdcBinding AdoptedBinding(string instanceKey, long generation)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(
                new CdcArtifactNameInput(
                    "deployment",
                    "edfi.documents.other",
                    instanceKey,
                    generation,
                    CoreCdc.CdcProvider.Postgresql
                )
            )
            .Inventory!;

        return Binding() with
        {
            InstanceKey = inventory.InstanceKey,
            Generation = inventory.Generation,
            DataStoreId = "1",
            ConnectorName = inventory.ConnectorName,
            TopicName = inventory.TopicName,
        };
    }

    private static CdcAdoptionProof AdoptionProof() => AdoptionProof(Binding());

    private static CdcAdoptionProof AdoptionProof(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            DateTimeOffset.UnixEpoch,
            binding,
            [
                .. Enum.GetValues<CdcAdoptionVerificationKind>()
                    .Select(kind => new CdcAdoptionVerificationResult(
                        kind,
                        CdcAdoptionVerificationState.ExactMatch,
                        "verified"
                    )),
            ]
        );
}
