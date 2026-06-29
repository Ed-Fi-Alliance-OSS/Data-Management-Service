// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Pure-function tests for the canonical stored-column resolution shared by the trigger and
/// tracked-change derivation passes via <see cref="IdentityProjectionResolver"/>.
/// </summary>
[TestFixture]
public class Given_Canonical_Stored_Column_Resolution
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _schema = new("edfi");

    /// <summary>
    /// Builds a single-table model with a stored canonical column and a unified-alias column over it.
    /// </summary>
    private static DbTableModel BuildTable()
    {
        var canonical = new DbColumnModel(
            new DbColumnName("SchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var alias = new DbColumnModel(
            new DbColumnName("EducationOrganizationId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId"), PresenceColumn: null)
        );

        return new DbTableModel(
            new DbTableName(_schema, "School"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_School", [new DbKeyColumn(new DbColumnName("Id"), ColumnKind.Scalar)]),
            [canonical, alias],
            []
        );
    }

    /// <summary>
    /// It should return a stored column unchanged.
    /// </summary>
    [Test]
    public void It_should_return_a_stored_column_unchanged()
    {
        var resolved = IdentityProjectionResolver.ResolveToStoredColumn(
            new DbColumnName("SchoolId"),
            BuildTable(),
            _resource
        );

        resolved.Value.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should unwrap a unified-alias column to its canonical storage column.
    /// </summary>
    [Test]
    public void It_should_unwrap_a_unified_alias_to_its_canonical_column()
    {
        var resolved = IdentityProjectionResolver.ResolveToStoredColumn(
            new DbColumnName("EducationOrganizationId"),
            BuildTable(),
            _resource
        );

        resolved.Value.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should throw when the requested column is not present on the table.
    /// </summary>
    [Test]
    public void It_should_throw_for_a_missing_column()
    {
        var act = () =>
            IdentityProjectionResolver.ResolveToStoredColumn(
                new DbColumnName("DoesNotExist"),
                BuildTable(),
                _resource
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*DoesNotExist*");
    }

    /// <summary>
    /// It should de-duplicate columns that resolve to the same canonical storage column.
    /// </summary>
    [Test]
    public void It_should_de_duplicate_columns_resolving_to_the_same_canonical_column()
    {
        var resolved = IdentityProjectionResolver.ResolveColumnsToStored(
            [
                new DbColumnName("EducationOrganizationId"),
                new DbColumnName("SchoolId"),
                new DbColumnName("EducationOrganizationId"),
            ],
            BuildTable(),
            _resource
        );

        resolved.Select(column => column.Value).Should().Equal("SchoolId");
    }

    /// <summary>
    /// It should preserve first-seen order while de-duplicating.
    /// </summary>
    [Test]
    public void It_should_preserve_first_seen_order_while_de_duplicating()
    {
        var idColumn = new DbColumnModel(
            new DbColumnName("Id"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var table = BuildTable() with { Columns = [.. BuildTable().Columns, idColumn] };

        var resolved = IdentityProjectionResolver.ResolveColumnsToStored(
            [
                new DbColumnName("Id"),
                new DbColumnName("EducationOrganizationId"),
                new DbColumnName("SchoolId"),
            ],
            table,
            _resource
        );

        resolved.Select(column => column.Value).Should().Equal("Id", "SchoolId");
    }
}

/// <summary>
/// Pure-function tests for selecting the single hash-element column out of a same-site logical
/// reference field group, where key unification can fan one identity JSON path out to multiple
/// physical member columns that all share one canonical storage column.
/// </summary>
[TestFixture]
public class Given_Key_Unified_Identity_Element_Column_Selection
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "Registration");
    private static readonly DbSchemaName _schema = new("edfi");

    /// <summary>
    /// Builds a root table with one stored canonical column, two unified-alias members over it, and a
    /// stored column that does not participate in key unification.
    /// </summary>
    private static DbTableModel BuildTable()
    {
        var canonical = new DbColumnModel(
            new DbColumnName("SchoolId_Unified"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var primaryAlias = new DbColumnModel(
            new DbColumnName("Offering_PrimarySchoolReferenceSchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Unified"), PresenceColumn: null)
        );

        var secondaryAlias = new DbColumnModel(
            new DbColumnName("Offering_SecondarySchoolReferenceSchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Unified"), PresenceColumn: null)
        );

        var unrelated = new DbColumnModel(
            new DbColumnName("RegistrationId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, 20),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        return new DbTableModel(
            new DbTableName(_schema, "Registration"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_Registration", [new DbKeyColumn(new DbColumnName("Id"), ColumnKind.Scalar)]),
            [canonical, primaryAlias, secondaryAlias, unrelated],
            []
        );
    }

    /// <summary>
    /// It should throw for an empty member column list, because an identity hash element must be
    /// represented by at least one column.
    /// </summary>
    [Test]
    public void It_should_throw_for_an_empty_member_column_list()
    {
        var act = () =>
            IdentityProjectionResolver.SelectIdentityElementColumn(
                [],
                BuildTable(),
                "$.offeringReference.schoolId",
                _resource
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$.offeringReference.schoolId*")
            .WithMessage("*empty logical reference field group*");
    }

    /// <summary>
    /// It should return a single member column unchanged.
    /// </summary>
    [Test]
    public void It_should_return_a_single_member_column_unchanged()
    {
        var selected = IdentityProjectionResolver.SelectIdentityElementColumn(
            [new DbColumnName("RegistrationId")],
            BuildTable(),
            "$.registrationId",
            _resource
        );

        selected.Value.Should().Be("RegistrationId");
    }

    /// <summary>
    /// It should select the first member column when all members share one canonical storage column.
    /// </summary>
    [Test]
    public void It_should_select_the_first_member_when_all_members_share_canonical_storage()
    {
        var selected = IdentityProjectionResolver.SelectIdentityElementColumn(
            [
                new DbColumnName("Offering_PrimarySchoolReferenceSchoolId"),
                new DbColumnName("Offering_SecondarySchoolReferenceSchoolId"),
            ],
            BuildTable(),
            "$.offeringReference.schoolId",
            _resource
        );

        selected.Value.Should().Be("Offering_PrimarySchoolReferenceSchoolId");
    }

    /// <summary>
    /// It should throw when members resolve to different canonical storage columns, because multiple
    /// stored values for one identity JSON path cannot be collapsed into a single hash element.
    /// </summary>
    [Test]
    public void It_should_throw_when_members_resolve_to_different_canonical_columns()
    {
        var act = () =>
            IdentityProjectionResolver.SelectIdentityElementColumn(
                [
                    new DbColumnName("Offering_PrimarySchoolReferenceSchoolId"),
                    new DbColumnName("RegistrationId"),
                ],
                BuildTable(),
                "$.offeringReference.schoolId",
                _resource
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$.offeringReference.schoolId*")
            .WithMessage("*SchoolId_Unified*")
            .WithMessage("*RegistrationId*");
    }
}

/// <summary>
/// Pure-function tests for the presence-aware source-value equivalence used by the MSSQL identity
/// propagation trigger via <see cref="IdentityProjectionResolver.ComputeSourceValueSignature"/> and
/// <see cref="IdentityProjectionResolver.ValidatePropagationSourceEquivalence"/>. The trigger collapses
/// key-unified identity bindings onto a single referrer column and emits one deterministic representative
/// source; that collapse is sound only when the candidate sources read the same effective value.
/// </summary>
[TestFixture]
public class Given_Propagation_Source_Value_Equivalence
{
    private static readonly QualifiedResourceName _target = new("Ed-Fi", "CourseOffering");
    private static readonly QualifiedResourceName _referrer = new("Ed-Fi", "Section");
    private static readonly DbSchemaName _schema = new("edfi");

    private static DbColumnModel Stored(string name, bool nullable) =>
        new(
            new DbColumnName(name),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: nullable,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel Alias(string name, string canonical, string? presence) =>
        new(
            new DbColumnName(name),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(
                new DbColumnName(canonical),
                presence is null ? null : new DbColumnName(presence)
            )
        );

    /// <summary>
    /// Builds a CourseOffering-shaped trigger table: one canonical SchoolId, two aliases gated by
    /// non-nullable DocumentId columns (the real shape), two aliases gated by nullable DocumentId columns,
    /// a gate-free alias, and a second canonical with its own alias for the divergent-source case.
    /// </summary>
    private static DbTableModel BuildTable()
    {
        DbColumnModel[] columns =
        [
            Stored("SchoolId_Unified", nullable: false),
            Stored("OtherId_Unified", nullable: false),
            Stored("School_DocumentId", nullable: false),
            Stored("Session_DocumentId", nullable: false),
            Stored("OptionalA_DocumentId", nullable: true),
            Stored("OptionalB_DocumentId", nullable: true),
            Alias("Bare_SchoolId", "SchoolId_Unified", presence: null),
            Alias("School_SchoolId", "SchoolId_Unified", "School_DocumentId"),
            Alias("Session_SchoolId", "SchoolId_Unified", "Session_DocumentId"),
            Alias("OptionalA_SchoolId", "SchoolId_Unified", "OptionalA_DocumentId"),
            Alias("OptionalB_SchoolId", "SchoolId_Unified", "OptionalB_DocumentId"),
            Alias("Other_SchoolId", "OtherId_Unified", presence: null),
        ];

        return new DbTableModel(
            new DbTableName(_schema, "CourseOffering"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                "PK_CourseOffering",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.Scalar)]
            ),
            columns,
            []
        );
    }

    private static SourceValueSignature Signature(string column) =>
        IdentityProjectionResolver.ComputeSourceValueSignature(
            BuildTable(),
            new DbColumnName(column),
            _target
        );

    private static void Validate(params (string Path, string Column)[] candidates) =>
        IdentityProjectionResolver.ValidatePropagationSourceEquivalence(
            BuildTable(),
            _target,
            _referrer,
            new DbColumnName("SchoolId_Unified"),
            candidates
                .Select(candidate =>
                    (JsonPathExpressionCompiler.Compile(candidate.Path), new DbColumnName(candidate.Column))
                )
                .ToArray()
        );

    /// <summary>
    /// It should treat a stored column as reading its own value with no presence gate.
    /// </summary>
    [Test]
    public void It_should_treat_a_stored_column_as_its_own_value()
    {
        Signature("SchoolId_Unified")
            .Should()
            .Be(new SourceValueSignature(new DbColumnName("SchoolId_Unified"), null));
    }

    /// <summary>
    /// It should unwrap a gate-free unified alias to its canonical column.
    /// </summary>
    [Test]
    public void It_should_unwrap_a_gate_free_alias_to_its_canonical()
    {
        Signature("Bare_SchoolId")
            .Should()
            .Be(new SourceValueSignature(new DbColumnName("SchoolId_Unified"), null));
    }

    /// <summary>
    /// It should drop a non-nullable presence gate: such a gate can never null the alias out, so it does
    /// not distinguish the value from the bare canonical.
    /// </summary>
    [Test]
    public void It_should_drop_a_non_nullable_presence_gate()
    {
        Signature("School_SchoolId")
            .Should()
            .Be(new SourceValueSignature(new DbColumnName("SchoolId_Unified"), null));
    }

    /// <summary>
    /// It should retain a nullable presence gate, which can null the alias out and therefore changes the
    /// effective value.
    /// </summary>
    [Test]
    public void It_should_retain_a_nullable_presence_gate()
    {
        Signature("OptionalA_SchoolId")
            .Should()
            .Be(
                new SourceValueSignature(
                    new DbColumnName("SchoolId_Unified"),
                    new DbColumnName("OptionalA_DocumentId")
                )
            );
    }

    /// <summary>
    /// It should make a stored column and aliases over it gated by non-nullable columns all equivalent.
    /// This is the CourseOffering shape: <c>School_SchoolId</c> and <c>Session_SchoolId</c> read
    /// <c>SchoolId_Unified</c> unconditionally because their DocumentId gates are non-nullable, so the
    /// guard must accept them rather than reject value-equal sources.
    /// </summary>
    [Test]
    public void It_should_make_non_nullable_gated_aliases_equivalent_to_the_bare_canonical()
    {
        Signature("School_SchoolId").Should().Be(Signature("Session_SchoolId"));
        Signature("School_SchoolId").Should().Be(Signature("SchoolId_Unified"));
    }

    /// <summary>
    /// It should allow value-equivalent candidates collapsing onto one referrer column, proving the
    /// CourseOffering shape passes and deterministic representative selection is permitted.
    /// </summary>
    [Test]
    public void It_should_allow_value_equivalent_candidates()
    {
        var act = () =>
            Validate(
                ("$.schoolReference.schoolId", "School_SchoolId"),
                ("$.sessionReference.schoolId", "Session_SchoolId")
            );

        act.Should().NotThrow();
    }

    /// <summary>
    /// It should throw when candidates collapsing onto one referrer column resolve to different canonical
    /// source columns — a single representative cannot stand in for distinct stored values.
    /// </summary>
    [Test]
    public void It_should_throw_when_candidates_resolve_to_different_canonical_sources()
    {
        var act = () =>
            Validate(
                ("$.schoolReference.schoolId", "School_SchoolId"),
                ("$.otherReference.schoolId", "Other_SchoolId")
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*School_SchoolId*")
            .WithMessage("*Other_SchoolId*")
            .WithMessage("*SchoolId_Unified*")
            .WithMessage("*OtherId_Unified*");
    }

    /// <summary>
    /// It should throw when candidates share a canonical column but are gated by different nullable
    /// presence columns, because the aliases can null out independently and therefore diverge in value.
    /// </summary>
    [Test]
    public void It_should_throw_when_candidates_differ_on_nullable_presence_gates()
    {
        var act = () =>
            Validate(
                ("$.optionalAReference.schoolId", "OptionalA_SchoolId"),
                ("$.optionalBReference.schoolId", "OptionalB_SchoolId")
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*OptionalA_DocumentId*")
            .WithMessage("*OptionalB_DocumentId*");
    }
}
