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
/// Test fixture for trigger set composition with nested collections, references, and abstract resources.
/// </summary>
[TestFixture]
public class Given_Trigger_Set_Composition
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger for root table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_root_table()
    {
        var schoolStamp = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "School" && t.Kind == DbTriggerKind.DocumentStamping
        );

        schoolStamp.Should().NotBeNull();
        schoolStamp!.Name.Value.Should().Be("TR_School_Stamp");
        schoolStamp.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should include identity projection columns on root table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_include_identity_projection_columns_on_root_table_stamping_trigger()
    {
        var schoolStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "School" && t.Kind == DbTriggerKind.DocumentStamping
        );

        schoolStamp.IdentityProjectionColumns.Should().NotBeEmpty();
        schoolStamp
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Contain("EducationOrganizationId");
    }

    /// <summary>
    /// It should create DocumentStamping trigger for child table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_child_table()
    {
        var childStamp = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.Should().NotBeNull();
        childStamp!.Name.Value.Should().Be("TR_SchoolAddress_Stamp");
    }

    /// <summary>
    /// It should use root document ID as KeyColumns on child table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_use_root_document_ID_as_KeyColumns_on_child_table_stamping_trigger()
    {
        var childStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.KeyColumns.Should().ContainSingle();
        childStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
    }

    /// <summary>
    /// It should have empty identity projection columns on child table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_have_empty_identity_projection_columns_on_child_table_stamping_trigger()
    {
        var childStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.IdentityProjectionColumns.Should().BeEmpty();
    }

    /// <summary>
    /// It should create ReferentialIdentityMaintenance trigger on root table.
    /// </summary>
    [Test]
    public void It_should_create_ReferentialIdentityMaintenance_trigger_on_root_table()
    {
        var refIdentity = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "School" && t.Kind == DbTriggerKind.ReferentialIdentityMaintenance
        );

        refIdentity.Should().NotBeNull();
        refIdentity!.Name.Value.Should().Be("TR_School_ReferentialIdentity");
        refIdentity.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
        refIdentity.IdentityProjectionColumns.Should().NotBeEmpty();
    }

    /// <summary>
    /// It should create AbstractIdentityMaintenance trigger for subclass resource.
    /// </summary>
    [Test]
    public void It_should_create_AbstractIdentityMaintenance_trigger_for_subclass_resource()
    {
        var abstractMaintenance = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "School" && t.Kind == DbTriggerKind.AbstractIdentityMaintenance
        );

        abstractMaintenance.Should().NotBeNull();
        abstractMaintenance!.Name.Value.Should().Be("TR_School_AbstractIdentity");
        abstractMaintenance.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
        abstractMaintenance.MaintenanceTargetTable.Should().NotBeNull();
        abstractMaintenance.MaintenanceTargetTable!.Value.Name.Should().Be("EducationOrganizationIdentity");
    }
}

/// <summary>
/// Test fixture for extension table trigger derivation.
/// </summary>
[TestFixture]
public class Given_Extension_Table_Triggers
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProjectSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger on extension table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_on_extension_table()
    {
        var extensionStamp = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "ContactExtension" && t.Kind == DbTriggerKind.DocumentStamping
        );

        extensionStamp.Should().NotBeNull();
    }

    /// <summary>
    /// It should use DocumentId as KeyColumns on extension table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_use_DocumentId_as_KeyColumns_on_extension_table_stamping_trigger()
    {
        var extensionStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "ContactExtension" && t.Kind == DbTriggerKind.DocumentStamping
        );

        extensionStamp.KeyColumns.Should().ContainSingle();
        extensionStamp.KeyColumns[0].Value.Should().Be("DocumentId");
    }
}

