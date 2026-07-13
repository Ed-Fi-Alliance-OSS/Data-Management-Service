// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.Tests.Common;
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
            t.Table.Name == "School" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "School" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "SchoolAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "SchoolAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "SchoolAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        childStamp.IdentityProjectionColumns.Should().BeEmpty();
    }

    /// <summary>
    /// It should set the root table as the mirror stamp target for the root stamping trigger.
    /// </summary>
    [Test]
    public void It_should_set_root_table_as_mirror_stamp_target_for_root_trigger()
    {
        var rootStamp = _triggers.Single(t =>
            t.Table.Name == "School" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        rootStamp.MirrorStampTargetTable.Should().NotBeNull();
        rootStamp.MirrorStampTargetTable!.Value.Name.Should().Be("School");
    }

    /// <summary>
    /// It should set the owning resource root as the mirror stamp target for the child stamping trigger.
    /// </summary>
    [Test]
    public void It_should_set_owning_root_as_mirror_stamp_target_for_child_trigger()
    {
        var childStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        childStamp.MirrorStampTargetTable.Should().NotBeNull();
        childStamp.MirrorStampTargetTable!.Value.Name.Should().Be("School");
    }

    /// <summary>
    /// It should set a non-null mirror stamp target for every DocumentStamping trigger.
    /// </summary>
    [Test]
    public void It_should_set_non_null_mirror_stamp_target_for_every_DocumentStamping_trigger()
    {
        _triggers
            .Where(t => t.Parameters is TriggerKindParameters.DocumentStamping)
            .Should()
            .OnlyContain(t => t.MirrorStampTargetTable.HasValue);
    }

    /// <summary>
    /// It should derive the shared dms.Descriptor stamping trigger even with no descriptor resources.
    /// </summary>
    [Test]
    public void It_should_derive_shared_descriptor_stamping_trigger_without_descriptor_resources()
    {
        var descriptorStamp = _triggers.SingleOrDefault(t =>
            t.Table.Equals(new DbTableName(new DbSchemaName("dms"), "Descriptor"))
            && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        descriptorStamp.Should().NotBeNull();
        descriptorStamp!.Name.Value.Should().Be("TR_Descriptor_Stamp_Document");
        descriptorStamp.IdentityProjectionColumns.Should().BeEmpty();
        descriptorStamp
            .MirrorStampTargetTable.Should()
            .Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
    }

    /// <summary>
    /// It should create ReferentialIdentityMaintenance trigger on root table.
    /// </summary>
    [Test]
    public void It_should_create_ReferentialIdentityMaintenance_trigger_on_root_table()
    {
        var refIdentity = _triggers.SingleOrDefault(t =>
            t.Table.Name == "School" && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );

        refIdentity.Should().NotBeNull();
        refIdentity!.Name.Value.Should().Be("TR_School_ReferentialIdentity");
        refIdentity.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
        refIdentity.IdentityProjectionColumns.Should().NotBeEmpty();
        var refIdParams = refIdentity.Parameters as TriggerKindParameters.ReferentialIdentityMaintenance;
        refIdParams.Should().NotBeNull();
        refIdParams!.IdentityElements.Should().NotBeEmpty();
        refIdParams.IdentityElements.Select(e => e.Column.Value).Should().Contain("EducationOrganizationId");
    }

    /// <summary>
    /// It should create AbstractIdentityMaintenance trigger for subclass resource.
    /// </summary>
    [Test]
    public void It_should_create_AbstractIdentityMaintenance_trigger_for_subclass_resource()
    {
        var abstractMaintenance = _triggers.SingleOrDefault(t =>
            t.Table.Name == "School" && t.Parameters is TriggerKindParameters.AbstractIdentityMaintenance
        );

        abstractMaintenance.Should().NotBeNull();
        abstractMaintenance!.Name.Value.Should().Be("TR_School_AbstractIdentity");
        abstractMaintenance.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
        var abstractParams =
            abstractMaintenance.Parameters as TriggerKindParameters.AbstractIdentityMaintenance;
        abstractParams.Should().NotBeNull();
        abstractParams!.TargetTable.Name.Should().Be("EducationOrganizationIdentity");
        abstractParams.TargetColumnMappings.Should().NotBeEmpty();
        abstractParams
            .TargetColumnMappings.Select(m => m.TargetColumn.Value)
            .Should()
            .Contain("EducationOrganizationId");
        abstractParams.DiscriminatorValue.Should().Be("Ed-Fi:School");
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
            t.Table.Name == "ContactExtension" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "ContactExtension" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        extensionStamp.KeyColumns.Should().ContainSingle();
        extensionStamp.KeyColumns[0].Value.Should().Be("DocumentId");
    }

    /// <summary>
    /// It should set the owning resource root as the mirror stamp target for the extension stamping trigger.
    /// </summary>
    [Test]
    public void It_should_set_owning_root_as_mirror_stamp_target_for_extension_trigger()
    {
        var extensionStamp = _triggers.Single(t =>
            t.Table.Name == "ContactExtension" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        extensionStamp.MirrorStampTargetTable.Should().NotBeNull();
        extensionStamp.MirrorStampTargetTable!.Value.Name.Should().Be("Contact");
    }
}

/// <summary>
/// Test fixture for stable-key collection and collection-aligned extension trigger derivation.
/// </summary>
[TestFixture]
public class Given_Stable_Key_Collections_And_Aligned_Extension_Scopes
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;
    private DbTableModel _addressTable = default!;
    private DbTableModel _extensionAddressTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ExtensionTableTestSchemaBuilder.BuildCoreProjectSchema();
        var extensionProjectSchema = ExtensionTableTestSchemaBuilder.BuildExtensionProjectSchema();
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
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveIndexInventoryPass(),
            new DeriveTriggerInventoryPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;

        var schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "School"
            )
            .RelationalModel;

        _addressTable = schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "SchoolAddress"
        );
        _extensionAddressTable = schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "SchoolExtensionAddress"
        );
    }

    /// <summary>
    /// It should stamp stable-key collection tables by the root document locator instead of the collection PK.
    /// </summary>
    [Test]
    public void It_should_stamp_stable_key_collection_tables_by_the_root_document_locator()
    {
        var addressStamp = _triggers.Single(trigger =>
            trigger.Table.Name == "SchoolAddress"
            && trigger.Parameters is TriggerKindParameters.DocumentStamping
        );

        _addressTable
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("CollectionItemId");
        addressStamp.KeyColumns.Select(column => column.Value).Should().Equal("School_DocumentId");
    }

    /// <summary>
    /// It should stamp collection-aligned extension scopes by the root document locator instead of the base PK.
    /// </summary>
    [Test]
    public void It_should_stamp_collection_aligned_extension_scopes_by_the_root_document_locator()
    {
        var extensionAddressStamp = _triggers.Single(trigger =>
            trigger.Table.Name == "SchoolExtensionAddress"
            && trigger.Parameters is TriggerKindParameters.DocumentStamping
        );

        _extensionAddressTable
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("BaseCollectionItemId");
        extensionAddressStamp.KeyColumns.Select(column => column.Value).Should().Equal("School_DocumentId");
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
    /// It should derive only the shared descriptor stamping trigger for a descriptor-only project.
    /// </summary>
    [Test]
    public void It_should_derive_only_the_shared_descriptor_stamping_trigger()
    {
        _triggers.Should().ContainSingle();

        var descriptorStamp = _triggers.Single();
        descriptorStamp.Table.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
        descriptorStamp.Name.Value.Should().Be("TR_Descriptor_Stamp_Document");
        descriptorStamp.Parameters.Should().BeOfType<TriggerKindParameters.DocumentStamping>();
        descriptorStamp.IdentityProjectionColumns.Should().BeEmpty();
        descriptorStamp
            .MirrorStampTargetTable.Should()
            .Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
    }

    /// <summary>
    /// It should not derive per-resource triggers for the SharedDescriptorTable descriptor resource.
    /// </summary>
    [Test]
    public void It_should_not_derive_per_resource_triggers_for_the_descriptor_resource()
    {
        _triggers.Should().NotContain(t => t.Table.Name == "GradeLevelDescriptor");
    }

    /// <summary>
    /// It should not attach change-tracking in the trigger pass (attachment is the tracked-change pass).
    /// </summary>
    [Test]
    public void It_should_leave_descriptor_stamping_change_tracking_unattached_in_the_trigger_pass()
    {
        var stamping = (TriggerKindParameters.DocumentStamping)_triggers.Single().Parameters;

        stamping.ChangeTracking.Should().BeNull();
    }
}

