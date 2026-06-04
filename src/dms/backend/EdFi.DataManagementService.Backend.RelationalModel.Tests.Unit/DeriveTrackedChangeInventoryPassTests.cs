// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared helpers for tracked-change inventory derivation pass tests.
/// </summary>
internal static class TrackedChangeDerivationTestHelpers
{
    /// <summary>
    /// Builds a derived model set from a single core project schema using the canonical production pass
    /// list, so the tracked-change pass runs in its real position with every dependency satisfied.
    /// </summary>
    internal static DerivedRelationalModelSet BuildSet(JsonObject coreProjectSchema)
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// Builds a derived model set from a core project plus one extension project using the canonical
    /// production pass list, so the tracked-change pass runs in its real position.
    /// </summary>
    internal static DerivedRelationalModelSet BuildSet(
        JsonObject coreProjectSchema,
        JsonObject extensionProjectSchema
    )
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// Returns the single tracked-change table whose live source table has the supplied name.
    /// </summary>
    internal static TrackedChangeTableInfo TableBySourceName(DerivedRelationalModelSet set, string sourceName)
    {
        return set.TrackedChangeTablesInNameOrder.Single(table => table.SourceTable.Name == sourceName);
    }

    /// <summary>
    /// Returns the single value column on the supplied tracked-change table with the given <c>Old_</c> name.
    /// </summary>
    internal static TrackedChangeColumnInfo ValueColumnByOldName(
        TrackedChangeTableInfo table,
        string oldColumnName
    )
    {
        return table.ValueColumnsInTableOrder.Single(column => column.OldColumnName.Value == oldColumnName);
    }

    /// <summary>
    /// Returns the single system column on the supplied tracked-change table with the given role.
    /// </summary>
    internal static TrackedChangeSystemColumnInfo SystemColumnByRole(
        TrackedChangeTableInfo table,
        TrackedChangeSystemColumnRole role
    )
    {
        return table.SystemColumns.Single(column => column.Role == role);
    }
}

