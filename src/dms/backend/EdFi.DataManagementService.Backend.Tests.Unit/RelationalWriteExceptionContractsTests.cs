// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteExceptionContracts
{
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName StudentResource = new("Ed-Fi", "Student");

    [Test]
    public void It_requires_non_blank_constraint_names_for_unique_constraint_violations()
    {
        var act = () => _ = new RelationalWriteExceptionClassification.UniqueConstraintViolation(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_requires_non_blank_constraint_names_for_foreign_key_constraint_violations()
    {
        var act = () =>
            _ = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_exposes_a_singleton_fallback_for_unrecognized_write_failures()
    {
        RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure.Instance.Should()
            .BeSameAs(RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance);
    }

    [Test]
    public void It_exposes_constructor_validated_contract_properties_as_read_only()
    {
        typeof(RelationalWriteExceptionClassification.UniqueConstraintViolation)
            .GetProperty(nameof(RelationalWriteExceptionClassification.ConstraintViolation.ConstraintName))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolutionRequest)
            .GetProperty(nameof(RelationalWriteConstraintResolutionRequest.WritePlan))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolutionRequest)
            .GetProperty(nameof(RelationalWriteConstraintResolutionRequest.ReferenceResolutionRequest))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolutionRequest)
            .GetProperty(nameof(RelationalWriteConstraintResolutionRequest.Violation))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolution.RequestReference)
            .GetProperty(nameof(RelationalWriteConstraintResolution.ConstraintMatch.ConstraintName))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolution.RequestReference)
            .GetProperty(nameof(RelationalWriteConstraintResolution.RequestReference.ReferenceKind))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolution.RequestReference)
            .GetProperty(nameof(RelationalWriteConstraintResolution.RequestReference.ReferencePath))!
            .CanWrite.Should()
            .BeFalse();
        typeof(RelationalWriteConstraintResolution.RequestReference)
            .GetProperty(nameof(RelationalWriteConstraintResolution.RequestReference.TargetResource))!
            .CanWrite.Should()
            .BeFalse();
    }

    [Test]
    public void It_requires_constraint_resolution_requests_to_match_the_write_plan_resource()
    {
        var mappingSet = CreateMappingSet(StudentResource);
        var writePlan = CreateWritePlan(SchoolResource);
        var violation = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
            "FK_edfi_School_SchoolTypeDescriptor"
        );

        var act = () =>
            _ = new RelationalWriteConstraintResolutionRequest(
                writePlan,
                new ReferenceResolverRequest(mappingSet, StudentResource, [], []),
                violation
            );

        act.Should().Throw<ArgumentException>().WithMessage("*referenceResolutionRequest*Ed-Fi.School*");
    }

    [Test]
    public void It_carries_request_reference_resolution_metadata_without_binding_to_handler_behavior()
    {
        var resolution = new RelationalWriteConstraintResolution.RequestReference(
            "FK_edfi_Student_SectionReference",
            RelationalWriteReferenceKind.Document,
            new JsonPathExpression("$.sectionReference", [new JsonPathSegment.Property("sectionReference")]),
            new QualifiedResourceName("Ed-Fi", "Section")
        );

        resolution.ConstraintName.Should().Be("FK_edfi_Student_SectionReference");
        resolution.ReferenceKind.Should().Be(RelationalWriteReferenceKind.Document);
        resolution.ReferencePath.Canonical.Should().Be("$.sectionReference");
        resolution.TargetResource.Should().Be(new QualifiedResourceName("Ed-Fi", "Section"));
    }

    private static ResourceWritePlan CreateWritePlan(QualifiedResourceName resource)
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), resource.ResourceName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{resource.ResourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var tableWritePlan = new TableWritePlan(
            tableModel,
            InsertSql: $"insert into edfi.\"{resource.ResourceName}\" values (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 1, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                resource,
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                tableModel,
                [tableModel],
                [],
                []
            ),
            [tableWritePlan]
        );
    }

    private static MappingSet CreateMappingSet(QualifiedResourceName resource)
    {
        var resourceKeyEntry = new ResourceKeyEntry(1, resource, "5.3.0", false);

        return new MappingSet(
            new MappingSetKey("test-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    "5.3",
                    "v1",
                    "test-hash",
                    1,
                    [1],
                    [new SchemaComponentInfo("ed-fi", resource.ProjectName, "5.3.0", false, "component")],
                    [resourceKeyEntry]
                ),
                SqlDialect.Pgsql,
                [
                    new ProjectSchemaInfo(
                        "ed-fi",
                        resource.ProjectName,
                        "5.3.0",
                        false,
                        new DbSchemaName("edfi")
                    ),
                ],
                [
                    new ConcreteResourceModel(
                        resourceKeyEntry,
                        ResourceStorageKind.RelationalTables,
                        CreateWritePlan(resource).Model
                    ),
                ],
                [],
                [],
                [],
                []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short> { [resource] = 1 },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry> { [1] = resourceKeyEntry },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }
}
