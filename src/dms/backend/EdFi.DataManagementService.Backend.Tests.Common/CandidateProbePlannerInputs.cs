// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Builds the inputs the real regular-resource and descriptor page keyset planners need, so the
/// candidate uniqueness probes can execute planner-produced candidate SQL rather than a hand-built
/// query specification.
/// </summary>
/// <remarks>
/// A hand-built <c>PageDocumentIdQuerySpec</c> proves the compiler; it cannot prove that a consumer
/// reaches the compiler with the root, discriminator, filters, and authorization it is supposed to
/// supply. These builders keep the probe on the production planner entry points instead.
/// </remarks>
internal static class CandidateProbePlannerInputs
{
    /// <summary>The descriptor resource key id the probe rows are discriminated by.</summary>
    public const short DescriptorResourceKeyId = 7;

    /// <summary>
    /// A second descriptor resource key id seeded into the probe table but never requested, so the
    /// mandatory <c>ResourceKeyId</c> discriminator has something to actually exclude.
    /// </summary>
    public const short UnrelatedDescriptorResourceKeyId = 8;

    /// <summary>The probe descriptor resource requested through the descriptor planner.</summary>
    public static readonly QualifiedResourceName DescriptorResource = new(
        "Ed-Fi",
        "CandidateProbeDescriptor"
    );

    /// <summary>
    /// Models the probe root table for the regular-resource planner: a <c>DocumentId</c> key, the
    /// mirrored <c>ContentVersion</c> the change-version window filters, and the two columns the
    /// authorization shapes read.
    /// </summary>
    public static DbTableModel CreateRootTableModel(DbTableName rootTable)
    {
        return new DbTableModel(
            rootTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CandidateProbeRoot",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn(
                    "DocumentId",
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn(
                    "ContentVersion",
                    ColumnKind.MirroredContentVersion,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn("SchoolId", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Int64)),
                CreateColumn(
                    "Namespace",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 255)
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
    }

    /// <summary>
    /// A representative regular-resource filter on the root <c>SchoolId</c> column.
    /// </summary>
    public static RelationalQueryPreprocessingResult CreateRootSchoolIdFilter(long schoolId)
    {
        var rawValue = schoolId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new RelationalQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            [
                new PreprocessedRelationalQueryElement(
                    new QueryElement("schoolId", [new JsonPath("$.schoolId")], rawValue, "number"),
                    new SupportedRelationalQueryField(
                        "schoolId",
                        new RelationalQueryFieldPath(new JsonPathExpression("$.schoolId", []), "number"),
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId"))
                    ),
                    new PreprocessedRelationalQueryValue.Raw(rawValue)
                ),
            ]
        );
    }

    /// <summary>
    /// A representative descriptor filter on the shared <c>CodeValue</c> column. <c>CodeValue</c> rather
    /// than <c>Namespace</c> keeps the filter independent of the namespace authorization check, so each
    /// exclusion the probe asserts is attributable to exactly one mechanism.
    /// </summary>
    public static DescriptorQueryPreprocessingResult CreateDescriptorCodeValueFilter(string codeValue)
    {
        return new DescriptorQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            [
                new PreprocessedDescriptorQueryElement(
                    new QueryElement("codeValue", [new JsonPath("$.codeValue")], codeValue, "string"),
                    new SupportedDescriptorQueryField(
                        "codeValue",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue"))
                    ),
                    new PreprocessedDescriptorQueryValue.Raw(codeValue)
                ),
            ]
        );
    }

    /// <summary>
    /// The minimum mapping set the descriptor planner reads: it resolves only the requested resource's
    /// <c>ResourceKeyId</c>.
    /// </summary>
    public static MappingSet CreateDescriptorMappingSet(SqlDialect dialect)
    {
        const string EffectiveSchemaHash = "candidate-probe-hash";
        var resourceKey = new ResourceKeyEntry(
            DescriptorResourceKeyId,
            DescriptorResource,
            "1.0",
            IsAbstractResource: false
        );

        var model = new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: EffectiveSchemaHash,
                ResourceKeyCount: 1,
                ResourceKeySeedHash: [1, 2, 3],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                ],
                ResourceKeysInIdOrder: [resourceKey]
            ),
            Dialect: dialect,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
            ],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(EffectiveSchemaHash, dialect, "v1"),
            Model: model,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [DescriptorResource] = DescriptorResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [DescriptorResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static DbColumnModel CreateColumn(
        string columnName,
        ColumnKind columnKind,
        RelationalScalarType scalarType
    )
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            columnKind,
            scalarType,
            IsNullable: columnName != "DocumentId",
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.Stored()
        );
    }
}