/// <summary>
/// Test fixture for tracked-change derivation over plain relational resources (no descriptors).
/// </summary>
[TestFixture]
public class Given_Regular_Resources_For_Tracked_Change_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _set = TrackedChangeDerivationTestHelpers.BuildSet(
            ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema()
        );
    }

    /// <summary>
    /// It should derive one Resource-kind tracked-change table per relational resource.
    /// </summary>
    [Test]
    public void It_should_derive_one_Resource_table_per_relational_resource()
    {
        _set.TrackedChangeTablesInNameOrder.Select(table => table.SourceTable.Name)
            .Should()
            .Contain(["Enrollment", "School", "Student"]);

        _set.TrackedChangeTablesInNameOrder.Should()
            .OnlyContain(table => table.Kind == TrackedChangeTableKind.Resource);
    }

    /// <summary>
    /// It should place the tracked-change table in the per-project tracked_changes schema.
    /// </summary>
    [Test]
    public void It_should_place_the_table_in_the_tracked_changes_schema()
    {
        var enrollment = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "Enrollment");

        enrollment.Table.Schema.Value.Should().Be("tracked_changes_edfi");
        enrollment.Table.Name.Should().Be("Enrollment");
        enrollment.SourceTable.Schema.Value.Should().Be("edfi");
    }

    /// <summary>
    /// It should expose Id, ChangeVersion, and CreatedAt system columns with no Discriminator.
    /// </summary>
    [Test]
    public void It_should_expose_the_non_descriptor_system_columns()
    {
        var enrollment = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "Enrollment");

        enrollment
            .SystemColumns.Select(column => column.Role)
            .Should()
            .Equal(
                TrackedChangeSystemColumnRole.Id,
                TrackedChangeSystemColumnRole.ChangeVersion,
                TrackedChangeSystemColumnRole.CreatedAt
            );

        enrollment
            .SystemColumns.Should()
            .NotContain(column => column.Role == TrackedChangeSystemColumnRole.Discriminator);
    }

    /// <summary>
    /// It should type the system columns by role and mark only ChangeVersion as the primary key.
    /// </summary>
    [Test]
    public void It_should_type_the_system_columns_by_role()
    {
        var enrollment = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "Enrollment");

        var id = TrackedChangeDerivationTestHelpers.SystemColumnByRole(
            enrollment,
            TrackedChangeSystemColumnRole.Id
        );
        id.ColumnName.Value.Should().Be("Id");
        id.ScalarType.Should().BeNull();
        id.IsNullable.Should().BeFalse();
        id.IsPrimaryKey.Should().BeFalse();

        var changeVersion = TrackedChangeDerivationTestHelpers.SystemColumnByRole(
            enrollment,
            TrackedChangeSystemColumnRole.ChangeVersion
        );
        changeVersion.ColumnName.Value.Should().Be("ChangeVersion");
        changeVersion.ScalarType!.Kind.Should().Be(ScalarKind.Int64);
        changeVersion.IsNullable.Should().BeFalse();
        changeVersion.IsPrimaryKey.Should().BeTrue();

        var createdAt = TrackedChangeDerivationTestHelpers.SystemColumnByRole(
            enrollment,
            TrackedChangeSystemColumnRole.CreatedAt
        );
        createdAt.ColumnName.Value.Should().Be("CreatedAt");
        createdAt.ScalarType!.Kind.Should().Be(ScalarKind.DateTime);
        createdAt.IsNullable.Should().BeFalse();
        createdAt.IsPrimaryKey.Should().BeFalse();
    }

    /// <summary>
    /// It should carry ChangeVersion as the single primary-key column.
    /// </summary>
    [Test]
    public void It_should_use_ChangeVersion_as_the_primary_key()
    {
        var enrollment = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "Enrollment");

        enrollment.PrimaryKeyColumns.Select(column => column.Value).Should().Equal("ChangeVersion");
    }

    /// <summary>
    /// It should materialize identity scalar value columns tagged with the Identity origin.
    /// </summary>
    [Test]
    public void It_should_materialize_identity_scalar_value_columns()
    {
        var enrollment = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "Enrollment");

        var schoolId = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            enrollment,
            "Old_School_SchoolId"
        );

        schoolId.NewColumnName.Value.Should().Be("New_School_SchoolId");
        schoolId.Role.Should().Be(TrackedChangeColumnRole.Scalar);
        schoolId.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.Identity);
        schoolId.ScalarType.Kind.Should().Be(ScalarKind.Int32);
        schoolId.SourceJsonPath.Should().Be("$.schoolReference.schoolId");
    }

    /// <summary>
    /// It should leave every New_ value column nullable so delete tombstones can omit them.
    /// </summary>
    [Test]
    public void It_should_leave_every_new_value_column_nullable()
    {
        _set.TrackedChangeTablesInNameOrder.SelectMany(table => table.ValueColumnsInTableOrder)
            .Should()
            .OnlyContain(column => column.IsNewColumnNullable);
    }

    /// <summary>
    /// It should not derive a shared descriptor table for a descriptor-free model.
    /// </summary>
    [Test]
    public void It_should_not_derive_a_shared_descriptor_table()
    {
        _set.TrackedChangeTablesInNameOrder.Should()
            .NotContain(table => table.Kind == TrackedChangeTableKind.SharedDescriptor);
    }
}

