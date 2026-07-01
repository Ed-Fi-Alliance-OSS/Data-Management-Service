// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Shared School / EducationOrganization abstract-identity model used by both the constraint-resolver and
/// the failure-mapper abstract-identity tests, so the two suites exercise identical table shapes and cannot
/// silently drift apart.
/// </summary>
internal static class AbstractIdentitySchoolTestData
{
    internal const string NaturalKeyConstraintName = "UX_EducationOrganizationIdentity_NK";
    internal const string ReferenceKeyConstraintName = "UX_EducationOrganizationIdentity_RefKey";
    internal const string GeneralStudentProgramAssociationNaturalKeyConstraintName =
        "UX_GeneralStudentProgramAssociationIdentity_NK";

    internal static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    internal static readonly QualifiedResourceName LocalEducationAgencyResource = new(
        "Ed-Fi",
        "LocalEducationAgency"
    );
    internal static readonly QualifiedResourceName EducationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    internal static readonly QualifiedResourceName GeneralStudentProgramAssociationResource = new(
        "Ed-Fi",
        "GeneralStudentProgramAssociation"
    );
    internal static readonly QualifiedResourceName StudentProgramAssociationResource = new(
        "Ed-Fi",
        "StudentProgramAssociation"
    );

    /// <summary>
    /// Builds the concrete School root table with its <c>SchoolId</c> identity column.
    /// </summary>
    internal static DbTableModel SchoolRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
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
                new DbColumnModel(
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        );

    /// <summary>
    /// Builds a concrete LocalEducationAgency root table with its <c>LocalEducationAgencyId</c> identity column.
    /// Used to prove abstract-identity conflicts report the writing subclass's own identity values rather than a
    /// hard-coded School identity.
    /// </summary>
    internal static DbTableModel LocalEducationAgencyRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_LocalEducationAgency",
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
                new DbColumnModel(
                    new DbColumnName("LocalEducationAgencyId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.localEducationAgencyId",
                        [new JsonPathSegment.Property("localEducationAgencyId")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        );

    /// <summary>
    /// Models edfi."EducationOrganizationIdentity" the way AbstractIdentityTableAndUnionViewDerivationPass
    /// builds it: DocumentId primary key, the _NK natural-key unique over the projected identity column, the
    /// _RefKey helper that also includes DocumentId, and the document FK.
    /// </summary>
    internal static DbTableModel EducationOrganizationIdentityTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("EducationOrganizationId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.educationOrganizationId",
                        [new JsonPathSegment.Property("educationOrganizationId")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 256),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_NK",
                    [new DbColumnName("EducationOrganizationId")]
                ),
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_RefKey",
                    [new DbColumnName("EducationOrganizationId"), new DbColumnName("DocumentId")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_EducationOrganizationIdentity_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")],
                    OnDelete: ReferentialAction.Cascade
                ),
            ]
        );

