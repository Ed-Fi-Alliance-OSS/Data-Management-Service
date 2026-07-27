// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Focused fixture for <see cref="DocumentReferenceLookupPlanCompiler"/> binding-shape coverage.
/// The integration tests verify end-to-end behavior for nested-collection,
/// collection-aligned-extension, and extension-child bindings; these unit tests assert at the
/// SQL-emission level that the join column resolves to the table's root-scope locator
/// (<c>&lt;Root&gt;_DocumentId</c>) rather than the table's PK (<c>CollectionItemId</c> or
/// <c>BaseCollectionItemId</c>) — the single rule shared with descriptor projection and table
/// hydration.
/// </summary>
[TestFixture]
public class Given_DocumentReferenceLookupPlanCompiler
{
    private const string StudentRootLocator = "Student_DocumentId";

    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );
    private static readonly JsonPathExpression _addressSchoolReferencePath = new(
        "$.addresses[*].schoolReference",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("schoolReference"),
        ]
    );

    [TestCase(SqlDialect.Pgsql, "\"", "\"")]
    [TestCase(SqlDialect.Mssql, "[", "]")]
    public void It_should_join_child_collection_binding_via_root_locator_column_not_pk(
        SqlDialect dialect,
        string openQuote,
        string closeQuote
    )
    {
        var model = BuildModelWithCollectionTableBinding();
        var lookup = CompileLookup(model, dialect);

        lookup.Should().NotBeNull();
        var sql = lookup!.SelectByKeysetSql;

        // The JOIN must use Student_DocumentId (the root-scope locator), not the table's PK
        // (CollectionItemId) or the unqualified DocumentId. Verifying via fully-qualified
        // column rendering keeps the assertion dialect-aware.
        sql.Should()
            .Contain(
                $"t0.{openQuote}{StudentRootLocator}{closeQuote} = k.{openQuote}DocumentId{closeQuote}",
                "the lookup must join the child collection back to the page keyset via its root-scope locator"
            );
        sql.Should()
            .NotContain($"t0.{openQuote}CollectionItemId{closeQuote} = k.{openQuote}DocumentId{closeQuote}");

        // Sanity: source metadata reports the child table, not the root.
        lookup.SourcesInOrder.Should().ContainSingle();
        lookup.SourcesInOrder[0].Table.Name.Should().Be("StudentAddress");
        lookup.SourcesInOrder[0].FkColumn.Value.Should().Be("School_DocumentId");
    }

    [TestCase(SqlDialect.Pgsql, "\"", "\"", "CROSS JOIN LATERAL")]
    [TestCase(SqlDialect.Mssql, "[", "]", "CROSS APPLY")]
    public void It_should_collapse_two_bindings_on_the_same_child_table_into_one_unpivoted_scan(
        SqlDialect dialect,
        string openQuote,
        string closeQuote,
        string rowSetJoinKeyword
    )
    {
        var model = BuildModelWithTwoChildCollectionBindings();
        var lookup = CompileLookup(model, dialect);

        lookup.Should().NotBeNull();
        var sql = lookup!.SelectByKeysetSql;

        // Two bindings on one table → one scan that expands both FK columns inline, so the
        // keyset join is emitted once rather than once per reference column.
        sql.Should().NotContain("UNION");
        sql.Should().Contain("SELECT DISTINCT ");
        sql.Should()
            .Contain(
                $"{rowSetJoinKeyword} (VALUES (t0.{openQuote}School_DocumentId{closeQuote}), "
                    + $"(t0.{openQuote}Sponsor_DocumentId{closeQuote})) AS v0({openQuote}DocumentId{closeQuote})"
            );
        sql.Should()
            .Contain($"t0.{openQuote}{StudentRootLocator}{closeQuote} = k.{openQuote}DocumentId{closeQuote}");
        sql.Should().NotContain("t1.");

        // The null predicate must apply to the expanded value, not to a single source column,
        // so a row contributes only its non-null references instead of being dropped entirely.
        sql.Should().Contain($"WHERE v0.{openQuote}DocumentId{closeQuote} IS NOT NULL");

        lookup
            .SourcesInOrder.Select(static source => source.FkColumn.Value)
            .Should()
            .Equal("School_DocumentId", "Sponsor_DocumentId");
    }

    [TestCase(SqlDialect.Pgsql, "\"", "\"", "CROSS JOIN LATERAL")]
    [TestCase(SqlDialect.Mssql, "[", "]", "CROSS APPLY")]
    public void It_should_emit_one_branch_per_source_table_and_unpivot_only_multi_column_tables(
        SqlDialect dialect,
        string openQuote,
        string closeQuote,
        string rowSetJoinKeyword
    )
    {
        var model = BuildModelWithRootAndTwoChildCollectionBindings();
        var lookup = CompileLookup(model, dialect);

        lookup.Should().NotBeNull();
        var sql = lookup!.SelectByKeysetSql;

        // Two source tables → exactly one UNION. The root contributes a single FK column and
        // keeps the plain projection; the child contributes two and is expanded inline.
        sql.Split("UNION", StringSplitOptions.None).Should().HaveCount(2);
        sql.Should()
            .Contain(
                $"SELECT t0.{openQuote}Sponsor_DocumentId{closeQuote} AS {openQuote}DocumentId{closeQuote}"
            );
        sql.Should()
            .Contain(
                $"{rowSetJoinKeyword} (VALUES (t1.{openQuote}School_DocumentId{closeQuote}), "
                    + $"(t1.{openQuote}Sponsor_DocumentId{closeQuote})) AS v1({openQuote}DocumentId{closeQuote})"
            );
        sql.Should().NotContain("v0(");

        lookup
            .SourcesInOrder.Select(static source => (source.Table.Name, source.FkColumn.Value))
            .Should()
            .Equal(
                ("Student", "Sponsor_DocumentId"),
                ("StudentAddress", "School_DocumentId"),
                ("StudentAddress", "Sponsor_DocumentId")
            );
    }

    [Test]
    public void It_should_report_when_binding_owner_table_is_missing_from_table_lookup()
    {
        var model = BuildModelWithCollectionTableBinding();
        var tablesByName = model
            .TablesInDependencyOrder.Where(static table => table.Table.Name != "StudentAddress")
            .ToDictionary(static table => table.Table, static table => table);

        Action act = () => CompileLookup(model, SqlDialect.Pgsql, tablesByName);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile document-reference lookup plan for 'edfi.StudentAddress': owning table is not present in TablesInDependencyOrder."
            );
    }

    [Test]
    public void It_should_report_when_binding_fk_column_is_missing()
    {
        var model = BuildModelWithMissingFkColumnBinding();

        Action act = () => CompileLookup(model, SqlDialect.Pgsql);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile document-reference lookup plan for 'edfi.StudentAddress': document-reference binding '$.addresses[*].schoolReference' FK column 'Missing_DocumentId' does not exist in table columns."
            );
    }

    [Test]
    public void It_should_report_when_binding_fk_column_is_not_document_fk()
    {
        var model = BuildModelWithScalarFkColumnBinding();

        Action act = () => CompileLookup(model, SqlDialect.Pgsql);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile document-reference lookup plan for 'edfi.StudentAddress': document-reference binding '$.addresses[*].schoolReference' FK column 'School_DocumentId' has kind 'Scalar'. Expected 'DocumentFk'."
            );
    }

    [Test]
    public void It_should_report_when_binding_owner_table_is_missing_from_dependency_order()
    {
        var model = BuildModelWithCollectionTableBinding();
        var tablesByName = model.TablesInDependencyOrder.ToDictionary(
            static table => table.Table,
            static table => table
        );
        var modelWithoutOwnerTableInDependencyOrder = model with { TablesInDependencyOrder = [model.Root] };

        Action act = () =>
            CompileLookup(modelWithoutOwnerTableInDependencyOrder, SqlDialect.Pgsql, tablesByName);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile document-reference lookup plan for 'edfi.StudentAddress': owning table is not present in TablesInDependencyOrder."
            );
    }

    /// <summary>
    /// Invokes the lookup compiler directly, bypassing upstream <see cref="ReadPlanCompiler"/>
    /// stages (descriptor projection, reference-identity projection) that require
    /// <c>IdentityBindings</c> the lookup compiler itself does not consume. This isolates the
    /// SUT to the join-column resolution rule.
    /// </summary>
    private static DocumentReferenceLookupPlan? CompileLookup(
        RelationalResourceModel model,
        SqlDialect dialect
    )
    {
        var tablesByName = model.TablesInDependencyOrder.ToDictionary(
            static table => table.Table,
            static table => table
        );

        return CompileLookup(model, dialect, tablesByName);
    }

    private static DocumentReferenceLookupPlan? CompileLookup(
        RelationalResourceModel model,
        SqlDialect dialect,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        var compiler = new DocumentReferenceLookupPlanCompiler(dialect);
        var keysetTable = KeysetTableConventions.GetKeysetTableContract(dialect);

        return compiler.Compile(model, keysetTable, tablesByName);
    }

    private static RelationalResourceModel BuildModelWithCollectionTableBinding()
    {
        var rootTable = BuildStudentRootTable();
        var addressTable = BuildStudentAddressTable(
            extraDocumentFkColumns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: _addressSchoolReferencePath,
                    TargetResource: _schoolResource
                ),
            ]
        );

        return new RelationalResourceModel(
            Resource: _studentResource,
            PhysicalSchema: _edfiSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, addressTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: _addressSchoolReferencePath,
                    Table: addressTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings: []
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel BuildModelWithTwoChildCollectionBindings()
    {
        var sponsorPath = new JsonPathExpression(
            "$.addresses[*].sponsorReference",
            [
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("sponsorReference"),
            ]
        );
        var rootTable = BuildStudentRootTable();
        var addressTable = BuildStudentAddressTable(
            extraDocumentFkColumns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: _addressSchoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Sponsor_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: sponsorPath,
                    TargetResource: _schoolResource
                ),
            ]
        );

        return new RelationalResourceModel(
            Resource: _studentResource,
            PhysicalSchema: _edfiSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, addressTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: _addressSchoolReferencePath,
                    Table: addressTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings: []
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: sponsorPath,
                    Table: addressTable.Table,
                    FkColumn: new DbColumnName("Sponsor_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings: []
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel BuildModelWithRootAndTwoChildCollectionBindings()
    {
        var childBindingModel = BuildModelWithTwoChildCollectionBindings();
        var addressTable = childBindingModel.TablesInDependencyOrder[1];
        var rootSponsorPath = new JsonPathExpression(
            "$.sponsorReference",
            [new JsonPathSegment.Property("sponsorReference")]
        );
        var rootTable = BuildStudentRootTable(
            extraDocumentFkColumns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Sponsor_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: rootSponsorPath,
                    TargetResource: _schoolResource
                ),
            ]
        );

        return childBindingModel with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable, addressTable],
            DocumentReferenceBindings =
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: rootSponsorPath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("Sponsor_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings: []
                ),
                .. childBindingModel.DocumentReferenceBindings,
            ],
        };
    }

    private static RelationalResourceModel BuildModelWithMissingFkColumnBinding()
    {
        var model = BuildModelWithCollectionTableBinding();

        return model with
        {
            DocumentReferenceBindings =
            [
                model.DocumentReferenceBindings[0] with
                {
                    FkColumn = new DbColumnName("Missing_DocumentId"),
                },
            ],
        };
    }

    private static RelationalResourceModel BuildModelWithScalarFkColumnBinding()
    {
        var model = BuildModelWithCollectionTableBinding();
        var addressTable = model.TablesInDependencyOrder[1];
        var scalarAddressTable = addressTable with
        {
            Columns = addressTable
                .Columns.Select(column =>
                    column.ColumnName.Value == "School_DocumentId"
                        ? column with
                        {
                            Kind = ColumnKind.Scalar,
                        }
                        : column
                )
                .ToArray(),
        };

        return model with
        {
            TablesInDependencyOrder = [model.Root, scalarAddressTable],
        };
    }

    private static DbTableModel BuildStudentRootTable() => BuildStudentRootTable([]);

    private static DbTableModel BuildStudentRootTable(IReadOnlyList<DbColumnModel> extraDocumentFkColumns)
    {
        List<DbColumnModel> baseColumns =
        [
            new(
                ColumnName: new DbColumnName("DocumentId"),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("StudentUniqueId"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression(
                    "$.studentUniqueId",
                    [new JsonPathSegment.Property("studentUniqueId")]
                ),
                TargetResource: null
            ),
        ];
        baseColumns.AddRange(extraDocumentFkColumns);

        return new DbTableModel(
            Table: new DbTableName(_edfiSchema, "Student"),
            JsonScope: _rootScope,
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: baseColumns,
            Constraints: []
        );
    }

    private static DbTableModel BuildStudentAddressTable(IReadOnlyList<DbColumnModel> extraDocumentFkColumns)
    {
        var baseColumns = new List<DbColumnModel>
        {
            new(
                ColumnName: new DbColumnName(StudentRootLocator),
                Kind: ColumnKind.ParentKeyPart,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("Ordinal"),
                Kind: ColumnKind.Ordinal,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                ColumnName: new DbColumnName("City"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression(
                    "$.addresses[*].city",
                    [
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city"),
                    ]
                ),
                TargetResource: null
            ),
        };
        baseColumns.AddRange(extraDocumentFkColumns);

        return new DbTableModel(
            Table: new DbTableName(_edfiSchema, "StudentAddress"),
            JsonScope: _addressesScope,
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName(StudentRootLocator), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns: baseColumns,
            Constraints: []
        );
    }
}