/// <summary>
/// Test fixture for descriptor resource trigger exclusion.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Trigger_Derivation
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should not derive triggers for descriptor resources.
    /// </summary>
    [Test]
    public void It_should_not_derive_triggers_for_descriptor_resources()
    {
        _triggers.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture verifying IdentityPropagationFallback triggers are not emitted on Pgsql,
/// even for schemas that have references qualifying for cascade propagation.
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_On_Pgsql
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should not emit any IdentityPropagationFallback triggers on Pgsql.
    /// </summary>
    [Test]
    public void It_should_not_emit_any_IdentityPropagationFallback_triggers_on_Pgsql()
    {
        var fallbackTriggers = _triggers.Where(t => t.Kind == DbTriggerKind.IdentityPropagationFallback);

        fallbackTriggers.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for root trigger identity projections when identity includes reference components.
/// </summary>
[TestFixture]
public class Given_Root_Trigger_Identity_Projection_With_Identity_Component_References
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should include identity-component reference part columns on root stamping trigger.
    /// </summary>
    [Test]
    public void It_should_include_identity_component_reference_part_columns_on_root_stamping_trigger()
    {
        var enrollmentStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "Enrollment" && t.Kind == DbTriggerKind.DocumentStamping
        );

        enrollmentStamp
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Equal("School_SchoolId", "School_EducationOrganizationId", "Student_StudentUniqueId");
    }

    /// <summary>
    /// It should include identity-component reference part columns on root referential trigger.
    /// </summary>
    [Test]
    public void It_should_include_identity_component_reference_part_columns_on_root_referential_trigger()
    {
        var referentialIdentity = _triggers.Single(t =>
            t.TriggerTable.Name == "Enrollment" && t.Kind == DbTriggerKind.ReferentialIdentityMaintenance
        );

        referentialIdentity
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Equal("School_SchoolId", "School_EducationOrganizationId", "Student_StudentUniqueId");
    }
}

/// <summary>
/// Test fixture for root identity projections with interleaved identity JSON paths.
/// </summary>
[TestFixture]
public class Given_Root_Trigger_Identity_Projection_With_Interleaved_Identity_Paths
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            TriggerInventoryTestSchemaBuilder.BuildReferenceIdentityProjectSchemaWithInterleavedIdentityPaths();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should order root stamping identity projection columns by identityJsonPaths.
    /// </summary>
    [Test]
    public void It_should_order_root_stamping_identity_projection_columns_by_identity_json_paths()
    {
        var enrollmentStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "Enrollment" && t.Kind == DbTriggerKind.DocumentStamping
        );

        enrollmentStamp
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Equal("School_SchoolId", "Student_StudentUniqueId", "School_EducationOrganizationId");
    }

    /// <summary>
    /// It should order root referential identity projection columns by identityJsonPaths.
    /// </summary>
    [Test]
    public void It_should_order_root_referential_identity_projection_columns_by_identity_json_paths()
    {
        var referentialIdentity = _triggers.Single(t =>
            t.TriggerTable.Name == "Enrollment" && t.Kind == DbTriggerKind.ReferentialIdentityMaintenance
        );

        referentialIdentity
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Equal("School_SchoolId", "Student_StudentUniqueId", "School_EducationOrganizationId");
    }
}

