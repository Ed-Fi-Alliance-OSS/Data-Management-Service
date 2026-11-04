// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Composite;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Owns the authorization-only second command's observable behavior: how many commands it issues, the
/// order its statements are emitted in, when it issues none, and how a provider AUTH1 failure maps back
/// to a caller-visible result. Executor orchestration tests assert precedence through a sequential seam;
/// everything the real composite command decides is asserted here.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Composite_Relational_Write_Proposed_Authorization
{
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );

    [Test]
    public async Task It_co_batches_the_namespace_and_relationship_checks_into_one_command()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var session = new ScriptedWriteSession(CreateAuthorizedReader(namespaceCheckCount: 1));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_emits_the_namespace_statement_before_the_relationship_statement()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var session = new ScriptedWriteSession(CreateAuthorizedReader(namespaceCheckCount: 1));

        await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // The command aborts at its first AUTH1, so emission order *is* the denial order the caller
        // observes. The allocator stamps each parameter with its statement's ordinal, which is how the
        // order is proven here without matching on compiled SQL.
        var command = session.Commands.Should().ContainSingle().Subject;
        var namespaceParameter = command
            .Parameters.Should()
            .Contain(parameter =>
                parameter.Name.Contains("namespacePrefixes_s0", StringComparison.OrdinalIgnoreCase)
            )
            .Which;
        var relationshipParameter = command
            .Parameters.Should()
            .Contain(parameter =>
                parameter.Name.Contains(
                    "ClaimEducationOrganizationIds_s1",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Which;
        command
            .CommandText.IndexOf(namespaceParameter.Name.TrimStart('@'), StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                command.CommandText.IndexOf(
                    relationshipParameter.Name.TrimStart('@'),
                    StringComparison.Ordinal
                )
            );
    }

    [Test]
    public async Task It_issues_one_command_for_a_namespace_check_alone()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: false);
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
        session.Commands[0].Parameters.Should().OnlyContain(parameter => parameter.Name.Contains("_s0"));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Rewritten_Proposed_Namespace_Parameter
    {
        private IReadOnlyList<RelationalParameter> _proposedParameters = null!;
        private NpgsqlParameter _configuredProposed = null!;

        [SetUp]
        public async Task Setup()
        {
            var request = CreateRequest(withNamespace: true, withRelationship: false);
            var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

            await CreateSut()
                .ResolveAsync(
                    request,
                    CreateMergeResult(request),
                    RelationalWriteSecondCommandMode.AuthorizationOnly,
                    session
                );

            _proposedParameters =
            [
                .. session
                    .Commands.SelectMany(static command => command.Parameters)
                    .Where(static parameter =>
                        parameter.Name.Contains("proposedNamespace_s0", StringComparison.Ordinal)
                    ),
            ];
            _configuredProposed = new NpgsqlParameter { Value = DBNull.Value };

            if (_proposedParameters.Count == 1)
            {
                _proposedParameters[0].ConfigureParameter?.Invoke(_configuredProposed);
            }
        }

        [Test]
        public void It_sends_one_proposed_namespace_parameter()
        {
            _proposedParameters.Should().ContainSingle();
        }

        [Test]
        public void It_keeps_the_string_type_on_the_rewritten_parameter()
        {
            _configuredProposed.DbType.Should().Be(DbType.String);
        }
    }

    [Test]
    public async Task It_issues_one_command_for_a_relationship_check_alone()
    {
        var request = CreateRequest(withNamespace: false, withRelationship: true);
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
    }

    [Test]
    public async Task It_issues_no_command_when_no_proposed_authorization_is_configured()
    {
        var request = CreateRequest(withNamespace: false, withRelationship: false);
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
    public async Task It_enriches_the_merge_result_with_the_extracted_runtime_check()
    {
        var request = CreateRequest(withNamespace: false, withRelationship: true);
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // The DML path reads the runtime check off the merge result, so the phase must publish it even
        // though this path issues no DML of its own.
        resolution.MergeResult.ProposedRelationshipAuthorizationRuntimeCheck.Should().NotBeNull();
    }

    [Test]
    public async Task It_defers_a_no_claims_denial_until_after_the_namespace_command_authorizes()
    {
        var baseRequest = CreateRequest(withNamespace: true, withRelationship: true);
        var request = baseRequest with
        {
            ProposedRelationshipAuthorization =
                Given_Default_Relational_Write_Executor.CreateProposedNoClaimsAuthorization(baseRequest),
        };
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // Namespace AND-composes before the relationship OR-group, so the namespace statement still has
        // to run and win if it denies; the no-claims denial needs no statement of its own.
        session.Commands.Should().ContainSingle();
        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>();
    }

    [Test]
    public async Task It_sends_nothing_when_the_namespace_plan_cannot_be_reconciled_with_the_root_row()
    {
        var baseRequest = CreateRequest(withNamespace: false, withRelationship: true);
        var request = baseRequest with
        {
            ProposedNamespaceAuthorization = new RelationalWriteNamespaceAuthorization(
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Proposed,
                        new DbTableName(new DbSchemaName("edfi"), "NotThisResource"),
                        new DbColumnName("Namespace")
                    ),
                ],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            ),
        };
        var session = new ScriptedWriteSession();

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // Decided in process before any statement is built, so the relationship check is never sent
        // either: an unreconcilable plan fails closed rather than authorizing on a partial command.
        session.Commands.Should().BeEmpty();
        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>();
    }

    [Test]
    public async Task It_runs_a_structured_claim_relationship_check_as_an_ordered_segment()
    {
        var request = CreateRequest(
            withNamespace: true,
            withRelationship: true,
            dialect: SqlDialect.Mssql,
            claimEducationOrganizationIds: CreateStructuredClaimIds()
        );
        var session = new ScriptedWriteSession(
            CreateReader(CreateAuthorizedTable()),
            CreateReader(CreateAuthorizedTable())
        );

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // A table-valued claim list cannot be renamed into a co-batched statement, so it runs as its own
        // command on the same session and transaction. This is the recorded deviation from the
        // one-command target, not a silent extra round trip.
        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().HaveCount(2);
    }

    [Test]
    public async Task It_runs_a_relationship_check_that_does_not_fit_the_remaining_budget_as_an_ordered_segment()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var session = new ScriptedWriteSession(
            CreateReader(CreateAuthorizedTable()),
            CreateReader(CreateAuthorizedTable())
        );

        var resolution = await CreateSut(commandBudget: ExactlyTheNamespaceBudget)
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // Each statement fits a command of its own, but not the same one. Feasibility has to be measured
        // against what the namespace statement already consumed, not against the whole command budget:
        // co-batching both anyway overflows the command the builder is still assembling.
        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().HaveCount(2);
        session
            .Commands[0]
            .Parameters.Should()
            .Contain(parameter =>
                parameter.Name.Contains("namespacePrefixes_s0", StringComparison.OrdinalIgnoreCase)
            )
            .And.NotContain(parameter =>
                parameter.Name.Contains("ClaimEducationOrganizationIds", StringComparison.OrdinalIgnoreCase)
            );
        session
            .Commands[1]
            .Parameters.Should()
            .Contain(parameter =>
                parameter.Name.Contains("ClaimEducationOrganizationIds", StringComparison.OrdinalIgnoreCase)
            );
    }

    [Test]
    public async Task It_issues_no_relationship_segment_when_the_namespace_command_denies()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        var session = new ScriptedWriteSession(new FakeDbException("AUTH1", "AUTH1"));

        var resolution = await CreateSut(
                new StubProviderFailureExtractor("AUTH1", payload),
                ExactlyTheNamespaceBudget
            )
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        // Splitting the checks across two commands must not cost the namespace its precedence: its
        // command runs first, and a denial there means the relationship segment is never sent.
        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>();
        session.Commands.Should().ContainSingle();
    }

    /// <summary>
    /// The exact boundary: this fixture's one-prefix proposed namespace statement binds two parameters and
    /// its proposed relationship statement binds two, so a two-parameter command holds either alone and
    /// neither pair. Both counts are what the arrangement actually emits, so a change to it moves this
    /// boundary rather than silently making the case unreachable.
    /// </summary>
    private static readonly RelationalCommandBudget ExactlyTheNamespaceBudget = new(2, 1000);

    [Test]
    public async Task It_maps_a_namespace_auth1_failure_to_the_namespace_denial()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        var session = new ScriptedWriteSession(new FakeDbException("AUTH1", "AUTH1"));

        var resolution = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        var notAuthorized = resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
    }

    [Test]
    public async Task It_maps_a_relationship_auth1_failure_to_the_authorization_exception()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                    RelationalWriteOperationKind.Put
                ),
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );
        var session = new ScriptedWriteSession(new FakeDbException("AUTH1", "AUTH1"));
        var sut = CreateSut(new StubProviderFailureExtractor("AUTH1", payload));

        // The relationship denial is raised as the exception the executor already maps to a result, so
        // the mapping stays identical to the standalone and insert-prefixed emission sites.
        var act = async () =>
            await sut.ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        await act.Should().ThrowAsync<RelationalWriteRelationshipAuthorizationNotAuthorizedException>();
    }

    [Test]
    public async Task It_lets_a_failure_that_is_not_an_authorization_denial_propagate_unchanged()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var providerFailure = new FakeDbException("duplicate key", "23505");
        var session = new ScriptedWriteSession(providerFailure);
        var sut = CreateSut(new StubProviderFailureExtractor("23505", "duplicate key"));

        var act = async () =>
            await sut.ResolveAsync(
                request,
                CreateMergeResult(request),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        (await act.Should().ThrowAsync<FakeDbException>()).Which.Should().BeSameAs(providerFailure);
    }

    [TestCase(SqlDialect.Pgsql, true, RelationshipAuthorizationSubjectFailureKind.NoRelationship)]
    [TestCase(SqlDialect.Pgsql, false, RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing)]
    [TestCase(SqlDialect.Mssql, true, RelationshipAuthorizationSubjectFailureKind.NoRelationship)]
    [TestCase(SqlDialect.Mssql, false, RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing)]
    public async Task It_binds_the_submitted_edorg_identity_before_mapping_a_deferred_reference_denial(
        SqlDialect dialect,
        bool schoolReferenceWasDeferred,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        var request = CreateDeferredSchoolReferenceRequest(dialect);
        if (!schoolReferenceWasDeferred)
        {
            request = request with { SelectedBody = new JsonObject() };
        }
        var session = new ScriptedWriteSession(new FakeDbException("AUTH1", "AUTH1")) { Dialect = dialect };
        var sut = CreateSut(
            CreateProposedFailureExtractor(
                dialect,
                schoolReferenceWasDeferred
                    ? RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    : RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
            )
        );

        var act = async () =>
            await sut.ResolveAsync(
                request,
                CreateDeferredSchoolReferenceMergeResult(request, schoolReferenceWasDeferred),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        var failure = (
            await act.Should().ThrowAsync<RelationalWriteRelationshipAuthorizationNotAuthorizedException>()
        )
            .Which
            .RelationshipFailure;
        failure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Proposed);
        var failedStrategy = failure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy.ConfiguredStrategyIndex.Should().Be(0);
        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.SubjectIndex.Should().Be(0);
        failedSubject.FailureKind.Should().Be(expectedFailureKind);
        failedSubject.RootBinding.ColumnName.Should().Be("SchoolId");
        failedSubject.SecurableElements.Should().ContainSingle().Which.ReadableName.Should().Be("SchoolId");
        session
            .Commands.Should()
            .ContainSingle()
            .Which.Parameters.Should()
            .ContainSingle(parameter =>
                parameter.Name.Contains("relationshipAuthorization_0_0_SchoolId", StringComparison.Ordinal)
            )
            .Which.Value.Should()
            .Be(schoolReferenceWasDeferred ? 999 : null);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_does_not_correct_a_malformed_auth1_payload(SqlDialect dialect)
    {
        var request = CreateDeferredSchoolReferenceRequest(dialect);
        var session = new ScriptedWriteSession(new FakeDbException("AUTH1", "AUTH1")) { Dialect = dialect };
        var sut = CreateSut(
            dialect is SqlDialect.Mssql
                ? new StubProviderFailureExtractor("50000", "AUTH1 - 1|9|x|not-a-payload")
                : new StubProviderFailureExtractor("AUTH1", "1|9|x|not-a-payload")
        );

        var act = async () =>
            await sut.ResolveAsync(
                request,
                CreateDeferredSchoolReferenceMergeResult(request, schoolReferenceWasDeferred: true),
                RelationalWriteSecondCommandMode.AuthorizationOnly,
                session
            );

        await act.Should().ThrowAsync<RelationalWriteInvalidRelationshipAuthorizationFailureException>();
    }

    [Test]
    public void It_corrects_the_standalone_mapping_boundary_without_changing_the_authorization_command()
    {
        var request = CreateDeferredSchoolReferenceRequest(SqlDialect.Pgsql);
        var unresolvedCheck = ExtractDeferredSchoolReferenceRuntimeCheck(
            request,
            schoolReferenceWasDeferred: true
        );
        var absentCheck = ExtractDeferredSchoolReferenceRuntimeCheck(
            request,
            schoolReferenceWasDeferred: false
        );

        ProposedRelationshipAuthorizationCommand
            .TryMapFailure(
                SqlDialect.Pgsql,
                CreateProposedFailureExtractor(SqlDialect.Pgsql),
                unresolvedCheck,
                new FakeDbException("AUTH1", "AUTH1"),
                out var relationshipFailure,
                out var invalidFailureDiagnostic
            )
            .Should()
            .BeTrue();
        invalidFailureDiagnostic.Should().BeNull();
        relationshipFailure!
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);

        // Provenance is request-local runtime state: the emitted statement and its parameter values are
        // the same whether the null subject came from an unresolved reference or genuine absence.
        var unresolvedCommand = ProposedRelationshipAuthorizationCommand.Build(
            request.MappingSet,
            request.WritePlan,
            unresolvedCheck,
            new DefaultRelationalParameterConfigurator()
        );
        var absentCommand = ProposedRelationshipAuthorizationCommand.Build(
            request.MappingSet,
            request.WritePlan,
            absentCheck,
            new DefaultRelationalParameterConfigurator()
        );

        unresolvedCommand.CommandText.Should().Be(absentCommand.CommandText);
        unresolvedCommand
            .Parameters.Select(static parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(absentCommand.Parameters.Select(static parameter => (parameter.Name, parameter.Value)));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Namespace_Only_Failure_That_Is_Not_An_Authorization_Denial
    {
        private FakeDbException _providerFailure = null!;
        private Exception? _thrown;

        [SetUp]
        public async Task Setup()
        {
            var request = CreateRequest(withNamespace: true, withRelationship: false);
            _providerFailure = new FakeDbException("duplicate key", "23505");
            var session = new ScriptedWriteSession(_providerFailure);
            var sut = CreateSut(new StubProviderFailureExtractor("23505", "duplicate key"));

            try
            {
                await sut.ResolveAsync(
                    request,
                    CreateMergeResult(request),
                    RelationalWriteSecondCommandMode.AuthorizationOnly,
                    session
                );
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }
        }

        [Test]
        public void It_rethrows_the_provider_failure_itself()
        {
            _thrown.Should().BeSameAs(_providerFailure);
        }

        [Test]
        public void It_keeps_the_stack_from_where_the_command_failed()
        {
            (_thrown?.StackTrace).Should().Contain("ScriptedDbCommand.ExecuteDbDataReader");
        }
    }

    private static CompositeRelationalWriteSecondCommand CreateSut(
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
        RelationalCommandBudget? commandBudget = null
    ) => new(relationalParameterConfigurator: null, providerFailureExtractor, commandBudget: commandBudget);

    /// <summary>
    /// Enough claims that the SQL Server parameterization becomes a table-valued parameter rather than a
    /// scalar list. The arrangement asserts that it did, so a change to the factory's threshold surfaces
    /// here rather than silently turning this into a co-batched case.
    /// </summary>
    private static long[] CreateStructuredClaimIds() =>
        [.. Enumerable.Range(1, 2000).Select(static id => (long)id)];

    private static RelationalWriteExecutorRequest CreateRequest(
        bool withNamespace,
        bool withRelationship,
        SqlDialect dialect = SqlDialect.Pgsql,
        long[]? claimEducationOrganizationIds = null
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
            dialect
        );
        var input = new RelationalWriteExecutorInput(
            mappingSet,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(ExistingDocumentUuid),
            writePlan,
            Given_Default_Relational_Write_Executor.CreateReadPlan(resourceModel, dialect),
            JsonNode.Parse("""{"schoolId":255901,"name":"uri://ed-fi.org/Survey"}""")!,
            allowIdentityUpdates: false,
            new TraceId("composite-proposed-authorization-test"),
            new ReferenceResolverRequest(mappingSet, resourceModel.Resource, [], [])
        );

        if (withNamespace)
        {
            input = input with
            {
                ProposedNamespaceAuthorization = CreateProposedNamespaceAuthorization(rootPlan, dialect),
            };
        }

        if (withRelationship)
        {
            input = input with
            {
                ProposedRelationshipAuthorization =
                    Given_Default_Relational_Write_Executor.CreateProposedSchoolIdRelationshipAuthorization(
                        input,
                        claimEducationOrganizationIds
                    ),
            };
        }

        return input.Resolve(
            new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L)
        );
    }

    /// <summary>
    /// Authorizes the root table's <c>Name</c> column as the namespace value, so one root row feeds both
    /// the namespace check and the relationship check's proposed <c>SchoolId</c>.
    /// </summary>
    private static RelationalWriteNamespaceAuthorization CreateProposedNamespaceAuthorization(
        TableWritePlan rootPlan,
        SqlDialect dialect
    ) =>
        new(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Proposed,
                    rootPlan.TableModel.Table,
                    new DbColumnName("Name")
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(dialect, ["uri://ed-fi.org/"], "namespacePrefixes")
        );

    private static RelationalWriteMergeResult CreateMergeResult(RelationalWriteExecutorRequest request) =>
        Given_Default_Relational_Write_Executor.CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "uri://ed-fi.org/Survey",
            mergedName: "uri://ed-fi.org/Survey"
        );

    private const string DeferredSchoolReferencePath = "$.schoolReference";

    /// <summary>
    /// A PUT whose proposed <c>SchoolId</c> subject derives from the root <c>schoolReference</c>, so a null
    /// finalized value is either a genuinely absent reference or a submitted one that could not be resolved.
    /// </summary>
    private static RelationalWriteExecutorRequest CreateDeferredSchoolReferenceRequest(SqlDialect dialect)
    {
        var scalarRootPlan = Given_Default_Relational_Write_Executor.CreateRootPlan();
        var schoolReferencePath = new JsonPathExpression(
            DeferredSchoolReferencePath,
            [new JsonPathSegment.Property("schoolReference")]
        );
        var rootPlan = scalarRootPlan with
        {
            ColumnBindings =
            [
                scalarRootPlan.ColumnBindings[0],
                scalarRootPlan.ColumnBindings[1] with
                {
                    Column = scalarRootPlan.ColumnBindings[1].Column with
                    {
                        SourceJsonPath = new JsonPathExpression(
                            "$.schoolReference.schoolId",
                            [
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolId"),
                            ]
                        ),
                    },
                    Source = new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            schoolReferencePath,
                            new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                            new JsonPathExpression(
                                "$.schoolReference.schoolId",
                                [
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolId"),
                                ]
                            )
                        )
                    ),
                },
                scalarRootPlan.ColumnBindings[2],
            ],
        };
        var resourceModel = Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(
            rootPlan.TableModel
        ) with
        {
            DocumentReferenceBindings =
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    schoolReferencePath,
                    rootPlan.TableModel.Table,
                    new DbColumnName("School_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "School"),
                    []
                ),
            ],
        };
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var mappingSet = Given_Default_Relational_Write_Executor.CreateMappingSet(
            resourceModel,
            [rootPlan],
            dialect
        );
        var input = new RelationalWriteExecutorInput(
            mappingSet,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(ExistingDocumentUuid),
            writePlan,
            Given_Default_Relational_Write_Executor.CreateReadPlan(resourceModel, dialect),
            JsonNode.Parse("""{"schoolReference":{"schoolId":999},"name":"Lincoln High"}""")!,
            allowIdentityUpdates: false,
            new TraceId("composite-proposed-authorization-deferred-reference-test"),
            new ReferenceResolverRequest(mappingSet, resourceModel.Resource, [], [])
        );
        input = input with
        {
            ProposedRelationshipAuthorization =
                Given_Default_Relational_Write_Executor.CreateProposedSchoolIdRelationshipAuthorization(
                    input
                ),
        };

        return input.Resolve(
            new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L)
        );
    }

    private static RelationalWriteMergeResult CreateDeferredSchoolReferenceMergeResult(
        RelationalWriteExecutorRequest request,
        bool schoolReferenceWasDeferred
    ) =>
        new(
            [
                new RelationalWriteMergedTableState(
                    request.WritePlan.TablePlansInDependencyOrder[0],
                    [CreateSchoolRow(255901)],
                    [CreateSchoolRow(null)]
                ),
            ],
            supportsGuardedNoOp: true
        )
        {
            DeferredMissingDocumentReferencePaths = new HashSet<string>(
                schoolReferenceWasDeferred ? [DeferredSchoolReferencePath] : [],
                StringComparer.Ordinal
            ),
        };

    private static RelationalWriteMergedTableRow CreateSchoolRow(int? schoolId)
    {
        FlattenedWriteValue[] values =
        [
            new FlattenedWriteValue.Literal(345L),
            new FlattenedWriteValue.Literal(schoolId),
            new FlattenedWriteValue.Literal("Lincoln High"),
        ];

        return new RelationalWriteMergedTableRow(values, values);
    }

    private static ProposedRelationshipAuthorizationRuntimeCheck ExtractDeferredSchoolReferenceRuntimeCheck(
        RelationalWriteExecutorRequest request,
        bool schoolReferenceWasDeferred
    )
    {
        var mergeResult = CreateDeferredSchoolReferenceMergeResult(request, schoolReferenceWasDeferred);
        var extraction = RelationshipAuthorizationProposedValueExtractor.Extract(
            (RelationshipAuthorizationResult.Authorized)request.ProposedRelationshipAuthorization!,
            request.WritePlan,
            RelationalWriteFinalizedRootRow.Build(request, mergeResult),
            RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(request.OperationKind),
            request.TargetContext,
            mergeResult.DeferredMissingDocumentReferencePaths
        );

        return extraction
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck;
    }

    /// <summary>
    /// An AUTH1 subject failure in each provider's transport shape:
    /// PostgreSQL carries the payload as the message under the AUTH1 SQLSTATE; SQL Server embeds it after
    /// the marker in a raised error's message.
    /// </summary>
    private static StubProviderFailureExtractor CreateProposedFailureExtractor(
        SqlDialect dialect,
        RelationshipAuthorizationAuth1SubjectFailureKind failureKind =
            RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
    )
    {
        var payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                    RelationalWriteOperationKind.Put
                ),
                [new RelationshipAuthorizationAuth1SubjectFailure(0, 0, failureKind)]
            )
        );

        return dialect is SqlDialect.Mssql
            ? new StubProviderFailureExtractor("50000", $"AUTH1 - {payload}")
            : new StubProviderFailureExtractor("AUTH1", payload);
    }

    private static DbDataReader CreateAuthorizedReader(int namespaceCheckCount)
    {
        List<DataTable> tables = [];

        for (var checkIndex = 0; checkIndex < namespaceCheckCount; checkIndex++)
        {
            tables.Add(CreateAuthorizedTable());
        }

        tables.Add(CreateAuthorizedTable());

        return CreateReader([.. tables]);
    }

    private static DbDataReader CreateReader(params DataTable[] tables) => new DataTableReader(tables);

    /// <summary>
    /// One authorizing check's result set. Both statement kinds project the same constant row; a denial
    /// aborts the command instead, so the row's contents carry no information.
    /// </summary>
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
