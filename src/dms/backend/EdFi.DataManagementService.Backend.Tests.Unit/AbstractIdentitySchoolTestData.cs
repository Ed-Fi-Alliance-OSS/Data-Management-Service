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

    internal static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    internal static readonly QualifiedResourceName LocalEducationAgencyResource = new(
        "Ed-Fi",
        "LocalEducationAgency"
    );
    internal static readonly QualifiedResourceName EducationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
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
        BuildEducationOrganizationSubclassWriteModel(
            SchoolResource,
            SchoolRootTable(),
            new DbColumnName("SchoolId"),
            "$.schoolId",
            abstractIdentityTable
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
        BuildEducationOrganizationSubclassWriteModel(
            LocalEducationAgencyResource,
            LocalEducationAgencyRootTable(),
            new DbColumnName("LocalEducationAgencyId"),
            "$.localEducationAgencyId",
            EducationOrganizationIdentityTable()
        );

    /// <summary>
    /// Builds the write plan and mapping set for a concrete EducationOrganization subclass that projects into
    /// the shared abstract identity table, parameterized by the subclass's identity column and JSONPath.
    /// </summary>
    private static (
        ResourceWritePlan WritePlan,
        MappingSet MappingSet
    ) BuildEducationOrganizationSubclassWriteModel(
        QualifiedResourceName concreteResource,
        DbTableModel concreteRootTable,
        DbColumnName identityColumnName,
        string identityJsonPath,
        DbTableModel abstractIdentityTable
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
        var educationOrganizationKey = new ResourceKeyEntry(2, EducationOrganizationResource, "1.0.0", true);

        // EducationOrganization subclass identifiers are Int32 in these fixtures; the failure mapper reads only
        // the identity JSONPath from this element, not its scalar type.
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
                    [concreteKey, educationOrganizationKey]
                ),
                SqlDialect.Pgsql,
                [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
                [new ConcreteResourceModel(concreteKey, ResourceStorageKind.RelationalTables, resourceModel)],
                [new AbstractIdentityTableInfo(educationOrganizationKey, abstractIdentityTable)],
                [],
                [],
                [referentialIdentityTrigger]
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan> { [concreteResource] = writePlan },
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>
            {
                [concreteResource] = concreteKey.ResourceKeyId,
                [EducationOrganizationResource] = educationOrganizationKey.ResourceKeyId,
            },
            new Dictionary<short, ResourceKeyEntry>
            {
                [concreteKey.ResourceKeyId] = concreteKey,
                [educationOrganizationKey.ResourceKeyId] = educationOrganizationKey,
            },
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        return (writePlan, mappingSet);
    }
}