/// <summary>
/// Test fixture for IdentityPropagationFallback triggers on MSSQL dialect with concrete
/// reference targets (allowIdentityUpdates).
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_On_Mssql_With_Concrete_Targets
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should emit propagation trigger for allowIdentityUpdates target.
    /// </summary>
    [Test]
    public void It_should_emit_propagation_trigger_for_allowIdentityUpdates_target()
    {
        var schoolPropagation = _triggers.SingleOrDefault(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );

        schoolPropagation.Should().NotBeNull();
        schoolPropagation!.TriggerTable.Name.Should().Be("School");
    }

    /// <summary>
    /// It should not emit propagation trigger for non-updatable target.
    /// </summary>
    [Test]
    public void It_should_not_emit_propagation_trigger_for_non_updatable_target()
    {
        var studentPropagation = _triggers.SingleOrDefault(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_Student_PropagateIdentity"
        );

        studentPropagation.Should().BeNull();
    }

    /// <summary>
    /// It should carry referrer action payload with FK document-id column.
    /// </summary>
    [Test]
    public void It_should_carry_referrer_action_payload_with_FK_document_id_column()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );

        schoolPropagation.KeyColumns.Should().BeEmpty();
        schoolPropagation.IdentityProjectionColumns.Should().BeEmpty();
        schoolPropagation.PropagationFallback.Should().NotBeNull();

        var action = schoolPropagation.PropagationFallback!.ReferrerActions.Should().ContainSingle().Which;
        action.ReferrerTable.Name.Should().Be("Enrollment");
        action.ReferrerDocumentIdColumn.Value.Should().Be("School_DocumentId");
        action.ReferencedDocumentIdColumn.Value.Should().Be("DocumentId");
    }

    /// <summary>
    /// It should include ordered storage column pairs in propagation payload.
    /// </summary>
    [Test]
    public void It_should_include_ordered_storage_column_pairs_in_propagation_payload()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );

        var identityColumnPairs = schoolPropagation
            .PropagationFallback!.ReferrerActions.Single()
            .IdentityColumnPairs;

        identityColumnPairs
            .Select(pair => (pair.ReferrerStorageColumn.Value, pair.ReferencedStorageColumn.Value))
            .Should()
            .ContainInOrder(
                ("School_SchoolId", "SchoolId"),
                ("School_EducationOrganizationId", "EducationOrganizationId")
            );
    }

    /// <summary>
    /// It should not use maintenance target for propagation fallback.
    /// </summary>
    [Test]
    public void It_should_not_set_maintenance_target_for_propagation_fallback()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );

        schoolPropagation.MaintenanceTargetTable.Should().BeNull();
    }
}

/// <summary>
/// Test fixture for IdentityPropagationFallback fan-out on MSSQL with mixed root and child referrers.
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_On_Mssql_With_Non_Root_Referrers
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should include child-table referrer actions in the referenced-table fan-out payload.
    /// </summary>
    [Test]
    public void It_should_include_child_referrer_action_in_referenced_table_fan_out_payload()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );

        schoolPropagation.TriggerTable.Name.Should().Be("School");
        schoolPropagation.PropagationFallback.Should().NotBeNull();
        schoolPropagation.PropagationFallback!.ReferrerActions.Should().HaveCount(2);

        var childAction = schoolPropagation.PropagationFallback.ReferrerActions.Single(action =>
            action.ReferrerTable.Name == "BusRouteAddress"
        );

        childAction.ReferrerDocumentIdColumn.Value.Should().Be("School_DocumentId");
        childAction.ReferencedDocumentIdColumn.Value.Should().Be("DocumentId");
        childAction
            .IdentityColumnPairs.Select(pair =>
                (pair.ReferrerStorageColumn.Value, pair.ReferencedStorageColumn.Value)
            )
            .Should()
            .ContainInOrder(
                ("School_SchoolId", "SchoolId"),
                ("School_EducationOrganizationId", "EducationOrganizationId")
            );
    }
}

/// <summary>
/// Test fixture for IdentityPropagationFallback triggers on MSSQL dialect with abstract
/// reference targets.
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_On_Mssql_With_Abstract_Targets
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should emit propagation trigger for abstract target.
    /// </summary>
    [Test]
    public void It_should_emit_propagation_trigger_for_abstract_target()
    {
        var propagation = _triggers.SingleOrDefault(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_EducationOrganizationIdentity_PropagateIdentity"
        );

        propagation.Should().NotBeNull();
        propagation!.TriggerTable.Name.Should().Be("EducationOrganizationIdentity");
    }

    /// <summary>
    /// It should include abstract target identity storage column in propagation payload.
    /// </summary>
    [Test]
    public void It_should_include_abstract_target_identity_storage_column_in_propagation_payload()
    {
        var propagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_EducationOrganizationIdentity_PropagateIdentity"
        );

        var action = propagation.PropagationFallback!.ReferrerActions.Should().ContainSingle().Which;
        action.ReferencedDocumentIdColumn.Value.Should().Be("DocumentId");
        action
            .IdentityColumnPairs.Select(pair =>
                (pair.ReferrerStorageColumn.Value, pair.ReferencedStorageColumn.Value)
            )
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("EducationOrganization_EducationOrganizationId", "EducationOrganizationId"));
    }

    /// <summary>
    /// It should use abstract reference FK as the referrer document-id column.
    /// </summary>
    [Test]
    public void It_should_use_abstract_reference_FK_as_the_referrer_document_ID_column()
    {
        var propagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_EducationOrganizationIdentity_PropagateIdentity"
        );

        propagation.KeyColumns.Should().BeEmpty();
        propagation.IdentityProjectionColumns.Should().BeEmpty();
        propagation.MaintenanceTargetTable.Should().BeNull();
        propagation
            .PropagationFallback!.ReferrerActions.Single()
            .ReferrerDocumentIdColumn.Value.Should()
            .Be("EducationOrganization_DocumentId");
    }
}