/// <summary>
/// Test fixture verifying no identity-value propagation trigger is emitted on either dialect:
/// SQL Server identity propagation is handled by native cascades under FK pruning, while the
/// stamping, referential-identity, and shared descriptor triggers remain unchanged.
/// </summary>
[TestFixture]
public class Given_Trigger_Inventory_Without_Identity_Propagation
{
    private IReadOnlyList<DbTriggerInfo> _mssqlTriggers = default!;
    private IReadOnlyList<DbTriggerInfo> _pgsqlTriggers = default!;

    /// <summary>
    /// Sets up the test fixture with a schema whose mutable School reference previously produced
    /// the MSSQL propagation trigger fallback.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        static EffectiveSchemaSet BuildSchemaSet()
        {
            var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema(),
                isExtensionProject: false
            );

            return EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        }

        var mssqlBuilder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );
        _mssqlTriggers = mssqlBuilder
            .Build(BuildSchemaSet(), SqlDialect.Mssql, new MssqlDialectRules())
            .TriggersInCreateOrder;

        var pgsqlBuilder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );
        _pgsqlTriggers = pgsqlBuilder
            .Build(BuildSchemaSet(), SqlDialect.Pgsql, new PgsqlDialectRules())
            .TriggersInCreateOrder;
    }

    /// <summary>
    /// It should emit no identity-value propagation trigger on either dialect.
    /// </summary>
    [Test]
    public void It_should_emit_no_identity_propagation_trigger_on_either_dialect()
    {
        _mssqlTriggers.Should().NotContain(t => t.Name.Value.Contains("PropagateIdentity"));
        _pgsqlTriggers.Should().NotContain(t => t.Name.Value.Contains("PropagateIdentity"));
    }

    /// <summary>
    /// It should keep the stamping, referential-identity, and shared descriptor triggers.
    /// </summary>
    [Test]
    public void It_should_keep_maintenance_and_stamping_triggers_unchanged()
    {
        _mssqlTriggers.Should().Contain(t => t.Parameters is TriggerKindParameters.DocumentStamping);
        _mssqlTriggers
            .Should()
            .Contain(t => t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance);
        _mssqlTriggers.Should().Contain(t => t.Name.Value == "TR_Descriptor_Stamp_Document");

        _mssqlTriggers
            .Select(t => (t.Table.Name, t.Name.Value))
            .Should()
            .Equal(_pgsqlTriggers.Select(t => (t.Table.Name, t.Name.Value)));
    }
}

