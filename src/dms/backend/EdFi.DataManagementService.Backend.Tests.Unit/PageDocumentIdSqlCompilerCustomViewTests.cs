// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_CustomView_Wiring_And_Sql_Emission
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _authSchema = new("auth");
    private static readonly DbTableName _studentSchoolAssociationTable = new(
        _edfiSchema,
        "StudentSchoolAssociation"
    );
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"Student_DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"auth\".\"StratA\" t0)"
    )]
    [TestCase(SqlDialect.Mssql, "r.[Student_DocumentId] IN (SELECT t0.[DocumentId] FROM [auth].[StratA] t0)")]
    public void It_emits_the_direct_custom_view_basis_path_for_page_and_total_count(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(CreateCustomViewSpec(CreateDirectStudentCustomViewCheck("StratA")));

        plan.PageDocumentIdSql.Should().Contain(expectedPredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicate);
        plan.PageParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("offset", "limit");
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Should().BeEmpty();
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"CourseTranscript\" t0 JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = t0.\"StudentAcademicRecord_DocumentId\" WHERE t1.\"Student_DocumentId\" IN (SELECT t2.\"DocumentId\" FROM \"auth\".\"StudentWithCTE\" t2))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[CourseTranscript] t0 JOIN [edfi].[StudentAcademicRecord] t1 ON t1.[DocumentId] = t0.[StudentAcademicRecord_DocumentId] WHERE t1.[Student_DocumentId] IN (SELECT t2.[DocumentId] FROM [auth].[StudentWithCTE] t2))"
    )]
    public void It_emits_the_transitive_custom_view_basis_path(SqlDialect dialect, string expectedPredicate)
    {
        var rootTable = new DbTableName(_edfiSchema, "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(_edfiSchema, "StudentAcademicRecord");
        var studentTable = new DbTableName(_edfiSchema, "Student");
        var check = new PageDocumentIdAuthorizationCustomViewCheck(
            "StudentWithCTE",
            0,
            new DbTableName(_authSchema, "StudentWithCTE"),
            _documentIdColumn,
            [
                new ColumnPathStep(
                    rootTable,
                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                    studentAcademicRecordTable,
                    _documentIdColumn
                ),
                new ColumnPathStep(
                    studentAcademicRecordTable,
                    new DbColumnName("Student_DocumentId"),
                    studentTable,
                    _documentIdColumn
                ),
            ],
            rootTable,
            _documentIdColumn
        );
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var plan = compiler.Compile(CreateCustomViewSpec(check, rootTable));

        plan.PageDocumentIdSql.Should().Contain(expectedPredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicate);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"StudentTransportation\" t0 WHERE t0.\"TransportationTypeDescriptor_DescriptorId\" IN (SELECT t1.\"DocumentId\" FROM \"auth\".\"TransportationTypeDescriptorWithABus\" t1))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[StudentTransportation] t0 WHERE t0.[TransportationTypeDescriptor_DescriptorId] IN (SELECT t1.[DocumentId] FROM [auth].[TransportationTypeDescriptorWithABus] t1))"
    )]
    public void It_emits_the_directly_referenced_descriptor_basis_path(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        // TransportationTypeDescriptorWithABus assigned to StudentTransportation: the descriptor is a
        // basis resource referenced directly by the subject via its *_DescriptorId FK. The FK holds the
        // descriptor's DocumentId (mirroring dms.Descriptor.DocumentId), so the emitted path filters that
        // FK against the DocumentIds returned by the custom view. See auth.md "Resolving the DB columns".
        var rootTable = new DbTableName(_edfiSchema, "StudentTransportation");
        var descriptorTable = new DbTableName(new DbSchemaName("dms"), "Descriptor");
        var check = new PageDocumentIdAuthorizationCustomViewCheck(
            "TransportationTypeDescriptorWithABus",
            0,
            new DbTableName(_authSchema, "TransportationTypeDescriptorWithABus"),
            _documentIdColumn,
            [
                new ColumnPathStep(
                    rootTable,
                    new DbColumnName("TransportationTypeDescriptor_DescriptorId"),
                    descriptorTable,
                    _documentIdColumn
                ),
            ],
            rootTable,
            _documentIdColumn
        );
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var plan = compiler.Compile(CreateCustomViewSpec(check, rootTable));

        plan.PageDocumentIdSql.Should().Contain(expectedPredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicate);
    }

    [Test]
    public void It_orders_namespace_and_custom_view_checks_by_raw_configured_index()
    {
        var namespaceCheck = new NamespaceAuthorizationCheckSpec(
            0,
            NamespaceAuthorizationCheckValueSource.Stored,
            _studentSchoolAssociationTable,
            new DbColumnName("Namespace"),
            RawConfiguredIndex: 2
        );
        var customViewCheck = CreateDirectStudentCustomViewCheck("StudentWithCTE", rawConfiguredIndex: 1);
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _studentSchoolAssociationTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks: [namespaceCheck],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                CustomViewChecks: [customViewCheck]
            )
        );

        var plan = compiler.Compile(spec);

        AssertFragmentAppearsBefore(plan.PageDocumentIdSql, "StudentWithCTE", "Namespace");
    }

    private static PageDocumentIdQuerySpec CreateCustomViewSpec(
        PageDocumentIdAuthorizationCustomViewCheck check,
        DbTableName? rootTable = null
    ) =>
        new(
            RootTable: rootTable ?? _studentSchoolAssociationTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            IncludeTotalCountSql: true,
            Authorization: new PageDocumentIdAuthorizationSpec(Strategies: [], CustomViewChecks: [check])
        );

    private static PageDocumentIdAuthorizationCustomViewCheck CreateDirectStudentCustomViewCheck(
        string strategyName,
        int rawConfiguredIndex = 0
    ) =>
        new(
            strategyName,
            rawConfiguredIndex,
            new DbTableName(_authSchema, strategyName),
            _documentIdColumn,
            [
                new ColumnPathStep(
                    _studentSchoolAssociationTable,
                    new DbColumnName("Student_DocumentId"),
                    null,
                    null
                ),
            ],
            _studentSchoolAssociationTable,
            _documentIdColumn
        );

    private static void AssertFragmentAppearsBefore(string sql, string firstFragment, string secondFragment)
    {
        var firstIndex = sql.IndexOf(firstFragment, StringComparison.Ordinal);
        var secondIndex = sql.IndexOf(secondFragment, StringComparison.Ordinal);

        firstIndex.Should().BeGreaterThanOrEqualTo(0);
        secondIndex.Should().BeGreaterThanOrEqualTo(0);
        firstIndex.Should().BeLessThan(secondIndex);
    }
}
