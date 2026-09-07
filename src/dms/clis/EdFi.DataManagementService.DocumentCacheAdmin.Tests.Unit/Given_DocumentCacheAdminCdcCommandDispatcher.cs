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
    private IDataStoreProvider _dataStores = null!;
    private IConnectionStringProvider _connectionStrings = null!;

    [SetUp]
    public void Setup()
    {
        _controller = A.Fake<ICdcSetupController>();
        _setupInputsFactory = A.Fake<ICdcProviderSetupInputsFactory>();
        _sourcePositions = A.Fake<ICdcProviderSourcePositionAdapter>();
        _dataStores = A.Fake<IDataStoreProvider>();
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

    /// <summary>
    /// A restart that was declined before it issued anything reports the same contract shape as one the
    /// worker applied, so the exit code is all that tells an automated caller the deployment was left
    /// exactly as the operator left it.
    /// </summary>
    [Test]
    public async Task It_reports_a_restart_that_started_nothing_as_rejected_before_mutation()
    {
        A.CallTo(() => _controller.RestartAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(DeclinedRestart());

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRestartVerbName)
        );

        using AssertionScope assertions = new();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result.Outcome.Should().Be("notReady", "the contract still reports the readiness it observed");
    }

    /// <summary>
    /// A restart the worker applied is classified by readiness like any other status read, because what
    /// the connector does next is an outcome to observe rather than a request to reissue.
    /// </summary>
    [Test]
    public async Task It_reports_an_applied_restart_as_successful()
    {
        A.CallTo(() => _controller.RestartAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.NotReady));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRestartVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
    }

    /// <summary>
    /// A planned fence that landed exits 0, even though the connector it just stopped is by definition
    /// not ready. Classifying it by readiness the way its sibling verbs are would report this verb's
    /// own success as a failure and fail the shutdown sequence that issued it.
    /// </summary>
    [Test]
    public async Task It_reports_an_applied_fence_as_successful_though_a_fenced_target_is_not_ready()
    {
        A.CallTo(() => _controller.StopAsync(A<CdcPlannedFenceRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.NotReady));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStopVerbName)
        );

        using AssertionScope assertions = new();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        result.Outcome.Should().Be("notReady", "the contract still reports the readiness it observed");
        A.CallTo(() => _controller.StopAsync(A<CdcPlannedFenceRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// The planned fence is dispatched before the Configuration Service is asked for the target's
    /// instance database and before the provider-setup inputs are derived from the effective schema.
    /// Both are inputs to observing the target; the fence acts on the connector the durable binding
    /// record names.
    /// </summary>
    [Test]
    public async Task It_fences_without_loading_data_stores_or_deriving_provider_setup_inputs()
    {
        A.CallTo(() => _controller.StopAsync(A<CdcPlannedFenceRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.NotReady));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStopVerbName)
        );

        using AssertionScope assertions = new();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connectionStrings.GetConnectionString(A<long>._, A<string?>._)).MustNotHaveHappened();
        A.CallTo(() => _setupInputsFactory.CreateAsync(A<CoreCdc.CdcProvider>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// And the same holds when those inputs are not merely unused but unusable, which is the state a
    /// deployment is in at the moment a connector most needs fencing.
    /// </summary>
    [Test]
    public async Task It_fences_though_the_configuration_service_and_the_provider_inputs_are_unusable()
    {
        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("the Configuration Service is unreachable"));
        A.CallTo(() => _setupInputsFactory.CreateAsync(A<CoreCdc.CdcProvider>._, A<CancellationToken>._))
            .Returns(
                CdcContractReadResult<CdcProviderSetupInputs>.Failure([
                    new CdcDiagnostic(
                        "cdcProviderSetupInputs",
                        CdcDiagnosticCategory.ProviderSetupInvalid,
                        CdcDiagnosticSeverity.Error,
                        CdcDiagnosticComponent.ProviderSetup,
                        DateTimeOffset.UtcNow,
                        "The effective schema could not supply the provider-setup inputs.",
                        false,
                        observed: "unavailable"
                    ),
                ])
            );
        A.CallTo(() => _controller.StopAsync(A<CdcPlannedFenceRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.NotReady));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStopVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
    }

    /// <summary>
    /// A fence the worker refused leaves a connector that may still be publishing, which is the
    /// opposite of what the caller asked for and is theirs to act on before the stack goes down around
    /// it. Exiting 0 would report a fence that is not there.
    /// </summary>
    [Test]
    public async Task It_reports_a_fence_that_did_not_apply_as_rejected_before_mutation()
    {
        A.CallTo(() => _controller.StopAsync(A<CdcPlannedFenceRequest>._, A<CancellationToken>._))
            .Returns(UnappliedFence());

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStopVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
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

    /// <summary>
    /// The instance database an adoption verifies against is resolved from the invocation's own tenant
    /// and data store, while the record supplies a logical target of its own - and the record's is the
    /// one the binding becomes durable under. A record naming another data store would have its claims
    /// verified against this one's database and then be persisted for that other target.
    /// </summary>
    [Test]
    public async Task It_refuses_an_adoption_whose_binding_record_names_another_data_store()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
                bindingJson: CdcJsonContract.Serialize(Binding() with { DataStoreId = "2" })
            )
        );

        using var _ = new AssertionScope();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "cdcAdoptionTargetMismatch");
        A.CallTo(() => _controller.AdoptAsync(A<CdcAdoptRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The same rule for the tenant half of the target, which the two sides spell differently: the
    /// invocation carries the default tenant as the empty string and a binding record never does, so
    /// the comparison maps between them rather than refusing every default-tenant adoption.
    /// </summary>
    [Test]
    public async Task It_refuses_an_adoption_whose_binding_record_names_another_tenant()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
                bindingJson: CdcJsonContract.Serialize(Binding() with { TenantKey = "other-tenant" })
            )
        );

        using var _ = new AssertionScope();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "cdcAdoptionTargetMismatch");
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

    /// <summary>
    /// The replacing generation runs the same initial readiness sequence, so it carries the same
    /// provisioning evidence, and the generation it supersedes is the operator's own rather than one
    /// inferred from what exists.
    /// </summary>
    [Test]
    public async Task It_replaces_a_source_with_the_operator_s_superseded_generation()
    {
        A.CallTo(() => _controller.ReplaceSourceAsync(A<CdcReplaceSourceRequest>._, A<CancellationToken>._))
            .Returns(Admission(CdcAdmissionState.Admitted));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName,
                databaseCreationMode: DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
                writeAdmission: DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue,
                previousGeneration: 4L
            )
        );

        using var _ = new AssertionScope();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        result.Contract.Should().BeOfType<CdcAdmission>();

        CdcReplaceSourceRequest replaceRequest = CapturedRequest<CdcReplaceSourceRequest>(
            nameof(ICdcSetupController.ReplaceSourceAsync)
        );
        replaceRequest.PreviousGeneration.Should().Be(4L);
        replaceRequest
            .ProvisioningEvidence.DatabaseCreationMode.Should()
            .Be(
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue
            );
        replaceRequest
            .ProvisioningEvidence.WriteAdmissionState.Should()
            .Be(DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue);
    }

    /// <summary>
    /// The superseded generation names the connector the replacement fences, so a request built without
    /// one is refused for itself rather than defaulted to a generation nobody asked for.
    /// </summary>
    [Test]
    public async Task It_refuses_a_source_replacement_that_names_no_superseded_generation()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName, previousGeneration: null)
        );

        using var _ = new AssertionScope();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.MissingRequiredField);
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

    /// <summary>
    /// After a guarded source replacement the data store's connection string is the replacing
    /// database's, while the superseded generation's provider artifacts are still in the database it
    /// was bound to — and retirement refuses any database that is not the binding's own physical
    /// source. Without a route to that database the retained generation could never be retired.
    /// </summary>
    [Test]
    public async Task It_retires_against_the_source_connection_the_named_environment_variable_holds()
    {
        const string VariableName = "DMS_TEST_SUPERSEDED_SOURCE";
        const string SupersededConnectionString = "Host=superseded.internal;Username=dms;Password=other";

        A.CallTo(() => _controller.RetireAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(CdcContractReadResult<CdcCleanupProof>.Success(CleanupProof()));

        Environment.SetEnvironmentVariable(VariableName, SupersededConnectionString);
        try
        {
            await ExecuteAsync(
                Request(
                    DocumentCacheAdminCommandSurface.CdcRetireVerbName,
                    sourceConnectionVariable: VariableName
                )
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, null);
        }

        CapturedRequest<CdcTargetOperationRequest>(nameof(ICdcSetupController.RetireAsync))
            .ConnectionString.Should()
            .Be(SupersededConnectionString);

        // The Configuration Service's own answer is not consulted at all: falling back to it would run
        // the teardown against the replacing database, which is the mistake the override prevents.
        A.CallTo(() => _connectionStrings.GetConnectionString(A<long>._, A<string?>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// "Not consulted at all" includes the load that fills the cache behind the read. The whole point
    /// of the override is a superseded generation whose database the deployment's data store no longer
    /// names: its binding, its source database, and its governed artifacts are all reachable, so the
    /// retirement must not fail on a Configuration Service it never reads.
    /// </summary>
    [Test]
    public async Task It_retires_against_a_named_source_connection_while_the_data_stores_are_unreachable()
    {
        const string VariableName = "DMS_TEST_SUPERSEDED_SOURCE_WITHOUT_CMS";
        const string SupersededConnectionString = "Host=superseded.internal;Username=dms;Password=other";

        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("Unable to connect to Configuration Service."));
        A.CallTo(() => _controller.RetireAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(CdcContractReadResult<CdcCleanupProof>.Success(CleanupProof()));

        Environment.SetEnvironmentVariable(VariableName, SupersededConnectionString);
        DocumentCacheAdminCdcCommandResult result;
        try
        {
            result = await ExecuteAsync(
                Request(
                    DocumentCacheAdminCommandSurface.CdcRetireVerbName,
                    sourceConnectionVariable: VariableName
                )
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, null);
        }

        result.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code == "cdcDataStoresUnavailable");
        CapturedRequest<CdcTargetOperationRequest>(nameof(ICdcSetupController.RetireAsync))
            .ConnectionString.Should()
            .Be(SupersededConnectionString);

        // Never issued, rather than issued and its failure tolerated: a load this path does not need
        // is a Configuration Service round trip a teardown should not be waiting on either.
        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// An unset variable is a refusal rather than a fallback, for the same reason.
    /// </summary>
    [Test]
    public async Task It_refuses_a_retirement_whose_named_source_connection_variable_is_unset()
    {
        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(
                DocumentCacheAdminCommandSurface.CdcRetireVerbName,
                sourceConnectionVariable: "DMS_TEST_VARIABLE_THAT_IS_NOT_SET"
            )
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);

        CdcDiagnostic refusal = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "cdcSourceConnectionVariableUnresolved")
            .Subject;

        // The variable is what the operator must act on, and the code survives the diagnostic's own
        // sanitizer so it stays matchable.
        refusal.Observed.Should().Contain("DMS_TEST_VARIABLE_THAT_IS_NOT_SET");
        A.CallTo(_controller).MustNotHaveHappened();
    }

    /// <summary>
    /// The value is a connection string; only the variable's name — the operator's own invocation
    /// argument — may appear in a diagnostic.
    /// </summary>
    [Test]
    public async Task It_publishes_no_source_connection_value_in_a_refusal()
    {
        const string VariableName = "DMS_TEST_SUPERSEDED_SOURCE_SECRET";
        const string SupersededConnectionString = "Host=superseded.internal;Password=not-published";

        A.CallTo(() => _controller.RetireAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(
                CdcContractReadResult<CdcCleanupProof>.Failure([
                    new CdcDiagnostic(
                        CdcDiagnosticCategory.SourceMismatch,
                        DateTimeOffset.UnixEpoch,
                        "$.binding.physicalSourceFingerprint",
                        "CDC retirement requires the connected database to be this binding's own source."
                    ),
                ])
            );

        Environment.SetEnvironmentVariable(VariableName, SupersededConnectionString);
        DocumentCacheAdminCdcCommandResult result;
        try
        {
            result = await ExecuteAsync(
                Request(
                    DocumentCacheAdminCommandSurface.CdcRetireVerbName,
                    sourceConnectionVariable: VariableName
                )
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, null);
        }

        result
            .Diagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.Message.Contains("not-published", StringComparison.Ordinal)
                || (diagnostic.Observed ?? string.Empty).Contains("not-published", StringComparison.Ordinal)
            );
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

    /// <summary>
    /// Automation reissues an incomplete retirement and corrects a rejected one, so a refusal that
    /// removed nothing must not be reported as a half-finished teardown a retry would resolve.
    /// </summary>
    [Test]
    public async Task It_reports_a_retirement_refused_before_any_mutation_as_rejected()
    {
        A.CallTo(() => _controller.RetireAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(
                CdcContractReadResult<CdcCleanupProof>.Failure([
                    new CdcDiagnostic(
                        CdcDiagnosticCategory.ProviderMismatch,
                        DateTimeOffset.UnixEpoch,
                        "$.binding.provider",
                        "CDC retirement requires the binding record to name this deployment's provider."
                    )
                    {
                        Code = CdcRetirementDiagnosticCodes.RefusedNoMutation,
                    },
                ])
            );

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRetireVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result.Outcome.Should().Be("rejectedNoMutation");
    }

    /// <summary>
    /// Nothing else on the cdc path loads the deployment's data stores. The DocumentCache status and
    /// mutating commands reach the Configuration Service through the target-registry refresh their own
    /// executor branch runs first; the cdc branch dispatches straight to this class, and the connection
    /// string provider reads a cache with no lazy load behind it. Without the load every verb refuses
    /// on an empty cache and names the wrong cause.
    /// </summary>
    [Test]
    public async Task It_loads_the_data_stores_before_it_reads_the_instance_database()
    {
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.Ready));

        await ExecuteAsync(Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName));

        A.CallTo(() => _dataStores.LoadDataStores(null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly()
            .Then(
                A.CallTo(() => _connectionStrings.GetConnectionString(1, null)).MustHaveHappenedOnceExactly()
            );
    }

    /// <summary>
    /// The tenant reaches the provider in its own terms: the target key normalizes the default tenant
    /// to the empty string, and the provider expects null for it. A verb that loaded one tenant and
    /// read another would refuse on a cache it had just filled.
    /// </summary>
    [Test]
    public async Task It_loads_the_same_tenant_it_reads_the_instance_database_for()
    {
        A.CallTo(() => _controller.StatusAsync(A<CdcTargetOperationRequest>._, A<CancellationToken>._))
            .Returns(Status(CdcReadiness.Ready));

        await ExecuteAsync(
            new DocumentCacheAdminCdcCommandRequest(
                DocumentCacheAdminCommandSurface.CdcStatusVerbName,
                DocumentCacheTargetKey.Create("district-a", 1),
                null,
                null,
                null,
                null,
                false,
                null
            )
        );

        A.CallTo(() => _dataStores.LoadDataStores("district-a", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _connectionStrings.GetConnectionString(1, "district-a")).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A Configuration Service the load cannot reach is reported as its own refusal rather than thrown:
    /// the verb still owes the operator a classified result, and an unresolvable instance database is
    /// not the same fault as a data store the deployment does not have.
    /// </summary>
    [Test]
    public async Task It_refuses_without_running_the_verb_when_the_data_stores_cannot_be_loaded()
    {
        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("Unable to connect to Configuration Service."));

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcRetireVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        CdcDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
        diagnostic.Code.Should().Be("cdcDataStoresUnavailable");
        // A Configuration Service that was momentarily unreachable is worth reissuing against, unlike
        // the sibling refusals that name a fact about the request itself.
        diagnostic.Retryable.Should().BeTrue();
        NoVerbRan();
    }

    /// <summary>
    /// The refusal names the rejection's type and nothing else: the provider's own message quotes the
    /// configured Configuration Service base address.
    /// </summary>
    [Test]
    public async Task It_does_not_publish_the_data_store_load_failure_message()
    {
        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .ThrowsAsync(
                new InvalidOperationException(
                    "Unable to connect to Configuration Service at http://cms:8081."
                )
            );

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        CdcDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
        diagnostic.Observed.Should().Be(nameof(InvalidOperationException));
        diagnostic.Message.Should().NotContain("http://cms:8081");
    }

    [Test]
    public async Task It_refuses_without_running_the_verb_when_the_instance_database_is_unresolved()
    {
        A.CallTo(() => _connectionStrings.GetConnectionString(A<long>._, A<string?>._)).Returns(null);

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result.Diagnostics.Should().NotBeEmpty();
        NoVerbRan();
    }

    [Test]
    public async Task It_refuses_without_running_the_verb_when_the_provider_setup_inputs_are_unavailable()
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
        NoVerbRan();
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
            Options.Create(ControlOptions()),
            TimeProvider.System,
            _dataStores,
            _connectionStrings
        ).ExecuteAsync(request);

    private CdcEnableRequest CapturedEnableRequest() =>
        CapturedRequest<CdcEnableRequest>(nameof(ICdcSetupController.EnableAsync));

    private TRequest CapturedRequest<TRequest>(string methodName)
        where TRequest : class =>
        Fake.GetCalls(_controller)
            .Single(call => string.Equals(call.Method.Name, methodName, StringComparison.Ordinal))
            .Arguments.Get<TRequest>(0)!;

    /// <summary>
    /// The stop a latched source-history loss still owes is attempted even by an invocation that could
    /// resolve none of its own observation inputs, and a stop the worker refused is reported alongside
    /// the refusal. Nothing else re-attempts it: latching raises no second incident candidate, and
    /// restart declines a lost continuity rather than acting on it.
    /// </summary>
    [Test]
    public async Task It_contains_a_latched_incident_when_the_data_stores_cannot_be_loaded()
    {
        A.CallTo(() => _dataStores.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("Unable to connect to Configuration Service."));
        A.CallTo(() =>
                _controller.ContainLatchedIncidentAsync(A<CdcPlannedFenceRequest>._, A<CancellationToken>._)
            )
            .Returns<IReadOnlyList<CdcDiagnostic>>([
                new CdcDiagnostic(
                    CdcDiagnosticCategory.SourceHistoryLost,
                    DateTimeOffset.UnixEpoch,
                    "$.connectorRuntime",
                    "CDC status could not fence the connector carrying the source-history loss."
                ),
            ]);

        DocumentCacheAdminCdcCommandResult result = await ExecuteAsync(
            Request(DocumentCacheAdminCommandSurface.CdcStatusVerbName)
        );

        using var _ = new AssertionScope();
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "cdcDataStoresUnavailable");
        result
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.SourceHistoryLost);
        CdcPlannedFenceRequest containment = CapturedRequest<CdcPlannedFenceRequest>(
            nameof(ICdcSetupController.ContainLatchedIncidentAsync)
        );
        containment.DataStoreId.Should().Be(1);
    }

    /// <summary>
    /// No cdc verb was run against the control plane. The containment of a latched source-history loss
    /// is not one: `cdc-streaming.md` requires it to be attempted from the binding record alone,
    /// independently of the observations a refusal says could not be made.
    /// </summary>
    private void NoVerbRan() =>
        A.CallTo(_controller)
            .Where(call =>
                !string.Equals(
                    call.Method.Name,
                    nameof(ICdcSetupController.ContainLatchedIncidentAsync),
                    StringComparison.Ordinal
                )
            )
            .MustNotHaveHappened();

    private static DocumentCacheAdminCdcCommandRequest Request(
        string verbName,
        string? databaseCreationMode = null,
        string? writeAdmission = null,
        long? previousGeneration = null,
        string? bindingJson = null,
        bool connectorAlreadyAbsent = false,
        string? sourceConnectionVariable = null
    ) =>
        new(
            verbName,
            DocumentCacheTargetKey.Create("", 1),
            databaseCreationMode,
            writeAdmission,
            previousGeneration,
            bindingJson,
            connectorAlreadyAbsent,
            sourceConnectionVariable
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
            ConnectorDatabasePrincipal = ConnectorPrincipal,
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

    /// <summary>
    /// One target status carrying the step that says no connector request was issued. Every component
    /// reads unknown, which is what a status collected against an unproved source reports anyway.
    /// </summary>
    private static CdcStatus DeclinedRestart()
    {
        CdcComponent unknown = new(
            CdcComponentState.Unknown,
            CdcBlockingCategory.ProviderHistoryUnknown,
            DateTimeOffset.UnixEpoch,
            null
        );

        return new(
            CdcJsonContract.CurrentContractVersion,
            DateTimeOffset.UnixEpoch,
            CdcReadiness.NotReady,
            CdcBlockingCategory.ProviderHistoryUnknown,
            [
                new CdcTargetStatus(
                    new("deployment", "", "1", "instance", 1, CoreCdc.CdcProvider.Postgresql),
                    CdcReadiness.NotReady,
                    CdcBlockingCategory.ProviderHistoryUnknown,
                    unknown,
                    unknown,
                    unknown,
                    unknown,
                    new(
                        CdcComponentState.Unknown,
                        CdcBlockingCategory.ProviderHistoryUnknown,
                        DateTimeOffset.UnixEpoch,
                        null,
                        CdcSourceHistoryContinuity.Unknown,
                        incidentLatched: false
                    ),
                    unknown,
                    unknown,
                    unknown,
                    unknown,
                    unknown,
                    [
                        new CdcDiagnostic(
                            CdcDiagnosticCategory.ProviderHistoryUnknown,
                            DateTimeOffset.UnixEpoch,
                            "$.bindingState",
                            "CDC restart issued no connector request."
                        )
                        {
                            Code = CdcRestartDiagnosticCodes.NotAttempted,
                        },
                    ]
                ),
            ]
        );
    }

    /// <summary>A stop the worker refused, carrying the diagnostic that says so.</summary>
    private static CdcStatus UnappliedFence()
    {
        CdcStatus declined = DeclinedRestart();
        CdcTargetStatus target = declined.Targets[0];

        return declined with
        {
            Targets =
            [
                target with
                {
                    Diagnostics =
                    [
                        new CdcDiagnostic(
                            CdcDiagnosticCategory.ConnectorNotRunning,
                            DateTimeOffset.UnixEpoch,
                            "$.connectorRuntime",
                            "CDC stop could not fence the connector."
                        )
                        {
                            Code = CdcFenceDiagnosticCodes.NotApplied,
                        },
                    ],
                },
            ],
        };
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
            // What a binding record carries for the default tenant. The invocation target spells the
            // same tenant as the empty string, and every binding-facing comparison maps between them.
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
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