/// <summary>
/// Test fixture for three-level nested collection trigger derivation.
/// </summary>
[TestFixture]
public class Given_Three_Level_Nested_Collections
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;
    private DbTableModel _grandchildTable = default!;

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
        _grandchildTable = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "School"
            )
            .RelationalModel.TablesInDependencyOrder.Single(table =>
                table.Table.Name == "SchoolAddressPeriod"
            );
    }

    /// <summary>
    /// It should create DocumentStamping trigger for grandchild table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_grandchild_table()
    {
        var grandchildStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "SchoolAddressPeriod" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        grandchildStamp.KeyColumns.Should().ContainSingle();
        grandchildStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
    }

    /// <summary>
    /// It should stamp nested stable-key collection rows by the root document locator instead of the row PK.
    /// </summary>
    [Test]
    public void It_should_stamp_nested_stable_key_collection_rows_by_the_root_document_locator()
    {
        var grandchildStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        _grandchildTable
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("CollectionItemId");
        grandchildStamp.KeyColumns.Select(column => column.Value).Should().Equal("School_DocumentId");
    }

    /// <summary>
    /// It should have empty identity projection columns on grandchild table.
    /// </summary>
    [Test]
    public void It_should_have_empty_identity_projection_columns_on_grandchild_table()
    {
        var grandchildStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Parameters is TriggerKindParameters.DocumentStamping
        );

        grandchildStamp.IdentityProjectionColumns.Should().BeEmpty();
    }

    /// <summary>
    /// It should create DocumentStamping triggers for all three levels.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_triggers_for_all_three_levels()
    {
        // Exclude the shared dms.Descriptor stamping trigger, which is derived once per model set.
        var stampTriggers = _triggers.Where(t =>
            t.Parameters is TriggerKindParameters.DocumentStamping && t.Table.Name != "Descriptor"
        );

        stampTriggers.Should().HaveCount(3);
    }

    /// <summary>
    /// It should use same root document ID column across all child levels.
    /// </summary>
    [Test]
    public void It_should_use_same_root_document_ID_column_across_all_child_levels()
    {
        var childStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
        );
        var grandchildStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "StudentAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
        );
        var telephoneStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "StudentTelephone" && t.Parameters is TriggerKindParameters.DocumentStamping
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
            t.Table.Name == "StudentAddress" && t.Parameters is TriggerKindParameters.DocumentStamping
        );
        var telephoneStamp = _triggers.Single(t =>
            t.Table.Name == "StudentTelephone" && t.Parameters is TriggerKindParameters.DocumentStamping
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
        // Exclude the shared dms.Descriptor stamping trigger, which is derived once per model set.
        var stampTriggers = _triggers.Where(t =>
            t.Parameters is TriggerKindParameters.DocumentStamping && t.Table.Name != "Descriptor"
        );

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
        static string KindLabel(TriggerKindParameters p) =>
            p switch
            {
                TriggerKindParameters.DocumentStamping => "DocumentStamping",
                TriggerKindParameters.ReferentialIdentityMaintenance => "ReferentialIdentityMaintenance",
                TriggerKindParameters.AbstractIdentityMaintenance => "AbstractIdentityMaintenance",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(p),
                    "Unsupported trigger kind parameters type."
                ),
            };

        var firstSequence = _triggersFirst
            .Select(t => (t.Table.Name, t.Name.Value, KindLabel(t.Parameters)))
            .ToList();
        var secondSequence = _triggersSecond
            .Select(t => (t.Table.Name, t.Name.Value, KindLabel(t.Parameters)))
            .ToList();

        firstSequence.Should().Equal(secondSequence);
    }
}

