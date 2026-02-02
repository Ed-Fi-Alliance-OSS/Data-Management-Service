// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Root_Unique_Constraint_Derivation
{
    private DbTableModel _rootTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ExtensionTableDerivationRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _rootTable = enrollmentModel.Root;
    }

    [Test]
    public void It_should_create_root_unique_using_reference_document_ids()
    {
        var uniqueConstraint = _rootTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "Student_DocumentId");
        uniqueConstraint.Name.Should().Be("UX_Enrollment_School_DocumentId_Student_DocumentId");
    }
}

[TestFixture]
public class Given_Descriptor_Unique_Constraint_Derivation
{
    private DbTableModel _descriptorTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildDescriptorProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var descriptorModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "GradeLevelDescriptor"
            )
            .RelationalModel;

        _descriptorTable = descriptorModel.Root;
    }

    [Test]
    public void It_should_add_uri_and_discriminator_columns()
    {
        var uriColumn = _descriptorTable.Columns.Single(column => column.ColumnName.Value == "Uri");
        var discriminatorColumn = _descriptorTable.Columns.Single(column =>
            column.ColumnName.Value == "Discriminator"
        );

        uriColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 306));
        uriColumn.IsNullable.Should().BeFalse();
        uriColumn.SourceJsonPath.Should().BeNull();

        discriminatorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 128));
        discriminatorColumn.IsNullable.Should().BeFalse();
        discriminatorColumn.SourceJsonPath.Should().BeNull();
    }

    [Test]
    public void It_should_define_unique_on_uri_and_discriminator()
    {
        var uniqueConstraint = _descriptorTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint.Columns.Select(column => column.Value).Should().Equal("Uri", "Discriminator");
        uniqueConstraint.Name.Should().Be("UX_Descriptor_Uri_Discriminator");
    }
}

[TestFixture]
public class Given_Unmappable_Identity_Paths
{
    [Test]
    public void It_should_fail_fast_when_identity_path_cannot_be_mapped()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildArrayIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action.Should().Throw<InvalidOperationException>().WithMessage("*addresses[*].streetNumberName*");
    }
}

[TestFixture]
public class Given_Incomplete_Reference_Identity_Mapping
{
    [Test]
    public void It_should_fail_fast_when_reference_mapping_is_missing_target_identity_paths()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintMissingIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Reference mapping 'School'*Ed-Fi:Enrollment*'$.educationOrganizationId'*Ed-Fi:School*"
            );
    }
}

[TestFixture]
public class Given_Incomplete_Abstract_Reference_Identity_Mapping
{
    [Test]
    public void It_should_fail_fast_when_abstract_reference_mapping_is_missing_identity_paths()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceMissingIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new AbstractIdentityTableDerivationRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Reference mapping 'EducationOrganization'*Ed-Fi:Enrollment*'$.organizationCode'*Ed-Fi:EducationOrganization*"
            );
    }
}

[TestFixture]
public class Given_Duplicate_Reference_Path_Bindings
{
    private DbTableModel _enrollmentTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildDuplicateReferencePathProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    [Test]
    public void It_should_include_each_identity_column_when_reference_path_is_shared()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");

        schoolFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "School_EducationOrganizationId", "School_SchoolId");
    }
}

[TestFixture]
public class Given_Reference_Constraint_Derivation
{
    private DbTableModel _enrollmentTable = default!;
    private DbTableModel _schoolTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        var schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "School")
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
        _schoolTable = schoolModel.Root;
    }

    [Test]
    public void It_should_order_reference_fk_columns_by_target_identity_order()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");

        schoolFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "School_EducationOrganizationId", "School_SchoolId");

        schoolFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }

    [Test]
    public void It_should_apply_allow_identity_updates_gating()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");
        var studentFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "Student_DocumentId");

        schoolFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        studentFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    [Test]
    public void It_should_add_target_side_unique_for_reference_fks()
    {
        var uniqueConstraint = _schoolTable
            .Constraints.OfType<TableConstraint.Unique>()
            .Single(constraint => constraint.Name == "UX_School_DocumentId_EducationOrganizationId_SchoolId");

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }
}

[TestFixture]
public class Given_Abstract_Reference_Constraint_Derivation
{
    private DbTableModel _enrollmentTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new AbstractIdentityTableDerivationRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    [Test]
    public void It_should_cascade_updates_for_abstract_targets()
    {
        var educationOrganizationFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "EducationOrganization_DocumentId");

        educationOrganizationFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        educationOrganizationFk.TargetTable.Name.Should().Be("EducationOrganizationIdentity");
    }
}