/// <summary>
/// Test fixture for IdentityPropagationFallback payload mapping with key unification on MSSQL.
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_On_Mssql_With_Key_Unification
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;
    private DbTableModel _enrollmentTable = default!;
    private DbTableModel _schoolTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithIdentityUnification();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivationWithKeyUnification()
        );

        var result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
        _enrollmentTable = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel.Root;
        _schoolTable = result
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "School")
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should collapse unified identity bindings to a single canonical storage pair.
    /// </summary>
    [Test]
    public void It_should_collapse_unified_identity_bindings_to_a_single_canonical_storage_pair()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );
        var action = schoolPropagation.PropagationFallback!.ReferrerActions.Should().ContainSingle().Which;

        var localCanonicalColumn = ResolveCanonicalColumn(_enrollmentTable, "School_EducationOrganizationId");
        ResolveCanonicalColumn(_enrollmentTable, "School_SchoolId").Should().Be(localCanonicalColumn);

        var targetCanonicalColumn = ResolveCanonicalColumn(_schoolTable, "EducationOrganizationId");
        ResolveCanonicalColumn(_schoolTable, "SchoolId").Should().Be(targetCanonicalColumn);

        action
            .IdentityColumnPairs.Select(pair =>
                (pair.ReferrerStorageColumn.Value, pair.ReferencedStorageColumn.Value)
            )
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be((localCanonicalColumn.Value, targetCanonicalColumn.Value));
    }

    /// <summary>
    /// It should emit only stored columns in propagation identity pairs.
    /// </summary>
    [Test]
    public void It_should_emit_only_stored_columns_in_propagation_identity_pairs()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Kind == DbTriggerKind.IdentityPropagationFallback
            && t.Name.Value == "TR_School_PropagateIdentity"
        );
        var action = schoolPropagation.PropagationFallback!.ReferrerActions.Should().ContainSingle().Which;

        foreach (var pair in action.IdentityColumnPairs)
        {
            _enrollmentTable
                .Columns.Single(column => column.ColumnName.Equals(pair.ReferrerStorageColumn))
                .Storage.Should()
                .BeOfType<ColumnStorage.Stored>();
            _schoolTable
                .Columns.Single(column => column.ColumnName.Equals(pair.ReferencedStorageColumn))
                .Storage.Should()
                .BeOfType<ColumnStorage.Stored>();
        }
    }

    /// <summary>
    /// Resolves canonical storage column for a unified alias.
    /// </summary>
    private static DbColumnName ResolveCanonicalColumn(DbTableModel table, string aliasColumnName)
    {
        var alias = table.Columns.Single(column => column.ColumnName.Value == aliasColumnName);
        return alias.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject.CanonicalColumn;
    }
}

/// <summary>
/// Test fixture for propagation fallback mapping/binding mismatch handling.
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_With_Unmapped_Reference_Mapping
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivationWithUnmappedReferenceMapping()
        );

        _build = () => builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when a mapping path has no derived reference binding.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_reference_mapping_path_has_no_derived_binding()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Reference mapping 'UnmappedReferenceMapping'");
        exception.Message.Should().Contain("resource '");
        exception.Message.Should().Contain("Enrollment");
        exception.Message.Should().Contain("reference object path '$.unmappedReference'");
    }
}

