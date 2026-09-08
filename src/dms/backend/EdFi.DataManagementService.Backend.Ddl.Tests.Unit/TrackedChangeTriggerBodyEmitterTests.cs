// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — plan resolution tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_Building_A_Plan
{
    private TrackedChangeInsertPlan _plan = null!;

    [SetUp]
    public void SetUp()
    {
        var tableInfo = TrackedChangeEmitterFixture.BuildTrackedTable();
        var sourceModel = TrackedChangeEmitterFixture.BuildSourceTableModel();
        _plan = TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfo, sourceModel);
    }

    [Test]
    public void It_should_resolve_a_plain_scalar_by_source_json_path()
    {
        // value[0] is the BeginDate scalar — resolved by SourceJsonPath matching,
        // because CanonicalStorageColumn is null on that column entry.
        var value = _plan.Values[0];
        value.Kind.Should().Be(TrackedChangeValueSourceKind.DirectColumn);
        value.SourceColumn.Should().Be(new DbColumnName("BeginDate"));
        value.JoinIndex.Should().Be(-1);
    }

    [Test]
    public void It_should_prefer_the_canonical_storage_column_for_unified_scalars()
    {
        // value[1] is SchoolId — has CanonicalStorageColumn = SchoolId_Unified,
        // so the emitter must use that rather than resolving by JsonPath.
        var value = _plan.Values[1];
        value.Kind.Should().Be(TrackedChangeValueSourceKind.DirectColumn);
        value.SourceColumn.Should().Be(new DbColumnName("SchoolId_Unified"));
        value.JoinIndex.Should().Be(-1);
    }

    [Test]
    public void It_should_resolve_descriptor_part_columns_to_the_table_level_join()
    {
        // values[2] and [3] are the GradeTypeDescriptor Namespace and CodeValue columns.
        var ns = _plan.Values[2];
        ns.Kind.Should().Be(TrackedChangeValueSourceKind.DescriptorJoin);
        ns.JoinIndex.Should().Be(0);
        ns.SourceColumn.Should().BeNull();

        var cv = _plan.Values[3];
        cv.Kind.Should().Be(TrackedChangeValueSourceKind.DescriptorJoin);
        cv.JoinIndex.Should().Be(0);
        cv.SourceColumn.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_person_columns_to_the_table_level_person_join()
    {
        // value[4] is the Student DocumentId column.
        var value = _plan.Values[4];
        value.Kind.Should().Be(TrackedChangeValueSourceKind.PersonJoin);
        value.JoinIndex.Should().Be(0);
        value.SourceColumn.Should().BeNull();
    }

    [Test]
    public void It_should_carry_the_id_and_change_version_system_columns()
    {
        _plan.IdColumn.Should().Be(new DbColumnName("Id"));
        _plan.ChangeVersionColumn.Should().Be(new DbColumnName("ChangeVersion"));
    }
}

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_With_Invalid_Inventory
{
    [Test]
    public void It_should_throw_when_scalar_source_json_path_resolves_to_no_column()
    {
        // Build a tracked table whose scalar path does not match any source column.
        var unresolvablePath = "$.nonExistent.value";
        var tableInfo = TrackedChangeEmitterFixture.BuildTrackedTable(overrideScalarPath: unresolvablePath);
        var sourceModel = TrackedChangeEmitterFixture.BuildSourceTableModel();

        var act = () => TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfo, sourceModel);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{unresolvablePath}*");
    }

    [Test]
    public void It_should_throw_when_descriptor_join_name_is_missing()
    {
        // Build a tracked table whose descriptor columns point to "NoSuchJoin" — a name that is
        // not present in DescriptorJoins — proving lookup is by name, not list non-emptiness.
        var tableInfo = TrackedChangeEmitterFixture.BuildTrackedTable(
            overrideDescriptorJoinName: "NoSuchJoin"
        );
        var sourceModel = TrackedChangeEmitterFixture.BuildSourceTableModel();

        var act = () => TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfo, sourceModel);

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchJoin*");
    }

    [Test]
    public void It_should_throw_when_scalar_source_json_path_matches_multiple_columns()
    {
        // Build a source model with two columns sharing the same SourceJsonPath — exercises the
        // multiple-matches branch in ResolveScalar.
        var ambiguousPath = "$.gradingPeriodReference.beginDate";
        var tableInfo = TrackedChangeEmitterFixture.BuildTrackedTable();
        var sourceModel = TrackedChangeEmitterFixture.BuildSourceTableModel(addDuplicateBeginDate: true);

        var act = () => TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfo, sourceModel);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ambiguousPath}*");
    }

    [Test]
    public void It_should_throw_when_person_join_name_is_missing()
    {
        // Build a tracked table with a person column whose join name is not in PersonJoins.
        var tableInfo = TrackedChangeEmitterFixture.BuildTrackedTable();
        var tableInfoWithoutPersonJoin = tableInfo with { PersonJoins = [] };
        var sourceModel = TrackedChangeEmitterFixture.BuildSourceTableModel();

        var act = () => TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfoWithoutPersonJoin, sourceModel);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Student*");
    }

    [Test]
    public void It_should_throw_when_person_join_step_has_null_target()
    {
        // Build a person join whose steps have null TargetTable / TargetColumnName.
        var tableInfo = TrackedChangeEmitterFixture.BuildTrackedTable(useInvalidPersonJoinPath: true);
        var sourceModel = TrackedChangeEmitterFixture.BuildSourceTableModel();

        var act = () => TrackedChangeTriggerBodyEmitter.BuildPlan(tableInfo, sourceModel);

        act.Should().Throw<InvalidOperationException>().WithMessage("*target*");
    }
}

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — PostgreSQL rendering tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_Rendering_Pgsql
{
    private string _tombstone = default!;
    private string _keyChange = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var plan = TrackedChangeTriggerBodyEmitter.BuildPlan(
            TrackedChangeEmitterFixture.BuildTrackedTable(),
            TrackedChangeEmitterFixture.BuildSourceTableModel()
        );

        var tombstoneWriter = new SqlWriter(dialect);
        TrackedChangeTriggerBodyEmitter.EmitPgsqlTombstoneInsert(
            tombstoneWriter,
            dialect,
            plan,
            new DbColumnName("DocumentId")
        );
        _tombstone = tombstoneWriter.ToString();

        var keyChangeWriter = new SqlWriter(dialect);
        TrackedChangeTriggerBodyEmitter.EmitPgsqlKeyChangeInsert(
            keyChangeWriter,
            dialect,
            plan,
            new DbColumnName("DocumentId")
        );
        _keyChange = keyChangeWriter.ToString();
    }

    [Test]
    public void It_should_render_the_tombstone_with_old_columns_only()
    {
        _tombstone.Should().Contain("INSERT INTO \"tracked_changes_edfi\".\"Grade\"");
        _tombstone.Should().Contain("\"OldBeginDate\"");
        _tombstone.Should().NotContain("\"NewBeginDate\"");
        _tombstone.Should().Contain("\"Id\"");
        _tombstone.Should().Contain("\"ChangeVersion\"");
    }

    [Test]
    public void It_should_read_tombstone_values_from_the_OLD_image_and_document_row()
    {
        _tombstone.Should().Contain("OLD.\"BeginDate\"");
        _tombstone.Should().Contain("OLD.\"SchoolId_Unified\"");
        _tombstone.Should().Contain("doc.\"DocumentUuid\"");
        _tombstone.Should().Contain("doc.\"ContentVersion\"");
        _tombstone.Should().Contain("WHERE doc.\"DocumentId\" = OLD.\"DocumentId\"");
    }

    [Test]
    public void It_should_join_descriptors_and_person_chain_for_the_old_image()
    {
        _tombstone
            .Should()
            .Contain(
                "INNER JOIN \"dms\".\"Descriptor\" oldDj0 ON oldDj0.\"DocumentId\" = OLD.\"GradeTypeDescriptor_DescriptorId\""
            );
        _tombstone
            .Should()
            .Contain(
                "INNER JOIN \"edfi\".\"StudentSectionAssociation\" oldPj0s0 ON oldPj0s0.\"DocumentId\" = OLD.\"StudentSectionAssociation_DocumentId\""
            );
        _tombstone
            .Should()
            .Contain(
                "INNER JOIN \"edfi\".\"Student\" oldPj0s1 ON oldPj0s1.\"DocumentId\" = oldPj0s0.\"Student_DocumentId\""
            );
        _tombstone.Should().Contain("oldDj0.\"Namespace\"");
        _tombstone.Should().Contain("oldDj0.\"CodeValue\"");
        _tombstone.Should().Contain("oldPj0s1.\"DocumentId\"");
    }

    [Test]
    public void It_should_render_the_key_change_with_old_and_new_images()
    {
        _keyChange.Should().Contain("\"OldBeginDate\"");
        _keyChange.Should().Contain("\"NewBeginDate\"");
        _keyChange.Should().Contain("OLD.\"BeginDate\"");
        _keyChange.Should().Contain("NEW.\"BeginDate\"");
        _keyChange.Should().Contain("newDj0.\"Namespace\"");
        _keyChange.Should().Contain("newPj0s1.\"DocumentId\"");
        _keyChange.Should().Contain("_stampedContentVersion");
        _keyChange.Should().Contain("WHERE doc.\"DocumentId\" = NEW.\"DocumentId\"");
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN \"dms\".\"Descriptor\" newDj0 ON newDj0.\"DocumentId\" = NEW.\"GradeTypeDescriptor_DescriptorId\""
            );
        _keyChange.Should().Contain("oldDj0.\"Namespace\"");
        _keyChange.Should().Contain("oldDj0.\"CodeValue\"");
        _keyChange.Should().Contain("oldPj0s1.\"DocumentId\"");
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN \"dms\".\"Descriptor\" oldDj0 ON oldDj0.\"DocumentId\" = OLD.\"GradeTypeDescriptor_DescriptorId\""
            );
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN \"edfi\".\"StudentSectionAssociation\" oldPj0s0 ON oldPj0s0.\"DocumentId\" = OLD.\"StudentSectionAssociation_DocumentId\""
            );
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN \"edfi\".\"Student\" oldPj0s1 ON oldPj0s1.\"DocumentId\" = oldPj0s0.\"Student_DocumentId\""
            );
    }
}

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — SQL Server rendering tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_Rendering_Mssql
{
    private string _tombstone = default!;
    private string _keyChange = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var plan = TrackedChangeTriggerBodyEmitter.BuildPlan(
            TrackedChangeEmitterFixture.BuildTrackedTable(),
            TrackedChangeEmitterFixture.BuildSourceTableModel()
        );

        var tombstoneWriter = new SqlWriter(dialect);
        TrackedChangeTriggerBodyEmitter.EmitMssqlTombstoneInsert(
            tombstoneWriter,
            dialect,
            plan,
            new DbColumnName("DocumentId")
        );
        _tombstone = tombstoneWriter.ToString();

        var keyChangeWriter = new SqlWriter(dialect);
        TrackedChangeTriggerBodyEmitter.EmitMssqlKeyChangeInsert(
            keyChangeWriter,
            dialect,
            plan,
            new DbColumnName("DocumentId")
        );
        _keyChange = keyChangeWriter.ToString();
    }

    [Test]
    public void It_should_render_the_tombstone_from_the_deleted_set()
    {
        _tombstone.Should().Contain("INSERT INTO [tracked_changes_edfi].[Grade]");
        _tombstone.Should().Contain("[OldBeginDate]");
        _tombstone.Should().NotContain("[NewBeginDate]");
        _tombstone.Should().Contain("FROM deleted del");
        _tombstone.Should().Contain("INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = del.[DocumentId]");
        _tombstone
            .Should()
            .Contain(
                "INNER JOIN [dms].[Descriptor] oldDj0 ON oldDj0.[DocumentId] = del.[GradeTypeDescriptor_DescriptorId]"
            );
        _tombstone
            .Should()
            .Contain(
                "INNER JOIN [edfi].[StudentSectionAssociation] oldPj0s0 ON oldPj0s0.[DocumentId] = del.[StudentSectionAssociation_DocumentId]"
            );
        _tombstone
            .Should()
            .Contain(
                "INNER JOIN [edfi].[Student] oldPj0s1 ON oldPj0s1.[DocumentId] = oldPj0s0.[Student_DocumentId]"
            );
        _tombstone.Should().Contain("oldDj0.[Namespace]");
        _tombstone.Should().Contain("doc.[DocumentUuid]");
        _tombstone.Should().Contain("doc.[ContentVersion]");
        _tombstone.Should().Contain("oldPj0s1.[DocumentId]");
        // The statement terminator must be attached to the last content line, never on its own line.
        _tombstone.Split('\n').Should().NotContain(l => l.Trim() == ";");
    }

    [Test]
    public void It_should_render_the_key_change_from_the_identity_changed_workset()
    {
        _keyChange.Should().Contain("[OldBeginDate]");
        _keyChange.Should().Contain("[NewBeginDate]");
        _keyChange.Should().Contain("FROM @identityChangedDocs idc");
        _keyChange.Should().Contain("INNER JOIN inserted i ON i.[DocumentId] = idc.[DocumentId]");
        _keyChange.Should().Contain("INNER JOIN deleted del ON del.[DocumentId] = i.[DocumentId]");
        _keyChange.Should().Contain("INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = i.[DocumentId]");
        _keyChange.Should().Contain("del.[BeginDate]");
        _keyChange.Should().Contain("i.[BeginDate]");
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN [dms].[Descriptor] newDj0 ON newDj0.[DocumentId] = i.[GradeTypeDescriptor_DescriptorId]"
            );
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN [edfi].[StudentSectionAssociation] newPj0s0 ON newPj0s0.[DocumentId] = i.[StudentSectionAssociation_DocumentId]"
            );
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN [edfi].[Student] newPj0s1 ON newPj0s1.[DocumentId] = newPj0s0.[Student_DocumentId]"
            );
        _keyChange.Should().Contain("doc.[DocumentUuid]");
        _keyChange.Should().Contain("idc.[ContentVersion]");
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN [dms].[Descriptor] oldDj0 ON oldDj0.[DocumentId] = del.[GradeTypeDescriptor_DescriptorId]"
            );
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN [edfi].[StudentSectionAssociation] oldPj0s0 ON oldPj0s0.[DocumentId] = del.[StudentSectionAssociation_DocumentId]"
            );
        _keyChange
            .Should()
            .Contain(
                "INNER JOIN [edfi].[Student] oldPj0s1 ON oldPj0s1.[DocumentId] = oldPj0s0.[Student_DocumentId]"
            );
        _keyChange.Should().Contain("oldDj0.[Namespace]");
        _keyChange.Should().Contain("oldDj0.[CodeValue]");
        _keyChange.Should().Contain("oldPj0s1.[DocumentId]");
    }
}

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — nullable join rendering
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_Rendering_Nullable_Joins
{
    [Test]
    public void It_should_render_nullable_descriptor_joins_as_left_joins()
    {
        var pgsqlDialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var mssqlDialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var plan = TrackedChangeTriggerBodyEmitter.BuildPlan(
            TrackedChangeEmitterFixture.BuildTrackedTable(nullableDescriptorJoin: true),
            TrackedChangeEmitterFixture.BuildSourceTableModel()
        );

        var pgsql = RenderPgsqlKeyChange(pgsqlDialect, plan);
        var mssql = RenderMssqlKeyChange(mssqlDialect, plan);

        pgsql
            .Should()
            .Contain(
                "LEFT JOIN \"dms\".\"Descriptor\" oldDj0 ON oldDj0.\"DocumentId\" = OLD.\"GradeTypeDescriptor_DescriptorId\""
            );
        pgsql
            .Should()
            .Contain(
                "LEFT JOIN \"dms\".\"Descriptor\" newDj0 ON newDj0.\"DocumentId\" = NEW.\"GradeTypeDescriptor_DescriptorId\""
            );
        mssql
            .Should()
            .Contain(
                "LEFT JOIN [dms].[Descriptor] oldDj0 ON oldDj0.[DocumentId] = del.[GradeTypeDescriptor_DescriptorId]"
            );
        mssql
            .Should()
            .Contain(
                "LEFT JOIN [dms].[Descriptor] newDj0 ON newDj0.[DocumentId] = i.[GradeTypeDescriptor_DescriptorId]"
            );
    }

    [Test]
    public void It_should_render_nullable_person_join_chains_as_left_joins()
    {
        var pgsqlDialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var mssqlDialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var plan = TrackedChangeTriggerBodyEmitter.BuildPlan(
            TrackedChangeEmitterFixture.BuildTrackedTable(nullablePersonJoin: true),
            TrackedChangeEmitterFixture.BuildSourceTableModel()
        );

        var pgsql = RenderPgsqlKeyChange(pgsqlDialect, plan);
        var mssql = RenderMssqlKeyChange(mssqlDialect, plan);

        pgsql
            .Should()
            .Contain(
                "LEFT JOIN \"edfi\".\"StudentSectionAssociation\" oldPj0s0 ON oldPj0s0.\"DocumentId\" = OLD.\"StudentSectionAssociation_DocumentId\""
            );
        pgsql
            .Should()
            .Contain(
                "LEFT JOIN \"edfi\".\"Student\" oldPj0s1 ON oldPj0s1.\"DocumentId\" = oldPj0s0.\"Student_DocumentId\""
            );
        pgsql
            .Should()
            .Contain(
                "LEFT JOIN \"edfi\".\"StudentSectionAssociation\" newPj0s0 ON newPj0s0.\"DocumentId\" = NEW.\"StudentSectionAssociation_DocumentId\""
            );
        pgsql
            .Should()
            .Contain(
                "LEFT JOIN \"edfi\".\"Student\" newPj0s1 ON newPj0s1.\"DocumentId\" = newPj0s0.\"Student_DocumentId\""
            );
        mssql
            .Should()
            .Contain(
                "LEFT JOIN [edfi].[StudentSectionAssociation] oldPj0s0 ON oldPj0s0.[DocumentId] = del.[StudentSectionAssociation_DocumentId]"
            );
        mssql
            .Should()
            .Contain(
                "LEFT JOIN [edfi].[Student] oldPj0s1 ON oldPj0s1.[DocumentId] = oldPj0s0.[Student_DocumentId]"
            );
        mssql
            .Should()
            .Contain(
                "LEFT JOIN [edfi].[StudentSectionAssociation] newPj0s0 ON newPj0s0.[DocumentId] = i.[StudentSectionAssociation_DocumentId]"
            );
        mssql
            .Should()
            .Contain(
                "LEFT JOIN [edfi].[Student] newPj0s1 ON newPj0s1.[DocumentId] = newPj0s0.[Student_DocumentId]"
            );
    }

    private static string RenderPgsqlKeyChange(ISqlDialect dialect, TrackedChangeInsertPlan plan)
    {
        var writer = new SqlWriter(dialect);
        TrackedChangeTriggerBodyEmitter.EmitPgsqlKeyChangeInsert(
            writer,
            dialect,
            plan,
            new DbColumnName("DocumentId")
        );
        return writer.ToString();
    }

    private static string RenderMssqlKeyChange(ISqlDialect dialect, TrackedChangeInsertPlan plan)
    {
        var writer = new SqlWriter(dialect);
        TrackedChangeTriggerBodyEmitter.EmitMssqlKeyChangeInsert(
            writer,
            dialect,
            plan,
            new DbColumnName("DocumentId")
        );
        return writer.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — scalar-only (no-joins) coverage
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_With_Scalar_Only_Table
{
    private string _pgsqlTombstone = default!;
    private string _mssqlTombstone = default!;

    [SetUp]
    public void Setup()
    {
        var pgsqlDialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var mssqlDialect = SqlDialectFactory.Create(SqlDialect.Mssql);

        var plan = TrackedChangeTriggerBodyEmitter.BuildPlan(
            TrackedChangeEmitterFixture.BuildScalarOnlyTrackedTable(),
            TrackedChangeEmitterFixture.BuildScalarOnlySourceTableModel()
        );

        var pgWriter = new SqlWriter(pgsqlDialect);
        TrackedChangeTriggerBodyEmitter.EmitPgsqlTombstoneInsert(
            pgWriter,
            pgsqlDialect,
            plan,
            new DbColumnName("DocumentId")
        );
        _pgsqlTombstone = pgWriter.ToString();

        var msWriter = new SqlWriter(mssqlDialect);
        TrackedChangeTriggerBodyEmitter.EmitMssqlTombstoneInsert(
            msWriter,
            mssqlDialect,
            plan,
            new DbColumnName("DocumentId")
        );
        _mssqlTombstone = msWriter.ToString();
    }

    [Test]
    public void It_should_render_valid_sql_for_tables_without_joins()
    {
        // PG tombstone has INSERT INTO and scalar columns, no descriptor/person joins
        _pgsqlTombstone.Should().Contain("INSERT INTO \"tracked_changes_edfi\".\"Grade\"");
        _pgsqlTombstone.Should().Contain("\"OldBeginDate\"");
        _pgsqlTombstone.Should().Contain("\"OldSchoolId\"");
        _pgsqlTombstone.Should().NotContain("INNER JOIN \"dms\".\"Descriptor\"");
        _pgsqlTombstone.Should().NotContain("Pj0");

        // MSSQL tombstone has INSERT INTO and scalar columns, no descriptor/person joins
        _mssqlTombstone.Should().Contain("INSERT INTO [tracked_changes_edfi].[Grade]");
        _mssqlTombstone.Should().Contain("[OldBeginDate]");
        _mssqlTombstone.Should().Contain("[OldSchoolId]");
        _mssqlTombstone.Should().NotContain("INNER JOIN [dms].[Descriptor]");
        _mssqlTombstone.Should().NotContain("Pj0");
    }
}

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — self person DocumentId coverage
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_With_Self_Person_DocumentId
{
    private TrackedChangeInsertPlan _plan = default!;
    private string _pgsqlTombstone = default!;
    private string _pgsqlKeyChange = default!;
    private string _mssqlTombstone = default!;
    private string _mssqlKeyChange = default!;

    [SetUp]
    public void Setup()
    {
        _plan = TrackedChangeTriggerBodyEmitter.BuildPlan(
            TrackedChangeEmitterFixture.BuildSelfPersonTrackedTable(),
            TrackedChangeEmitterFixture.BuildSelfPersonSourceTableModel()
        );

        var pgsqlDialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var pgsqlTombstoneWriter = new SqlWriter(pgsqlDialect);
        TrackedChangeTriggerBodyEmitter.EmitPgsqlTombstoneInsert(
            pgsqlTombstoneWriter,
            pgsqlDialect,
            _plan,
            new DbColumnName("DocumentId")
        );
        _pgsqlTombstone = pgsqlTombstoneWriter.ToString();

        var pgsqlKeyChangeWriter = new SqlWriter(pgsqlDialect);
        TrackedChangeTriggerBodyEmitter.EmitPgsqlKeyChangeInsert(
            pgsqlKeyChangeWriter,
            pgsqlDialect,
            _plan,
            new DbColumnName("DocumentId")
        );
        _pgsqlKeyChange = pgsqlKeyChangeWriter.ToString();

        var mssqlDialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var mssqlTombstoneWriter = new SqlWriter(mssqlDialect);
        TrackedChangeTriggerBodyEmitter.EmitMssqlTombstoneInsert(
            mssqlTombstoneWriter,
            mssqlDialect,
            _plan,
            new DbColumnName("DocumentId")
        );
        _mssqlTombstone = mssqlTombstoneWriter.ToString();

        var mssqlKeyChangeWriter = new SqlWriter(mssqlDialect);
        TrackedChangeTriggerBodyEmitter.EmitMssqlKeyChangeInsert(
            mssqlKeyChangeWriter,
            mssqlDialect,
            _plan,
            new DbColumnName("DocumentId")
        );
        _mssqlKeyChange = mssqlKeyChangeWriter.ToString();
    }

    [Test]
    public void It_should_resolve_self_person_document_id_columns_to_the_source_document_id_column()
    {
        var value = _plan.Values.Single(value =>
            value.Column.OldColumnName == new DbColumnName("OldStudent_DocumentId")
        );

        value.Kind.Should().Be(TrackedChangeValueSourceKind.DirectColumn);
        value.SourceColumn.Should().Be(new DbColumnName("DocumentId"));
        value.JoinIndex.Should().Be(-1);
    }

    [Test]
    public void It_should_render_pgsql_self_person_document_id_values_from_row_images()
    {
        _pgsqlTombstone.Should().Contain("OLD.\"DocumentId\",");
        _pgsqlKeyChange.Should().Contain("OLD.\"DocumentId\",");
        _pgsqlKeyChange.Should().Contain("NEW.\"DocumentId\",");
        _pgsqlTombstone.Should().NotContain("oldPj");
        _pgsqlKeyChange.Should().NotContain("newPj");
    }

    [Test]
    public void It_should_render_mssql_self_person_document_id_values_from_row_images()
    {
        _mssqlTombstone.Should().Contain("del.[DocumentId],");
        _mssqlKeyChange.Should().Contain("del.[DocumentId],");
        _mssqlKeyChange.Should().Contain("i.[DocumentId],");
        _mssqlTombstone.Should().NotContain("oldPj");
        _mssqlKeyChange.Should().NotContain("newPj");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Shared fixture
// ═══════════════════════════════════════════════════════════════════

internal static class TrackedChangeEmitterFixture
{
    internal static readonly DbSchemaName EdfiSchema = new("edfi");
    internal static readonly DbTableName SourceTable = new(EdfiSchema, "Grade");
    internal static readonly DbTableName TrackedTable = new(
        new DbSchemaName("tracked_changes_edfi"),
        "Grade"
    );
    internal static readonly DbTableName SsaTable = new(EdfiSchema, "StudentSectionAssociation");
    internal static readonly DbTableName StudentTable = new(EdfiSchema, "Student");

    /// <summary>
    /// Builds a source table model for the Grade-like resource, covering:
    ///   DocumentId (Int64, no path),
    ///   BeginDate (Date, path "$.gradingPeriodReference.beginDate"),
    ///   SchoolId_Unified (Int64, path "$.schoolReference.schoolId"),
    ///   GradeTypeDescriptor_DescriptorId (Int64, path "$.gradeTypeDescriptor"),
    ///   StudentSectionAssociation_DocumentId (Int64, no path).
    /// </summary>
    /// <param name="addDuplicateBeginDate">
    /// When true, adds a second column with the same SourceJsonPath as BeginDate to exercise
    /// the multiple-matches error path in scalar resolution.
    /// </param>
    internal static DbTableModel BuildSourceTableModel(bool addDuplicateBeginDate = false)
    {
        var columns = new List<DbColumnModel>
        {
            new DbColumnModel(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("BeginDate"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Date),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression("$.gradingPeriodReference.beginDate", []),
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("SchoolId_Unified"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression("$.schoolReference.schoolId", []),
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("GradeTypeDescriptor_DescriptorId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression("$.gradeTypeDescriptor", []),
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("StudentSectionAssociation_DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        };

        if (addDuplicateBeginDate)
        {
            // A second column sharing the same SourceJsonPath — triggers the ambiguous-scalar error.
            columns.Add(
                new DbColumnModel(
                    new DbColumnName("BeginDate_Duplicate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression("$.gradingPeriodReference.beginDate", []),
                    TargetResource: null
                )
            );
        }

        return new DbTableModel(
            SourceTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Grade",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        );
    }

    /// <summary>
    /// Builds a source table model for a top-level Student resource.
    /// </summary>
    internal static DbTableModel BuildSelfPersonSourceTableModel()
    {
        var columns = new List<DbColumnModel>
        {
            new DbColumnModel(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("StudentUniqueId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, 32),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression("$.studentUniqueId", []),
                TargetResource: null
            ),
        };

        return new DbTableModel(
            StudentTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        );
    }

    /// <summary>
    /// Builds a TrackedChangeTableInfo for a top-level Student resource with a self person DocumentId
    /// value column and no person joins.
    /// </summary>
    internal static TrackedChangeTableInfo BuildSelfPersonTrackedTable()
    {
        var valueColumns = new List<TrackedChangeColumnInfo>
        {
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldStudent_DocumentId"),
                NewColumnName: new DbColumnName("NewStudent_DocumentId"),
                SourceJsonPath: "$.studentUniqueId",
                CanonicalStorageColumn: new DbColumnName("DocumentId"),
                IsOldColumnNullable: false,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                Role: TrackedChangeColumnRole.PersonDocumentId,
                Origin: TrackedChangeColumnOrigin.SecurableElement
            ),
        };

        var systemColumns = new List<TrackedChangeSystemColumnInfo>
        {
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.Id,
                new DbColumnName("Id"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.ChangeVersion,
                new DbColumnName("ChangeVersion"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: true
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.CreatedAt,
                new DbColumnName("CreatedAt"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
        };

        return new TrackedChangeTableInfo(
            Table: new DbTableName(new DbSchemaName("tracked_changes_edfi"), "Student"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: StudentTable,
            ValueColumnsInTableOrder: valueColumns,
            SystemColumns: systemColumns,
            PrimaryKeyColumns: [new DbColumnName("ChangeVersion")],
            DescriptorJoins: [],
            PersonJoins: []
        );
    }

    /// <summary>
    /// Builds a TrackedChangeTableInfo for the Grade-like resource.
    /// </summary>
    /// <param name="overrideScalarPath">
    /// When set, overrides the BeginDate value column's SourceJsonPath to a non-matching value
    /// (used to exercise the unresolvable-path error path).
    /// </param>
    /// <param name="overrideDescriptorJoinName">
    /// When set, overrides the join name used by the descriptor value columns (default "GradeTypeDescriptor").
    /// Pass a name not present in DescriptorJoins to exercise the missing-join error path.
    /// </param>
    /// <param name="useInvalidPersonJoinPath">
    /// When true, builds a person join whose path steps have null TargetTable/TargetColumnName.
    /// </param>
    /// <param name="overrideCanonicalStorageColumn">
    /// Overrides the OldSchoolId column's canonical storage column; pass a column name absent
    /// from the source table to exercise the missing-canonical-column validation.
    /// </param>
    internal static TrackedChangeTableInfo BuildTrackedTable(
        string? overrideScalarPath = null,
        string? overrideDescriptorJoinName = null,
        bool useInvalidPersonJoinPath = false,
        DbColumnName? overrideCanonicalStorageColumn = null,
        bool nullableDescriptorJoin = false,
        bool nullablePersonJoin = false
    )
    {
        var descriptorJoinName = overrideDescriptorJoinName ?? "GradeTypeDescriptor";

        var beginDatePath = overrideScalarPath ?? "$.gradingPeriodReference.beginDate";

        var valueColumns = new List<TrackedChangeColumnInfo>
        {
            // [0] BeginDate — plain Scalar, no canonical storage column
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldBeginDate"),
                NewColumnName: new DbColumnName("NewBeginDate"),
                SourceJsonPath: beginDatePath,
                CanonicalStorageColumn: null,
                IsOldColumnNullable: false,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.Date),
                Role: TrackedChangeColumnRole.Scalar,
                Origin: TrackedChangeColumnOrigin.Identity
            ),
            // [1] SchoolId — Scalar with CanonicalStorageColumn set (key unification)
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldSchoolId"),
                NewColumnName: new DbColumnName("NewSchoolId"),
                SourceJsonPath: "$.schoolReference.schoolId",
                CanonicalStorageColumn: overrideCanonicalStorageColumn
                    ?? new DbColumnName("SchoolId_Unified"),
                IsOldColumnNullable: false,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                Role: TrackedChangeColumnRole.Scalar,
                Origin: TrackedChangeColumnOrigin.Identity
            ),
            // [2] GradeTypeDescriptor Namespace
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldGradeTypeDescriptor_Namespace"),
                NewColumnName: new DbColumnName("NewGradeTypeDescriptor_Namespace"),
                SourceJsonPath: "$.gradeTypeDescriptor",
                CanonicalStorageColumn: null,
                IsOldColumnNullable: nullableDescriptorJoin,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 255),
                Role: TrackedChangeColumnRole.DescriptorNamespace,
                Origin: TrackedChangeColumnOrigin.Identity,
                DescriptorJoinName: descriptorJoinName
            ),
            // [3] GradeTypeDescriptor CodeValue
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldGradeTypeDescriptor_CodeValue"),
                NewColumnName: new DbColumnName("NewGradeTypeDescriptor_CodeValue"),
                SourceJsonPath: "$.gradeTypeDescriptor",
                CanonicalStorageColumn: null,
                IsOldColumnNullable: nullableDescriptorJoin,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                Role: TrackedChangeColumnRole.DescriptorCodeValue,
                Origin: TrackedChangeColumnOrigin.Identity,
                DescriptorJoinName: descriptorJoinName
            ),
            // [4] Student DocumentId — PersonDocumentId
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldStudentSectionAssociation_Student_DocumentId"),
                NewColumnName: new DbColumnName("NewStudentSectionAssociation_Student_DocumentId"),
                SourceJsonPath: "$.studentSectionAssociationReference.studentReference.studentUniqueId",
                CanonicalStorageColumn: null,
                IsOldColumnNullable: nullablePersonJoin,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                Role: TrackedChangeColumnRole.PersonDocumentId,
                Origin: TrackedChangeColumnOrigin.SecurableElement,
                PersonJoinName: "Student"
            ),
        };

        var systemColumns = new List<TrackedChangeSystemColumnInfo>
        {
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.Id,
                new DbColumnName("Id"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.ChangeVersion,
                new DbColumnName("ChangeVersion"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: true
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.CreatedAt,
                new DbColumnName("CreatedAt"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
        };

        var descriptorJoins = new List<TrackedChangeDescriptorJoinInfo>
        {
            new TrackedChangeDescriptorJoinInfo(
                DescriptorJoinName: "GradeTypeDescriptor",
                SourceColumn: new DbColumnName("GradeTypeDescriptor_DescriptorId"),
                DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradeTypeDescriptor")
            ),
        };

        IReadOnlyList<ColumnPathStep> personJoinPath = useInvalidPersonJoinPath
            ?
            [
                // Step with null TargetTable/TargetColumnName — triggers the validation error.
                new ColumnPathStep(
                    SourceTable,
                    new DbColumnName("StudentSectionAssociation_DocumentId"),
                    TargetTable: null,
                    TargetColumnName: null
                ),
            ]
            :
            [
                new ColumnPathStep(
                    SourceTable,
                    new DbColumnName("StudentSectionAssociation_DocumentId"),
                    SsaTable,
                    new DbColumnName("DocumentId")
                ),
                new ColumnPathStep(
                    SsaTable,
                    new DbColumnName("Student_DocumentId"),
                    StudentTable,
                    new DbColumnName("DocumentId")
                ),
            ];

        var personJoins = new List<TrackedChangePersonJoinInfo>
        {
            new TrackedChangePersonJoinInfo(
                PersonJoinName: "Student",
                PersonKind: SecurableElementKind.Student,
                JoinPath: personJoinPath
            ),
        };

        return new TrackedChangeTableInfo(
            Table: TrackedTable,
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: SourceTable,
            ValueColumnsInTableOrder: valueColumns,
            SystemColumns: systemColumns,
            PrimaryKeyColumns: [new DbColumnName("ChangeVersion")],
            DescriptorJoins: descriptorJoins,
            PersonJoins: personJoins
        );
    }

    /// <summary>
    /// Builds a source table model with ONLY two scalar value columns (BeginDate + SchoolId_Unified),
    /// no descriptor or person FK columns.
    /// </summary>
    internal static DbTableModel BuildScalarOnlySourceTableModel()
    {
        var columns = new List<DbColumnModel>
        {
            new DbColumnModel(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("BeginDate"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Date),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression("$.gradingPeriodReference.beginDate", []),
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("SchoolId_Unified"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression("$.schoolReference.schoolId", []),
                TargetResource: null
            ),
        };

        return new DbTableModel(
            SourceTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Grade",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        );
    }

    /// <summary>
    /// Builds a TrackedChangeTableInfo for a Grade-like resource with ONLY two scalar value
    /// columns (BeginDate + SchoolId), empty DescriptorJoins, and empty PersonJoins.
    /// </summary>
    internal static TrackedChangeTableInfo BuildScalarOnlyTrackedTable()
    {
        var valueColumns = new List<TrackedChangeColumnInfo>
        {
            // [0] BeginDate — plain Scalar
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldBeginDate"),
                NewColumnName: new DbColumnName("NewBeginDate"),
                SourceJsonPath: "$.gradingPeriodReference.beginDate",
                CanonicalStorageColumn: null,
                IsOldColumnNullable: false,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.Date),
                Role: TrackedChangeColumnRole.Scalar,
                Origin: TrackedChangeColumnOrigin.Identity
            ),
            // [1] SchoolId — Scalar with CanonicalStorageColumn (key unification)
            new TrackedChangeColumnInfo(
                OldColumnName: new DbColumnName("OldSchoolId"),
                NewColumnName: new DbColumnName("NewSchoolId"),
                SourceJsonPath: "$.schoolReference.schoolId",
                CanonicalStorageColumn: new DbColumnName("SchoolId_Unified"),
                IsOldColumnNullable: false,
                IsNewColumnNullable: true,
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                Role: TrackedChangeColumnRole.Scalar,
                Origin: TrackedChangeColumnOrigin.Identity
            ),
        };

        var systemColumns = new List<TrackedChangeSystemColumnInfo>
        {
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.Id,
                new DbColumnName("Id"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.ChangeVersion,
                new DbColumnName("ChangeVersion"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: true
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.CreatedAt,
                new DbColumnName("CreatedAt"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
        };

        return new TrackedChangeTableInfo(
            Table: TrackedTable,
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: SourceTable,
            ValueColumnsInTableOrder: valueColumns,
            SystemColumns: systemColumns,
            PrimaryKeyColumns: [new DbColumnName("ChangeVersion")],
            DescriptorJoins: [],
            PersonJoins: []
        );
    }
}

// ═══════════════════════════════════════════════════════════════════
// TrackedChangeTriggerBodyEmitter — canonical storage column validation
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_TrackedChangeTriggerBodyEmitter_With_Missing_Canonical_Storage_Column
{
    [Test]
    public void It_should_throw_when_the_canonical_storage_column_is_absent_from_the_source_table()
    {
        var act = () =>
            TrackedChangeTriggerBodyEmitter.BuildPlan(
                TrackedChangeEmitterFixture.BuildTrackedTable(
                    overrideCanonicalStorageColumn: new DbColumnName("SchoolId_Missing")
                ),
                TrackedChangeEmitterFixture.BuildSourceTableModel()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*canonical storage column 'SchoolId_Missing'*");
    }
}
