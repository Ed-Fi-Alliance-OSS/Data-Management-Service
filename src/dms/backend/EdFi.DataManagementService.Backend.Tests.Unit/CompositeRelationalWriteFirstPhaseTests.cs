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
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_The_Composite_Relational_Write_First_Phase
{
    private static readonly DocumentUuid CandidateDocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );

    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );

    [Test]
    public async Task It_resolves_post_create_from_one_vacuous_composite_command()
    {
        var input = CreateInput(RelationalWriteOperationKind.Post);
        var session = new ScriptedWriteSession(CreateCompositeReader(target: null));

        var resolution = await CreateSut().ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        resolution
            .Outcome!.ExecutionRequest.TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.CreateNew>();
        resolution.Outcome.LockedTarget.Should().BeNull();
        resolution.Outcome.CurrentState.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public async Task It_resolves_an_existing_target_and_hydrates_the_captured_content_version(
        RelationalWriteOperationKind operationKind
    )
    {
        var input = CreateInput(operationKind);
        var session = new ScriptedWriteSession(
            CreateCompositeReader(new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value))
        );

        var resolution = await CreateSut().ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        resolution
            .Outcome!.ExecutionRequest.TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.ExistingDocument>();
        resolution.Outcome.CurrentState!.DocumentMetadata.ContentVersion.Should().Be(44L);
        resolution.Outcome.LockedTarget!.DocumentId.Should().Be(345L);
        resolution.Outcome.LockedTarget.ObservedContentVersion.Should().Be(44L);
        resolution.Outcome.LockedTarget.IsHeldBy(session).Should().BeTrue();
        resolution.Outcome.LockedTarget.IsHeldBy(new ScriptedWriteSession()).Should().BeFalse();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_returns_missing_put_without_decoding_absent_hydration_as_current_state()
    {
        var input = CreateInput(RelationalWriteOperationKind.Put);
        var session = new ScriptedWriteSession(CreateCompositeReader(target: null));

        var resolution = await CreateSut().ResolveAsync(input, session);

        resolution
            .ImmediateResult.Should()
            .Be(new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists()));
        resolution.Outcome.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_selects_the_post_create_immediate_result_before_reference_or_hydration_execution()
    {
        var expected = RelationalWriteExecutorResults.BuildUnknownFailureResult(
            RelationalWriteOperationKind.Post,
            "create plan denied"
        );
        var input = CreateInput(RelationalWriteOperationKind.Post) with
        {
            PostRelationshipAuthorizationPlans = CreatePostPlans(expected),
        };
        var adapterFactory = new TestReferenceResolverAdapterFactory
        {
            ExceptionToThrow = new FakeDbException("reference lookup must not execute"),
        };
        var session = new ScriptedWriteSession(CreateCaptureReader(target: null));

        var resolution = await CreateSut(adapterFactory).ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeSameAs(expected);
        resolution.Outcome.Should().BeNull();
        session.Commands.Should().ContainSingle();
        session.Commands[0].CommandText.Should().NotContain("current-state-hydration");
    }

    [Test]
    public async Task It_selects_the_existing_post_authorization_plan_after_capture()
    {
        var createImmediate = RelationalWriteExecutorResults.BuildUnknownFailureResult(
            RelationalWriteOperationKind.Post,
            "create branch only"
        );
        var input = CreateInput(RelationalWriteOperationKind.Post) with
        {
            PostRelationshipAuthorizationPlans = CreatePostPlans(createImmediate),
        };
        var target = new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value);
        var session = new ScriptedWriteSession(
            CreateCaptureReader(target),
            CreateReader(CreateDocumentMetadataTable(target, 44L), CreateRootTable(target))
        );

        var resolution = await CreateSut().ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        resolution
            .Outcome!.ExecutionRequest.TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.ExistingDocument>();
        resolution.Outcome.ExecutionRequest.PostRelationshipAuthorizationPlans.Should().BeNull();
        resolution
            .Outcome.ExecutionRequest.StoredRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.NoAuthorizationRequired>();
        session.Commands.Should().HaveCount(2);
    }

    [Test]
    public async Task It_keeps_stored_authorization_and_hydration_vacuous_after_an_absent_capture()
    {
        var input = CreateInput(RelationalWriteOperationKind.Post, includeReadPlan: false) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
        };
        var session = new ScriptedWriteSession(
            CreateReader(CreateCaptureTable(target: null), CreateAuthorizationTable())
        );

        var resolution = await CreateSut().ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        resolution
            .Outcome!.ExecutionRequest.TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.CreateNew>();
        resolution.Outcome.CurrentState.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_rejects_missing_hydration_metadata_for_a_captured_target()
    {
        var input = CreateInput(RelationalWriteOperationKind.Put);
        var session = new ScriptedWriteSession(
            CreateCompositeReader(
                new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value),
                includeMetadata: false
            )
        );

        Func<Task> act = async () => await CreateSut().ResolveAsync(input, session);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no metadata*345*");
    }

    [Test]
    public async Task It_rejects_hydration_content_version_misalignment()
    {
        var input = CreateInput(RelationalWriteOperationKind.Put);
        var session = new ScriptedWriteSession(
            CreateCompositeReader(
                new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value),
                hydratedContentVersion: 45L
            )
        );

        Func<Task> act = async () => await CreateSut().ResolveAsync(input, session);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*45*44*misaligned*");
    }

    [Test]
    public async Task It_preserves_provider_failures()
    {
        var expected = new FakeDbException("provider failed");
        var input = CreateInput(RelationalWriteOperationKind.Post);
        var session = new ScriptedWriteSession(expected);

        Func<Task> act = async () => await CreateSut().ResolveAsync(input, session);

        (await act.Should().ThrowAsync<FakeDbException>()).Which.Should().BeSameAs(expected);
    }

    [Test]
    public async Task It_propagates_cancellation()
    {
        var input = CreateInput(RelationalWriteOperationKind.Post);
        var session = new ScriptedWriteSession(CreateCompositeReader(target: null));
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        Func<Task> act = async () => await CreateSut().ResolveAsync(input, session, cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_maps_namespace_auth1_denial_before_deferred_relationship_denial()
    {
        var input = CreateInput(RelationalWriteOperationKind.Put, includeReadPlan: false) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
            StoredRelationshipAuthorization = new RelationshipAuthorizationResult.NoClaims([], []),
        };
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        var extractor = new TestProviderFailureExtractor(
            NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            payload
        );
        var session = new ScriptedWriteSession(
            CreateCaptureReader(new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value)),
            new FakeDbException("namespace denial")
        );

        var resolution = await CreateSut(providerFailureExtractor: extractor).ResolveAsync(input, session);

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>();
        session.Commands.Should().HaveCount(2);
        session.Commands[1].CommandText.Should().Contain("AUTH1");
    }

    [Test]
    public async Task It_maps_an_invalid_namespace_auth1_payload_to_security_configuration_failure()
    {
        var input = CreateInput(RelationalWriteOperationKind.Put, includeReadPlan: false) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
        };
        var extractor = new TestProviderFailureExtractor(
            NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            "ns1|9|m"
        );
        var session = new ScriptedWriteSession(new FakeDbException("invalid AUTH1 payload"));

        var resolution = await CreateSut(providerFailureExtractor: extractor).ResolveAsync(input, session);

        resolution
            .ImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>();
        resolution.Outcome.Should().BeNull();
    }

    [Test]
    public async Task It_binds_the_decoded_target_to_a_structured_relationship_fallback()
    {
        var input = CreateInput(
            RelationalWriteOperationKind.Put,
            includeReadPlan: true,
            dialect: SqlDialect.Mssql
        );
        input = input with
        {
            StoredRelationshipAuthorization =
                Given_Default_Relational_Write_Executor.CreateStoredSchoolIdRelationshipAuthorization(
                    input,
                    Enumerable.Range(1, 2000).Select(static value => (long)value).ToArray()
                ),
        };
        var target = new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value);
        var session = new ScriptedWriteSession(
            CreateCaptureReader(target),
            CreateReader(CreateStoredRelationshipAuthorizationTable(target)),
            CreateReader(CreateDocumentMetadataTable(target, 44L), CreateRootTable(target))
        );

        var resolution = await CreateSut().ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        resolution.Outcome!.LockedTarget!.IsHeldBy(session).Should().BeTrue();
        session.Commands.Should().HaveCount(3);
        session
            .Commands[1]
            .Parameters.Single(parameter =>
                parameter.Name.Equals("@DocumentId", StringComparison.OrdinalIgnoreCase)
            )
            .Value.Should()
            .Be(target.DocumentId);
        session.Commands[1].CommandText.Should().NotContain("@dms_composite_target_documentid");
    }

    [Test]
    public void It_accepts_an_exact_guarded_no_op_lock_proof_from_the_current_session()
    {
        var session = new ScriptedWriteSession();
        var proof = CreateLockProof(session, documentId: 345L, contentVersion: 44L);
        var target = new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L);

        Action act = () =>
            DefaultRelationalWriteExecutor.ValidateGuardedNoOpLockProof(proof, target, session);

        act.Should().NotThrow();
    }

    [Test]
    public void It_rejects_a_guarded_no_op_lock_proof_from_another_session()
    {
        var originatingSession = new ScriptedWriteSession();
        var currentSession = new ScriptedWriteSession();
        var proof = CreateLockProof(originatingSession, documentId: 345L, contentVersion: 44L);
        var target = new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L);

        Action act = () =>
            DefaultRelationalWriteExecutor.ValidateGuardedNoOpLockProof(proof, target, currentSession);

        act.Should().Throw<InvalidOperationException>().WithMessage("*current write session*");
    }

    [Test]
    public void It_rejects_a_missing_guarded_no_op_lock_proof()
    {
        var session = new ScriptedWriteSession();
        var target = new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L);

        Action act = () => DefaultRelationalWriteExecutor.ValidateGuardedNoOpLockProof(null, target, session);

        act.Should().Throw<InvalidOperationException>().WithMessage("*matching capture lock proof*");
    }

    [TestCase(346L, 44L)]
    [TestCase(345L, 45L)]
    public void It_rejects_a_guarded_no_op_lock_proof_that_disagrees_with_the_target(
        long documentId,
        long contentVersion
    )
    {
        var session = new ScriptedWriteSession();
        var proof = CreateLockProof(session, documentId, contentVersion);
        var target = new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L);

        Action act = () =>
            DefaultRelationalWriteExecutor.ValidateGuardedNoOpLockProof(proof, target, session);

        act.Should().Throw<InvalidOperationException>().WithMessage("*matching capture lock proof*");
    }

    [TestCase(3, 1)]
    [TestCase(2, 3)]
    public async Task It_preflights_exact_fit_and_one_over_budget_before_selecting_a_segment(
        int parameterBudget,
        int expectedCommandCount
    )
    {
        var input = CreateInput(RelationalWriteOperationKind.Post) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
        };
        var target = new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value);
        var scripts =
            parameterBudget == 3
                ? new object[]
                {
                    CreateReader(
                        CreateCaptureTable(target),
                        CreateAuthorizationTable(),
                        CreateDocumentMetadataTable(target, 44L),
                        CreateRootTable(target)
                    ),
                }
                :
                [
                    CreateCaptureReader(target),
                    CreateReader(CreateAuthorizationTable()),
                    CreateReader(CreateDocumentMetadataTable(target, 44L), CreateRootTable(target)),
                ];
        var session = new ScriptedWriteSession(scripts);
        var sut = CreateSut(
            commandBudget: new RelationalCommandBudget(parameterBudget, MaxRowsPerStatement: 1000)
        );

        var resolution = await sut.ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().HaveCount(expectedCommandCount);
        session
            .Commands[0]
            .CommandText.Contains("Namespace", StringComparison.OrdinalIgnoreCase)
            .Should()
            .Be(parameterBudget == 3);

        if (parameterBudget == 2)
        {
            session
                .Commands[1]
                .Parameters.Single(parameter =>
                    parameter.Name.Equals("@documentId", StringComparison.OrdinalIgnoreCase)
                )
                .Value.Should()
                .Be(target.DocumentId);
        }
    }

    [TestCase(5, true)]
    [TestCase(4, false)]
    public async Task It_preflights_the_combined_capture_authorization_and_reference_budget(
        int parameterBudget,
        bool expectCompositeReferenceLookup
    )
    {
        var referentialId = new ReferentialId(Guid.Parse("87654321-1111-2222-3333-444444444444"));
        var input = CreateInput(
            RelationalWriteOperationKind.Post,
            includeReadPlan: true,
            documentReferences: [CreateDocumentReference(referentialId)]
        ) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
        };
        var lookupCommand = new RelationalCommand(
            "SELECT @lookup0 AS \"LookupMarker0\", @lookup1 AS \"LookupMarker1\" WHERE FALSE",
            [new RelationalParameter("@lookup0", 1), new RelationalParameter("@lookup1", 2)]
        );
        var factory = new TestReferenceResolverAdapterFactory { EmbeddableCommand = lookupCommand };
        var target = new CapturedTarget(345L, 44L, ExistingDocumentUuid.Value);
        object[] scripts = expectCompositeReferenceLookup
            ?
            [
                CreateReader(
                    CreateCaptureTable(target),
                    CreateAuthorizationTable(),
                    CreateReferenceLookupTable(),
                    CreateDocumentMetadataTable(target, 44L),
                    CreateRootTable(target)
                ),
            ]
            :
            [
                CreateCaptureReader(target),
                CreateReader(CreateAuthorizationTable()),
                CreateReader(CreateDocumentMetadataTable(target, 44L), CreateRootTable(target)),
            ];
        var session = new ScriptedWriteSession(scripts);
        var sut = CreateSut(
            factory,
            commandBudget: new RelationalCommandBudget(parameterBudget, MaxRowsPerStatement: 1000)
        );

        var resolution = await sut.ResolveAsync(input, session);

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().HaveCount(expectCompositeReferenceLookup ? 1 : 3);
        session
            .Commands[0]
            .CommandText.Contains("LookupMarker", StringComparison.Ordinal)
            .Should()
            .Be(expectCompositeReferenceLookup);

        if (!expectCompositeReferenceLookup)
        {
            session
                .Commands[1]
                .Parameters.Single(parameter =>
                    parameter.Name.Equals("@documentId", StringComparison.OrdinalIgnoreCase)
                )
                .Value.Should()
                .Be(target.DocumentId);
        }
    }

    private static CompositeRelationalWriteFirstPhase CreateSut(
        TestReferenceResolverAdapterFactory? adapterFactory = null,
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
        RelationalCommandBudget? commandBudget = null
    ) =>
        new(
            adapterFactory ?? new TestReferenceResolverAdapterFactory(),
            relationshipAuthorizationProviderFailureExtractor: providerFailureExtractor,
            commandBudget: commandBudget
        );

    private static RelationalWriteLockedTarget CreateLockProof(
        IRelationalWriteSession session,
        long documentId,
        long contentVersion
    ) =>
        RelationalWriteLockedTarget.FromCaptureOutcome(
            new RelationalCompositeStatementOutcome(
                0,
                "capture-target",
                new RelationalCompositeCapturedTarget(documentId, contentVersion, ExistingDocumentUuid.Value)
            ),
            session
        );

    private static RelationalWriteExecutorInput CreateInput(
        RelationalWriteOperationKind operationKind,
        bool includeReadPlan = true,
        SqlDialect dialect = SqlDialect.Pgsql,
        IReadOnlyList<DocumentReference>? documentReferences = null
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

        return new RelationalWriteExecutorInput(
            mappingSet,
            operationKind,
            operationKind is RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(ExistingDocumentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.Parse("12345678-1111-2222-3333-444444444444")),
                    CandidateDocumentUuid
                ),
            writePlan,
            includeReadPlan
                ? Given_Default_Relational_Write_Executor.CreateReadPlan(resourceModel, dialect)
                : null,
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            allowIdentityUpdates: false,
            new TraceId("composite-first-phase-test"),
            new ReferenceResolverRequest(mappingSet, resourceModel.Resource, documentReferences ?? [], [])
        );
    }

    private static PostRelationshipAuthorizationPlans CreatePostPlans(
        RelationalWriteExecutorResult createNewImmediateResult
    )
    {
        var noAuthorizationRequired = new RelationshipAuthorizationResult.NoAuthorizationRequired([]);

        return new PostRelationshipAuthorizationPlans(
            new RelationshipAuthorizationUpdatePlan(noAuthorizationRequired, noAuthorizationRequired, [], []),
            CreateNewProposedRelationshipAuthorization: null,
            createNewImmediateResult
        );
    }

    private static RelationalWriteNamespaceAuthorization CreateStoredNamespaceAuthorization() =>
        new(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    new DbTableName(new DbSchemaName("edfi"), "School"),
                    new DbColumnName("Name")
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );

    private static DocumentReference CreateDocumentReference(ReferentialId referentialId) =>
        new(
            new BaseResourceInfo(new ProjectName("Ed-Fi"), new ResourceName("School"), IsDescriptor: false),
            new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
            referentialId,
            new JsonPath("$.schoolReference")
        );

    private static DbDataReader CreateCompositeReader(
        CapturedTarget? target,
        bool includeMetadata = true,
        long hydratedContentVersion = 44L
    ) =>
        CreateReader(
            CreateCaptureTable(target),
            CreateDocumentMetadataTable(
                target is not null && includeMetadata ? target : null,
                hydratedContentVersion
            ),
            CreateRootTable(target is null || !includeMetadata ? null : target)
        );

    private static DbDataReader CreateCaptureReader(CapturedTarget? target) =>
        CreateReader(CreateCaptureTable(target));

    private static DbDataReader CreateReader(params DataTable[] tables) => new DataTableReader(tables);

    private static DataTable CreateCaptureTable(CapturedTarget? target)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));

        if (target is not null)
        {
            table.Rows.Add(target.DocumentId, target.ContentVersion, target.DocumentUuid);
        }
        else
        {
            table.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value);
        }

        return table;
    }

    private static DataTable CreateAuthorizationTable()
    {
        var table = new DataTable();
        table.Columns.Add("Authorized", typeof(int));
        return table;
    }

    private static DataTable CreateStoredRelationshipAuthorizationTable(CapturedTarget target)
    {
        var table = new DataTable();
        table.Columns.Add("AuthorizationResult", typeof(int));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Rows.Add(1, target.ContentVersion);
        return table;
    }

    private static DataTable CreateReferenceLookupTable()
    {
        var table = new DataTable();
        table.Columns.Add("ReferentialId", typeof(Guid));
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("ResourceKeyId", typeof(short));
        table.Columns.Add("ReferentialIdentityResourceKeyId", typeof(short));
        table.Columns.Add("IsDescriptor", typeof(bool));
        table.Columns.Add("VerificationIdentityKey", typeof(string));
        return table;
    }

    private static DataTable CreateDocumentMetadataTable(CapturedTarget? target, long hydratedContentVersion)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("IdentityLastModifiedAt", typeof(DateTimeOffset));

        if (target is not null)
        {
            table.Rows.Add(
                target.DocumentId,
                target.DocumentUuid,
                hydratedContentVersion,
                9L,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch
            );
        }

        return table;
    }

    private static DataTable CreateRootTable(CapturedTarget? target)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("SchoolId", typeof(int));
        table.Columns.Add("Name", typeof(string));

        if (target is not null)
        {
            table.Rows.Add(target.DocumentId, 255901, "Lincoln High");
        }

        return table;
    }

    private sealed record CapturedTarget(long DocumentId, long ContentVersion, Guid DocumentUuid);

    private sealed class TestReferenceResolverAdapterFactory : IReferenceResolverAdapterFactory
    {
        public RelationalCommand? EmbeddableCommand { get; init; }

        public DbException? ExceptionToThrow { get; init; }

        public IReferenceResolverAdapter CreateAdapter() => new TestReferenceResolverAdapter(this);

        public IReferenceResolverAdapter CreateSessionAdapter(IRelationalCommandExecutor commandExecutor) =>
            new TestReferenceResolverAdapter(this);

        public RelationalCommand? TryBuildSessionLookupCommand(ReferenceLookupRequest request) =>
            EmbeddableCommand;

        private sealed class TestReferenceResolverAdapter(TestReferenceResolverAdapterFactory factory)
            : IReferenceResolverAdapter
        {
            public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
                ReferenceLookupRequest request,
                CancellationToken cancellationToken = default
            )
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (factory.ExceptionToThrow is not null)
                {
                    throw factory.ExceptionToThrow;
                }

                return Task.FromResult<IReadOnlyList<ReferenceLookupResult>>([]);
            }
        }
    }

    private sealed class TestProviderFailureExtractor(string? providerErrorCode, string providerMessage)
        : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception) =>
            new(providerErrorCode, providerMessage);
    }

    private sealed class FakeDbException(string message) : DbException(message);
}
