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
        var fallbackTriggers = _triggers.Where(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
        );

        fallbackTriggers.Should().BeEmpty();
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
    /// It should emit propagation trigger on referenced resource.
    /// </summary>
    [Test]
    public void It_should_emit_propagation_trigger_on_referenced_resource()
    {
        var schoolPropagation = _triggers.SingleOrDefault(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_School_Propagation"
        );

        schoolPropagation.Should().NotBeNull();
        schoolPropagation!.Table.Name.Should().Be("School");
    }

    /// <summary>
    /// It should not emit propagation trigger for non-updatable target.
    /// </summary>
    [Test]
    public void It_should_not_emit_propagation_trigger_for_non_updatable_target()
    {
        var studentPropagation = _triggers.SingleOrDefault(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_Student_Propagation"
        );

        studentPropagation.Should().BeNull();
    }

    /// <summary>
    /// It should use DocumentId as key column on referenced table.
    /// </summary>
    [Test]
    public void It_should_use_DocumentId_as_key_column()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_School_Propagation"
        );

        schoolPropagation.KeyColumns.Should().ContainSingle();
        schoolPropagation.KeyColumns[0].Value.Should().Be("DocumentId");
    }

    /// <summary>
    /// It should include identity projection columns from referenced table.
    /// </summary>
    [Test]
    public void It_should_include_identity_projection_columns()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_School_Propagation"
        );

        schoolPropagation
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Contain("EducationOrganizationId")
            .And.Contain("SchoolId");
    }

    /// <summary>
    /// It should include referrer updates for Enrollment.
    /// </summary>
    [Test]
    public void It_should_include_referrer_updates()
    {
        var schoolPropagation = _triggers.Single(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_School_Propagation"
        );

        var propagationParams =
            schoolPropagation.Parameters as TriggerKindParameters.IdentityPropagationFallback;
        propagationParams.Should().NotBeNull();
        propagationParams!.ReferrerUpdates.Should().ContainSingle();

        var referrerUpdate = propagationParams.ReferrerUpdates[0];
        referrerUpdate.ReferrerTable.Name.Should().Be("Enrollment");
        referrerUpdate.ReferrerFkColumn.Value.Should().Be("School_DocumentId");
        referrerUpdate
            .ColumnMappings.Select(m => m.TargetColumn.Value)
            .Should()
            .Contain("School_EducationOrganizationId")
            .And.Contain("School_SchoolId");
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
    /// It should emit propagation trigger on abstract identity table.
    /// </summary>
    [Test]
    public void It_should_emit_propagation_trigger_on_abstract_identity_table()
    {
        var propagation = _triggers.SingleOrDefault(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_EducationOrganizationIdentity_Propagation"
        );

        propagation.Should().NotBeNull();
        propagation!.Table.Name.Should().Be("EducationOrganizationIdentity");
    }

    /// <summary>
    /// It should include referrer updates for Enrollment.
    /// </summary>
    [Test]
    public void It_should_include_referrer_updates_for_enrollment()
    {
        var propagation = _triggers.Single(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_EducationOrganizationIdentity_Propagation"
        );

        var propagationParams = propagation.Parameters as TriggerKindParameters.IdentityPropagationFallback;
        propagationParams.Should().NotBeNull();
        propagationParams!.ReferrerUpdates.Should().ContainSingle();

        var referrerUpdate = propagationParams.ReferrerUpdates[0];
        referrerUpdate.ReferrerTable.Name.Should().Be("Enrollment");
        referrerUpdate.ReferrerFkColumn.Value.Should().Be("EducationOrganization_DocumentId");
    }

    /// <summary>
    /// It should use DocumentId as key column for abstract identity table.
    /// </summary>
    [Test]
    public void It_should_use_DocumentId_as_key_column_for_abstract_identity_table()
    {
        var propagation = _triggers.Single(t =>
            t.Parameters is TriggerKindParameters.IdentityPropagationFallback
            && t.Name.Value == "TR_EducationOrganizationIdentity_Propagation"
        );

        propagation.KeyColumns.Should().ContainSingle();
        propagation.KeyColumns[0].Value.Should().Be("DocumentId");
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
        var stampTriggers = _triggers.Where(t => t.Parameters is TriggerKindParameters.DocumentStamping);

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
        var stampTriggers = _triggers.Where(t => t.Parameters is TriggerKindParameters.DocumentStamping);

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
                TriggerKindParameters.IdentityPropagationFallback => "IdentityPropagationFallback",
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