/// <summary>
/// Test fixture for tracked-change derivation over a descriptor-only project.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Tracked_Change_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _set = TrackedChangeDerivationTestHelpers.BuildSet(
            CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema()
        );
    }

    /// <summary>
    /// It should derive exactly one shared descriptor tracked-change table.
    /// </summary>
    [Test]
    public void It_should_derive_exactly_one_shared_descriptor_table()
    {
        var sharedDescriptorTables = _set.TrackedChangeTablesInNameOrder.Where(table =>
            table.Kind == TrackedChangeTableKind.SharedDescriptor
        );

        sharedDescriptorTables.Should().ContainSingle();
    }

    /// <summary>
    /// It should target the shared dms.Descriptor table from the core tracked_changes schema.
    /// </summary>
    [Test]
    public void It_should_target_the_shared_descriptor_table()
    {
        var descriptor = _set.TrackedChangeTablesInNameOrder.Single(table =>
            table.Kind == TrackedChangeTableKind.SharedDescriptor
        );

        descriptor.Table.Schema.Value.Should().Be("tracked_changes_edfi");
        descriptor.Table.Name.Should().Be("Descriptor");
        descriptor.SourceTable.Schema.Value.Should().Be("dms");
        descriptor.SourceTable.Name.Should().Be("Descriptor");
    }

    /// <summary>
    /// It should add a Discriminator system column on the shared descriptor table.
    /// </summary>
    [Test]
    public void It_should_add_a_discriminator_system_column()
    {
        var descriptor = _set.TrackedChangeTablesInNameOrder.Single(table =>
            table.Kind == TrackedChangeTableKind.SharedDescriptor
        );

        var discriminator = TrackedChangeDerivationTestHelpers.SystemColumnByRole(
            descriptor,
            TrackedChangeSystemColumnRole.Discriminator
        );

        discriminator.ColumnName.Value.Should().Be("Discriminator");
        discriminator.ScalarType!.Kind.Should().Be(ScalarKind.String);
        discriminator.ScalarType.MaxLength.Should().Be(128);
        discriminator.IsNullable.Should().BeFalse();
        discriminator.IsPrimaryKey.Should().BeFalse();
    }

    /// <summary>
    /// It should materialize Namespace and CodeValue value columns sourced from the descriptor identity.
    /// </summary>
    [Test]
    public void It_should_materialize_namespace_and_code_value_columns()
    {
        var descriptor = _set.TrackedChangeTablesInNameOrder.Single(table =>
            table.Kind == TrackedChangeTableKind.SharedDescriptor
        );

        var ns = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(descriptor, "Old_Namespace");
        ns.NewColumnName.Value.Should().Be("New_Namespace");
        ns.Role.Should().Be(TrackedChangeColumnRole.Scalar);
        ns.Origin.Should().Be(TrackedChangeColumnOrigin.Identity);
        ns.SourceJsonPath.Should().Be("$.namespace");

        var codeValue = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(descriptor, "Old_CodeValue");
        codeValue.NewColumnName.Value.Should().Be("New_CodeValue");
        codeValue.Role.Should().Be(TrackedChangeColumnRole.Scalar);
        codeValue.Origin.Should().Be(TrackedChangeColumnOrigin.Identity);
        codeValue.SourceJsonPath.Should().Be("$.codeValue");
    }

    /// <summary>
    /// It should not derive a per-resource table for the SharedDescriptorTable descriptor resource.
    /// </summary>
    [Test]
    public void It_should_not_derive_a_per_descriptor_resource_table()
    {
        _set.TrackedChangeTablesInNameOrder.Should()
            .NotContain(table => table.SourceTable.Name == "GradeLevelDescriptor");
    }
}

/// <summary>
/// Test fixture proving the shared descriptor tracked-change table is always rendered in the core
/// schema, even when an extension project's descriptor sorts ahead of <c>ed-fi</c> in endpoint order.
/// </summary>
[TestFixture]
public class Given_An_Extension_Descriptor_Sorting_Before_Core_For_Tracked_Change_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture: the extension endpoint (<c>abc-extension</c>) sorts before <c>ed-fi</c>.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _set = TrackedChangeDerivationTestHelpers.BuildSet(
            CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema(),
            CommonInventoryTestSchemaBuilder.BuildExtensionDescriptorProjectSchema()
        );
    }

    /// <summary>
    /// It should still target the core tracked_changes_edfi.Descriptor table, not the extension schema.
    /// </summary>
    [Test]
    public void It_should_target_the_core_descriptor_schema()
    {
        var descriptor = _set.TrackedChangeTablesInNameOrder.Single(table =>
            table.Kind == TrackedChangeTableKind.SharedDescriptor
        );

        descriptor.Table.Schema.Value.Should().Be("tracked_changes_edfi");
        descriptor.Table.Name.Should().Be("Descriptor");
    }

    /// <summary>
    /// It should derive exactly one shared descriptor table across both projects' descriptors.
    /// </summary>
    [Test]
    public void It_should_derive_exactly_one_shared_descriptor_table()
    {
        _set.TrackedChangeTablesInNameOrder.Where(table =>
                table.Kind == TrackedChangeTableKind.SharedDescriptor
            )
            .Should()
            .ContainSingle();
    }
}