[TestFixture]
public class Given_Array_Uniqueness_Constraint_Derivation
{
    private DbTableModel _addressTable = default!;
    private DbTableModel _periodTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildArrayUniquenessProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "BusRoute"
            )
            .RelationalModel;

        _addressTable = busRouteModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddress"
        );
        _periodTable = busRouteModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddressPeriod"
        );
    }

    [Test]
    public void It_should_map_reference_identity_paths_to_document_id()
    {
        var uniqueConstraint = _addressTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "School_DocumentId");
        uniqueConstraint.Columns.Should().NotContain(column => column.Value == "Ordinal");
    }

    [Test]
    public void It_should_include_parent_key_parts_for_nested_arrays()
    {
        var uniqueConstraint = _periodTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "AddressOrdinal", "BeginDate");
        uniqueConstraint.Columns.Should().NotContain(column => column.Value == "Ordinal");
    }
}

[TestFixture]
public class Given_Nested_Array_Uniqueness_Constraint_Derivation
{
    private DbTableModel _periodTable = default!;
    private DbTableModel _sessionTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildNestedArrayUniquenessProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "BusRoute"
            )
            .RelationalModel;

        _periodTable = busRouteModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddressPeriod"
        );
        _sessionTable = busRouteModel.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddressPeriodSession"
        );
    }

    [Test]
    public void It_should_include_parent_key_parts_for_nested_constraints()
    {
        var uniqueConstraint = _periodTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "AddressOrdinal", "BeginDate");
    }

    [Test]
    public void It_should_include_parent_key_parts_for_deeper_nested_constraints()
    {
        var uniqueConstraint = _sessionTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "AddressOrdinal", "PeriodOrdinal", "SessionName");
    }
}

[TestFixture]
public class Given_Unmappable_Array_Uniqueness_Path
{
    [Test]
    public void It_should_fail_fast_when_path_does_not_map_to_column()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildArrayUniquenessUnmappableProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action.Should().Throw<InvalidOperationException>().WithMessage("*schoolReference.link*");
    }
}

[TestFixture]
public class Given_Extension_Array_Uniqueness_Constraint_Alignment
{
    private DbTableModel _periodTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessCoreProjectSchema();
        var extensionProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var contactModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "Contact"
            )
            .RelationalModel;

        _periodTable = contactModel.TablesInReadDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.addresses[*].periods[*]"
        );
    }

    [Test]
    public void It_should_align_extension_scoped_constraints_to_base_tables()
    {
        var uniqueConstraint = _periodTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("Contact_DocumentId", "AddressOrdinal", "BeginDate");
    }
}

[TestFixture]
public class Given_Extension_Array_Uniqueness_Constraint_With_Missing_Base_Table
{
    private Action _action = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessCoreProjectSchema();
        var extensionProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessMissingExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        _action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    [Test]
    public void It_should_fail_fast_when_extension_alignment_has_no_target_table()
    {
        var exception = _action.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("$._ext.sample.missing[*]");
        exception.Message.Should().Contain("Contact");
    }
}

