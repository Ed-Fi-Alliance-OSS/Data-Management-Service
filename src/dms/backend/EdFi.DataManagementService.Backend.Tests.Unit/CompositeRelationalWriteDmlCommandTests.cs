// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Composite;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Owns the DML-mode second command's observable behavior: how many commands it issues, which statements
/// it co-batches and in what order, and what it decodes back as the persisted target.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Composite_Relational_Write_Second_Command_In_Dml_Mode
{
    private static readonly DocumentUuid ExistingDocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );

    private static readonly DocumentUuid CreatedDocumentUuid = new(
        Guid.Parse("dddddddd-4444-5555-6666-eeeeeeeeeeee")
    );

    [Test]
    public async Task It_co_batches_the_root_update_and_the_content_version_read_into_one_command()
    {
        var request = CreateExistingTargetRequest();
        var session = new ScriptedWriteSession(CreateReader(Sentinel(0), Scalar(77L)));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateChangedRootMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
        resolution.PersistResult!.ContentVersion.Should().Be(77L);
    }

    [Test]
    public async Task It_co_batches_the_document_insert_with_the_rows_it_makes_room_for()
    {
        var request = CreateCreatedTargetRequest();
        var session = new ScriptedWriteSession(CreateReader(Scalar(900L), Sentinel(1), Scalar(1L)));

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateNewRootMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
        resolution.PersistResult!.DocumentId.Should().Be(900L);
    }

    [Test]
    public async Task It_derives_the_created_document_id_from_a_scalar_subquery_on_the_document_uuid()
    {
        var request = CreateCreatedTargetRequest();
        var session = new ScriptedWriteSession(CreateReader(Scalar(900L), Sentinel(1), Scalar(1L)));

        await CreateSut()
            .ResolveAsync(
                request,
                CreateNewRootMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        // dms.Document.DocumentId is an identity column, so the value only exists once the insert has run
        // server-side. A CTE cannot be referenced from a following statement's VALUES list, so the
        // portable way for a later statement in the same command to consume it is a scalar subquery on
        // the unique DocumentUuid.
        var command = session.Commands.Should().ContainSingle().Subject;
        command.CommandText.Should().Contain(CreatedDocumentIdSubqueryPrefix);
    }

    [Test]
    public async Task It_binds_one_created_document_uuid_parameter_for_every_statement_that_derives_the_id()
    {
        var request = CreateCreatedTargetRequest();
        var session = new ScriptedWriteSession(CreateReader(Scalar(900L), Sentinel(1), Scalar(1L)));

        await CreateSut()
            .ResolveAsync(
                request,
                CreateNewRootMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        // Both the root insert and the ContentVersion read derive the id, and they share the command's
        // single bound uuid parameter rather than each binding their own copy of the same value.
        var command = session.Commands.Should().ContainSingle().Subject;
        var boundNames = SubqueryParameterNames(command.CommandText);
        boundNames.Should().HaveCountGreaterThan(1);
        boundNames.Distinct(StringComparer.Ordinal).Should().ContainSingle();
    }

    [Test]
    public async Task It_emits_the_proposed_authorization_statements_before_the_document_insert()
    {
        var request = CreateCreatedTargetRequest(withProposedAuthorization: true);
        var session = new ScriptedWriteSession(
            CreateReader(Authorized(), Authorized(), Scalar(900L), Sentinel(3), Scalar(1L))
        );

        var resolution = await CreateSut()
            .ResolveAsync(
                request,
                CreateNewRootMergeResult(request),
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        // Create artifacts only after proposed authorization: the command aborts at its first AUTH1, so
        // the dms.Document insert standing textually after both checks is what keeps a denied create from
        // leaving a row behind. The allocator stamps each parameter with its statement's ordinal, which is
        // how the order is proven here without matching on compiled authorization SQL.
        resolution.ImmediateResult.Should().BeNull();
        var command = session.Commands.Should().ContainSingle().Subject;
        var namespaceIndex = command.CommandText.IndexOf(
            "namespacePrefixes_s0",
            StringComparison.OrdinalIgnoreCase
        );
        var relationshipIndex = command.CommandText.IndexOf(
            "ClaimEducationOrganizationIds_s1",
            StringComparison.OrdinalIgnoreCase
        );
        var documentInsertIndex = command.CommandText.IndexOf(
            DocumentInsertStatementPrefix,
            StringComparison.Ordinal
        );

        namespaceIndex.Should().BePositive();
        relationshipIndex.Should().BeGreaterThan(namespaceIndex);
        documentInsertIndex.Should().BeGreaterThan(relationshipIndex);
    }

    [Test]
    public async Task It_emits_the_collection_delete_before_the_root_update()
    {
        var rootPlan = Given_Relational_Write_No_Profile_Persister.CreateRootPlan();
        var collectionPlan = Given_Relational_Write_No_Profile_Persister.CreateCollectionPlan();
        var request = Given_Relational_Write_No_Profile_Persister.CreateRequest(
            Given_Relational_Write_No_Profile_Persister.CreateWritePlan([rootPlan, collectionPlan]),
            RelationalWriteOperationKind.Put
        );
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")],
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255902, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(910L, 345L, 0, "Home")],
                    []
                ),
            ],
            supportsGuardedNoOp: true
        );
        var session = new ScriptedWriteSession(CreateReader(Sentinel(0), Sentinel(1), Scalar(78L)));

        var resolution = await CreateSut()
            .ResolveAsync(request, mergeResult, RelationalWriteSecondCommandMode.Dml, session);

        // Deletes before upserts, and children before parents on delete: the collection row's delete has to
        // land before the root row it hangs off is rewritten.
        resolution.ImmediateResult.Should().BeNull();
        var command = session.Commands.Should().ContainSingle().Subject;
        var deleteIndex = command.CommandText.IndexOf(
            "delete from edfi.\"SchoolAddress\"",
            StringComparison.OrdinalIgnoreCase
        );
        var updateIndex = command.CommandText.IndexOf(
            "update edfi.\"School\"",
            StringComparison.OrdinalIgnoreCase
        );

        deleteIndex.Should().BeGreaterThanOrEqualTo(0);
        updateIndex.Should().BeGreaterThan(deleteIndex);
    }

    [Test]
    public async Task It_inlines_the_collection_key_sequence_for_a_single_added_row()
    {
        var rootPlan = Given_Relational_Write_No_Profile_Persister.CreateRootPlan();
        var collectionPlan = Given_Relational_Write_No_Profile_Persister.CreateCollectionPlan();
        var request = Given_Relational_Write_No_Profile_Persister.CreateRequest(
            Given_Relational_Write_No_Profile_Persister.CreateWritePlan([rootPlan, collectionPlan]),
            RelationalWriteOperationKind.Put
        );
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")],
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [],
                    [
                        Given_Relational_Write_No_Profile_Persister.CreateRow(
                            Given_Relational_Write_No_Profile_Persister.NewCollectionItemId(),
                            345L,
                            0,
                            "Home"
                        ),
                    ]
                ),
            ],
            supportsGuardedNoOp: true
        );
        var session = new ScriptedWriteSession(CreateReader(Sentinel(0), Scalar(79L)));

        var resolution = await CreateSut()
            .ResolveAsync(request, mergeResult, RelationalWriteSecondCommandMode.Dml, session);

        // A token occurring once, at its owning table's preallocated key, is produced by the inserting row
        // itself, so no reservation round trip is owed and the whole write stays one command.
        resolution.ImmediateResult.Should().BeNull();
        var command = session.Commands.Should().ContainSingle().Subject;
        command.CommandText.Should().Contain("""nextval('"dms"."CollectionItemIdSequence"')""");
    }

    [Test]
    public async Task It_reserves_every_dependent_collection_key_in_one_shared_command()
    {
        var rootPlan = Given_Relational_Write_No_Profile_Persister.CreateRootPlan();
        var collectionPlan = Given_Relational_Write_No_Profile_Persister.CreateCollectionPlan();
        var scopePlan = Given_Relational_Write_No_Profile_Persister.CreateCollectionExtensionScopePlan();
        var request = Given_Relational_Write_No_Profile_Persister.CreateRequest(
            // Dependency order deliberately places the aligned scope ahead of the collection it hangs off,
            // so the emitted statement order cannot come from this order alone.
            Given_Relational_Write_No_Profile_Persister.CreateWritePlan([
                rootPlan,
                scopePlan,
                collectionPlan,
            ]),
            RelationalWriteOperationKind.Put
        );
        var addressCollectionItemId = Given_Relational_Write_No_Profile_Persister.NewCollectionItemId();
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")],
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    scopePlan,
                    [],
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(addressCollectionItemId, "Blue")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [],
                    [
                        Given_Relational_Write_No_Profile_Persister.CreateRow(
                            addressCollectionItemId,
                            345L,
                            0,
                            "Home"
                        ),
                    ]
                ),
            ],
            supportsGuardedNoOp: true
        );
        var session = new ScriptedWriteSession(
            Reserved(910L),
            CreateReader(Sentinel(0), Sentinel(1), Scalar(80L))
        );

        var resolution = await CreateSut()
            .ResolveAsync(request, mergeResult, RelationalWriteSecondCommandMode.Dml, session);

        // A token another table's statement binds cannot be inlined, so it is reserved — once, for every
        // table that needs one, and never once per table.
        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().HaveCount(2);
        session.Commands[0].CommandText.Should().Contain("CollectionItemIdSequence");
        session
            .Commands[1]
            .CommandText.IndexOf("insert into edfi.\"SchoolAddress\"", StringComparison.OrdinalIgnoreCase)
            .Should()
            .BeLessThan(
                session
                    .Commands[1]
                    .CommandText.IndexOf(
                        "insert into sample.\"SchoolExtensionAddress\"",
                        StringComparison.OrdinalIgnoreCase
                    )
            );
    }

    [Test]
    public async Task It_opens_another_command_when_the_remaining_rows_do_not_fit_the_parameter_budget()
    {
        var session = new ScriptedWriteSession(
            CreateReader(Sentinel(0)),
            CreateReader(Sentinel(0), Scalar(81L))
        );

        var resolution = await CreateSut(new RelationalCommandBudget(6, 1000))
            .ResolveAsync(
                CreateAddedCollectionRowsRequest(out var mergeResult, rowCount: 3),
                mergeResult,
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        // Each added row binds three parameters, so a six-parameter budget holds two rows and the third
        // opens the next command. The split is at the row group, never at the table: the rows are not
        // reordered and no table gets a command of its own.
        resolution.ImmediateResult.Should().BeNull();
        resolution.PersistResult!.ContentVersion.Should().Be(81L);
        session.Commands.Should().HaveCount(2);
        session.Commands[0].Parameters.Should().HaveCount(6);
    }

    [Test]
    public async Task It_keeps_one_command_when_the_rows_fit_the_parameter_budget()
    {
        var session = new ScriptedWriteSession(CreateReader(Sentinel(0), Scalar(82L)));

        var resolution = await CreateSut(new RelationalCommandBudget(64, 1000))
            .ResolveAsync(
                CreateAddedCollectionRowsRequest(out var mergeResult, rowCount: 3),
                mergeResult,
                RelationalWriteSecondCommandMode.Dml,
                session
            );

        resolution.ImmediateResult.Should().BeNull();
        session.Commands.Should().ContainSingle();
    }

    /// <summary>
    /// A PUT whose root row is unchanged and whose collection gains <paramref name="rowCount"/> rows, so the
    /// only statements owed are one collection insert and the <c>ContentVersion</c> read.
    /// </summary>
    private static RelationalWriteExecutorRequest CreateAddedCollectionRowsRequest(
        out RelationalWriteMergeResult mergeResult,
        int rowCount
    )
    {
        var rootPlan = Given_Relational_Write_No_Profile_Persister.CreateRootPlan();
        var collectionPlan = Given_Relational_Write_No_Profile_Persister.CreateCollectionPlan();
        var request = Given_Relational_Write_No_Profile_Persister.CreateRequest(
            Given_Relational_Write_No_Profile_Persister.CreateWritePlan([rootPlan, collectionPlan]),
            RelationalWriteOperationKind.Put
        );

        mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")],
                    [Given_Relational_Write_No_Profile_Persister.CreateRow(345L, 255901, "Lincoln High")]
                ),
                new RelationalWriteMergedTableState(
                    collectionPlan,
                    [],
                    [
                        .. Enumerable
                            .Range(0, rowCount)
                            .Select(ordinal =>
                                Given_Relational_Write_No_Profile_Persister.CreateRow(
                                    Given_Relational_Write_No_Profile_Persister.NewCollectionItemId(),
                                    345L,
                                    ordinal,
                                    $"Home{ordinal}"
                                )
                            ),
                    ]
                ),
            ],
            supportsGuardedNoOp: true
        );

        return request;
    }

    private const string CreatedDocumentIdSubqueryPrefix =
        """(SELECT "DocumentId" FROM dms."Document" WHERE "DocumentUuid" = """;

    private const string DocumentInsertStatementPrefix = """INSERT INTO dms."Document" """;

    /// <summary>
    /// The parameter name each occurrence of the created-document-id subquery binds, in emission order.
    /// </summary>
    private static IReadOnlyList<string> SubqueryParameterNames(string commandText)
    {
        List<string> names = [];

        for (
            var index = commandText.IndexOf(CreatedDocumentIdSubqueryPrefix, StringComparison.Ordinal);
            index >= 0;
            index = commandText.IndexOf(CreatedDocumentIdSubqueryPrefix, index + 1, StringComparison.Ordinal)
        )
        {
            var nameStart = index + CreatedDocumentIdSubqueryPrefix.Length;
            var nameEnd = nameStart;

            while (
                nameEnd < commandText.Length
                && (char.IsLetterOrDigit(commandText[nameEnd]) || commandText[nameEnd] is '_' or '@')
            )
            {
                nameEnd++;
            }

            names.Add(commandText[nameStart..nameEnd]);
        }

        return names;
    }

    private static CompositeRelationalWriteSecondCommand CreateSut(
        RelationalCommandBudget? commandBudget = null
    ) => new(commandBudget: commandBudget);

    private static RelationalWriteExecutorRequest CreateExistingTargetRequest() =>
        CreateInput(new RelationalWriteTargetRequest.Put(ExistingDocumentUuid))
            .Resolve(new RelationalWriteTargetContext.ExistingDocument(345L, ExistingDocumentUuid, 44L));

    private static RelationalWriteExecutorRequest CreateCreatedTargetRequest(
        bool withProposedAuthorization = false
    ) =>
        CreateInput(
                new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
                    CreatedDocumentUuid
                ),
                withProposedAuthorization
            )
            .Resolve(new RelationalWriteTargetContext.CreateNew(CreatedDocumentUuid));

    private static RelationalWriteExecutorInput CreateInput(
        RelationalWriteTargetRequest targetRequest,
        bool withProposedAuthorization = false
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
            targetRequest is RelationalWriteTargetRequest.Post
                ? RelationalWriteOperationKind.Post
                : RelationalWriteOperationKind.Put,
            targetRequest,
            writePlan,
            Given_Default_Relational_Write_Executor.CreateReadPlan(resourceModel, SqlDialect.Pgsql),
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            allowIdentityUpdates: false,
            new TraceId("composite-dml-test"),
            new ReferenceResolverRequest(mappingSet, resourceModel.Resource, [], [])
        );

        if (!withProposedAuthorization)
        {
            return input;
        }

        input = input with
        {
            // The root table's Name column doubles as the namespace value, so one root row feeds both the
            // namespace check and the relationship check's proposed SchoolId.
            ProposedNamespaceAuthorization = new RelationalWriteNamespaceAuthorization(
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Proposed,
                        rootPlan.TableModel.Table,
                        new DbColumnName("Name")
                    ),
                ],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["Lincoln"],
                    "namespacePrefixes"
                )
            ),
        };

        return input with
        {
            ProposedRelationshipAuthorization =
                Given_Default_Relational_Write_Executor.CreateProposedSchoolIdRelationshipAuthorization(
                    input,
                    null
                ),
        };
    }

    /// <summary>
    /// A root row whose merged scalars differ from the loaded ones, so the plan owes exactly one update.
    /// The document id is carried as the unresolved marker the flattener actually emits, not as a literal,
    /// so the statement builder has to resolve it the way production does.
    /// </summary>
    private static RelationalWriteMergeResult CreateChangedRootMergeResult(
        RelationalWriteExecutorRequest request
    ) =>
        new(
            [
                new RelationalWriteMergedTableState(
                    request.WritePlan.TablePlansInDependencyOrder[0],
                    [CreateRootRow(255901, "Lincoln High")],
                    [CreateRootRow(255902, "Lincoln High")]
                ),
            ],
            supportsGuardedNoOp: true
        );

    /// <summary>A create's root table owes one insert and has no loaded rows to compare against.</summary>
    private static RelationalWriteMergeResult CreateNewRootMergeResult(
        RelationalWriteExecutorRequest request
    ) =>
        new(
            [
                new RelationalWriteMergedTableState(
                    request.WritePlan.TablePlansInDependencyOrder[0],
                    [],
                    [CreateRootRow(255901, "Lincoln High")]
                ),
            ],
            supportsGuardedNoOp: true
        );

    private static RelationalWriteMergedTableRow CreateRootRow(int schoolId, string name)
    {
        FlattenedWriteValue[] values =
        [
            FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
            new FlattenedWriteValue.Literal(schoolId),
            new FlattenedWriteValue.Literal(name),
        ];

        return new RelationalWriteMergedTableRow(values, values);
    }

    /// <summary>
    /// A reader over the command's declared result-set stream. Every set carries the authorization
    /// decoder's column name, which is the only name any DML-mode statement reads by name.
    /// </summary>
    private static DbDataReader CreateReader(params IReadOnlyList<object?[]>[] resultSets) =>
        new ScriptedDbDataReader(
            resultSets,
            [.. resultSets.Select(static _ => new[] { "AuthorizationResult" })]
        );

    /// <summary>The one-row result set a data-modifying statement's trailing sentinel select produces.</summary>
    private static IReadOnlyList<object?[]> Sentinel(int ordinal) =>
        [
            [ordinal],
        ];

    private static IReadOnlyList<object?[]> Scalar(object? value) =>
        [
            [value],
        ];

    /// <summary>
    /// One authorizing check's result set. A denial aborts the command instead, so the row's contents
    /// carry no information.
    /// </summary>
    private static IReadOnlyList<object?[]> Authorized() =>
        [
            [1],
        ];

    /// <summary>
    /// The shared reservation command's result. One key is read as a scalar and several as ordered rows,
    /// matching the two shapes the reservation emits.
    /// </summary>
    private static DbDataReader Reserved(params long[] collectionItemIds) =>
        collectionItemIds.Length == 1
            ? new ScriptedDbDataReader(
                [
                    [
                        [collectionItemIds[0]],
                    ],
                ],
                [
                    ["CollectionItemId"],
                ]
            )
            : new ScriptedDbDataReader(
                [
                    [.. collectionItemIds.Select(static (id, index) => new object?[] { index + 1, id })],
                ],
                [
                    ["Ordinal", "CollectionItemId"],
                ]
            );

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

        /// <summary>
        /// The scripted reader's first cell, which is what a single-value command — the one-key collection
        /// reservation — reads.
        /// </summary>
        public override object? ExecuteScalar()
        {
            var reader = (DbDataReader)script;

            return reader.Read() ? reader.GetValue(0) : null;
        }

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
}