/// <summary>
/// Test fixture proving concrete subclasses of an abstract resource each get their own
/// ConcreteAbstract-kind tracked-change table.
/// </summary>
[TestFixture]
public class Given_A_Concrete_Abstract_Resource_For_Tracked_Change_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _set = TrackedChangeDerivationTestHelpers.BuildSet(
            TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema()
        );
    }

    /// <summary>
    /// It should classify the concrete subclass table as ConcreteAbstract.
    /// </summary>
    [Test]
    public void It_should_classify_the_concrete_subclass_as_ConcreteAbstract()
    {
        var school = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "School");

        school.Kind.Should().Be(TrackedChangeTableKind.ConcreteAbstract);
    }

    /// <summary>
    /// It should give the concrete subclass its own tracked-change table (not the abstract identity table).
    /// </summary>
    [Test]
    public void It_should_give_the_concrete_subclass_its_own_table()
    {
        var school = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "School");

        school.Table.Name.Should().Be("School");
        school.Table.Schema.Value.Should().Be("tracked_changes_edfi");
    }
}

/// <summary>
/// Test fixture for uniform ChangeTracking attachment onto stamping triggers, using a descriptor-free
/// composite model (subclass root + child collection).
/// </summary>
[TestFixture]
public class Given_Change_Tracking_Attachment_On_Stamping_Triggers
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _set = TrackedChangeDerivationTestHelpers.BuildSet(
            TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema()
        );
    }

    /// <summary>
    /// It should attach the resource's tracked-change table to the root-table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_attach_change_tracking_to_the_root_stamping_trigger()
    {
        var rootStamp = _set.TriggersInCreateOrder.Single(t =>
            t.Table.Name == "School" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        var stamping = (TriggerKindParameters.DocumentStamping)rootStamp.Parameters;
        stamping.ChangeTracking.Should().NotBeNull();
        stamping.ChangeTracking!.TrackedChangeTable.Schema.Value.Should().Be("tracked_changes_edfi");
        stamping.ChangeTracking.TrackedChangeTable.Name.Should().Be("School");
    }

    /// <summary>
    /// It should leave child-table stamping triggers unattached (the root tombstone covers children).
    /// </summary>
    [Test]
    public void It_should_not_attach_change_tracking_to_child_stamping_triggers()
    {
        var childStamp = _set.TriggersInCreateOrder.Single(t =>
            t.Table.Name == "SchoolAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        var stamping = (TriggerKindParameters.DocumentStamping)childStamp.Parameters;
        stamping.ChangeTracking.Should().BeNull();
    }

    /// <summary>
    /// It should leave the descriptor stamping trigger unattached when no descriptor resource exists.
    /// </summary>
    [Test]
    public void It_should_not_attach_change_tracking_to_descriptor_trigger_without_descriptor_resources()
    {
        var descriptorStamp = _set.TriggersInCreateOrder.Single(t =>
            t.Table.Equals(new DbTableName(new DbSchemaName("dms"), "Descriptor"))
            && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        var stamping = (TriggerKindParameters.DocumentStamping)descriptorStamp.Parameters;
        stamping.ChangeTracking.Should().BeNull();
    }
}

/// <summary>
/// Test fixture for ChangeTracking attachment onto the shared descriptor stamping trigger when descriptor
/// resources exist.
/// </summary>
[TestFixture]
public class Given_Change_Tracking_Attachment_For_Descriptor_Resources
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _set = TrackedChangeDerivationTestHelpers.BuildSet(
            CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema()
        );
    }

    /// <summary>
    /// It should attach the shared descriptor tracked-change table to the descriptor stamping trigger.
    /// </summary>
    [Test]
    public void It_should_attach_change_tracking_to_the_descriptor_stamping_trigger()
    {
        var descriptorStamp = _set.TriggersInCreateOrder.Single(t =>
            t.Table.Equals(new DbTableName(new DbSchemaName("dms"), "Descriptor"))
            && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        var stamping = (TriggerKindParameters.DocumentStamping)descriptorStamp.Parameters;
        stamping.ChangeTracking.Should().NotBeNull();
        stamping.ChangeTracking!.TrackedChangeTable.Schema.Value.Should().Be("tracked_changes_edfi");
        stamping.ChangeTracking.TrackedChangeTable.Name.Should().Be("Descriptor");
    }
}

/// <summary>
/// Test fixture for tracked-change derivation determinism and the per-table de-duplication invariant.
/// </summary>
[TestFixture]
public class Given_Deterministic_Tracked_Change_Derivation
{
    /// <summary>
    /// It should produce an identical tracked-change table sequence on repeated builds.
    /// </summary>
    [Test]
    public void It_should_produce_identical_table_sequence_on_repeated_builds()
    {
        static IReadOnlyList<(string, string)> Build()
        {
            var set = TrackedChangeDerivationTestHelpers.BuildSet(
                ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema()
            );
            return set
                .TrackedChangeTablesInNameOrder.Select(table => (table.Table.Schema.Value, table.Table.Name))
                .ToList();
        }

        Build().Should().Equal(Build());
    }

    /// <summary>
    /// It should never repeat an Old_ value-column name within a single tracked-change table.
    /// </summary>
    [Test]
    public void It_should_not_repeat_an_old_value_column_name_within_a_table()
    {
        var set = TrackedChangeDerivationTestHelpers.BuildSet(
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithIdentityUnification()
        );

        foreach (var table in set.TrackedChangeTablesInNameOrder)
        {
            table
                .ValueColumnsInTableOrder.Select(column => column.OldColumnName.Value)
                .Should()
                .OnlyHaveUniqueItems(
                    $"table {table.Table.Name} must de-duplicate value columns by Old_ name"
                );
        }
    }

    /// <summary>
    /// It should emit value columns in a stable, pinned order. Column order derives from identity-then-
    /// securable path enumeration; this guards against a refactor that silently reorders columns (which
    /// would otherwise only surface as a golden-fixture diff).
    /// </summary>
    [Test]
    public void It_should_order_value_columns_deterministically()
    {
        var set = TrackedChangeDerivationTestHelpers.BuildSet(
            ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema()
        );

        var enrollment = TrackedChangeDerivationTestHelpers.TableBySourceName(set, "Enrollment");

        enrollment
            .ValueColumnsInTableOrder.Select(column => column.OldColumnName.Value)
            .Should()
            .Equal(
                "Old_School_SchoolId",
                "Old_School_EducationOrganizationId",
                "Old_Student_StudentUniqueId"
            );
    }
}

/// <summary>
/// Test fixture for the strict-resolution invariant: every identity and securable path must resolve to a
/// stored column, otherwise derivation fails loudly rather than silently dropping the column.
/// </summary>
[TestFixture]
public class Given_An_Unresolvable_Securable_Path_For_Tracked_Change_Derivation
{
    /// <summary>
    /// It should throw rather than silently omit a securable path that resolves to no stored column.
    /// </summary>
    [Test]
    public void It_should_throw_when_a_securable_path_has_no_stored_column()
    {
        var schema = ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema();
        var enrollment = (JsonObject)((JsonObject)schema["resourceSchemas"]!)["enrollments"]!;

        var securableElements = enrollment["securableElements"] as JsonObject;
        if (securableElements is null)
        {
            securableElements = new JsonObject();
            enrollment["securableElements"] = securableElements;
        }
        securableElements["Namespace"] = new JsonArray { "$.unresolvableSecurablePath" };

        var build = () => TrackedChangeDerivationTestHelpers.BuildSet(schema);

        build
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*could not resolve identity/securable path*$.unresolvableSecurablePath*");
    }
}

/// <summary>
/// Test fixture asserting the rich tracked-change derivation scenarios (descriptor joins, person joins,
/// concrete-abstract classification, and merged identity+securable origins) against the authoritative
/// DS-5.2 + Sample effective schema set, which exercises shapes that hand-built fixtures cannot.
/// </summary>
[TestFixture]
public class Given_The_Authoritative_Schema_Set_For_Tracked_Change_Derivation
{
    private DerivedRelationalModelSet _set = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var authoritativeFixtureRoot = BackendFixturePaths.GetAuthoritativeFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var coreInputPath = Path.Combine(
            authoritativeFixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );
        var extensionInputPath = Path.Combine(
            authoritativeFixtureRoot,
            "sample",
            "inputs",
            "sample-api-schema-authoritative.json"
        );

        File.Exists(coreInputPath).Should().BeTrue($"fixture missing at {coreInputPath}");
        File.Exists(extensionInputPath).Should().BeTrue($"fixture missing at {extensionInputPath}");

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            LoadProjectSchema(coreInputPath),
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            LoadProjectSchema(extensionInputPath),
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        _set = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should derive exactly one shared descriptor tracked-change table for the whole set.
    /// </summary>
    [Test]
    public void It_should_derive_exactly_one_shared_descriptor_table()
    {
        _set.TrackedChangeTablesInNameOrder.Where(table =>
                table.Kind == TrackedChangeTableKind.SharedDescriptor
            )
            .Should()
            .ContainSingle()
            .Which.SourceTable.Should()
            .Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
    }

    /// <summary>
    /// It should classify EducationOrganization subclasses (School / LEA / SEA) as ConcreteAbstract.
    /// </summary>
    [Test]
    public void It_should_classify_education_organization_subclasses_as_ConcreteAbstract()
    {
        foreach (var name in new[] { "School", "LocalEducationAgency", "StateEducationAgency" })
        {
            TrackedChangeDerivationTestHelpers
                .TableBySourceName(_set, name)
                .Kind.Should()
                .Be(TrackedChangeTableKind.ConcreteAbstract, $"{name} is a concrete EducationOrganization");
        }
    }

    /// <summary>
    /// It should merge Identity and SecurableElement origins onto a column reached by both purposes.
    /// </summary>
    [Test]
    public void It_should_merge_identity_and_securable_origins()
    {
        var academicWeek = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "AcademicWeek");

        var schoolId = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            academicWeek,
            "Old_School_SchoolId"
        );
        schoolId.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.Identity);
        schoolId.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.SecurableElement);

        var weekIdentifier = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            academicWeek,
            "Old_WeekIdentifier"
        );
        weekIdentifier.Origin.Should().Be(TrackedChangeColumnOrigin.Identity);
    }

    /// <summary>
    /// It should exclude array-nested securable paths from tracked-change value columns: their source
    /// columns live on child collection tables with 0..N rows per document, which a single-row-per-
    /// ChangeVersion tombstone cannot represent. Identity-origin columns (always root scalars) remain.
    /// </summary>
    [Test]
    public void It_should_exclude_array_nested_securable_paths()
    {
        // Set-wide invariant: no tracked value column sources from an array-nested path.
        foreach (var table in _set.TrackedChangeTablesInNameOrder)
        {
            table
                .ValueColumnsInTableOrder.Where(column => column.SourceJsonPath.Contains("[*]"))
                .Should()
                .BeEmpty($"table {table.Table.Name} must not track array-nested (child-collection) paths");
        }

        // Named examples: AssessmentAdministration's battery-part namespace securable
        // ($.assessmentBatteryParts[*].assessmentBatteryPartReference.namespace) and GraduationPlan's
        // required-assessment namespace securable are dropped, while identity-origin namespaces remain.
        var administration = TrackedChangeDerivationTestHelpers.TableBySourceName(
            _set,
            "AssessmentAdministration"
        );
        administration
            .ValueColumnsInTableOrder.Select(column => column.OldColumnName.Value)
            .Should()
            .NotContain("Old_AssessmentBatteryPart_Namespace")
            .And.Contain("Old_Assessment_Namespace");

        var graduationPlan = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "GraduationPlan");
        graduationPlan
            .ValueColumnsInTableOrder.Select(column => column.OldColumnName.Value)
            .Should()
            .NotContain("Old_RequiredAssessmentAssessment_Namespace");
    }

    /// <summary>
    /// It should materialize a descriptor reference as Namespace/CodeValue columns plus one named join.
    /// </summary>
    [Test]
    public void It_should_materialize_descriptor_reference_columns_and_one_join()
    {
        var competencyObjective = TrackedChangeDerivationTestHelpers.TableBySourceName(
            _set,
            "CompetencyObjective"
        );

        var join = competencyObjective.DescriptorJoins.Single(j =>
            j.DescriptorJoinName == "ObjectiveGradeLevelDescriptor"
        );
        join.SourceColumn.Value.Should().Be("ObjectiveGradeLevelDescriptor_DescriptorId");
        join.DescriptorResource.Should().Be(new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"));

        var namespaceColumn = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            competencyObjective,
            "Old_ObjectiveGradeLevelDescriptor_Namespace"
        );
        namespaceColumn.Role.Should().Be(TrackedChangeColumnRole.DescriptorNamespace);
        namespaceColumn.DescriptorJoinName.Should().Be("ObjectiveGradeLevelDescriptor");

        var codeValueColumn = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            competencyObjective,
            "Old_ObjectiveGradeLevelDescriptor_CodeValue"
        );
        codeValueColumn.Role.Should().Be(TrackedChangeColumnRole.DescriptorCodeValue);
        codeValueColumn.DescriptorJoinName.Should().Be("ObjectiveGradeLevelDescriptor");

        competencyObjective
            .ValueColumnsInTableOrder.Should()
            .NotContain(column =>
                column.OldColumnName.Value == "Old_ObjectiveGradeLevelDescriptor_DescriptorId"
            );
    }

    /// <summary>
    /// It should materialize a person securable as a DocumentId column plus one named person join.
    /// </summary>
    [Test]
    public void It_should_materialize_person_document_id_column_and_one_join()
    {
        var association = TrackedChangeDerivationTestHelpers.TableBySourceName(
            _set,
            "StudentSchoolAssociation"
        );

        var join = association.PersonJoins.Single(j => j.PersonJoinName == "Student");
        join.PersonKind.Should().Be(SecurableElementKind.Student);
        join.JoinPath.Should().NotBeEmpty();

        var personColumn = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            association,
            "Old_Student_DocumentId"
        );
        personColumn.Role.Should().Be(TrackedChangeColumnRole.PersonDocumentId);
        personColumn.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.SecurableElement);
        personColumn.PersonJoinName.Should().Be("Student");
    }

    /// <summary>
    /// It should record the canonical storage column on a key-unified tracked value column, and leave it
    /// null for a column that does not participate in key unification.
    /// </summary>
    [Test]
    public void It_should_record_the_canonical_storage_column_for_key_unified_columns()
    {
        var courseOffering = TrackedChangeDerivationTestHelpers.TableBySourceName(_set, "CourseOffering");

        var unified = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            courseOffering,
            "Old_SchoolId_Unified"
        );
        unified.CanonicalStorageColumn.Should().Be(new DbColumnName("SchoolId_Unified"));

        var nonUnified = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(
            courseOffering,
            "Old_LocalCourseCode"
        );
        nonUnified.CanonicalStorageColumn.Should().BeNull();
    }

    /// <summary>
    /// It should mark the shared descriptor Namespace as both identity and securable element (descriptors
    /// are namespace-secured), while CodeValue remains identity-only.
    /// </summary>
    [Test]
    public void It_should_carry_securable_origin_on_the_shared_descriptor_namespace()
    {
        var descriptor = _set.TrackedChangeTablesInNameOrder.Single(table =>
            table.Kind == TrackedChangeTableKind.SharedDescriptor
        );

        var ns = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(descriptor, "Old_Namespace");
        ns.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.Identity);
        ns.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.SecurableElement);

        var codeValue = TrackedChangeDerivationTestHelpers.ValueColumnByOldName(descriptor, "Old_CodeValue");
        codeValue.Origin.Should().Be(TrackedChangeColumnOrigin.Identity);
    }

    /// <summary>
    /// It should never repeat an Old_ value-column name within any derived tracked-change table.
    /// </summary>
    [Test]
    public void It_should_de_duplicate_value_columns_across_the_whole_set()
    {
        foreach (var table in _set.TrackedChangeTablesInNameOrder)
        {
            table
                .ValueColumnsInTableOrder.Select(column => column.OldColumnName.Value)
                .Should()
                .OnlyHaveUniqueItems(
                    $"table {table.Table.Name} must de-duplicate value columns by Old_ name"
                );
        }
    }

    private static JsonObject LoadProjectSchema(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException($"ApiSchema parsed null or non-object: {path}");
        }

        return RelationalModelSetSchemaHelpers.RequireObject(rootObject["projectSchema"], "projectSchema");
    }
}