/// <summary>
/// Test fixture proving that reference-bearing identity elements resolve to identity-part
/// columns (e.g. School_SchoolId) rather than FK DocumentId columns (e.g. School_DocumentId)
/// in ReferentialIdentityMaintenance triggers.
/// </summary>
[TestFixture]
public class Given_Reference_Bearing_Identity_For_ReferentialIdentity_Trigger
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

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

    [Test]
    public void It_should_use_identity_part_columns_not_FK_DocumentId_in_ReferentialIdentity_trigger()
    {
        var refIdentity = _triggers.SingleOrDefault(t =>
            t.Table.Name == "Enrollment"
            && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );

        refIdentity.Should().NotBeNull("Enrollment should have a ReferentialIdentityMaintenance trigger");
        var refIdParams = (TriggerKindParameters.ReferentialIdentityMaintenance)refIdentity!.Parameters;

        var columnNames = refIdParams.IdentityElements.Select(e => e.Column.Value).ToList();

        // Must resolve to identity-part columns, not FK DocumentId columns
        columnNames.Should().Contain("School_SchoolId");
        columnNames.Should().Contain("School_EducationOrganizationId");
        columnNames.Should().Contain("Student_StudentUniqueId");
        columnNames.Should().NotContain("School_DocumentId");
        columnNames.Should().NotContain("Student_DocumentId");
    }

    [Test]
    public void It_should_pair_identity_part_columns_with_correct_json_paths()
    {
        var refIdentity = _triggers.Single(t =>
            t.Table.Name == "Enrollment"
            && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );

        var refIdParams = (TriggerKindParameters.ReferentialIdentityMaintenance)refIdentity.Parameters;

        var mappings = refIdParams
            .IdentityElements.Select(e => (e.Column.Value, e.IdentityJsonPath))
            .ToList();

        mappings
            .Should()
            .Contain(("School_SchoolId", "$.schoolReference.schoolId"))
            .And.Contain(("School_EducationOrganizationId", "$.schoolReference.educationOrganizationId"))
            .And.Contain(("Student_StudentUniqueId", "$.studentReference.studentUniqueId"));
    }

    [Test]
    public void It_should_use_identity_part_columns_in_identity_projection_columns()
    {
        var refIdentity = _triggers.Single(t =>
            t.Table.Name == "Enrollment"
            && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );

        var projectionColumnNames = refIdentity.IdentityProjectionColumns.Select(c => c.Value).ToList();

        projectionColumnNames.Should().Contain("School_SchoolId");
        projectionColumnNames.Should().Contain("School_EducationOrganizationId");
        projectionColumnNames.Should().Contain("Student_StudentUniqueId");
        projectionColumnNames.Should().NotContain("School_DocumentId");
        projectionColumnNames.Should().NotContain("Student_DocumentId");
    }
}

