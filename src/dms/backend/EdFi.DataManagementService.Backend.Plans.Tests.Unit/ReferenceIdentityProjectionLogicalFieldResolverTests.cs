// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ReferenceIdentityProjectionLogicalFieldResolver
{
    private static readonly DbTableName _tableName = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );
    private static readonly DbColumnName _fkColumn = new("School_DocumentId");
    private static readonly JsonPathExpression _referenceObjectPath = Path("schoolReference");
    private static readonly JsonPathExpression _schoolIdPath = Path("schoolReference", "schoolId");
    private static readonly JsonPathExpression _schoolYearPath = Path("schoolReference", "schoolYear");
    private static readonly JsonPathExpression _localEducationAgencyIdPath = Path(
        "schoolReference",
        "localEducationAgencyId"
    );
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    [Test]
    public void It_should_resolve_stored_logical_fields_without_alias_presence_validation()
    {
        var tableModel = CreateTableModel(CreateColumn("SchoolId", _schoolIdPath));
        var binding = CreateBinding(CreateIdentityBinding(_schoolIdPath, "SchoolId"));

        var result = ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
            tableModel,
            binding,
            CreateException
        );

        result
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new ResolvedReferenceIdentityProjectionLogicalField(
                    _schoolIdPath,
                    new DbColumnName("SchoolId"),
                    [new DbColumnName("SchoolId")],
                    new DbColumnName("SchoolId")
                )
            );
    }

    [Test]
    public void It_should_resolve_logical_fields_in_binding_order()
    {
        var tableModel = CreateTableModel(
            CreateColumn(
                "SchoolId_Alias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Stored"), _fkColumn)
            ),
            CreateColumn("SchoolId_Stored", _schoolIdPath),
            CreateColumn("SchoolYear", _schoolYearPath)
        );
        var binding = CreateBinding(
            CreateIdentityBinding(_schoolIdPath, "SchoolId_Alias"),
            CreateIdentityBinding(_schoolYearPath, "SchoolYear")
        );

        var result = ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
            tableModel,
            binding,
            CreateException
        );

        result
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new ResolvedReferenceIdentityProjectionLogicalField(
                        _schoolIdPath,
                        new DbColumnName("SchoolId_Alias"),
                        [new DbColumnName("SchoolId_Alias")],
                        new DbColumnName("SchoolId_Stored")
                    ),
                    new ResolvedReferenceIdentityProjectionLogicalField(
                        _schoolYearPath,
                        new DbColumnName("SchoolYear"),
                        [new DbColumnName("SchoolYear")],
                        new DbColumnName("SchoolYear")
                    ),
                },
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_should_report_an_empty_identity_binding()
    {
        var tableModel = CreateTableModel(CreateColumn("SchoolId", _schoolIdPath));
        var binding = CreateBinding();

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' does not contain any identity bindings"
            );
    }

    [Test]
    public void It_should_report_multiple_storage_columns_for_one_logical_field()
    {
        var tableModel = CreateTableModel(
            CreateColumn("SchoolId_A", _schoolIdPath),
            CreateColumn("SchoolId_B", _schoolIdPath)
        );
        var binding = CreateBinding(
            CreateIdentityBinding(_schoolIdPath, "SchoolId_A"),
            CreateIdentityBinding(_schoolIdPath, "SchoolId_B")
        );

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' resolves to multiple storage columns: 'SchoolId_A' -> 'SchoolId_A', 'SchoolId_B' -> 'SchoolId_B'"
            );
    }

    [Test]
    public void It_should_report_multiple_presence_columns_for_one_logical_field()
    {
        var tableModel = CreateTableModel(
            CreateColumn("SchoolId_Stored", _schoolIdPath),
            CreateColumn(
                "SchoolId_Alias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Stored"), _fkColumn)
            )
        );
        var binding = CreateBinding(
            CreateIdentityBinding(_schoolIdPath, "SchoolId_Stored"),
            CreateIdentityBinding(_schoolIdPath, "SchoolId_Alias")
        );

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' resolves to multiple presence columns: 'SchoolId_Stored' -> '<none>', 'SchoolId_Alias' -> 'School_DocumentId'"
            );
    }

    [Test]
    public void It_should_report_unowned_unified_alias_presence()
    {
        var tableModel = CreateTableModel(
            CreateColumn(
                "SchoolId_Alias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Stored"), null)
            ),
            CreateColumn("SchoolId_Stored", _schoolIdPath)
        );
        var binding = CreateBinding(
            CreateIdentityBinding(_schoolIdPath, "SchoolId_Alias"),
            CreateIdentityBinding(_schoolIdPath, "SchoolId_Stored")
        );

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' resolves alias presence column '<none>', but owning reference FK column is 'School_DocumentId'"
            );
    }

    [Test]
    public void It_should_report_member_source_path_mismatches()
    {
        var tableModel = CreateTableModel(CreateColumn("SchoolId", _localEducationAgencyIdPath));
        var binding = CreateBinding(CreateIdentityBinding(_schoolIdPath, "SchoolId"));

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' member column 'SchoolId' has DbColumnModel.SourceJsonPath '$.schoolReference.localEducationAgencyId', which does not match grouped ReferenceJsonPath '$.schoolReference.schoolId'"
            );
    }

    [Test]
    public void It_should_report_missing_canonical_storage_columns()
    {
        var tableModel = CreateTableModel(
            CreateColumn(
                "SchoolId_Alias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Stored"), _fkColumn)
            )
        );
        var binding = CreateBinding(CreateIdentityBinding(_schoolIdPath, "SchoolId_Alias"));

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' member column 'SchoolId_Alias' resolves to missing canonical storage column 'SchoolId_Stored'"
            );
    }

    [Test]
    public void It_should_report_transitive_canonical_alias_columns()
    {
        var tableModel = CreateTableModel(
            CreateColumn(
                "SchoolId_Alias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_CanonicalAlias"), _fkColumn)
            ),
            CreateColumn(
                "SchoolId_CanonicalAlias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Stored"), _fkColumn)
            ),
            CreateColumn("SchoolId_Stored", _schoolIdPath)
        );
        var binding = CreateBinding(CreateIdentityBinding(_schoolIdPath, "SchoolId_Alias"));

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' member column 'SchoolId_Alias' resolves to canonical alias column 'SchoolId_CanonicalAlias'. Transitive UnifiedAlias resolution is not supported for alias column 'SchoolId_Alias' -> 'SchoolId_CanonicalAlias'"
            );
    }

    [Test]
    public void It_should_report_canonical_columns_that_are_not_stored()
    {
        var tableModel = CreateTableModel(
            CreateColumn(
                "SchoolId_Alias",
                _schoolIdPath,
                new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_NotStored"), _fkColumn)
            ),
            CreateColumn("SchoolId_NotStored", _schoolIdPath, new UnsupportedStorage())
        );
        var binding = CreateBinding(CreateIdentityBinding(_schoolIdPath, "SchoolId_Alias"));

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' member column 'SchoolId_Alias' resolves to canonical storage column 'SchoolId_NotStored', but that column is not stored"
            );
    }

    [Test]
    public void It_should_report_unsupported_binding_column_storage()
    {
        var tableModel = CreateTableModel(CreateColumn("SchoolId", _schoolIdPath, new UnsupportedStorage()));
        var binding = CreateBinding(CreateIdentityBinding(_schoolIdPath, "SchoolId"));

        Action act = () =>
            ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(
                tableModel,
                binding,
                CreateException
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "reference identity projection binding '$.schoolReference' on table 'edfi.StudentSchoolAssociation' grouped logical field '$.schoolReference.schoolId' member column 'SchoolId' uses unsupported storage metadata 'UnsupportedStorage'"
            );
    }

    private static DbTableModel CreateTableModel(params DbColumnModel[] columns)
    {
        return new DbTableModel(
            _tableName,
            Path(),
            new TableKey("PK_StudentSchoolAssociation", []),
            columns,
            []
        );
    }

    private static DbColumnModel CreateColumn(
        string columnName,
        JsonPathExpression sourceJsonPath,
        ColumnStorage? storage = null
    )
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String),
            IsNullable: false,
            sourceJsonPath,
            TargetResource: null,
            storage ?? new ColumnStorage.Stored()
        );
    }

    private static DocumentReferenceBinding CreateBinding(params ReferenceIdentityBinding[] identityBindings)
    {
        return new DocumentReferenceBinding(
            IsIdentityComponent: true,
            _referenceObjectPath,
            _tableName,
            _fkColumn,
            _schoolResource,
            identityBindings
        );
    }

    private static ReferenceIdentityBinding CreateIdentityBinding(
        JsonPathExpression referenceJsonPath,
        string columnName
    )
    {
        return new ReferenceIdentityBinding(
            IdentityJsonPath: new JsonPathExpression(
                referenceJsonPath.Canonical.Replace("$.schoolReference.", "$."),
                referenceJsonPath.Segments.Skip(1).ToArray()
            ),
            referenceJsonPath,
            new DbColumnName(columnName)
        );
    }

    private static JsonPathExpression Path(params string[] segments)
    {
        return new JsonPathExpression(
            segments.Length == 0 ? "$" : $"$.{string.Join('.', segments)}",
            segments.Select(segment => new JsonPathSegment.Property(segment)).ToArray()
        );
    }

    private static InvalidOperationException CreateException(string message) => new(message);

    private sealed record UnsupportedStorage : ColumnStorage;
}