    /// <summary>
    /// Builds a concrete StudentProgramAssociation root table whose identity component is reference-backed
    /// (<c>Program_EducationOrganizationId</c>, sourced from the nested <c>$.programReference</c> path).
    /// </summary>
    internal static DbTableModel StudentProgramAssociationRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentProgramAssociation"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentProgramAssociation",
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
                new DbColumnModel(
                    new DbColumnName("Program_EducationOrganizationId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.programReference.educationOrganizationId",
                        [
                            new JsonPathSegment.Property("programReference"),
                            new JsonPathSegment.Property("educationOrganizationId"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        );

    /// <summary>
    /// Models edfi."GeneralStudentProgramAssociationIdentity" the way
    /// AbstractIdentityTableAndUnionViewDerivationPass builds a reference-backed abstract identity table:
    /// DocumentId primary key, the _NK natural-key unique over the reference-backed identity column, the
    /// _RefKey helper that also includes DocumentId, and the document FK.
    /// </summary>
    internal static DbTableModel GeneralStudentProgramAssociationIdentityTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "GeneralStudentProgramAssociationIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_GeneralStudentProgramAssociationIdentity",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Program_EducationOrganizationId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression(
                        "$.programReference.educationOrganizationId",
                        [
                            new JsonPathSegment.Property("programReference"),
                            new JsonPathSegment.Property("educationOrganizationId"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 256),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    GeneralStudentProgramAssociationNaturalKeyConstraintName,
                    [new DbColumnName("Program_EducationOrganizationId")]
                ),
                new TableConstraint.Unique(
                    "UX_GeneralStudentProgramAssociationIdentity_RefKey",
                    [new DbColumnName("Program_EducationOrganizationId"), new DbColumnName("DocumentId")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_GeneralStudentProgramAssociationIdentity_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")],
                    OnDelete: ReferentialAction.Cascade
                ),
            ]
        );

    /// <summary>
    /// Builds the write plan and mapping set shared by the abstract-identity tests, using the single-column
    /// EducationOrganization identity table.
    /// </summary>
    internal static (ResourceWritePlan WritePlan, MappingSet MappingSet) BuildSchoolWriteModel() =>
        BuildSchoolWriteModel(EducationOrganizationIdentityTable());

    /// <summary>
    /// Builds the School write plan and mapping set shared by the abstract-identity tests. The School
    /// referential-identity trigger is always present so the failure mapper can project the concrete identity
    /// values; the constraint resolver ignores triggers.
    /// </summary>
    /// <param name="abstractIdentityTable">
    /// The abstract identity table placed in the mapping set's abstract-identity inventory. Tests pass a
    /// composite-key variant to cover multi-column natural keys.
    /// </param>
    internal static (ResourceWritePlan WritePlan, MappingSet MappingSet) BuildSchoolWriteModel(
        DbTableModel abstractIdentityTable
    ) =>
        BuildSubclassWriteModel(
            SchoolResource,
            SchoolRootTable(),
            new DbColumnName("SchoolId"),
            "$.schoolId",
            abstractIdentityTable,
            EducationOrganizationResource
        );

    /// <summary>
    /// Builds a LocalEducationAgency write plan and mapping set that projects into the same single-column
    /// EducationOrganization identity table as School. Used to prove abstract-identity conflicts report the
    /// writing subclass's own identity element (<c>localEducationAgencyId</c>) rather than School's.
    /// </summary>
    internal static (
        ResourceWritePlan WritePlan,
        MappingSet MappingSet
    ) BuildLocalEducationAgencyWriteModel() =>
        BuildSubclassWriteModel(
            LocalEducationAgencyResource,
            LocalEducationAgencyRootTable(),
            new DbColumnName("LocalEducationAgencyId"),
            "$.localEducationAgencyId",
            EducationOrganizationIdentityTable(),
            EducationOrganizationResource
        );

    /// <summary>
    /// Builds a concrete StudentProgramAssociation write model that projects into the
    /// GeneralStudentProgramAssociation abstract identity table, whose identity element is reference-backed and
    /// therefore addressed by a multi-segment JSONPath (<c>$.programReference.educationOrganizationId</c>).
    /// Used to prove the failure mapper resolves abstract-identity conflict values by walking a nested request
    /// body, not just a top-level property.
    /// </summary>
    internal static (
        ResourceWritePlan WritePlan,
        MappingSet MappingSet
    ) BuildReferenceBackedSubclassWriteModel() =>
        BuildSubclassWriteModel(
            StudentProgramAssociationResource,
            StudentProgramAssociationRootTable(),
            new DbColumnName("Program_EducationOrganizationId"),
            "$.programReference.educationOrganizationId",
            GeneralStudentProgramAssociationIdentityTable(),
            GeneralStudentProgramAssociationResource
        );

    /// <summary>
    /// Builds the write plan and mapping set for a concrete subclass that projects into the shared abstract
    /// identity table, parameterized by the subclass's identity column, JSONPath, and abstract superclass.
    /// </summary>
    private static (ResourceWritePlan WritePlan, MappingSet MappingSet) BuildSubclassWriteModel(
        QualifiedResourceName concreteResource,
        DbTableModel concreteRootTable,
        DbColumnName identityColumnName,
        string identityJsonPath,
        DbTableModel abstractIdentityTable,
        QualifiedResourceName abstractResource
    )
    {
        var resourceModel = new RelationalResourceModel(
            concreteResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            concreteRootTable,
            [concreteRootTable],
            [],
            []
        );

        var writePlan = new ResourceWritePlan(resourceModel, []);

        var concreteKey = new ResourceKeyEntry(1, concreteResource, "1.0.0", false);
        var abstractResourceKey = new ResourceKeyEntry(2, abstractResource, "1.0.0", true);

        // Subclass identifiers carry an Int32 scalar type in these fixtures; the failure mapper reads only the
        // identity JSONPath from this element (walking it against the request body), not its scalar type, so a
        // reference-backed nested path resolves the same way.
        var referentialIdentityTrigger = new DbTriggerInfo(
            new DbTriggerName($"TR_{concreteResource.ResourceName}_ReferentialIdentity"),
            concreteRootTable.Table,
            [new DbColumnName("DocumentId")],
            [identityColumnName],
            new TriggerKindParameters.ReferentialIdentityMaintenance(
                concreteKey.ResourceKeyId,
                concreteResource.ProjectName,
                concreteResource.ResourceName,
                [
                    new IdentityElementMapping(
                        identityColumnName,
                        identityJsonPath,
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                ]
            )
        );

        var mappingSet = new MappingSet(
            new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    "1.0",
                    "v1",
                    "schema-hash",
                    2,
                    [1, 2],
                    [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                    [concreteKey, abstractResourceKey]
                ),
                SqlDialect.Pgsql,
                [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
                [new ConcreteResourceModel(concreteKey, ResourceStorageKind.RelationalTables, resourceModel)],
                [new AbstractIdentityTableInfo(abstractResourceKey, abstractIdentityTable)],
                [],
                [],
                [referentialIdentityTrigger]
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan> { [concreteResource] = writePlan },
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>
            {
                [concreteResource] = concreteKey.ResourceKeyId,
                [abstractResource] = abstractResourceKey.ResourceKeyId,
            },
            new Dictionary<short, ResourceKeyEntry>
            {
                [concreteKey.ResourceKeyId] = concreteKey,
                [abstractResourceKey.ResourceKeyId] = abstractResourceKey,
            },
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        return (writePlan, mappingSet);
    }
}
