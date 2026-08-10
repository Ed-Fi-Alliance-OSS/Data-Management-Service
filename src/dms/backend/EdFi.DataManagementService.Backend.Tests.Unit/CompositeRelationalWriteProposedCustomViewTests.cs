// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Composite;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Owns the proposed custom view-based checks the second command issues: which runs it emits, the order it
/// emits them in relative to <c>NamespaceBased</c>, how a <c>cv1</c> payload maps back to a caller-visible
/// denial, and how a self-basis check is settled without SQL.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Composite_Relational_Write_Proposed_Custom_View_Authorization
{
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "School");
    private static readonly DbColumnName BasisColumn = new("SchoolId");
    private static readonly DocumentUuid CreatedDocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );

    [Test]
    public async Task It_emits_one_run_carrying_the_planner_assigned_index()
    {
        // The proposed slice follows the stored one, so its first index is 1 rather than 0. That index is what
        // the payload carries, so it has to survive into the emitted SQL.
        var request = CreateRequest(CreateStoredAndProposedPlan("SchoolWithATag", storedIndex: 0));
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
        session.Commands[0].CommandText.Should().Contain("cv1|1|").And.Contain("SchoolWithATag");
    }

    [Test]
    public async Task It_binds_the_proposed_basis_value_from_the_finalized_root_row()
    {
        var request = CreateRequest(CreateStoredAndProposedPlan("SchoolWithATag", storedIndex: 0));
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // 255901 is the merged SchoolId, not the stored target's DocumentId: a proposed check authorizes the
        // value that will be persisted.
        session.Commands[0].Parameters.Should().Contain(parameter => Equals(parameter.Value, 255901));
    }

    [Test]
    public async Task It_emits_no_stored_document_id_parameter_for_a_proposed_only_run()
    {
        var request = CreateRequest(CreateStoredAndProposedPlan("SchoolWithATag", storedIndex: 0));
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        session
            .Commands[0]
            .Parameters.Should()
            .NotContain(parameter =>
                parameter.Name.Contains("documentId", StringComparison.OrdinalIgnoreCase)
            );
    }

    [Test]
    public async Task It_emits_the_custom_view_runs_around_the_namespace_check_in_configured_order()
    {
        var request = CreateRequest(
            CreateProposedOnlyPlan(("SchoolWithAnEarlyTag", 0), ("SchoolWithALateTag", 2)),
            withNamespace: true
        );
        var session = new ScriptedWriteSession(
            CreateReader(CreateAuthorizedTable(), CreateAuthorizedTable(), CreateAuthorizedTable())
        );

        await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        var commandText = session.Commands.Should().ContainSingle().Subject.CommandText;
        var earlyPosition = commandText.IndexOf("SchoolWithAnEarlyTag", StringComparison.Ordinal);
        var namespacePosition = commandText.IndexOf("namespacePrefixes", StringComparison.Ordinal);
        var latePosition = commandText.IndexOf("SchoolWithALateTag", StringComparison.Ordinal);

        earlyPosition.Should().BePositive();
        earlyPosition.Should().BeLessThan(namespacePosition);
        namespacePosition.Should().BeLessThan(latePosition);
    }

    [Test]
    public async Task It_runs_a_custom_view_configured_before_namespace_before_reporting_a_namespace_planning_failure()
    {
        // The namespace check names a column the write plan does not bind, so its plan cannot be reconciled
        // with the finalized root row. The view configured before NamespaceBased still executes first, so its
        // denial is the answer rather than the later namespace security-configuration failure, and the
        // namespace check itself is never emitted.
        var request = CreateRequest(
            CreateProposedOnlyPlan(("SchoolWithAnEarlyTag", 0)),
            withNamespace: true,
            namespaceColumn: new DbColumnName("NotANamespaceColumn")
        );
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        var resolution = await CreateSut(CustomViewFailureExtractor(0))
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        FailureOf(resolution).StrategyName.Should().Be("SchoolWithAnEarlyTag");
        var commandText = session.Commands.Should().ContainSingle().Subject.CommandText;
        commandText.Should().Contain("SchoolWithAnEarlyTag");
        commandText.Should().NotContain("namespacePrefixes");
    }

    [Test]
    public async Task It_does_not_plan_a_custom_view_configured_after_namespace_when_namespace_planning_failed()
    {
        // The mirror of the case above. The only custom view is configured after NamespaceBased, and its
        // proposed basis column is unbound, so extracting it would report an invalid plan ahead of the
        // namespace failure that precedes it. It must not be planned at all: the namespace failure stands and
        // no custom-view statement is built.
        var request = CreateRequest(
            CreateProposedOnlyPlanOn(new DbColumnName("NotAColumn"), ("SchoolWithALateTag", 2)),
            withNamespace: true,
            namespaceColumn: new DbColumnName("NotANamespaceColumn")
        );
        var session = new ScriptedWriteSession();

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        var errors = resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
            .Subject.Errors;
        // The two families word their failures distinctly, which is what tells the namespace failure apart
        // from the custom-view invalid-plan failure that must not preempt it.
        errors.Should().ContainSingle().Which.Should().StartWith("Proposed namespace authorization");
        errors
            .Should()
            .NotContain(error =>
                error.Contains("Proposed custom view authorization", StringComparison.Ordinal)
            );
        session.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_resolves_a_straddling_run_payload_against_the_full_planned_check_list()
    {
        // The later view lands in the second run and carries index 1. Mapping has to use the request's whole
        // planned list, or the denial would name the earlier view.
        var request = CreateRequest(
            CreateProposedOnlyPlan(("SchoolWithAnEarlyTag", 0), ("SchoolWithALateTag", 2)),
            withNamespace: true
        );
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        var resolution = await CreateSut(CustomViewFailureExtractor(1))
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        FailureOf(resolution).StrategyName.Should().Be("SchoolWithALateTag");
    }

    [Test]
    public async Task It_maps_a_no_matching_row_payload_to_the_access_denied_failure()
    {
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        var resolution = await CreateSut(CustomViewFailureExtractor(0))
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        var failure = FailureOf(resolution);
        failure.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        failure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Proposed);
        failure.ReadableSecurableElements.Should().Equal("SchoolWithATagElement");
        failure.Hint.Should().Be("You may need a SchoolWithATag hint.");
    }

    [Test]
    public async Task It_maps_a_missing_proposed_value_payload_to_the_element_required_failure()
    {
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        var resolution = await CreateSut(
                CustomViewFailureExtractor(
                    0,
                    CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing
                )
            )
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        FailureOf(resolution)
            .FailureKind.Should()
            .Be(CustomViewAuthorizationFailureKind.ProposedValueMissing);
    }

    [Test]
    public async Task It_maps_a_payload_addressing_no_planned_check_to_a_security_configuration_failure()
    {
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        var resolution = await CreateSut(CustomViewFailureExtractor(7))
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>();
    }

    [Test]
    public async Task It_denies_a_self_basis_check_on_a_create_without_issuing_a_membership_statement()
    {
        var request = CreateRequest(CreateSelfBasisPlan(("SchoolWithATag", 0)), resolveToCreate: true);
        // The only command the session may see is the view validation; a membership statement would need a
        // result set this script does not offer.
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        var failure = resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;
        failure.StrategyName.Should().Be("SchoolWithATag");
        failure.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        failure.ReadableSecurableElements.Should().Equal("SchoolWithATagElement");
        session.Commands.Should().ContainSingle();
        session.Commands[0].CommandText.Should().Contain("pg_catalog");
    }

    [Test]
    public async Task It_reports_a_missing_view_rather_than_the_self_basis_denial()
    {
        // Eager validation runs before the 403 precisely so this stays a 500: a view that is absent or does
        // not meet the DocumentId contract is a configuration defect, not an authorization answer.
        var request = CreateRequest(CreateSelfBasisPlan(("SchoolWithATag", 0)), resolveToCreate: true);
        var session = new ScriptedWriteSession(new FakeDbException("relation does not exist", "42P01"));

        var act = async () =>
            await CreateSut()
                .ResolveAsync(
                    request,
                    CreateMergeResult(request),
                    RelationalWriteSecondCommandMode.AuthorizationOnly,
                    session
                );

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
    }

    [Test]
    public async Task It_runs_an_earlier_custom_view_before_settling_a_later_self_basis_denial()
    {
        var request = CreateRequest(
            CreateSelfBasisPlan(("SchoolWithASelfTag", 2), ("SchoolWithAnEarlyTag", 0)),
            resolveToCreate: true
        );
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        // The earlier view denies, so its denial is the one reported and the self-basis check never settles.
        var resolution = await CreateSut(CustomViewFailureExtractor(1))
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure.StrategyName.Should()
            .Be("SchoolWithAnEarlyTag");
    }

    [Test]
    public async Task It_does_not_issue_a_custom_view_configured_after_a_self_basis_denial()
    {
        // The denial is deterministic at its configured position, so the run would have aborted before the
        // later view. Issuing it anyway could report that view's denial as the first failure instead.
        var request = CreateRequest(
            CreateSelfBasisPlan(("SchoolWithASelfTag", 0), ("SchoolWithALaterTag", 2)),
            resolveToCreate: true
        );
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure.StrategyName.Should()
            .Be("SchoolWithASelfTag");
        // The one command is the view validation probe, not a membership statement.
        session.Commands.Should().ContainSingle();
        session.Commands[0].CommandText.Should().NotContain("SchoolWithALaterTag");
    }

    [Test]
    public async Task It_does_not_issue_the_namespace_check_a_self_basis_denial_preempts()
    {
        // NamespaceBased is configured after the self-basis view, so the run would have aborted before
        // reaching it. Issuing it anyway could report the wrong first failure.
        var request = CreateRequest(
            CreateSelfBasisPlan(("SchoolWithATag", 0)),
            withNamespace: true,
            resolveToCreate: true
        );
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        session.Commands.Should().ContainSingle();
        session.Commands[0].CommandText.Should().NotContain("namespacePrefixes");
    }

    [Test]
    public async Task It_treats_a_self_basis_check_as_satisfied_when_a_paired_stored_check_was_planned()
    {
        // An existing target's DocumentId is immutable, so the stored check already authorized the very value
        // the proposed check would bind.
        var request = CreateRequest(CreateSelfBasisPlanWithStoredPair("SchoolWithATag"));
        var session = new ScriptedWriteSession();

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_closed_when_a_self_basis_check_has_no_paired_stored_check()
    {
        // Nothing authorized the existing row against this view, so treating the check as satisfied would
        // serve a write the strategy restricts.
        var request = CreateRequest(CreateSelfBasisPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession();

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>();
        session.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_closed_when_the_proposed_basis_column_is_not_bound_by_the_write_plan()
    {
        var request = CreateRequest(
            CreateProposedOnlyPlan(("SchoolWithATag", 0)),
            basisColumn: new DbColumnName("NotAColumn")
        );
        var session = new ScriptedWriteSession();

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>();
        session.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_an_invalid_view_behind_a_failure_that_carries_no_authorization_payload()
    {
        // The command aborted with something that is not an AUTH1 payload. Position cannot say whether the
        // custom-view statement caused it, so the emitted views are probed instead; this one is broken, so the
        // documented configuration 500 is what the caller must see.
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(new FakeDbException("relation does not exist", "42P01"));
        var validationExecutor = new StubValidationCommandExecutor(
            new FakeDbException("missing authorization view", "42P01")
        );

        var act = async () =>
            await CreateSut(
                    new StubProviderFailureExtractor("42P01", "relation does not exist"),
                    validationExecutor
                )
                .ResolveAsync(
                    request,
                    CreateMergeResult(request),
                    RelationalWriteSecondCommandMode.AuthorizationOnly,
                    session
                );

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        validationExecutor.ExecutedCommands.Should().ContainSingle();
        validationExecutor.ExecutedCommands[0].CommandText.Should().Contain("SchoolWithATag");
    }

    [Test]
    public async Task It_probes_only_the_views_the_failing_command_actually_emitted()
    {
        // The self-basis view is configured after the early one and carries no statement, so this command
        // never touched it. Probing it anyway could blame an unrelated view for the failure.
        var request = CreateRequest(
            CreateSelfBasisPlan(("SchoolWithASelfTag", 2), ("SchoolWithAnEarlyTag", 0)),
            resolveToCreate: true
        );
        var session = new ScriptedWriteSession(new FakeDbException("relation does not exist", "42P01"));
        var validationExecutor = new StubValidationCommandExecutor();

        var act = async () =>
            await CreateSut(
                    new StubProviderFailureExtractor("42P01", "relation does not exist"),
                    validationExecutor
                )
                .ResolveAsync(
                    request,
                    CreateMergeResult(request),
                    RelationalWriteSecondCommandMode.AuthorizationOnly,
                    session
                );

        await act.Should().ThrowAsync<DbException>();
        var probed = validationExecutor.ExecutedCommands.Should().ContainSingle().Subject.CommandText;
        probed.Should().Contain("SchoolWithAnEarlyTag").And.NotContain("SchoolWithASelfTag");
    }

    [Test]
    public async Task It_leaves_a_non_authorization_failure_alone_when_the_emitted_views_are_valid()
    {
        // A constraint violation must not be relabelled as a security configuration error just because a
        // custom-view statement shared the command.
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var originalFailure = new FakeDbException("duplicate key value", "23505");
        var session = new ScriptedWriteSession(originalFailure);
        var validationExecutor = new StubValidationCommandExecutor();

        var act = async () =>
            await CreateSut(
                    new StubProviderFailureExtractor("23505", "duplicate key value"),
                    validationExecutor
                )
                .ResolveAsync(
                    request,
                    CreateMergeResult(request),
                    RelationalWriteSecondCommandMode.AuthorizationOnly,
                    session
                );

        (await act.Should().ThrowAsync<DbException>()).Which.Should().BeSameAs(originalFailure);
        validationExecutor.ExecutedCommands.Should().ContainSingle();
    }

    [Test]
    public async Task It_does_not_probe_the_views_for_a_recognized_custom_view_denial()
    {
        // A cv1 payload already names the failing check, so probing would be a wasted round trip.
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(CreateAuth1Failure());
        var validationExecutor = new StubValidationCommandExecutor();

        await CreateSut(CustomViewFailureExtractor(0), validationExecutor)
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        validationExecutor.ExecutedCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_probe_the_views_for_another_families_authorization_payload()
    {
        // A namespace denial shares the command with a custom-view statement. It is a recognized authorization
        // answer, so it belongs to the namespace mapper: probing the views here would be a wasted round trip,
        // and a view that happened to be broken would replace the denial the caller must see with a 500.
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)), withNamespace: true);
        var session = new ScriptedWriteSession(CreateAuth1Failure());
        var validationExecutor = new StubValidationCommandExecutor(
            new FakeDbException("missing authorization view", "42P01")
        );

        var resolution = await CreateSut(NamespaceFailureExtractor(), validationExecutor)
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>();
        validationExecutor.ExecutedCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_emits_the_proposed_custom_view_check_before_the_document_and_resource_dml()
    {
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(CreateDmlReader(Authorized(), Sentinel(1), Scalar(77L)));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateChangedMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        var commandText = session.Commands.Should().ContainSingle().Subject.CommandText;
        var checkPosition = commandText.IndexOf("SchoolWithATag", StringComparison.Ordinal);
        var updatePosition = commandText.IndexOf(
            "update edfi.\"School\"",
            StringComparison.OrdinalIgnoreCase
        );

        checkPosition.Should().BePositive();
        updatePosition.Should().BePositive();
        checkPosition.Should().BeLessThan(updatePosition);
    }

    [Test]
    public async Task It_issues_no_dml_when_a_custom_view_run_placed_in_an_earlier_command_denies()
    {
        // A budget of three fits the root row's own parameters but not the check alongside them, so the check
        // becomes its own earlier command. Its denial has to stop the write before any row is touched.
        var request = CreateRequest(CreateProposedOnlyPlan(("SchoolWithATag", 0)));
        var session = new ScriptedWriteSession(CreateAuth1Failure());

        var resolution = await CreateSut(
                CustomViewFailureExtractor(0),
                commandBudget: new RelationalCommandBudget(3, 1000)
            )
            .ResolveAsync(
                request,
                CreateChangedMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        FailureOf(resolution).StrategyName.Should().Be("SchoolWithATag");
        resolution.PersistResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
        session
            .Commands[0]
            .CommandText.Should()
            .NotContainEquivalentOf("update edfi.\"School\"")
            .And.NotContainEquivalentOf("insert into edfi.\"School\"");
    }

    private static CustomViewAuthorizationFailure FailureOf(
        RelationalWriteSecondCommandResolution resolution
    ) =>
        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;

    private static SingleRecordCustomViewAuthorizationCheckSpec Check(
        int index,
        CustomViewAuthorizationCheckValueSource valueSource,
        string strategyName,
        int rawConfiguredIndex,
        CustomViewAuthorizationCheckTarget target
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, rawConfiguredIndex),
            index,
            valueSource,
            new DbTableName(new DbSchemaName("auth"), strategyName),
            new DbColumnName("DocumentId"),
            [new ColumnPathStep(RootTable, new DbColumnName("DocumentId"), null, null)],
            target,
            new QualifiedResourceName("Ed-Fi", "School"),
            [$"{strategyName}Element"],
            $"You may need a {strategyName} hint."
        );

    private static CustomViewAuthorizationCheckTarget ProposedTarget(DbColumnName basisColumn) =>
        new CustomViewAuthorizationCheckTarget.Proposed(
            RootTable,
            new CustomViewAuthorizationProposedValueBinding(
                RootTable,
                basisColumn,
                basisColumn.Value,
                "cvBasis"
            )
        );

    /// <summary>A PUT plan: the stored check first, then its proposed pair, indexed request-wide.</summary>
    private static RelationalCustomViewAuthorization CreateStoredAndProposedPlan(
        string strategyName,
        int storedIndex
    ) =>
        new([
            Check(
                storedIndex,
                CustomViewAuthorizationCheckValueSource.Stored,
                strategyName,
                0,
                new CustomViewAuthorizationCheckTarget.Stored(RootTable, new DbColumnName("DocumentId"))
            ),
            Check(
                storedIndex + 1,
                CustomViewAuthorizationCheckValueSource.Proposed,
                strategyName,
                0,
                ProposedTarget(BasisColumn)
            ),
        ]);

    private static RelationalCustomViewAuthorization CreateProposedOnlyPlan(
        params (string StrategyName, int RawConfiguredIndex)[] strategies
    ) => CreateProposedOnlyPlanOn(BasisColumn, strategies);

    private static RelationalCustomViewAuthorization CreateProposedOnlyPlanOn(
        DbColumnName basisColumn,
        params (string StrategyName, int RawConfiguredIndex)[] strategies
    ) =>
        new([
            .. strategies.Select(
                (strategy, index) =>
                    Check(
                        index,
                        CustomViewAuthorizationCheckValueSource.Proposed,
                        strategy.StrategyName,
                        strategy.RawConfiguredIndex,
                        ProposedTarget(basisColumn)
                    )
            ),
        ]);

    /// <summary>
    /// The first strategy is the self-basis one; any others are ordinary proposed checks, so the fixture can
    /// place a decidable view before or after the self-basis position.
    /// </summary>
    private static RelationalCustomViewAuthorization CreateSelfBasisPlan(
        params (string StrategyName, int RawConfiguredIndex)[] strategies
    ) =>
        new([
            .. strategies.Select(
                (strategy, index) =>
                    Check(
                        index,
                        CustomViewAuthorizationCheckValueSource.Proposed,
                        strategy.StrategyName,
                        strategy.RawConfiguredIndex,
                        index == 0
                            ? new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(RootTable)
                            : ProposedTarget(BasisColumn)
                    )
            ),
        ]);

    private static RelationalCustomViewAuthorization CreateSelfBasisPlanWithStoredPair(string strategyName) =>
        new([
            Check(
                0,
                CustomViewAuthorizationCheckValueSource.Stored,
                strategyName,
                0,
                new CustomViewAuthorizationCheckTarget.Stored(RootTable, new DbColumnName("DocumentId"))
            ),
            Check(
                1,
                CustomViewAuthorizationCheckValueSource.Proposed,
                strategyName,
                0,
                new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(RootTable)
            ),
        ]);

    private static CompositeRelationalWriteSecondCommand CreateSut(
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
        IRelationalCommandExecutor? customViewValidationCommandExecutor = null,
        RelationalCommandBudget? commandBudget = null
    ) =>
        new(
            relationalParameterConfigurator: null,
            providerFailureExtractor,
            commandBudget: commandBudget,
            customViewValidationCommandExecutor: customViewValidationCommandExecutor
        );

    private static StubProviderFailureExtractor CustomViewFailureExtractor(
        int index,
        CustomViewAuthorizationAuth1FailureKind failureKind =
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
    ) =>
        new(
            CustomViewAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(
                new CustomViewAuthorizationAuth1FailurePayload(index, failureKind)
            )
        );

    private static StubProviderFailureExtractor NamespaceFailureExtractor() =>
        new(
            NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    0,
                    NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing
                )
            )
        );

    private static RelationalWriteExecutorRequest CreateRequest(
        RelationalCustomViewAuthorization customViewAuthorization,
        bool withNamespace = false,
        bool resolveToCreate = false,
        DbColumnName? basisColumn = null,
        DbColumnName? namespaceColumn = null
    )
    {
        var rootPlan = Given_Default_Relational_Write_Executor.CreateRootPlan();
        var resourceModel = Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(
            rootPlan.TableModel
        );
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var mappingSet = Given_Default_Relational_Write_Executor.CreateMappingSet(
            resourceModel,
            [rootPlan],
            SqlDialect.Pgsql
        );
        var input = new RelationalWriteExecutorInput(
            mappingSet,
            resolveToCreate ? RelationalWriteOperationKind.Post : RelationalWriteOperationKind.Put,
            resolveToCreate
                ? new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
                    CreatedDocumentUuid
                )
                : new RelationalWriteTargetRequest.Put(ExistingDocumentUuid),
            writePlan,
            Given_Default_Relational_Write_Executor.CreateReadPlan(resourceModel, SqlDialect.Pgsql),
            JsonNode.Parse("""{"schoolId":255901,"name":"uri://ed-fi.org/Survey"}""")!,
            allowIdentityUpdates: false,
            new TraceId("composite-proposed-custom-view-test"),
            new ReferenceResolverRequest(mappingSet, resourceModel.Resource, [], [])
        )
        {
            CustomViewAuthorization = basisColumn is { } unboundBasisColumn
                ? CreateProposedOnlyPlanOn(unboundBasisColumn, ("SchoolWithATag", 0))
                : customViewAuthorization,
        };

        if (withNamespace)
        {
            input = input with
            {
                ProposedNamespaceAuthorization = new RelationalWriteNamespaceAuthorization(
                    [
                        new NamespaceAuthorizationCheckSpec(
                            1,
                            NamespaceAuthorizationCheckValueSource.Proposed,
                            rootPlan.TableModel.Table,
                            namespaceColumn ?? new DbColumnName("Name")
                        ),
                    ],
                    NamespacePrefixParameterizationFactory.Create(
                        SqlDialect.Pgsql,
                        ["uri://ed-fi.org/"],
                        "namespacePrefixes"
                    )
                ),
            };
        }

        return input.Resolve(
            resolveToCreate
                ? new RelationalWriteTargetContext.CreateNew(CreatedDocumentUuid)
                : new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L)
        );
    }

    private static RelationalWriteMergeResult CreateMergeResult(RelationalWriteExecutorRequest request) =>
        Given_Default_Relational_Write_Executor.CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "uri://ed-fi.org/Survey",
            mergedName: "uri://ed-fi.org/Survey"
        );

    private static FakeDbException CreateAuth1Failure() => new("custom view denial", "P0001");

    /// <summary>A root row whose merged Name differs from the stored one, so DML mode has a row to write.</summary>
    private static RelationalWriteMergeResult CreateChangedMergeResult(
        RelationalWriteExecutorRequest request
    ) =>
        Given_Default_Relational_Write_Executor.CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "uri://ed-fi.org/Survey",
            mergedName: "uri://ed-fi.org/Renamed"
        );

    private static DbDataReader CreateDmlReader(params IReadOnlyList<object?[]>[] resultSets) =>
        new ScriptedDbDataReader(
            resultSets,
            [.. resultSets.Select(static _ => new[] { "AuthorizationResult" })]
        );

    private static IReadOnlyList<object?[]> Authorized() =>
        [
            [1],
        ];

    private static IReadOnlyList<object?[]> Sentinel(int ordinal) =>
        [
            [ordinal],
        ];

    private static IReadOnlyList<object?[]> Scalar(object? value) =>
        [
            [value],
        ];

    /// <summary>
    /// Stands in for the fresh-connection executor the validation probe uses, recording what it was asked to
    /// run and optionally failing the way a missing view would.
    /// </summary>
    private sealed class StubValidationCommandExecutor(DbException? failure = null)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public List<RelationalCommand> ExecutedCommands { get; } = [];

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ExecutedCommands.Add(command);

            return failure is null
                ? Task.FromResult(default(TResult)!)
                : Task.FromException<TResult>(failure);
        }
    }

    private static DbDataReader CreateReader(params DataTable[] tables) => new DataTableReader(tables);

    private static DataTable CreateAuthorizedTable()
    {
        var table = new DataTable();
        table.Columns.Add("AuthorizationResult", typeof(int));
        table.Rows.Add(1);

        return table;
    }

    private sealed class StubProviderFailureExtractor(string? providerErrorCode, string providerMessage)
        : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new RelationshipAuthorizationProviderFailure(providerErrorCode, providerMessage);
        }
    }

    private sealed class FakeDbException(string message, string sqlState) : DbException(message)
    {
        public override string SqlState => sqlState;
    }
}