/// <summary>
/// Test fixture proving descriptor-valued identity elements carry descriptor metadata into
/// ReferentialIdentityMaintenance triggers.
/// </summary>
[TestFixture]
public class Given_Descriptor_Valued_Identity_For_ReferentialIdentity_Trigger
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = AbstractIdentityTableTestSchemaBuilder.BuildDescriptorIdentityProjectSchema();
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

    [Test]
    public void It_should_mark_descriptor_identity_elements_as_descriptor_references()
    {
        var refIdentity = _triggers.Single(t =>
            t.Table.Name == "ProgramOffering"
            && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );

        var refIdParams = (TriggerKindParameters.ReferentialIdentityMaintenance)refIdentity.Parameters;
        var element = refIdParams.IdentityElements.Should().ContainSingle().Subject;

        element.Column.Value.Should().Be("ProgramTypeDescriptor_DescriptorId");
        element.IdentityJsonPath.Should().Be("$.programTypeDescriptor");
        element.IsDescriptorReference.Should().BeTrue();
    }

    [Test]
    public void It_should_preserve_descriptor_metadata_on_superclass_alias_identity_elements()
    {
        var refIdentity = _triggers.Single(t =>
            t.Table.Name == "ProgramOffering"
            && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );

        var refIdParams = (TriggerKindParameters.ReferentialIdentityMaintenance)refIdentity.Parameters;
        refIdParams.SuperclassAlias.Should().NotBeNull();
        var aliasElement = refIdParams.SuperclassAlias!.IdentityElements.Should().ContainSingle().Subject;

        aliasElement.Column.Value.Should().Be("ProgramTypeDescriptor_DescriptorId");
        aliasElement.IdentityJsonPath.Should().Be("$.programTypeDescriptor");
        aliasElement.IsDescriptorReference.Should().BeTrue();
    }
}

/// <summary>
/// Test fixture proving ReferentialIdentityMaintenance identity elements stay aligned with the
/// resource's identity JSON paths when key unification fans one reference-site logical field out to
/// multiple physical binding columns (one identity path, two unified alias columns), while distinct
/// identity paths that merely share canonical storage each keep their own hash element.
/// </summary>
[TestFixture]
public class Given_Key_Unified_Reference_Identity_For_ReferentialIdentity_Trigger
{
    private DerivedRelationalModelSet _result = default!;
    private DbTriggerInfo _registrationTrigger = default!;
    private TriggerKindParameters.ReferentialIdentityMaintenance _registrationRefId = default!;
    private TriggerKindParameters.ReferentialIdentityMaintenance _offeringRefId = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            TriggerInventoryTestSchemaBuilder.BuildKeyUnifiedReferenceIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        _result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _registrationTrigger = _result.TriggersInCreateOrder.Single(t =>
            t.Table.Name == "Registration"
            && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
        );
        _registrationRefId = (TriggerKindParameters.ReferentialIdentityMaintenance)
            _registrationTrigger.Parameters;

