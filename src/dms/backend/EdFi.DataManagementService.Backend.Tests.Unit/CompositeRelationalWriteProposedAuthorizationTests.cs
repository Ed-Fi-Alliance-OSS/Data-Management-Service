// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
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

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_emits_the_namespace_statement_before_the_relationship_statement()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var session = new ScriptedWriteSession(CreateAuthorizedReader(namespaceCheckCount: 1));

        await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

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

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
        session.Commands[0].Parameters.Should().OnlyContain(parameter => parameter.Name.Contains("_s0"));
    }

    [Test]
    public async Task It_issues_one_command_for_a_relationship_check_alone()
    {
        var request = CreateRequest(withNamespace: false, withRelationship: true);
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_issues_no_command_when_no_proposed_authorization_is_configured()
    {
        var request = CreateRequest(withNamespace: false, withRelationship: false);
        var session = new ScriptedWriteSession();

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_enriches_the_merge_result_with_the_extracted_runtime_check()
    {
        var request = CreateRequest(withNamespace: false, withRelationship: true);
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

        // The DML path reads the runtime check off the merge result, so the phase must publish it even
        // though this path issues no DML of its own.
        resolution.MergeResult.ProposedRelationshipAuthorizationRuntimeCheck.Should().NotBeNull();
    }

    [Test]
    public async Task It_defers_a_no_claims_denial_until_after_the_namespace_command_authorizes()
    {
        var baseRequest = CreateRequest(withNamespace: true, withRelationship: true);
        var request = baseRequest with { ProposedRelationshipAuthorization = CreateNoClaims(baseRequest) };
        var session = new ScriptedWriteSession(CreateReader(CreateAuthorizedTable()));

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

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

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

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

        var resolution = await CreateSut().ResolveAsync(request, CreateMergeResult(request), session);

        // A table-valued claim list cannot be renamed into a co-batched statement, so it runs as its own
        // command on the same session and transaction. This is the recorded deviation from the
        // one-command target, not a silent extra round trip.
        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().HaveCount(2);
    }

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
            .ResolveAsync(request, CreateMergeResult(request), session);

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
        var act = async () => await sut.ResolveAsync(request, CreateMergeResult(request), session);

        await act.Should().ThrowAsync<RelationalWriteRelationshipAuthorizationNotAuthorizedException>();
    }

    [Test]
    public async Task It_lets_a_failure_that_is_not_an_authorization_denial_propagate_unchanged()
    {
        var request = CreateRequest(withNamespace: true, withRelationship: true);
        var providerFailure = new FakeDbException("duplicate key", "23505");
        var session = new ScriptedWriteSession(providerFailure);
        var sut = CreateSut(new StubProviderFailureExtractor("23505", "duplicate key"));

        var act = async () => await sut.ResolveAsync(request, CreateMergeResult(request), session);

        (await act.Should().ThrowAsync<FakeDbException>()).Which.Should().BeSameAs(providerFailure);
    }

    /// <summary>
    /// The deferred no-claims disposition: the caller holds no education-organization claims, so the
    /// relationship check needs no statement, only a denial the namespace command may outrank.
    /// </summary>
    private static RelationshipAuthorizationResult.NoClaims CreateNoClaims(
        RelationalWriteExecutorRequest request
    )
    {
        var authorized = (RelationshipAuthorizationResult.Authorized)
            request.ProposedRelationshipAuthorization!;
        var checkSpec = authorized.CheckSpecs.Single();

        return new RelationshipAuthorizationResult.NoClaims(
            authorized.CheckSpecs,
            [
                new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                    request.WritePlan.Model.Resource,
                    checkSpec.ConfiguredStrategy,
                    checkSpec.RelationshipLocalOrder,
                    checkSpec.ValueSource,
                    checkSpec.Subjects[0].AuthObject,
                    new RelationshipAuthorizationFailureLocation(
                        Kind: SecurableElementKind.EducationOrganization,
                        JsonPath: "$.schoolId",
                        ReadableName: "SchoolId",
                        Table: request.WritePlan.Model.Root.Table,
                        Column: new DbColumnName("SchoolId")
                    ),
                    Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                ),
            ]
        );
    }

    private static CompositeRelationalWriteProposedAuthorization CreateSut(
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
    ) => new(relationalParameterConfigurator: null, providerFailureExtractor);

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

    /// <summary>
    /// Serves one script per <see cref="CreateCommand"/>: a reader to hand back, or an exception to raise
    /// from the reader-open boundary.
    /// </summary>
    private sealed class ScriptedWriteSession(params object[] scripts) : IRelationalWriteSession
    {
        private readonly Queue<object> _scripts = new(scripts);

        public DbConnection Connection { get; } = null!;

        public DbTransaction Transaction { get; } = null!;

        public List<RelationalCommand> Commands { get; } = [];

        public DbCommand CreateCommand(RelationalCommand command)
        {
            Commands.Add(command);

            if (_scripts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No command script remains for command {Commands.Count}."
                );
            }

            return new ScriptedDbCommand(_scripts.Dequeue());
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedDbCommand(object script) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } =
            new ScriptedDbParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new ScriptedDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            script switch
            {
                DbDataReader reader => reader,
                Exception exception => throw exception,
                _ => throw new InvalidOperationException(
                    $"Unsupported command script '{script.GetType().Name}'."
                ),
            };
    }

    private sealed class ScriptedDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];

        public override int Count => _parameters.Count;

        public override object SyncRoot => _parameters;

        public override int Add(object value)
        {
            _parameters.Add((DbParameter)value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear() => _parameters.Clear();

        public override bool Contains(object value) => _parameters.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) =>
            ((System.Collections.ICollection)_parameters).CopyTo(array, index);

        public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();

        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) =>
            _parameters.FindIndex(parameter =>
                string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)
            );

        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _parameters.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _parameters.RemoveAt(index);

        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _parameters[index];

        protected override DbParameter GetParameter(string parameterName) =>
            _parameters[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) =>
            _parameters[IndexOf(parameterName)] = value;
    }

    private sealed class ScriptedDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }

        public override void ResetDbType() { }
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
