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

    [TestCase(SqlDialect.Pgsql, "\"", "\"")]
    [TestCase(SqlDialect.Mssql, "[", "]")]
    public void It_should_dedup_two_bindings_on_the_same_child_table_with_distinct_fk_columns(
        SqlDialect dialect,
        string openQuote,
        string closeQuote
    )
    {
        var model = BuildModelWithTwoChildCollectionBindings();
        var lookup = CompileLookup(model, dialect);

        lookup.Should().NotBeNull();
        var sql = lookup!.SelectByKeysetSql;

        // Two bindings → two UNION branches (no DISTINCT prefix), both joined via the same
        // child-table root locator.
        sql.Should().Contain("UNION");
        sql.Should().NotContain("SELECT DISTINCT ");
        sql.Should()
            .Contain($"t0.{openQuote}{StudentRootLocator}{closeQuote} = k.{openQuote}DocumentId{closeQuote}");
        sql.Should()
            .Contain($"t1.{openQuote}{StudentRootLocator}{closeQuote} = k.{openQuote}DocumentId{closeQuote}");

        lookup
            .SourcesInOrder.Select(static source => source.FkColumn.Value)
            .Should()
            .Equal("School_DocumentId", "Sponsor_DocumentId");
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
        var compiler = new DocumentReferenceLookupPlanCompiler(dialect);
        var keysetTable = KeysetTableConventions.GetKeysetTableContract(dialect);
        var tablesByName = model.TablesInDependencyOrder.ToDictionary(
            static table => table.Table,
            static table => table
        );

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

    private static DbTableModel BuildStudentRootTable() =>
        new(
            Table: new DbTableName(_edfiSchema, "Student"),
            JsonScope: _rootScope,
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
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
            ],
            Constraints: []
        );

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