/// <summary>
/// Test fixture for presence-gate validation in trigger derivation without index derivation.
/// </summary>
[TestFixture]
public class Given_Trigger_Derivation_With_Invalid_Presence_Gate_Without_Index_Derivation
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithIdentityUnification();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivationWithKeyUnificationAndInvalidPresenceGateWithoutIndexDerivation()
        );

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast on an invalid unified-alias presence gate without relying on index derivation.
    /// </summary>
    [Test]
    public void It_should_fail_fast_on_invalid_presence_gate_without_index_derivation()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Unified alias column");
        exception.Message.Should().Contain("table");
        exception.Message.Should().Contain("MissingPresenceGate");
        exception.Message.Should().Contain("presence-gate column");
    }
}

/// <summary>
/// Test fixture for three-level nested collection trigger derivation.
/// </summary>
[TestFixture]
public class Given_Three_Level_Nested_Collections
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildThreeLevelNestedProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger for grandchild table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_grandchild_table()
    {
        var grandchildStamp = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        grandchildStamp.Should().NotBeNull();
        grandchildStamp!.Name.Value.Should().Be("TR_SchoolAddressPeriod_Stamp");
    }

    /// <summary>
    /// It should use root document ID as KeyColumns on grandchild table.
    /// </summary>
    [Test]
    public void It_should_use_root_document_ID_as_KeyColumns_on_grandchild_table()
    {
        var grandchildStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        grandchildStamp.KeyColumns.Should().ContainSingle();
        grandchildStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
    }

    /// <summary>
    /// It should have empty identity projection columns on grandchild table.
    /// </summary>
    [Test]
    public void It_should_have_empty_identity_projection_columns_on_grandchild_table()
    {
        var grandchildStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        grandchildStamp.IdentityProjectionColumns.Should().BeEmpty();
    }

    /// <summary>
    /// It should create DocumentStamping triggers for all three levels.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_triggers_for_all_three_levels()
    {
        var stampTriggers = _triggers.Where(t => t.Kind == DbTriggerKind.DocumentStamping);

        stampTriggers.Should().HaveCount(3);
    }

    /// <summary>
    /// It should use same root document ID column across all child levels.
    /// </summary>
    [Test]
    public void It_should_use_same_root_document_ID_column_across_all_child_levels()
    {
        var childStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );
        var grandchildStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
        grandchildStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
    }
}

/// <summary>
/// Test fixture for multiple sibling collection trigger derivation.
/// </summary>
[TestFixture]
public class Given_Multiple_Sibling_Collections
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildSiblingCollectionsProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger for each sibling collection.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_each_sibling_collection()
    {
        var addressStamp = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "StudentAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );
        var telephoneStamp = _triggers.SingleOrDefault(t =>
            t.TriggerTable.Name == "StudentTelephone" && t.Kind == DbTriggerKind.DocumentStamping
        );

        addressStamp.Should().NotBeNull();
        telephoneStamp.Should().NotBeNull();
    }

    /// <summary>
    /// It should produce distinct trigger names for sibling tables.
    /// </summary>
    [Test]
    public void It_should_produce_distinct_trigger_names_for_sibling_tables()
    {
        var addressStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "StudentAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );
        var telephoneStamp = _triggers.Single(t =>
            t.TriggerTable.Name == "StudentTelephone" && t.Kind == DbTriggerKind.DocumentStamping
        );

        addressStamp.Name.Value.Should().Be("TR_StudentAddress_Stamp");
        telephoneStamp.Name.Value.Should().Be("TR_StudentTelephone_Stamp");
    }

    /// <summary>
    /// It should create three DocumentStamping triggers total.
    /// </summary>
    [Test]
    public void It_should_create_three_DocumentStamping_triggers_total()
    {
        var stampTriggers = _triggers.Where(t => t.Kind == DbTriggerKind.DocumentStamping);

        stampTriggers.Should().HaveCount(3);
    }
}

