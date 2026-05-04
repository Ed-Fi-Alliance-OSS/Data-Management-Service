// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// Input-Order Permutation Regression Tests
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Regression tests that verify the emitter produces byte-for-byte identical DDL regardless of the
/// order in which resources, schemas, indexes, and triggers appear in the <see cref="DerivedRelationalModelSet"/>.
/// This guards against regressions where dictionary iteration order or JSON-file processing order
/// leaks into the emitted SQL text.
/// </summary>
[TestFixture(SqlDialect.Pgsql, "\"Alpha\"", "\"Zeta\"", "\"edfi\"", "\"tpdm\"")]
[TestFixture(SqlDialect.Mssql, "[Alpha]", "[Zeta]", "[edfi]", "[tpdm]")]
public class Given_RelationalModelDdlEmitter_InputOrder_Is_Irrelevant(
    SqlDialect sqlDialect,
    string quotedAlpha,
    string quotedZeta,
    string quotedEdfi,
    string quotedTpdm
)
{
    private string _zetaFirstDdl = default!;
    private string _alphaFirstDdl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(sqlDialect);
        var emitter = new RelationalModelDdlEmitter(dialect);

        _zetaFirstDdl = emitter.Emit(PermutationInputOrderFixture.BuildZetaFirstOrder(dialect.Rules.Dialect));
        _alphaFirstDdl = emitter.Emit(
            PermutationInputOrderFixture.BuildAlphaFirstOrder(dialect.Rules.Dialect)
        );
    }

    [Test]
    public void It_should_produce_identical_ddl_regardless_of_input_ordering()
    {
        _zetaFirstDdl
            .Should()
            .Be(
                _alphaFirstDdl,
                "DDL output must be byte-for-byte identical regardless of input element ordering"
            );
    }

    [Test]
    public void It_should_emit_alpha_table_before_zeta_table()
    {
        // Use a regex to match CREATE TABLE statements containing the quoted table name,
        // accounting for dialect differences (schema prefix, IF NOT EXISTS clause, etc.).
        var alphaMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedAlpha)}"
        );
        var zetaMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedZeta)}"
        );

        alphaMatch.Success.Should().BeTrue("expected CREATE TABLE for Alpha in DDL");
        zetaMatch.Success.Should().BeTrue("expected CREATE TABLE for Zeta in DDL");
        alphaMatch
            .Index.Should()
            .BeLessThan(zetaMatch.Index, "Alpha must precede Zeta in canonical ordinal order");
    }

    [Test]
    public void It_should_emit_edfi_schema_before_tpdm_schema()
    {
        var edfiMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE SCHEMA\b.*{System.Text.RegularExpressions.Regex.Escape(quotedEdfi)}"
        );
        var tpdmMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE SCHEMA\b.*{System.Text.RegularExpressions.Regex.Escape(quotedTpdm)}"
        );

        edfiMatch.Success.Should().BeTrue("expected CREATE SCHEMA for edfi in DDL");
        tpdmMatch.Success.Should().BeTrue("expected CREATE SCHEMA for tpdm in DDL");
        edfiMatch
            .Index.Should()
            .BeLessThan(tpdmMatch.Index, "edfi schema must precede tpdm schema in canonical order");
    }

    [Test]
    public void It_should_emit_edfi_tables_before_tpdm_tables()
    {
        // Within concrete resource tables, Ed-Fi resources (ProjectName "Ed-Fi") are ordered
        // before TPDM resources (ProjectName "TPDM") because "Ed-Fi" < "TPDM" ordinally.
        // Check that the first edfi-schema table appears before the first tpdm-schema table.
        var firstEdfiTable = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedEdfi)}\."
        );
        var firstTpdmTable = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedTpdm)}\."
        );

        firstEdfiTable.Success.Should().BeTrue("expected at least one edfi table in DDL");
        firstTpdmTable.Success.Should().BeTrue("expected at least one tpdm table in DDL");

        firstEdfiTable
            .Index.Should()
            .BeLessThan(
                firstTpdmTable.Index,
                "first edfi table must precede first tpdm table in canonical order"
            );
    }
}

