// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_FlatteningResolvedReferenceLookupSet
{
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly BaseResourceInfo _schoolResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("School"),
        false
    );
    private static readonly BaseResourceInfo _schoolTypeDescriptorResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("SchoolTypeDescriptor"),
        true
    );

    private ResourceWritePlan _writePlan = null!;
    private TableWritePlan _sectionPlan = null!;
    private WriteValueSource.DescriptorReference _sectionDescriptorReference = null!;
    private FlatteningResolvedReferenceLookupSet _sut = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = CreateRootPlan();
        _sectionPlan = CreateSectionPlan();
        var sessionPlan = CreateSessionPlan();
        _sectionDescriptorReference = (WriteValueSource.DescriptorReference)
            _sectionPlan.ColumnBindings[4].Source;

        _writePlan = new ResourceWritePlan(
            Model: new RelationalResourceModel(
                Resource: _studentResource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    _sectionPlan.TableModel,
                    sessionPlan.TableModel,
                ],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        false,
                        Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                        rootPlan.TableModel.Table,
                        new DbColumnName("School_DocumentId"),
                        _schoolResource,
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: Path(
                                    "$.schoolId",
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                ReferenceJsonPath: Path(
                                    "$.schoolReference.schoolId",
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                new DbColumnName("SchoolId")
                            ),
                        ]
                    ),
                    new DocumentReferenceBinding(
                        false,
                        Path(
                            "$.sections[*].schoolReference",
                            new JsonPathSegment.Property("sections"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolReference")
                        ),
                        _sectionPlan.TableModel.Table,
                        new DbColumnName("School_DocumentId"),
                        _schoolResource,
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: Path(
                                    "$.schoolId",
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                ReferenceJsonPath: Path(
                                    "$.sections[*].schoolReference.schoolId",
                                    new JsonPathSegment.Property("sections"),
                                    new JsonPathSegment.AnyArrayElement(),
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                new DbColumnName("SchoolId")
                            ),
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: Path(
                                    "$.schoolTypeDescriptor",
                                    new JsonPathSegment.Property("schoolTypeDescriptor")
                                ),
                                ReferenceJsonPath: Path(
                                    "$.sections[*].schoolReference.schoolTypeDescriptor",
                                    new JsonPathSegment.Property("sections"),
                                    new JsonPathSegment.AnyArrayElement(),
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolTypeDescriptor")
                                ),
                                new DbColumnName("SchoolReferenceTypeDescriptor_DocumentId")
                            ),
                        ]
                    ),
                    new DocumentReferenceBinding(
                        false,
                        Path(
                            "$.sections[*].sessions[*].schoolReference",
                            new JsonPathSegment.Property("sections"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("sessions"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolReference")
                        ),
                        sessionPlan.TableModel.Table,
                        new DbColumnName("School_DocumentId"),
                        _schoolResource,
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: Path(
                                    "$.schoolId",
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                ReferenceJsonPath: Path(
                                    "$.sections[*].sessions[*].schoolReference.schoolId",
                                    new JsonPathSegment.Property("sections"),
                                    new JsonPathSegment.AnyArrayElement(),
                                    new JsonPathSegment.Property("sessions"),
                                    new JsonPathSegment.AnyArrayElement(),
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolId")
                                ),
                                new DbColumnName("SchoolId")
                            ),
                        ]
                    ),
                ],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        false,
                        Path(
                            "$.sections[*].schoolTypeDescriptor",
                            new JsonPathSegment.Property("sections"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolTypeDescriptor")
                        ),
                        _sectionPlan.TableModel.Table,
                        new DbColumnName("SchoolTypeDescriptor_DocumentId"),
                        _schoolTypeDescriptorResource
                    ),
                    new DescriptorEdgeSource(
                        false,
                        Path(
                            "$.sections[*].schoolReference.schoolTypeDescriptor",
                            new JsonPathSegment.Property("sections"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolTypeDescriptor")
                        ),
                        _sectionPlan.TableModel.Table,
                        new DbColumnName("SchoolReferenceTypeDescriptor_DocumentId"),
                        _schoolTypeDescriptorResource
                    ),
                ]
            ),
            TablePlansInDependencyOrder: [rootPlan, _sectionPlan, sessionPlan]
        );

        _sut = FlatteningResolvedReferenceLookupSet.Create(_writePlan, CreateResolvedReferenceSet());
    }

    private ReferenceDerivedValueSourceMetadata CreateReferenceDerivedSource(
        int bindingIndex,
        int identityBindingIndex
    )
    {
        var binding = _writePlan.Model.DocumentReferenceBindings[bindingIndex];

        return new ReferenceDerivedValueSourceMetadata(
            BindingIndex: bindingIndex,
            ReferenceObjectPath: binding.ReferenceObjectPath,
            IdentityJsonPath: binding.IdentityBindings[identityBindingIndex].IdentityJsonPath,
            ReferenceJsonPath: binding.IdentityBindings[identityBindingIndex].ReferenceJsonPath
        );
    }

    private DbColumnName CreateReferenceDerivedColumn(int bindingIndex, int identityBindingIndex)
    {
        return _writePlan
            .Model
            .DocumentReferenceBindings[bindingIndex]
            .IdentityBindings[identityBindingIndex]
            .Column;
    }

    [Test]
    public void It_returns_the_root_document_fk_for_the_empty_ordinal_path()
    {
        _sut.GetDocumentId(0, []).Should().Be(101L);
    }

    [Test]
    public void It_returns_nested_document_fks_for_distinct_ordinal_paths()
    {
        _sut.GetDocumentId(1, [0]).Should().Be(201L);
        _sut.GetDocumentId(1, [1]).Should().Be(202L);
        _sut.GetDocumentId(2, [0, 0]).Should().Be(301L);
        _sut.GetDocumentId(2, [0, 1]).Should().Be(302L);
        _sut.GetDocumentId(2, [1, 0]).Should().Be(303L);
    }

    [Test]
    public void It_returns_root_reference_identity_values_from_the_resolved_reference_identity_order()
    {
        _sut.GetReferenceIdentityValue(
                CreateReferenceDerivedSource(0, 0),
                CreateReferenceDerivedColumn(0, 0),
                []
            )
            .Should()
            .Be("255901");
    }

    [Test]
    public void It_returns_nested_reference_identity_values_from_the_resolved_reference_identity_order()
    {
        _sut.GetReferenceIdentityValue(
                CreateReferenceDerivedSource(1, 0),
                CreateReferenceDerivedColumn(1, 0),
                [0]
            )
            .Should()
            .Be("255901");
        _sut.GetReferenceIdentityValue(
                CreateReferenceDerivedSource(1, 0),
                CreateReferenceDerivedColumn(1, 0),
                [1]
            )
            .Should()
            .Be("255902");
        _sut.GetReferenceIdentityValue(
                CreateReferenceDerivedSource(2, 0),
                CreateReferenceDerivedColumn(2, 0),
                [0, 1]
            )
            .Should()
            .Be("255902");
    }

    [Test]
    public void It_returns_descriptor_backed_reference_identity_values_from_resolved_descriptor_lookups()
    {
        _sut.GetReferenceIdentityDescriptorId(
                CreateReferenceDerivedSource(1, 1),
                CreateReferenceDerivedColumn(1, 1),
                [0]
            )
            .Should()
            .Be(501L);
        _sut.GetReferenceIdentityDescriptorId(
                CreateReferenceDerivedSource(1, 1),
                CreateReferenceDerivedColumn(1, 1),
                [1]
            )
            .Should()
            .Be(502L);
    }

    [Test]
    public void It_accepts_descriptor_backed_reference_identity_values_using_target_identity_paths()
    {
        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            _writePlan,
            CreateResolvedReferenceSetWithTargetDescriptorIdentityPaths()
        );

        lookupSet
            .GetReferenceIdentityDescriptorId(
                CreateReferenceDerivedSource(1, 1),
                CreateReferenceDerivedColumn(1, 1),
                [0]
            )
            .Should()
            .Be(501L);
        lookupSet
            .GetReferenceIdentityDescriptorId(
                CreateReferenceDerivedSource(1, 1),
                CreateReferenceDerivedColumn(1, 1),
                [1]
            )
            .Should()
            .Be(502L);
    }

    [Test]
    public void It_resolves_duplicate_scalar_reference_json_paths_by_binding_column()
    {
        var (_, lookupSet, firstScalarColumn, secondScalarColumn, referenceSource) =
            CreateDuplicateReferenceJsonPathScalarFixture();

        lookupSet.GetReferenceIdentityValue(referenceSource, firstScalarColumn, []).Should().Be("255901");
        lookupSet.GetReferenceIdentityValue(referenceSource, secondScalarColumn, []).Should().Be("255902");
    }

    [Test]
    public void It_resolves_duplicate_scalar_and_descriptor_reference_json_paths_by_binding_column()
    {
        var (_, lookupSet, scalarColumn, descriptorColumn, referenceSource) =
            CreateDuplicateReferenceJsonPathDescriptorFixture();

        lookupSet.GetReferenceIdentityValue(referenceSource, scalarColumn, []).Should().Be("255901");
        lookupSet.GetReferenceIdentityDescriptorId(referenceSource, descriptorColumn, []).Should().Be(501L);
    }

    [Test]
    public void It_returns_descriptor_fks_from_resolved_concrete_paths_without_json_re_reads()
    {
        _sut.GetDescriptorId(_sectionPlan, _sectionDescriptorReference, [0]).Should().Be(401L);
        _sut.GetDescriptorId(_sectionPlan, _sectionDescriptorReference, [1]).Should().Be(402L);
    }

    [Test]
    public void It_rejects_identity_count_drift_between_compiled_bindings_and_resolved_occurrences()
    {
        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            _writePlan,
            CreateResolvedReferenceSetWithIdentityCountDrift()
        );

        var act = () =>
            lookupSet.GetReferenceIdentityDescriptorId(
                CreateReferenceDerivedSource(1, 1),
                CreateReferenceDerivedColumn(1, 1),
                [0]
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*'$.sections[0].schoolReference'*carried 1 identity value(s), but compiled metadata expects 2*"
            );
    }

    [Test]
    public void It_resolves_descriptor_identity_elements_even_when_the_resolved_payload_order_drifts()
    {
        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            _writePlan,
            CreateResolvedReferenceSetWithIdentityKindDrift()
        );

        lookupSet
            .GetReferenceIdentityDescriptorId(
                CreateReferenceDerivedSource(1, 1),
                CreateReferenceDerivedColumn(1, 1),
                [0]
            )
            .Should()
            .Be(501L);
    }

    [Test]
    public void It_resolves_duplicate_scalar_reference_json_paths_by_identity_path_when_resolved_order_drifts()
    {
        // Positional matching was the original bridge between Core extraction and backend materialization.
        // Same-kind identity members make that unsafe, so runtime lookup must select by IdentityJsonPath.
        var (writePlan, _, firstScalarColumn, secondScalarColumn, referenceSource) =
            CreateDuplicateReferenceJsonPathScalarFixture();
        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
                {
                    [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                        _schoolResourceInfo,
                        "$.schoolReference",
                        101L,
                        ("$.localEducationAgencyId", "255902"),
                        ("$.schoolId", "255901")
                    ),
                },
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            )
        );

        lookupSet.GetReferenceIdentityValue(referenceSource, firstScalarColumn, []).Should().Be("255901");
        lookupSet.GetReferenceIdentityValue(referenceSource, secondScalarColumn, []).Should().Be("255902");
    }

    [Test]
    public void It_rejects_missing_expected_identity_paths_even_when_identity_counts_match()
    {
        var (writePlan, _, _, secondScalarColumn, referenceSource) =
            CreateDuplicateReferenceJsonPathScalarFixture();
        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
                {
                    [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                        _schoolResourceInfo,
                        "$.schoolReference",
                        101L,
                        ("$.schoolId", "255901"),
                        ("$.educationOrganizationId", "255902")
                    ),
                },
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            )
        );

        var act = () => lookupSet.GetReferenceIdentityValue(referenceSource, secondScalarColumn, []);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*expected identity path '$.localEducationAgencyId'*found '$.educationOrganizationId' at compiled position 1*Actual identity paths: '$.schoolId', '$.educationOrganizationId'*"
            );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSet()
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.schoolReference",
                    101L,
                    ("$.schoolId", "255901")
                ),
                [new JsonPath("$.sections[0].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[0].schoolReference",
                    201L,
                    ("$.schoolId", "255901"),
                    (
                        DocumentIdentity.DescriptorIdentityJsonPath.Value,
                        "uri://ed-fi.org/schooltypedescriptor#elementary"
                    )
                ),
                [new JsonPath("$.sections[1].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[1].schoolReference",
                    202L,
                    ("$.schoolId", "255902"),
                    (
                        DocumentIdentity.DescriptorIdentityJsonPath.Value,
                        "uri://ed-fi.org/schooltypedescriptor#secondary"
                    )
                ),
                [new JsonPath("$.sections[0].sessions[0].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[0].sessions[0].schoolReference",
                    301L,
                    ("$.schoolId", "255901")
                ),
                [new JsonPath("$.sections[0].sessions[1].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[0].sessions[1].schoolReference",
                    302L,
                    ("$.schoolId", "255902")
                ),
                [new JsonPath("$.sections[1].sessions[0].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[1].sessions[0].schoolReference",
                    303L,
                    ("$.schoolId", "255903")
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.sections[0].schoolTypeDescriptor")] = CreateResolvedDescriptorReference(
                    _schoolTypeDescriptorResourceInfo,
                    "$.sections[0].schoolTypeDescriptor",
                    401L
                ),
                [new JsonPath("$.sections[1].schoolTypeDescriptor")] = CreateResolvedDescriptorReference(
                    _schoolTypeDescriptorResourceInfo,
                    "$.sections[1].schoolTypeDescriptor",
                    402L
                ),
                [new JsonPath("$.sections[0].schoolReference.schoolTypeDescriptor")] =
                    CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.sections[0].schoolReference.schoolTypeDescriptor",
                        501L
                    ),
                [new JsonPath("$.sections[1].schoolReference.schoolTypeDescriptor")] =
                    CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.sections[1].schoolReference.schoolTypeDescriptor",
                        502L
                    ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSetWithIdentityCountDrift()
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.schoolReference",
                    101L,
                    ("$.schoolId", "255901")
                ),
                [new JsonPath("$.sections[0].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[0].schoolReference",
                    201L,
                    ("$.schoolId", "255901")
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.sections[0].schoolReference.schoolTypeDescriptor")] =
                    CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.sections[0].schoolReference.schoolTypeDescriptor",
                        501L
                    ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSetWithIdentityKindDrift()
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.schoolReference",
                    101L,
                    ("$.schoolId", "255901")
                ),
                [new JsonPath("$.sections[0].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[0].schoolReference",
                    201L,
                    (
                        DocumentIdentity.DescriptorIdentityJsonPath.Value,
                        "uri://ed-fi.org/schooltypedescriptor#elementary"
                    ),
                    ("$.schoolId", "255901")
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.sections[0].schoolReference.schoolTypeDescriptor")] =
                    CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.sections[0].schoolReference.schoolTypeDescriptor",
                        501L
                    ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSetWithTargetDescriptorIdentityPaths()
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.schoolReference",
                    101L,
                    ("$.schoolId", "255901")
                ),
                [new JsonPath("$.sections[0].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[0].schoolReference",
                    201L,
                    ("$.schoolId", "255901"),
                    ("$.schoolTypeDescriptor", "501")
                ),
                [new JsonPath("$.sections[1].schoolReference")] = CreateResolvedDocumentReference(
                    _schoolResourceInfo,
                    "$.sections[1].schoolReference",
                    202L,
                    ("$.schoolId", "255902"),
                    ("$.schoolTypeDescriptor", "502")
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.sections[0].schoolReference.schoolTypeDescriptor")] =
                    CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.sections[0].schoolReference.schoolTypeDescriptor",
                        501L
                    ),
                [new JsonPath("$.sections[1].schoolReference.schoolTypeDescriptor")] =
                    CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.sections[1].schoolReference.schoolTypeDescriptor",
                        502L
                    ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResolvedDocumentReference CreateResolvedDocumentReference(
        BaseResourceInfo resourceInfo,
        string path,
        long documentId,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    )
    {
        return new ResolvedDocumentReference(
            new DocumentReference(
                resourceInfo,
                new DocumentIdentity([
                    .. identityElements.Select(identityElement => new DocumentIdentityElement(
                        new JsonPath(identityElement.IdentityJsonPath),
                        identityElement.IdentityValue
                    )),
                ]),
                new ReferentialId(Guid.NewGuid()),
                new JsonPath(path)
            ),
            documentId,
            11
        );
    }

    private static (
        ResourceWritePlan WritePlan,
        FlatteningResolvedReferenceLookupSet LookupSet,
        DbColumnName FirstScalarColumn,
        DbColumnName SecondScalarColumn,
        ReferenceDerivedValueSourceMetadata ReferenceSource
    ) CreateDuplicateReferenceJsonPathScalarFixture()
    {
        var table = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Program"),
            Path("$"),
            new TableKey(
                "PK_Program",
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
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                    _schoolResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PrimarySchoolCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String),
                    true,
                    Path(
                        "$.schoolReference.schoolCode",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolCode")
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SecondarySchoolCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String),
                    true,
                    Path(
                        "$.schoolReference.schoolCode",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolCode")
                    ),
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

        var referencePath = Path("$.schoolReference", new JsonPathSegment.Property("schoolReference"));
        var duplicatePath = Path(
            "$.schoolReference.schoolCode",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolCode")
        );
        var schoolIdIdentityPath = Path("$.schoolId", new JsonPathSegment.Property("schoolId"));
        var localEducationAgencyIdIdentityPath = Path(
            "$.localEducationAgencyId",
            new JsonPathSegment.Property("localEducationAgencyId")
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: referencePath,
            Table: table.Table,
            FkColumn: new DbColumnName("School_DocumentId"),
            TargetResource: _schoolResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    schoolIdIdentityPath,
                    duplicatePath,
                    new DbColumnName("PrimarySchoolCode")
                ),
                new ReferenceIdentityBinding(
                    localEducationAgencyIdIdentityPath,
                    duplicatePath,
                    new DbColumnName("SecondarySchoolCode")
                ),
            ]
        );

        var writePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _studentResource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: table,
                TablesInDependencyOrder: [table],
                DocumentReferenceBindings: [binding],
                DescriptorEdgeSources: []
            ),
            []
        );

        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
                {
                    [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                        _schoolResourceInfo,
                        "$.schoolReference",
                        101L,
                        ("$.schoolId", "255901"),
                        ("$.localEducationAgencyId", "255902")
                    ),
                },
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            )
        );

        return (
            writePlan,
            lookupSet,
            new DbColumnName("PrimarySchoolCode"),
            new DbColumnName("SecondarySchoolCode"),
            new ReferenceDerivedValueSourceMetadata(
                BindingIndex: 0,
                ReferenceObjectPath: referencePath,
                IdentityJsonPath: schoolIdIdentityPath,
                ReferenceJsonPath: duplicatePath
            )
        );
    }

    private static (
        ResourceWritePlan WritePlan,
        FlatteningResolvedReferenceLookupSet LookupSet,
        DbColumnName ScalarColumn,
        DbColumnName DescriptorColumn,
        ReferenceDerivedValueSourceMetadata ReferenceSource
    ) CreateDuplicateReferenceJsonPathDescriptorFixture()
    {
        var table = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Program"),
            Path("$"),
            new TableKey(
                "PK_Program",
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
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                    _schoolResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String),
                    true,
                    Path(
                        "$.schoolReference.schoolCategory",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolCategory")
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolCategoryDescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    Path(
                        "$.schoolReference.schoolCategory",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolCategory")
                    ),
                    _schoolTypeDescriptorResource,
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

        var referencePath = Path("$.schoolReference", new JsonPathSegment.Property("schoolReference"));
        var duplicatePath = Path(
            "$.schoolReference.schoolCategory",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolCategory")
        );
        var schoolCategoryCodeIdentityPath = Path(
            "$.schoolCategoryCode",
            new JsonPathSegment.Property("schoolCategoryCode")
        );
        var schoolCategoryDescriptorIdentityPath = Path(
            "$.schoolCategoryDescriptor",
            new JsonPathSegment.Property("schoolCategoryDescriptor")
        );
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: referencePath,
            Table: table.Table,
            FkColumn: new DbColumnName("School_DocumentId"),
            TargetResource: _schoolResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    schoolCategoryCodeIdentityPath,
                    duplicatePath,
                    new DbColumnName("SchoolCode")
                ),
                new ReferenceIdentityBinding(
                    schoolCategoryDescriptorIdentityPath,
                    duplicatePath,
                    new DbColumnName("SchoolCategoryDescriptorId")
                ),
            ]
        );

        var writePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: _studentResource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: table,
                TablesInDependencyOrder: [table],
                DocumentReferenceBindings: [binding],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: duplicatePath,
                        Table: table.Table,
                        FkColumn: new DbColumnName("SchoolCategoryDescriptorId"),
                        DescriptorResource: _schoolTypeDescriptorResource
                    ),
                ]
            ),
            []
        );

        var lookupSet = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
                {
                    [new JsonPath("$.schoolReference")] = CreateResolvedDocumentReference(
                        _schoolResourceInfo,
                        "$.schoolReference",
                        101L,
                        ("$.schoolCategoryCode", "255901"),
                        (
                            DocumentIdentity.DescriptorIdentityJsonPath.Value,
                            "uri://ed-fi.org/schooltypedescriptor#elementary"
                        )
                    ),
                },
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
                {
                    [new JsonPath("$.schoolReference.schoolCategory")] = CreateResolvedDescriptorReference(
                        _schoolTypeDescriptorResourceInfo,
                        "$.schoolReference.schoolCategory",
                        501L
                    ),
                },
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            )
        );

        return (
            writePlan,
            lookupSet,
            new DbColumnName("SchoolCode"),
            new DbColumnName("SchoolCategoryDescriptorId"),
            new ReferenceDerivedValueSourceMetadata(
                BindingIndex: 0,
                ReferenceObjectPath: referencePath,
                IdentityJsonPath: schoolCategoryCodeIdentityPath,
                ReferenceJsonPath: duplicatePath
            )
        );
    }

    private static ResolvedDescriptorReference CreateResolvedDescriptorReference(
        BaseResourceInfo resourceInfo,
        string path,
        long documentId
    )
    {
        return new ResolvedDescriptorReference(
            new DescriptorReference(
                resourceInfo,
                new DocumentIdentity([]),
                new ReferentialId(Guid.NewGuid()),
                new JsonPath(path)
            ),
            documentId,
            13
        );
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            Path("$"),
            new TableKey(
                "PK_Student",
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
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                    _schoolResource,
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

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"Student\" values (@DocumentId, @School_DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentReference(0),
                    "School_DocumentId"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateSectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "StudentSection"),
            Path(
                "$.sections[*]",
                new JsonPathSegment.Property("sections"),
                new JsonPathSegment.AnyArrayElement()
            ),
            new TableKey(
                "PK_StudentSection",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Student_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                    _schoolResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolTypeDescriptor_DocumentId"),
                    ColumnKind.DescriptorFk,
                    null,
                    true,
                    Path("$.schoolTypeDescriptor", new JsonPathSegment.Property("schoolTypeDescriptor")),
                    _schoolTypeDescriptorResource,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("Student_DocumentId")],
                [new DbColumnName("Student_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                        new DbColumnName("School_DocumentId")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"StudentSection\" values (@CollectionItemId, @Student_DocumentId, @Ordinal, @School_DocumentId, @SchoolTypeDescriptor_DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "Student_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.DocumentReference(1),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DescriptorReference(
                        _schoolTypeDescriptorResource,
                        Path("$.schoolTypeDescriptor", new JsonPathSegment.Property("schoolTypeDescriptor")),
                        Path(
                            "$.sections[*].schoolTypeDescriptor",
                            new JsonPathSegment.Property("sections"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("schoolTypeDescriptor")
                        )
                    ),
                    "SchoolTypeDescriptor_DocumentId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"StudentSection\" set \"School_DocumentId\" = @School_DocumentId where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"StudentSection\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateSessionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "StudentSectionSession"),
            Path(
                "$.sections[*].sessions[*]",
                new JsonPathSegment.Property("sections"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("sessions"),
                new JsonPathSegment.AnyArrayElement()
            ),
            new TableKey(
                "PK_StudentSectionSession",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Student_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Section_CollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    true,
                    Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                    _schoolResource,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("Student_DocumentId")],
                [new DbColumnName("Section_CollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                        new DbColumnName("School_DocumentId")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"StudentSectionSession\" values (@CollectionItemId, @Student_DocumentId, @Section_CollectionItemId, @Ordinal, @School_DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "Student_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "Section_CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.DocumentReference(2),
                    "School_DocumentId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        Path("$.schoolReference", new JsonPathSegment.Property("schoolReference")),
                        4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"StudentSectionSession\" set \"School_DocumentId\" = @School_DocumentId where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"StudentSectionSession\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [4, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static JsonPathExpression Path(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);
}