        _offeringRefId = (TriggerKindParameters.ReferentialIdentityMaintenance)
            _result
                .TriggersInCreateOrder.Single(t =>
                    t.Table.Name == "Offering"
                    && t.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance
                )
                .Parameters;
    }

    [Test]
    public void It_should_emit_one_identity_element_per_identity_path_on_the_referencing_resource()
    {
        _registrationRefId
            .IdentityElements.Select(e => e.IdentityJsonPath)
            .Should()
            .Equal("$.offeringReference.offeringName", "$.offeringReference.schoolId", "$.registrationId");
    }

    [Test]
    public void It_should_retain_the_first_member_column_of_the_key_unified_logical_field_group()
    {
        var registrationModel = _result
            .ConcreteResourcesInNameOrder.Single(m => m.ResourceKey.Resource.ResourceName == "Registration")
            .RelationalModel;
        var offeringBinding = registrationModel.DocumentReferenceBindings.Single(b =>
            b.ReferenceObjectPath.Canonical == "$.offeringReference"
        );
        var schoolIdGroup = offeringBinding
            .GetLogicalFieldGroups()
            .Single(g => g.ReferenceJsonPath.Canonical == "$.offeringReference.schoolId");

        schoolIdGroup
            .MemberColumns.Should()
            .HaveCountGreaterThan(
                1,
                "the fixture must fan one logical reference field out to multiple unified columns"
            );

        var element = _registrationRefId.IdentityElements.Single(e =>
            e.IdentityJsonPath == "$.offeringReference.schoolId"
        );
        element.Column.Should().Be(schoolIdGroup.MemberColumns[0]);
    }

    [Test]
    public void It_should_retain_distinct_identity_paths_that_share_canonical_storage()
    {
        _offeringRefId
            .IdentityElements.Select(e => e.IdentityJsonPath)
            .Should()
            .Equal(
                "$.offeringName",
                "$.primarySchoolReference.schoolId",
                "$.secondarySchoolReference.schoolId"
            );
    }

    [Test]
    public void It_should_keep_identity_projection_columns_on_deduplicated_canonical_stored_columns()
    {
        var registrationRoot = _result
            .ConcreteResourcesInNameOrder.Single(m => m.ResourceKey.Resource.ResourceName == "Registration")
            .RelationalModel.Root;

        var storedColumns = registrationRoot
            .Columns.Where(c => c.Storage is not ColumnStorage.UnifiedAlias)
            .Select(c => c.ColumnName)
            .ToHashSet();

        _registrationTrigger.IdentityProjectionColumns.Should().OnlyHaveUniqueItems();
        _registrationTrigger.IdentityProjectionColumns.Should().HaveCount(3);
        _registrationTrigger
            .IdentityProjectionColumns.Should()
            .OnlyContain(column => storedColumns.Contains(column));
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
/// Test fixture proving that AbstractIdentityMaintenance trigger column mappings for an
/// abstract resource with composite reference identity carry the renamed columns
/// (e.g. <c>Program_EducationOrganizationId</c>, <c>Program_ProgramTypeDescriptor_DescriptorId</c>,
/// <c>Student_StudentUniqueId</c>), not old concatenated names.
/// Uses a schema where the abstract resource has identity paths routed through resource references,
/// which produces the Ref_Field / Ref_Field_DescriptorId naming convention.
/// </summary>
[TestFixture]
public class Given_AbstractIdentityMaintenance_Trigger_Carries_Renamed_Abstract_Identity_Columns
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    [SetUp]
    public void Setup()
    {
        // Schema with GeneralStudentProgramAssociation as abstract resource:
        // identity paths through EducationOrganization reference, Program reference (with descriptor),
        // and Student reference.  The resulting abstract identity table columns must use the
        // renamed convention: Resource_Field and Resource_Field_DescriptorId.
        var coreProjectSchema = BuildAbstractCompositeReferenceProjectSchema();
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

    [Test]
    public void It_should_emit_AbstractIdentityMaintenance_trigger_for_the_concrete_subclass()
    {
        var abstractMaintenance = _triggers.SingleOrDefault(t =>
            t.Table.Name == "StudentArtProgramAssociation"
            && t.Parameters is TriggerKindParameters.AbstractIdentityMaintenance
        );

        abstractMaintenance.Should().NotBeNull();
        abstractMaintenance!.Name.Value.Should().Be("TR_StudentArtProgramAssociation_AbstractIdentity");
    }

    [Test]
    public void It_should_carry_renamed_columns_in_TargetColumnMappings()
    {
        var abstractMaintenance = _triggers.Single(t =>
            t.Table.Name == "StudentArtProgramAssociation"
            && t.Parameters is TriggerKindParameters.AbstractIdentityMaintenance
        );

        var abstractParams =
            abstractMaintenance.Parameters as TriggerKindParameters.AbstractIdentityMaintenance;
        abstractParams.Should().NotBeNull();

        var targetColumnNames = abstractParams!
            .TargetColumnMappings.Select(m => m.TargetColumn.Value)
            .ToList();

        // Renamed scalar reference column
        targetColumnNames.Should().Contain("Program_EducationOrganizationId");
        // Renamed additional scalar reference column (a reference-backed scalar other than the first)
        targetColumnNames.Should().Contain("Program_ProgramName");
        // Renamed descriptor reference column
        targetColumnNames.Should().Contain("Program_ProgramTypeDescriptor_DescriptorId");
        // Renamed student reference column
        targetColumnNames.Should().Contain("Student_StudentUniqueId");

        // Must NOT carry old concatenated-name patterns
        targetColumnNames
            .Should()
            .NotContain(c => c.StartsWith("ProgramReference", StringComparison.Ordinal));
        targetColumnNames
            .Should()
            .NotContain(c => c.StartsWith("StudentReference", StringComparison.Ordinal));
    }

    private static JsonObject BuildAbstractCompositeReferenceProjectSchema() =>
        GeneralStudentProgramAssociationTestSchema.BuildProjectSchema();
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
            new TransitiveIdentityMutabilityPass(),
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

    /// <summary>
    /// Build project schema where a referenced resource's identity contains two reference paths whose
    /// columns are key-unified, so a downstream referencing resource fans one reference-site logical
    /// field out to multiple unified binding columns.
    /// </summary>
    internal static JsonObject BuildKeyUnifiedReferenceIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["offerings"] = BuildKeyUnifiedOfferingSchema(),
                ["registrations"] = BuildKeyUnifiedRegistrationSchema(),
                ["schools"] = BuildKeyUnifiedSchoolSchema(),
            },
        };
    }

    private static JsonObject BuildKeyUnifiedSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("schoolId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Offering's identity includes both school references; their schoolId columns are key-unified
    /// through the same-site grouping of the downstream reference plus this resource's own identity.
    /// </summary>
    private static JsonObject BuildKeyUnifiedOfferingSchema()
    {
        var schoolReferenceSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("schoolId"),
        };

        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["offeringName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                ["primarySchoolReference"] = schoolReferenceSchema,
                ["secondarySchoolReference"] = schoolReferenceSchema.DeepClone(),
            },
            ["required"] = new JsonArray(
                "offeringName",
                "primarySchoolReference",
                "secondarySchoolReference"
            ),
        };

        return new JsonObject
        {
            ["resourceName"] = "Offering",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.offeringName",
                "$.primarySchoolReference.schoolId",
                "$.secondarySchoolReference.schoolId",
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.primarySchoolReference.schoolId",
                    ["targetJsonPath"] = "$.secondarySchoolReference.schoolId",
                },
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["OfferingName"] = new JsonObject { ["isReference"] = false, ["path"] = "$.offeringName" },
                ["PrimarySchool"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.primarySchoolReference.schoolId",
                        },
                    },
                },
                ["SecondarySchool"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.secondarySchoolReference.schoolId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Registration references Offering; two of Offering's identity paths map to the single
    /// offeringReference.schoolId field, fanning one logical reference field out to two columns.
    /// </summary>
    private static JsonObject BuildKeyUnifiedRegistrationSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["registrationId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                ["offeringReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["offeringName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                    },
                    ["required"] = new JsonArray("offeringName", "schoolId"),
                },
            },
            ["required"] = new JsonArray("registrationId", "offeringReference"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Registration",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.offeringReference.offeringName",
                "$.offeringReference.schoolId",
                "$.registrationId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["Offering"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Offering",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.offeringName",
                            ["referenceJsonPath"] = "$.offeringReference.offeringName",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.primarySchoolReference.schoolId",
                            ["referenceJsonPath"] = "$.offeringReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.secondarySchoolReference.schoolId",
                            ["referenceJsonPath"] = "$.offeringReference.schoolId",
                        },
                    },
                },
                ["RegistrationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.registrationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}

/// <summary>
/// Trigger derivation over a grouped-duplicate reference, where one reference field
/// (<c>$.schoolReference.schoolId</c>) is bound to two target identity fields (<c>$.schoolId</c> and
/// <c>$.localEducationAgencyId</c>) that key-unify on the referenced School. This exercises two trigger
/// paths that previously mishandled duplicate <c>ReferenceJsonPath</c> bindings:
/// <list type="bullet">
/// <item>Abstract identity maintenance must accept the converging duplicate (two identity-part columns
/// resolving to one stored column) rather than rejecting any count other than one.</item>
/// <item>Identity propagation must pair each referrer target column with the source column for its own
/// target identity path, instead of collapsing duplicates by reference path and applying one identity
/// path's source column to every binding.</item>
/// </list>
/// </summary>
[TestFixture]
public class Given_Grouped_Duplicate_Reference_Trigger_Derivation_On_Mssql
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture using the default duplicate reference binding order.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _triggers = BuildTriggers(reverseDuplicateReferenceBindings: false);
    }

    /// <summary>
    /// Derives the trigger inventory for the grouped-duplicate fixture. Key unification must run before
    /// abstract identity derivation so the grouped duplicate columns converge; transitive identity
    /// mutability must run so the identity-propagation trigger is emitted on the mutable referenced
    /// resource.
    /// </summary>
    private static IReadOnlyList<DbTriggerInfo> BuildTriggers(bool reverseDuplicateReferenceBindings)
    {
        var coreProjectSchema =
            AbstractIdentityTableTestSchemaBuilder.BuildGroupedReferenceIdentityProjectSchema(
                reverseDuplicateReferenceBindings
            );
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
                new RootIdentityConstraintPass(),
                new TransitiveIdentityMutabilityPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
                new ApplyConstraintDialectHashingPass(),
                new DeriveIndexInventoryPass(),
                new DeriveTriggerInventoryPass(),
            }
        );
        return builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules()).TriggersInCreateOrder;
    }

    private static IReadOnlyList<(string Source, string Target)> AbstractMaintenanceMappings(
        IReadOnlyList<DbTriggerInfo> triggers
    )
    {
        var trigger = triggers.Single(t =>
            t.Table.Name == "EnrollmentSchoolCarrier"
            && t.Parameters is TriggerKindParameters.AbstractIdentityMaintenance
        );
        var parameters = (TriggerKindParameters.AbstractIdentityMaintenance)trigger.Parameters;

        return parameters
            .TargetColumnMappings.Select(m => (m.SourceColumn.Value, m.TargetColumn.Value))
            .ToArray();
    }

    /// <summary>
    /// The abstract identity maintenance trigger must be derived for the grouped-duplicate subclass: the
    /// two identity-part columns bound to <c>$.schoolReference.schoolId</c> converge to one stored column
    /// (<c>School_SchoolId</c>), so the mapping must use that converged column rather than failing because
    /// more than one identity-part column was found.
    /// </summary>
    [Test]
    public void It_should_derive_abstract_identity_maintenance_for_grouped_duplicate_reference()
    {
        AbstractMaintenanceMappings(_triggers).Should().Contain(("School_SchoolId", "School_SchoolId"));
    }

    /// <summary>
    /// Reversing the duplicate reference binding order must not change the emitted trigger source columns.
    /// The field-name-matched binding is chosen deterministically (the same rule that names abstract
    /// identity columns), so abstract identity maintenance keeps the SchoolId-derived source rather than
    /// the value-equal LocalEducationAgencyId one a binding-order-sensitive selection would emit under the
    /// reversed order.
    /// </summary>
    [Test]
    public void It_should_derive_trigger_sources_independent_of_duplicate_binding_order()
    {
        var reversed = BuildTriggers(reverseDuplicateReferenceBindings: true);

        AbstractMaintenanceMappings(reversed).Should().Equal(AbstractMaintenanceMappings(_triggers));

        AbstractMaintenanceMappings(reversed)
            .Select(m => m.Source)
            .Should()
            .NotContain("School_LocalEducationAgencyId");
    }
}