/// <summary>
/// Test fixture for deterministic trigger ordering.
/// </summary>
[TestFixture]
public class Given_Deterministic_Trigger_Ordering
{
    private IReadOnlyList<DbTriggerInfo> _triggersFirst = default!;
    private IReadOnlyList<DbTriggerInfo> _triggersSecond = default!;

    /// <summary>
    /// Sets up the test fixture by running the pipeline twice.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _triggersFirst = BuildTriggers();
        _triggersSecond = BuildTriggers();
    }

    private static IReadOnlyList<DbTriggerInfo> BuildTriggers()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        return result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should produce identical trigger sequence on repeated builds.
    /// </summary>
    [Test]
    public void It_should_produce_identical_trigger_sequence_on_repeated_builds()
    {
        var firstSequence = _triggersFirst.Select(t => (t.TriggerTable.Name, t.Name.Value, t.Kind)).ToList();
        var secondSequence = _triggersSecond
            .Select(t => (t.TriggerTable.Name, t.Name.Value, t.Kind))
            .ToList();

        firstSequence.Should().Equal(secondSequence);
    }
}

/// <summary>
/// Test pass that injects a synthetic unmapped reference mapping to validate fail-fast behavior.
/// </summary>
file sealed class UnmappedReferenceMappingFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            if (builderContext.DocumentReferenceMappings.Count == 0)
            {
                continue;
            }

            var firstMapping = builderContext.DocumentReferenceMappings[0];

            builderContext.DocumentReferenceMappings =
            [
                .. builderContext.DocumentReferenceMappings,
                firstMapping with
                {
                    MappingKey = "UnmappedReferenceMapping",
                    ReferenceObjectPath = JsonPathExpressionCompiler.Compile("$.unmappedReference"),
                },
            ];

            return;
        }

        throw new InvalidOperationException(
            "Test fixture requires at least one resource with document reference mappings."
        );
    }
}

/// <summary>
/// Test pass that rewrites one unified-alias presence gate to a missing column.
/// </summary>
file sealed class InvalidUnifiedAliasPresenceGateFixturePass : IRelationalModelSetPass
{
    private static readonly DbColumnName InvalidPresenceGateColumn = new("MissingPresenceGate");

    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (
            var resourceIndex = 0;
            resourceIndex < context.ConcreteResourcesInNameOrder.Count;
            resourceIndex++
        )
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[resourceIndex];

            if (concreteResource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            var relationalModel = concreteResource.RelationalModel;

            foreach (var table in relationalModel.TablesInDependencyOrder)
            {
                var aliasColumn = table.Columns.FirstOrDefault(column =>
                    column.Storage is ColumnStorage.UnifiedAlias { PresenceColumn: not null }
                );

                if (aliasColumn is null || aliasColumn.Storage is not ColumnStorage.UnifiedAlias unifiedAlias)
                {
                    continue;
                }

                var rewrittenAliasColumn = aliasColumn with
                {
                    Storage = new ColumnStorage.UnifiedAlias(
                        unifiedAlias.CanonicalColumn,
                        InvalidPresenceGateColumn
                    ),
                };
                var rewrittenColumns = table
                    .Columns.Select(column =>
                        column.ColumnName.Equals(aliasColumn.ColumnName) ? rewrittenAliasColumn : column
                    )
                    .ToArray();
                var rewrittenTable = table with { Columns = rewrittenColumns };
                var rewrittenTables = relationalModel
                    .TablesInDependencyOrder.Select(existingTable =>
                        existingTable.Table.Equals(table.Table) ? rewrittenTable : existingTable
                    )
                    .ToArray();
                var rewrittenRoot = relationalModel.Root.Table.Equals(table.Table)
                    ? rewrittenTable
                    : relationalModel.Root;
                var rewrittenModel = relationalModel with
                {
                    Root = rewrittenRoot,
                    TablesInDependencyOrder = rewrittenTables,
                };

                context.ConcreteResourcesInNameOrder[resourceIndex] = concreteResource with
                {
                    RelationalModel = rewrittenModel,
                };

                return;
            }
        }

        throw new InvalidOperationException(
            "Test fixture requires at least one unified alias with a presence gate."
        );
    }
}