/// <summary>
/// Fixture for input-order permutation tests.
/// Two schemas ("edfi" and "tpdm"), three concrete resources ("Alpha", "AlphaChild", "Zeta" in edfi
/// plus "Gamma" in tpdm), abstract identity tables, abstract union views, multiple FKs per table,
/// multiple indexes per table, and multiple triggers per table are provided in opposite orderings
/// across the two <c>Build*</c> methods so the emitter's canonical sort is the only thing that can
/// produce identical output.
/// </summary>
internal static class PermutationInputOrderFixture
{
    private static DerivedRelationalModelSet Build(
        SqlDialect dialect,
        IReadOnlyList<ProjectSchemaInfo> projectSchemaOrder,
        IReadOnlyList<ConcreteResourceModel> resourceOrder,
        IReadOnlyList<AbstractIdentityTableInfo> abstractIdentityTableOrder,
        IReadOnlyList<AbstractUnionViewInfo> abstractUnionViewOrder,
        IReadOnlyList<DbIndexInfo> indexOrder,
        IReadOnlyList<DbTriggerInfo> triggerOrder
    )
    {
        var alphaResource = new QualifiedResourceName("Ed-Fi", "Alpha");
        var alphaKey = new ResourceKeyEntry(1, alphaResource, "1.0.0", false);

        var zetaResource = new QualifiedResourceName("Ed-Fi", "Zeta");
        var zetaKey = new ResourceKeyEntry(2, zetaResource, "1.0.0", false);

        var alphaAbstractResource = new QualifiedResourceName("Ed-Fi", "AlphaAbstract");
        var alphaAbstractKey = new ResourceKeyEntry(3, alphaAbstractResource, "1.0.0", true);

        var zetaAbstractResource = new QualifiedResourceName("Ed-Fi", "ZetaAbstract");
        var zetaAbstractKey = new ResourceKeyEntry(4, zetaAbstractResource, "1.0.0", true);

        var gammaResource = new QualifiedResourceName("TPDM", "Gamma");
        var gammaKey = new ResourceKeyEntry(5, gammaResource, "1.0.0", false);

        var gammaAbstractResource = new QualifiedResourceName("TPDM", "GammaAbstract");
        var gammaAbstractKey = new ResourceKeyEntry(6, gammaAbstractResource, "1.0.0", true);

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "abc123",
                6,
                [0xAB, 0xC1],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                    new SchemaComponentInfo(
                        "tpdm",
                        "TPDM",
                        "1.0.0",
                        false,
                        "tpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdm"
                    ),
                ],
                [alphaKey, zetaKey, alphaAbstractKey, zetaAbstractKey, gammaKey, gammaAbstractKey]
            ),
            dialect,
            projectSchemaOrder,
            resourceOrder,
            abstractIdentityTableOrder,
            abstractUnionViewOrder,
            indexOrder,
            triggerOrder
        );
    }

    private static ConcreteResourceModel BuildConcreteResource(
        DbSchemaName schema,
        DbColumnName documentIdColumn,
        short keyId,
        string resourceName,
        string projectName = "Ed-Fi",
        TableConstraint.ForeignKey? extraChildFk = null
    )
    {
        var resource = new QualifiedResourceName(projectName, resourceName);
        var key = new ResourceKeyEntry(keyId, resource, "1.0.0", false);
        var parentTableName = new DbTableName(schema, resourceName);
        var childTableName = new DbTableName(schema, $"{resourceName}Child");
        var childOrdinalColumn = new DbColumnName("ChildOrdinal");

        var parentTable = new DbTableModel(
            parentTableName,
            new JsonPathExpression("$", []),
            new TableKey($"PK_{resourceName}", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        List<DbColumnModel> childColumns =
        [
            new(
                documentIdColumn,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                childOrdinalColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        ];

        List<TableConstraint> childConstraints =
        [
            new TableConstraint.ForeignKey(
                $"FK_{resourceName}Child_{resourceName}",
                [documentIdColumn],
                parentTableName,
                [documentIdColumn],
                ReferentialAction.Cascade,
                ReferentialAction.NoAction
            ),
        ];

        if (extraChildFk != null)
        {
            // Add the extra FK column to the child table
            childColumns.Add(
                new DbColumnModel(
                    extraChildFk.Columns[0],
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                )
            );
            childConstraints.Add(extraChildFk);
        }

        var childTable = new DbTableModel(
            childTableName,
            new JsonPathExpression("$.children[*]", []),
            new TableKey(
                $"PK_{resourceName}Child",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(childOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            childColumns,
            childConstraints
        );

        var model = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            parentTable,
            [parentTable, childTable],
            [],
            []
        );
        return new ConcreteResourceModel(key, ResourceStorageKind.RelationalTables, model);
    }

    private static AbstractIdentityTableInfo BuildAbstractIdentityTable(
        DbSchemaName schema,
        DbColumnName documentIdColumn,
        short keyId,
        string resourceName,
        string projectName = "Ed-Fi"
    )
    {
        var resource = new QualifiedResourceName(projectName, resourceName);
        var key = new ResourceKeyEntry(keyId, resource, "1.0.0", true);
        var table = new DbTableModel(
            new DbTableName(schema, $"{resourceName}Identity"),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{resourceName}Identity",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );
        return new AbstractIdentityTableInfo(key, table);
    }

    private static AbstractUnionViewInfo BuildAbstractUnionView(
        DbSchemaName schema,
        List<AbstractUnionViewOutputColumn> outputColumns,
        short abstractKeyId,
        string abstractResourceName,
        short concreteKeyId,
        string concreteResourceName,
        string projectName = "Ed-Fi"
    )
    {
        var documentIdColumn = new DbColumnName("DocumentId");
        var abstractResource = new QualifiedResourceName(projectName, abstractResourceName);
        var abstractKey = new ResourceKeyEntry(abstractKeyId, abstractResource, "1.0.0", true);
        var concreteKey = new ResourceKeyEntry(
            concreteKeyId,
            new QualifiedResourceName(projectName, concreteResourceName),
            "1.0.0",
            false
        );
        return new AbstractUnionViewInfo(
            abstractKey,
            new DbTableName(schema, abstractResourceName),
            outputColumns,
            [
                new AbstractUnionViewArm(
                    concreteKey,
                    new DbTableName(schema, concreteResourceName),
                    [
                        new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                        new AbstractUnionViewProjectionExpression.StringLiteral(concreteResourceName),
                    ]
                ),
            ]
        );
    }

    private static DbIndexInfo BuildIndex(
        DbSchemaName schema,
        string tableName,
        string columnName = "DocumentId"
    )
    {
        return new DbIndexInfo(
            new DbIndexName($"IX_{tableName}_{columnName}"),
            new DbTableName(schema, tableName),
            [new DbColumnName(columnName)],
            false,
            DbIndexKind.ForeignKeySupport
        );
    }

    private static DbTriggerInfo BuildTrigger(
        DbSchemaName schema,
        string tableName,
        string triggerSuffix = "Stamp"
    )
    {
        return new DbTriggerInfo(
            new DbTriggerName($"TR_{tableName}_{triggerSuffix}"),
            new DbTableName(schema, tableName),
            [new DbColumnName("DocumentId")],
            [],
            new TriggerKindParameters.DocumentStamping()
        );
    }

    /// <summary>
    /// Builds a model set with all elements in Zeta-first (non-alphabetical) order.
    /// Schema ordering is tpdm-first to exercise cross-schema sorting.
    /// </summary>
    internal static DerivedRelationalModelSet BuildZetaFirstOrder(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var tpdmSchema = new DbSchemaName("tpdm");
        var docId = new DbColumnName("DocumentId");
        var (alpha, zeta, gamma, alphaIdentity, zetaIdentity, gammaIdentity, alphaView, zetaView, gammaView) =
            BuildAllModels(edfiSchema, tpdmSchema, docId);

        // Intentionally non-alphabetical: Zeta first, then Gamma (tpdm), then Alpha
        return Build(
            dialect,
            projectSchemaOrder:
            [
                new ProjectSchemaInfo("tpdm", "TPDM", "1.0.0", false, tpdmSchema),
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
            ],
            resourceOrder: [zeta, gamma, alpha],
            abstractIdentityTableOrder: [zetaIdentity, gammaIdentity, alphaIdentity],
            abstractUnionViewOrder: [zetaView, gammaView, alphaView],
            indexOrder:
            [
                BuildIndex(tpdmSchema, "Gamma"),
                BuildIndex(edfiSchema, "Zeta"),
                BuildIndex(edfiSchema, "Alpha", "ReferentialId"),
                BuildIndex(edfiSchema, "Alpha"),
            ],
            triggerOrder:
            [
                BuildTrigger(tpdmSchema, "Gamma"),
                BuildTrigger(edfiSchema, "Zeta"),
                BuildTrigger(edfiSchema, "Alpha", "Version"),
                BuildTrigger(edfiSchema, "Alpha"),
            ]
        );
    }

    /// <summary>
    /// Builds a model set with the same data as <see cref="BuildZetaFirstOrder"/> but with
    /// all elements in Alpha-first (alphabetical) order.
    /// Schema ordering is edfi-first (alphabetical).
    /// </summary>
    internal static DerivedRelationalModelSet BuildAlphaFirstOrder(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var tpdmSchema = new DbSchemaName("tpdm");
        var docId = new DbColumnName("DocumentId");
        var (alpha, zeta, gamma, alphaIdentity, zetaIdentity, gammaIdentity, alphaView, zetaView, gammaView) =
            BuildAllModels(edfiSchema, tpdmSchema, docId);

        // Alphabetical order: Alpha first, then Zeta, then Gamma (tpdm)
        return Build(
            dialect,
            projectSchemaOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
                new ProjectSchemaInfo("tpdm", "TPDM", "1.0.0", false, tpdmSchema),
            ],
            resourceOrder: [alpha, zeta, gamma],
            abstractIdentityTableOrder: [alphaIdentity, zetaIdentity, gammaIdentity],
            abstractUnionViewOrder: [alphaView, zetaView, gammaView],
            indexOrder:
            [
                BuildIndex(edfiSchema, "Alpha"),
                BuildIndex(edfiSchema, "Alpha", "ReferentialId"),
                BuildIndex(edfiSchema, "Zeta"),
                BuildIndex(tpdmSchema, "Gamma"),
            ],
            triggerOrder:
            [
                BuildTrigger(edfiSchema, "Alpha"),
                BuildTrigger(edfiSchema, "Alpha", "Version"),
                BuildTrigger(edfiSchema, "Zeta"),
                BuildTrigger(tpdmSchema, "Gamma"),
            ]
        );
    }

    private static (
        ConcreteResourceModel alpha,
        ConcreteResourceModel zeta,
        ConcreteResourceModel gamma,
        AbstractIdentityTableInfo alphaIdentity,
        AbstractIdentityTableInfo zetaIdentity,
        AbstractIdentityTableInfo gammaIdentity,
        AbstractUnionViewInfo alphaView,
        AbstractUnionViewInfo zetaView,
        AbstractUnionViewInfo gammaView
    ) BuildAllModels(DbSchemaName edfiSchema, DbSchemaName tpdmSchema, DbColumnName documentIdColumn)
    {
        var discriminator = new DbColumnName("Discriminator");
        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(discriminator, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        ];

        // Cross-resource FK: AlphaChild references Zeta parent table via ZetaDocumentId
        var zetaDocIdColumn = new DbColumnName("ZetaDocumentId");
        var zetaParentTable = new DbTableName(edfiSchema, "Zeta");
        var alphaChildCrossRefFk = new TableConstraint.ForeignKey(
            "FK_AlphaChild_Zeta",
            [zetaDocIdColumn],
            zetaParentTable,
            [documentIdColumn],
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );

        return (
            BuildConcreteResource(
                edfiSchema,
                documentIdColumn,
                1,
                "Alpha",
                extraChildFk: alphaChildCrossRefFk
            ),
            BuildConcreteResource(edfiSchema, documentIdColumn, 2, "Zeta"),
            BuildConcreteResource(tpdmSchema, documentIdColumn, 5, "Gamma", projectName: "TPDM"),
            BuildAbstractIdentityTable(edfiSchema, documentIdColumn, 3, "AlphaAbstract"),
            BuildAbstractIdentityTable(edfiSchema, documentIdColumn, 4, "ZetaAbstract"),
            BuildAbstractIdentityTable(tpdmSchema, documentIdColumn, 6, "GammaAbstract", projectName: "TPDM"),
            BuildAbstractUnionView(edfiSchema, outputColumns, 3, "AlphaAbstract", 1, "Alpha"),
            BuildAbstractUnionView(edfiSchema, outputColumns, 4, "ZetaAbstract", 2, "Zeta"),
            BuildAbstractUnionView(
                tpdmSchema,
                outputColumns,
                6,
                "GammaAbstract",
                5,
                "Gamma",
                projectName: "TPDM"
            )
        );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Authorization Index DDL Emission Tests
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Verifies that the DDL emitter produces the expected CREATE INDEX statements with INCLUDE
/// clauses for the five PrimaryAssociation authorization indexes (DMS-1054), in both
/// PostgreSQL and SQL Server dialects. Index names use the canonical (post-key-unification)
/// column names.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_PrimaryAssociation_Authorization_Indexes(SqlDialect sqlDialect)
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(sqlDialect);
        _ddl = new RelationalModelDdlEmitter(dialect).Emit(
            AuthorizationIndexEmissionFixture.Build(dialect.Rules.Dialect)
        );
    }

    [TestCase("StudentSchoolAssociation", "SchoolId_Unified", "Student_DocumentId")]
    [TestCase("StudentContactAssociation", "Student_DocumentId", "Contact_DocumentId")]
    [TestCase(
        "StaffEducationOrganizationAssignmentAssociation",
        "EducationOrganization_EducationOrganizationId",
        "Staff_DocumentId"
    )]
    [TestCase(
        "StaffEducationOrganizationEmploymentAssociation",
        "EducationOrganization_EducationOrganizationId",
        "Staff_DocumentId"
    )]
    [TestCase(
        "StudentEducationOrganizationResponsibilityAssociation",
        "EducationOrganization_EducationOrganizationId",
        "Student_DocumentId"
    )]
    public void It_should_emit_create_index_with_include_for_primary_association(
        string tableName,
        string keyColumn,
        string includeColumn
    )
    {
        var indexName = $"IX_{tableName}_{keyColumn}_Auth";
        var (q1, q2) = QuotePair(sqlDialect);
        var expected =
            sqlDialect == SqlDialect.Pgsql
                ? $"CREATE INDEX IF NOT EXISTS {q1}{indexName}{q2} ON {q1}edfi{q2}.{q1}{tableName}{q2} ({q1}{keyColumn}{q2}) INCLUDE ({q1}{includeColumn}{q2});"
                : $"CREATE INDEX {q1}{indexName}{q2} ON {q1}edfi{q2}.{q1}{tableName}{q2} ({q1}{keyColumn}{q2}) INCLUDE ({q1}{includeColumn}{q2});";

        _ddl.Should().Contain(expected);
    }

    [Test]
    public void It_should_guard_mssql_create_index_with_existence_check()
    {
        if (sqlDialect != SqlDialect.Mssql)
        {
            Assert.Ignore("MSSQL-only assertion.");
        }

        // MSSQL has no IF NOT EXISTS for indexes; the emitter wraps each CREATE INDEX in an
        // IF NOT EXISTS-equivalent guard against sys.indexes.
        _ddl.Should().Contain("WHERE s.name = N'edfi'");
        _ddl.Should().Contain("i.name = N'IX_StudentSchoolAssociation_SchoolId_Unified_Auth'");
    }

    private static (string Open, string Close) QuotePair(SqlDialect dialect) =>
        dialect == SqlDialect.Pgsql ? ("\"", "\"") : ("[", "]");
}

/// <summary>
/// Builds a focused <see cref="DerivedRelationalModelSet"/> for authorization-index DDL
/// emission tests. Contains the five PrimaryAssociation root tables (with the key + INCLUDE
/// columns required by each auth index) and the matching authorization-kind index entries.
/// </summary>
internal static class AuthorizationIndexEmissionFixture
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private const string EdFi = "Ed-Fi";

    private static readonly (string Resource, string KeyColumn, string IncludeColumn)[] _entries =
    [
        ("StudentSchoolAssociation", "SchoolId_Unified", "Student_DocumentId"),
        ("StudentContactAssociation", "Student_DocumentId", "Contact_DocumentId"),
        (
            "StaffEducationOrganizationAssignmentAssociation",
            "EducationOrganization_EducationOrganizationId",
            "Staff_DocumentId"
        ),
        (
            "StaffEducationOrganizationEmploymentAssociation",
            "EducationOrganization_EducationOrganizationId",
            "Staff_DocumentId"
        ),
        (
            "StudentEducationOrganizationResponsibilityAssociation",
            "EducationOrganization_EducationOrganizationId",
            "Student_DocumentId"
        ),
    ];

    public static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var resources = new List<ConcreteResourceModel>();
        var indexes = new List<DbIndexInfo>();
        var resourceKeys = new List<ResourceKeyEntry>();

        short keyId = 1;
        foreach (var (resourceName, keyColumn, includeColumn) in _entries)
        {
            var resource = new QualifiedResourceName(EdFi, resourceName);
            var key = new ResourceKeyEntry(keyId++, resource, "1.0.0", false);
            resourceKeys.Add(key);
            resources.Add(BuildResource(resource, key, keyColumn, includeColumn));

            var tableName = new DbTableName(_edfiSchema, resourceName);
            indexes.Add(
                new DbIndexInfo(
                    new DbIndexName($"IX_{resourceName}_{keyColumn}_Auth"),
                    tableName,
                    [new DbColumnName(keyColumn)],
                    IsUnique: false,
                    Kind: DbIndexKind.Authorization,
                    IncludeColumns: [new DbColumnName(includeColumn)]
                )
            );
        }

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "abc123",
                (short)resourceKeys.Count,
                [0xAB, 0xC1],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        EdFi,
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                resourceKeys
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", EdFi, "1.0.0", false, _edfiSchema)],
            resources,
            [],
            [],
            indexes,
            []
        );
    }

    private static ConcreteResourceModel BuildResource(
        QualifiedResourceName resource,
        ResourceKeyEntry key,
        string keyColumn,
        string includeColumn
    )
    {
        var documentIdColumn = new DbColumnName("DocumentId");
        var rootTableName = new DbTableName(_edfiSchema, resource.ResourceName);
        var rootTable = new DbTableModel(
            rootTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{resource.ResourceName}",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName(keyColumn),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName(includeColumn),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var model = new RelationalResourceModel(
            resource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );
        return new ConcreteResourceModel(key, ResourceStorageKind.RelationalTables, model);
    }
}