internal static class ConstraintDerivationTestSchemaBuilder
{
    internal static JsonObject BuildReferenceConstraintProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildReferenceConstraintEnrollmentSchema(),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    internal static JsonObject BuildDuplicateReferencePathProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildDuplicateReferencePathEnrollmentSchema(),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildReferenceConstraintMissingIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildIncompleteReferenceConstraintEnrollmentSchema(),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildAbstractReferenceProjectSchema()
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
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildAbstractReferenceEnrollmentSchema(),
                ["schools"] = BuildAbstractReferenceSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildAbstractReferenceMissingIdentityProjectSchema()
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
                    ["identityJsonPaths"] = new JsonArray
                    {
                        "$.educationOrganizationId",
                        "$.organizationCode",
                    },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildAbstractReferenceMissingIdentityEnrollmentSchema(),
                ["schools"] = BuildAbstractReferenceSchoolWithOrganizationCodeSchema(),
            },
        };
    }

    internal static JsonObject BuildReferenceIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    internal static JsonObject BuildDescriptorProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["gradeLevelDescriptors"] = BuildDescriptorSchema() },
        };
    }

    internal static JsonObject BuildArrayIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildArrayIdentitySchema() },
        };
    }

    internal static JsonObject BuildArrayUniquenessProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = BuildBusRouteArrayUniquenessSchema(BuildBusRouteArrayUniquenessConstraints()),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildNestedArrayUniquenessProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = BuildBusRouteNestedArrayUniquenessSchema(
                    BuildBusRouteNestedArrayUniquenessConstraints()
                ),
            },
        };
    }

    internal static JsonObject BuildArrayUniquenessUnmappableProjectSchema()
    {
        JsonArray arrayUniquenessConstraints =
        [
            new JsonObject { ["paths"] = new JsonArray { "$.addresses[*].schoolReference.link" } },
        ];

        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = BuildBusRouteArrayUniquenessSchema(arrayUniquenessConstraints),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildExtensionArrayUniquenessCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildContactSchema() },
        };
    }

    internal static JsonObject BuildExtensionArrayUniquenessExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["contacts"] = BuildContactExtensionSchema(
                    BuildContactExtensionAddressesSchema(),
                    BuildContactExtensionArrayUniquenessConstraints()
                ),
            },
        };
    }

    internal static JsonObject BuildExtensionArrayUniquenessMissingExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["contacts"] = BuildContactExtensionSchema(
                    BuildContactExtensionMissingSchema(),
                    BuildContactExtensionMissingArrayUniquenessConstraints()
                ),
            },
        };
    }

    private static JsonObject BuildIncompleteReferenceConstraintEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
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
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildDuplicateReferencePathEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
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
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildReferenceConstraintEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
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
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonArray BuildBusRouteArrayUniquenessConstraints()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray
                {
                    "$.addresses[*].schoolReference.schoolId",
                    "$.addresses[*].schoolReference.educationOrganizationId",
                },
                ["nestedConstraints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["basePath"] = "$.addresses[*]",
                        ["paths"] = new JsonArray { "$.periods[*].beginDate" },
                    },
                },
            },
        };
    }

    private static JsonArray BuildBusRouteNestedArrayUniquenessConstraints()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray { "$.addresses[*].addressType" },
                ["nestedConstraints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["basePath"] = "$.addresses[*]",
                        ["paths"] = new JsonArray { "$.periods[*].beginDate" },
                        ["nestedConstraints"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["basePath"] = "$.addresses[*].periods[*]",
                                ["paths"] = new JsonArray { "$.sessions[*].sessionName" },
                            },
                        },
                    },
                },
            },
        };
    }

    private static JsonObject BuildBusRouteArrayUniquenessSchema(JsonArray arrayUniquenessConstraints)
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            ["schoolReference"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["schoolId"] = new JsonObject { ["type"] = "integer" },
                                    ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                                    ["link"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                                },
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
        };

        return new JsonObject
        {
            ["resourceName"] = "BusRoute",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
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
                            ["referenceJsonPath"] = "$.addresses[*].schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.addresses[*].schoolReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildBusRouteNestedArrayUniquenessSchema(JsonArray arrayUniquenessConstraints)
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["addressType"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
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
                                        ["sessions"] = new JsonObject
                                        {
                                            ["type"] = "array",
                                            ["items"] = new JsonObject
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new JsonObject
                                                {
                                                    ["sessionName"] = new JsonObject
                                                    {
                                                        ["type"] = "string",
                                                        ["maxLength"] = 60,
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "BusRoute",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildContactSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
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
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildContactExtensionSchema(
        JsonObject extensionProjectSchema,
        JsonArray arrayUniquenessConstraints
    )
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["sample"] = extensionProjectSchema },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildContactExtensionAddressesSchema()
    {
        var addressItems = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sponsorCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            },
                        },
                    },
                },
                ["periods"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["beginDate"] = new JsonObject { ["type"] = "string", ["format"] = "date" },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject { ["type"] = "array", ["items"] = addressItems },
            },
        };
    }

    private static JsonObject BuildContactExtensionMissingSchema()
    {
        var missingItems = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["marker"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            },
                        },
                    },
                },
                ["foo"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["missing"] = new JsonObject { ["type"] = "array", ["items"] = missingItems },
            },
        };
    }

    private static JsonArray BuildContactExtensionArrayUniquenessConstraints()
    {
        return
        [
            new JsonObject
            {
                ["basePath"] = "$._ext.sample.addresses[*]",
                ["paths"] = new JsonArray { "$.periods[*].beginDate" },
            },
        ];
    }

    private static JsonArray BuildContactExtensionMissingArrayUniquenessConstraints()
    {
        return
        [
            new JsonObject
            {
                ["basePath"] = "$._ext.sample.missing[*]",
                ["paths"] = new JsonArray { "$.foo" },
            },
        ];
    }

    private static JsonObject BuildReferenceConstraintSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer" },
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray("schoolId", "educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildAbstractReferenceEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "EducationOrganization",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildAbstractReferenceMissingIdentityEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                        ["organizationCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "EducationOrganization",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildAbstractReferenceSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
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

    private static JsonObject BuildAbstractReferenceSchoolWithOrganizationCodeSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                ["organizationCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
            ["required"] = new JsonArray("educationOrganizationId", "organizationCode"),
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
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.organizationCode" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
                ["OrganizationCode"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.organizationCode",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.schoolReference.schoolId",
                "$.schoolReference.educationOrganizationId",
                "$.studentReference.studentUniqueId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
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
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer" },
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray("schoolId", "educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId", "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildStudentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
            },
            ["required"] = new JsonArray("studentUniqueId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
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

    private static JsonObject BuildDescriptorSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "GradeLevelDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildArrayIdentitySchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                        },
                        ["required"] = new JsonArray("streetNumberName"),
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.addresses[*].streetNumberName" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StreetNumberName"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.addresses[*].streetNumberName",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