/// <summary>
/// Test schema builder for trigger inventory pass tests.
/// </summary>
internal static class TriggerInventoryTestSchemaBuilder
{
    /// <summary>
    /// Build the standard pass list through trigger derivation.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughTriggerDerivation()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveIndexInventoryPass(),
            new DeriveTriggerInventoryPass(),
        ];
    }

    /// <summary>
    /// Build pass list through trigger derivation with an injected unmapped reference mapping.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughTriggerDerivationWithUnmappedReferenceMapping()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveIndexInventoryPass(),
            new UnmappedReferenceMappingFixturePass(),
            new DeriveTriggerInventoryPass(),
        ];
    }

    /// <summary>
    /// Build the standard pass list through trigger derivation with key unification.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughTriggerDerivationWithKeyUnification()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveIndexInventoryPass(),
            new DeriveTriggerInventoryPass(),
        ];
    }

    /// <summary>
    /// Build pass list through trigger derivation with key unification and invalid presence-gate fixture,
    /// without index derivation.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughTriggerDerivationWithKeyUnificationAndInvalidPresenceGateWithoutIndexDerivation()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new InvalidUnifiedAliasPresenceGateFixturePass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveTriggerInventoryPass(),
        ];
    }

    /// <summary>
    /// Build reference identity project schema with interleaved identityJsonPaths.
    /// </summary>
    internal static JsonObject BuildReferenceIdentityProjectSchemaWithInterleavedIdentityPaths()
    {
        var projectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema();

        if (projectSchema["resourceSchemas"] is not JsonObject resourceSchemas)
        {
            throw new InvalidOperationException(
                "Reference identity project schema is missing resourceSchemas."
            );
        }

        if (resourceSchemas["enrollments"] is not JsonObject enrollmentSchema)
        {
            throw new InvalidOperationException(
                "Reference identity project schema is missing enrollments resource schema."
            );
        }

        enrollmentSchema["identityJsonPaths"] = new JsonArray
        {
            "$.schoolReference.schoolId",
            "$.studentReference.studentUniqueId",
            "$.schoolReference.educationOrganizationId",
        };

        return projectSchema;
    }

    /// <summary>
    /// Build project schema with nested collections, references, and an abstract resource.
    /// </summary>
    internal static JsonObject BuildCompositeProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSubclassSchoolWithAddressesSchema() },
        };
    }

    /// <summary>
    /// Build project schema with three-level nested collections (School not a subclass).
    /// </summary>
    internal static JsonObject BuildThreeLevelNestedProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSchoolWithAddressPeriodsSchema() },
        };
    }

    /// <summary>
    /// Build project schema with sibling collections.
    /// </summary>
    internal static JsonObject BuildSiblingCollectionsProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["students"] = BuildStudentWithSiblingCollectionsSchema(),
            },
        };
    }

    private static JsonObject BuildSubclassSchoolWithAddressesSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 150,
                            },
                            ["city"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                        },
                    },
                },
            },
            ["required"] = new JsonArray("educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["isSubclass"] = true,
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildSchoolWithAddressPeriodsSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 150,
                            },
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["beginDate"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["format"] = "date",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            ["required"] = new JsonArray("educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildStudentWithSiblingCollectionsSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 150,
                            },
                        },
                    },
                },
                ["telephones"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["telephoneNumber"] = new JsonObject { ["type"] = "string", ["maxLength"] = 24 },
                        },
                    },
                },
            },
            ["required"] = new JsonArray("studentUniqueId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StudentUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.studentUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