/// <summary>
/// Test fixture proving the AbstractIdentityMaintenance trigger bridges a concrete relational.nameOverrides
/// column into the override-free abstract identity column. Campus overrides its reference identity column to
/// SchoolBase_CampusId; the trigger maintaining the SchoolCarrierIdentity table must map that overridden
/// concrete source column to the override-free abstract target column SchoolBase_SchoolId.
/// </summary>
[TestFixture]
public class Given_AbstractIdentityMaintenance_Trigger_With_Overridden_Member_Reference_Column
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = SchoolCarrierOverrideTestSchema.BuildProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
                new RootIdentityConstraintPass(),
                new TransitiveIdentityMutabilityPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
                new ApplyConstraintDialectHashingPass(),
                new DeriveIndexInventoryPass(),
                new DeriveTriggerInventoryPass(),
            }
        );
        _triggers = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules()).TriggersInCreateOrder;
    }

    /// <summary>
    /// The Campus AbstractIdentityMaintenance trigger maps the overridden concrete source column
    /// (SchoolBase_CampusId) to the override-free abstract target column (SchoolBase_SchoolId).
    /// </summary>
    [Test]
    public void It_should_map_overridden_concrete_source_to_override_free_abstract_target()
    {
        var trigger = _triggers.Single(t =>
            t.Table.Name == "Campus" && t.Parameters is TriggerKindParameters.AbstractIdentityMaintenance
        );
        var parameters = (TriggerKindParameters.AbstractIdentityMaintenance)trigger.Parameters;

        parameters
            .TargetColumnMappings.Select(m => (m.SourceColumn.Value, m.TargetColumn.Value))
            .Should()
            .Contain(("SchoolBase_CampusId", "SchoolBase_SchoolId"));
    }
}
