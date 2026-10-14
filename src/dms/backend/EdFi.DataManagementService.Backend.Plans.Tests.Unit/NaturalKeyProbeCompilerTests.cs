// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Shared resource names and lookups for natural-key probe tests. Entries are always located by identity
/// path, never by position.
/// </summary>
internal static class NaturalKeyProbeTestSupport
{
    public static readonly QualifiedResourceName Session = new("Ed-Fi", "Session");
    public static readonly QualifiedResourceName Grade = new("Ed-Fi", "Grade");
    public static readonly QualifiedResourceName ChartOfAccount = new("Ed-Fi", "ChartOfAccount");
    public static readonly QualifiedResourceName Program = new("Ed-Fi", "Program");
    public static readonly QualifiedResourceName StudentSchoolAttendanceEvent = new(
        "Ed-Fi",
        "StudentSchoolAttendanceEvent"
    );
    public static readonly QualifiedResourceName EducationOrganization = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    public static readonly QualifiedResourceName GeneralStudentProgramAssociation = new(
        "Ed-Fi",
        "GeneralStudentProgramAssociation"
    );

    public static NaturalKeyProbeKeyColumn Key(IReadOnlyList<NaturalKeyProbeKeyColumn> keys, string path) =>
        keys.Single(key => key.IdentityJsonPath.Canonical == path);

    public static string[] Paths(IReadOnlyList<NaturalKeyProbeKeyColumn> keys) =>
        [.. keys.Select(key => key.IdentityJsonPath.Canonical)];

    public static string[] DistinctColumns(IReadOnlyList<NaturalKeyProbeKeyColumn> keys) =>
        [.. keys.Select(key => key.Column.Value).Distinct()];

    public static DbColumnModel Column(DbTableModel table, string columnName) =>
        table.Columns.Single(column => column.ColumnName.Value == columnName);
}

/// <summary>
/// Pins the natural-key probes compiled for authoritative DS 5.2 shapes.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_Authoritative_Ds52_Natural_Key_Probes(SqlDialect dialect)
{
    private static readonly string[] _gradeIdentityPaths =
    [
        "$.gradeTypeDescriptor",
        "$.gradingPeriodReference.gradingPeriodDescriptor",
        "$.gradingPeriodReference.gradingPeriodName",
        "$.gradingPeriodReference.schoolId",
        "$.gradingPeriodReference.schoolYear",
        "$.studentSectionAssociationReference.beginDate",
        "$.studentSectionAssociationReference.localCourseCode",
        "$.studentSectionAssociationReference.schoolId",
        "$.studentSectionAssociationReference.schoolYear",
        "$.studentSectionAssociationReference.sectionIdentifier",
        "$.studentSectionAssociationReference.sessionName",
        "$.studentSectionAssociationReference.studentUniqueId",
    ];

    private DerivedRelationalModelSet _modelSet = null!;
    private MappingSet _mappingSet = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        (_modelSet, _mappingSet) = Ds52FixtureHelper.BuildAndCompile(dialect);
    }

    [Test]
    public void It_gives_a_never_referenced_resource_an_own_key_probe_and_no_target_probe()
    {
        _mappingSet
            .OwnNaturalKeyProbesByResource.Should()
            .ContainKey(NaturalKeyProbeTestSupport.StudentSchoolAttendanceEvent);
        _mappingSet
            .NaturalKeyProbeTargets.Should()
            .NotContainKey(NaturalKeyProbeTestSupport.StudentSchoolAttendanceEvent);
    }

    [Test]
    public void It_orders_the_session_target_probe_in_RefKey_order()
    {
        var target = _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.Session];

        target.Kind.Should().Be(NaturalKeyProbeTargetKind.Concrete);
        target
            .KeyColumns.Select(key => (key.IdentityJsonPath.Canonical, key.Column.Value))
            .Should()
            .Equal(
                ("$.schoolReference.schoolId", "School_SchoolId"),
                ("$.schoolYearTypeReference.schoolYear", "SchoolYear_SchoolYear"),
                ("$.sessionName", "SessionName")
            );
        target.DocumentIdColumn.Value.Should().Be("DocumentId");
    }

    [Test]
    public void It_binds_session_own_key_references_to_their_document_reference_bindings()
    {
        var ownKey = _mappingSet.OwnNaturalKeyProbesByResource[NaturalKeyProbeTestSupport.Session];
        var bindings = _mappingSet
            .WritePlansByResource[NaturalKeyProbeTestSupport.Session]
            .Model
            .DocumentReferenceBindings;

        ownKey
            .KeyColumns.Select(key => (key.IdentityJsonPath.Canonical, key.Column.Value))
            .Should()
            .Equal(
                ("$.schoolReference.schoolId", "School_DocumentId"),
                ("$.schoolYearTypeReference.schoolYear", "SchoolYear_DocumentId"),
                ("$.sessionName", "SessionName")
            );

        foreach (var path in new[] { "$.schoolReference.schoolId", "$.schoolYearTypeReference.schoolYear" })
        {
            var key = NaturalKeyProbeTestSupport.Key(ownKey.KeyColumns, path);
            var binding = key
                .Binding.Should()
                .BeOfType<NaturalKeyProbeKeyBinding.DocumentReference>()
                .Subject;

            bindings[binding.DocumentReferenceBindingIndex].FkColumn.Should().Be(key.Column);
        }

        NaturalKeyProbeTestSupport
            .Key(ownKey.KeyColumns, "$.sessionName")
            .Binding.Should()
            .BeOfType<NaturalKeyProbeKeyBinding.Scalar>();
    }

    [Test]
    public void It_keeps_one_grade_target_entry_per_identity_path()
    {
        var target = _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.Grade];

        NaturalKeyProbeTestSupport.Paths(target.KeyColumns).Should().Equal(_gradeIdentityPaths);
    }

    [Test]
    public void It_binds_key_unified_grade_paths_to_their_shared_storage_column()
    {
        var keys = _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.Grade].KeyColumns;

        NaturalKeyProbeTestSupport
            .Key(keys, "$.gradingPeriodReference.schoolId")
            .Column.Value.Should()
            .Be("SchoolId_Unified");
        NaturalKeyProbeTestSupport
            .Key(keys, "$.studentSectionAssociationReference.schoolId")
            .Column.Value.Should()
            .Be("SchoolId_Unified");
        NaturalKeyProbeTestSupport
            .Key(keys, "$.gradingPeriodReference.schoolYear")
            .Column.Value.Should()
            .Be("SchoolYear_Unified");
        NaturalKeyProbeTestSupport
            .Key(keys, "$.studentSectionAssociationReference.schoolYear")
            .Column.Value.Should()
            .Be("SchoolYear_Unified");
    }

    [Test]
    public void It_resolves_the_grade_target_to_the_RefKey_identity_columns()
    {
        var target = _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.Grade];

        NaturalKeyProbeTestSupport
            .DistinctColumns(target.KeyColumns)
            .Should()
            .Equal(
                "GradeTypeDescriptor_DescriptorId",
                "GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId",
                "GradingPeriodGradingPeriod_GradingPeriodName",
                "SchoolId_Unified",
                "SchoolYear_Unified",
                "StudentSectionAssociation_BeginDate",
                "StudentSectionAssociation_LocalCourseCode",
                "StudentSectionAssociation_SectionIdentifier",
                "StudentSectionAssociation_SessionName",
                "StudentSectionAssociation_StudentUniqueId"
            );
    }

    [Test]
    public void It_binds_the_grade_grading_period_descriptor_as_a_descriptor_in_the_target_probe()
    {
        var key = NaturalKeyProbeTestSupport.Key(
            _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.Grade].KeyColumns,
            "$.gradingPeriodReference.gradingPeriodDescriptor"
        );

        key.Column.Value.Should().Be("GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId");
        key.Binding.Should()
            .Be(
                new NaturalKeyProbeKeyBinding.Descriptor(
                    new QualifiedResourceName("Ed-Fi", "GradingPeriodDescriptor")
                )
            );
    }

    [Test]
    public void It_binds_the_grade_grading_period_descriptor_as_a_document_reference_in_the_own_key_probe()
    {
        var ownKey = _mappingSet.OwnNaturalKeyProbesByResource[NaturalKeyProbeTestSupport.Grade];
        var key = NaturalKeyProbeTestSupport.Key(
            ownKey.KeyColumns,
            "$.gradingPeriodReference.gradingPeriodDescriptor"
        );
        var bindings = _mappingSet
            .WritePlansByResource[NaturalKeyProbeTestSupport.Grade]
            .Model
            .DocumentReferenceBindings;

        key.Column.Value.Should().Be("GradingPeriodGradingPeriod_DocumentId");
        var binding = key.Binding.Should().BeOfType<NaturalKeyProbeKeyBinding.DocumentReference>().Subject;
        bindings[binding.DocumentReferenceBindingIndex].FkColumn.Should().Be(key.Column);
        NaturalKeyProbeTestSupport
            .DistinctColumns(ownKey.KeyColumns)
            .Should()
            .Equal(
                "GradeTypeDescriptor_DescriptorId",
                "GradingPeriodGradingPeriod_DocumentId",
                "StudentSectionAssociation_DocumentId"
            );
    }

    [Test]
    public void It_binds_chart_of_account_fiscal_year_to_storage_in_the_target_and_to_the_alias_in_the_own_key()
    {
        NaturalKeyProbeTestSupport
            .Key(
                _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.ChartOfAccount].KeyColumns,
                "$.fiscalYear"
            )
            .Column.Value.Should()
            .Be("FiscalYear_Unified");
        NaturalKeyProbeTestSupport
            .Key(
                _mappingSet
                    .OwnNaturalKeyProbesByResource[NaturalKeyProbeTestSupport.ChartOfAccount]
                    .KeyColumns,
                "$.fiscalYear"
            )
            .Column.Value.Should()
            .Be("FiscalYear");
    }

    [Test]
    public void It_binds_a_descriptor_identity_part_to_the_int32_descriptor_id_column()
    {
        var target = _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.Program];
        var key = NaturalKeyProbeTestSupport.Key(target.KeyColumns, "$.programTypeDescriptor");
        var root = _modelSet
            .GetConcreteResourceModelsByResource()[NaturalKeyProbeTestSupport.Program]
            .RelationalModel.Root;

        key.Column.Value.Should().Be("ProgramTypeDescriptor_DescriptorId");
        key.Binding.Should()
            .Be(
                new NaturalKeyProbeKeyBinding.Descriptor(
                    new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                )
            );
        key.Column.Should().NotBe(target.DocumentIdColumn);
        NaturalKeyProbeTestSupport
            .Column(root, key.Column.Value)
            .ScalarType!.Kind.Should()
            .Be(ScalarKind.Int32);
        NaturalKeyProbeTestSupport
            .Column(root, target.DocumentIdColumn.Value)
            .ScalarType!.Kind.Should()
            .Be(ScalarKind.Int64);
    }

    [Test]
    public void It_compiles_the_education_organization_abstract_probe_with_the_concrete_resource_key()
    {
        var target = _mappingSet.NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.EducationOrganization];
        var table = _modelSet
            .AbstractIdentityTablesInNameOrder.Single(info =>
                info.AbstractResourceKey.Resource == NaturalKeyProbeTestSupport.EducationOrganization
            )
            .TableModel;

        target.Kind.Should().Be(NaturalKeyProbeTargetKind.Abstract);
        target.Table.Name.Should().Be("EducationOrganizationIdentity");
        target
            .KeyColumns.Select(key => (key.IdentityJsonPath.Canonical, key.Column.Value))
            .Should()
            .Equal(("$.educationOrganizationId", "EducationOrganizationId"));
        target.ResourceKeyIdColumn.Should().Be(new DbColumnName("ResourceKeyId"));
        NaturalKeyProbeTestSupport
            .Column(table, "ResourceKeyId")
            .ScalarType!.Kind.Should()
            .Be(ScalarKind.Int16);
    }

    [Test]
    public void It_compiles_the_general_student_program_association_abstract_probe_with_a_descriptor_projection()
    {
        var key = NaturalKeyProbeTestSupport.Key(
            _mappingSet
                .NaturalKeyProbeTargets[NaturalKeyProbeTestSupport.GeneralStudentProgramAssociation]
                .KeyColumns,
            "$.programReference.programTypeDescriptor"
        );

        key.Column.Value.Should().Be("Program_ProgramTypeDescriptor_DescriptorId");
        key.Binding.Should()
            .Be(
                new NaturalKeyProbeKeyBinding.Descriptor(
                    new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                )
            );
    }

    [Test]
    public void It_never_keys_an_abstract_probe_on_resource_key_id_or_discriminator()
    {
        var abstractTargets = _modelSet
            .AbstractIdentityTablesInNameOrder.Select(info =>
                _mappingSet.NaturalKeyProbeTargets[info.AbstractResourceKey.Resource]
            )
            .ToArray();

        abstractTargets.Should().HaveCount(_modelSet.AbstractIdentityTablesInNameOrder.Count);
        abstractTargets
            .SelectMany(target => target.KeyColumns)
            .Select(key => key.Column.Value)
            .Should()
            .NotContain(["ResourceKeyId", "Discriminator"]);
        abstractTargets
            .Should()
            .OnlyContain(target =>
                target.Kind == NaturalKeyProbeTargetKind.Abstract
                && target.ResourceKeyIdColumn == RelationalNameConventions.ResourceKeyIdColumnName
            );
    }

    [Test]
    public void It_matches_the_shared_descriptor_probe_to_the_derived_descriptor_table()
    {
        var probe = _mappingSet.DescriptorProbeTarget;
        var descriptorRoots = _modelSet
            .ConcreteResourcesInNameOrder.Where(resource =>
                resource.StorageKind is ResourceStorageKind.SharedDescriptorTable
            )
            .Select(resource => resource.RelationalModel.Root)
            .ToArray();

        descriptorRoots.Should().NotBeEmpty();

        foreach (var root in descriptorRoots)
        {
            root.Table.Should().Be(probe.DescriptorTable);
            root.Key.Columns.Select(column => column.ColumnName).Should().Equal(probe.DescriptorIdColumn);
            NaturalKeyProbeTestSupport
                .Column(root, probe.DescriptorIdColumn.Value)
                .ScalarType!.Kind.Should()
                .Be(ScalarKind.Int32);
            NaturalKeyProbeTestSupport
                .Column(root, probe.DocumentIdColumn.Value)
                .ScalarType!.Kind.Should()
                .Be(ScalarKind.Int64);
            NaturalKeyProbeTestSupport.Column(root, probe.NamespaceColumn.Value).Should().NotBeNull();
            NaturalKeyProbeTestSupport.Column(root, probe.CodeValueColumn.Value).Should().NotBeNull();
        }
    }

    [Test]
    public void It_names_the_shared_descriptor_probe_columns_by_relational_name_conventions()
    {
        var probe = _mappingSet.DescriptorProbeTarget;

        probe.Should().Be(DescriptorProbeTarget.SharedDescriptorTable);
        probe.DescriptorIdColumn.Should().Be(RelationalNameConventions.DescriptorKeyColumnName);
        probe.DocumentIdColumn.Should().Be(RelationalNameConventions.DocumentIdColumnName);
        probe.ResourceKeyIdColumn.Should().Be(RelationalNameConventions.ResourceKeyIdColumnName);
    }
}

/// <summary>
/// Pins that dialect-specific identifier shortening never changes a probe's canonical identity paths.
/// </summary>
[TestFixture]
public class Given_Authoritative_Ds52_Natural_Key_Probes_On_Both_Dialects
{
    private MappingSet _pgsql = null!;
    private MappingSet _mssql = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _pgsql = Ds52FixtureHelper.BuildAndCompile(SqlDialect.Pgsql).MappingSet;
        _mssql = Ds52FixtureHelper.BuildAndCompile(SqlDialect.Mssql).MappingSet;
    }

    [Test]
    public void It_keeps_the_same_target_probe_paths_on_both_dialects()
    {
        _mssql.NaturalKeyProbeTargets.Keys.Should().BeEquivalentTo(_pgsql.NaturalKeyProbeTargets.Keys);

        foreach (var (resource, target) in _pgsql.NaturalKeyProbeTargets)
        {
            NaturalKeyProbeTestSupport
                .Paths(_mssql.NaturalKeyProbeTargets[resource].KeyColumns)
                .Should()
                .Equal(NaturalKeyProbeTestSupport.Paths(target.KeyColumns), $"{resource} target paths");
        }
    }

    [Test]
    public void It_keeps_the_same_own_key_probe_paths_on_both_dialects()
    {
        _mssql
            .OwnNaturalKeyProbesByResource.Keys.Should()
            .BeEquivalentTo(_pgsql.OwnNaturalKeyProbesByResource.Keys);

        foreach (var (resource, ownKey) in _pgsql.OwnNaturalKeyProbesByResource)
        {
            NaturalKeyProbeTestSupport
                .Paths(_mssql.OwnNaturalKeyProbesByResource[resource].KeyColumns)
                .Should()
                .Equal(NaturalKeyProbeTestSupport.Paths(ownKey.KeyColumns), $"{resource} own-key paths");
        }
    }
}

/// <summary>
/// Pins the mapping-set validation rejections for natural-key probes.
/// </summary>
[TestFixture]
public class Given_Natural_Key_Probe_Validation
{
    private Action _validateCompiled = null!;
    private Action _validateTargetForNeverReferencedResource = null!;
    private Action _validateTargetWithDocumentReferenceKey = null!;
    private Action _validateReorderedTarget = null!;
    private Action _validateEmptyTarget = null!;
    private Action _validateEmptyOwnKey = null!;
    private Action _validateOwnKeyBoundToAnotherReferenceSite = null!;
    private Action _validateOwnKeyBoundPastTheLastReferenceSite = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var (modelSet, mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        var targets = mappingSet.NaturalKeyProbeTargets;
        var ownKeys = mappingSet.OwnNaturalKeyProbesByResource;
        var session = targets[NaturalKeyProbeTestSupport.Session];
        var concreteResources = modelSet.GetConcreteResourceModelsByResource();

        // Built exactly as the compiler builds a referenced resource's probe, so the missing RefKey
        // is the only possible reason for rejection.
        var attendanceEventTarget = NaturalKeyProbeCompiler.BuildConcreteTarget(
            concreteResources[NaturalKeyProbeTestSupport.StudentSchoolAttendanceEvent]
        );

        _validateCompiled = () => NaturalKeyProbeCompiler.Validate(modelSet, targets, ownKeys);

        _validateTargetForNeverReferencedResource = () =>
            NaturalKeyProbeCompiler.Validate(modelSet, WithTarget(targets, attendanceEventTarget), ownKeys);

        _validateTargetWithDocumentReferenceKey = () =>
            NaturalKeyProbeCompiler.Validate(
                modelSet,
                WithTarget(
                    targets,
                    session with
                    {
                        KeyColumns =
                        [
                            session.KeyColumns[0] with
                            {
                                Binding = new NaturalKeyProbeKeyBinding.DocumentReference(0),
                            },
                            .. session.KeyColumns.Skip(1),
                        ],
                    }
                ),
                ownKeys
            );

        _validateReorderedTarget = () =>
            NaturalKeyProbeCompiler.Validate(
                modelSet,
                WithTarget(targets, session with { KeyColumns = [.. session.KeyColumns.Reverse()] }),
                ownKeys
            );

        _validateEmptyTarget = () =>
            NaturalKeyProbeCompiler.Validate(
                modelSet,
                WithTarget(targets, session with { KeyColumns = [] }),
                ownKeys
            );

        var sessionOwnKey = ownKeys[NaturalKeyProbeTestSupport.Session];
        Dictionary<QualifiedResourceName, OwnNaturalKeyProbe> emptyOwnKeys = new(ownKeys)
        {
            [NaturalKeyProbeTestSupport.Session] = sessionOwnKey with { KeyColumns = [] },
        };
        _validateEmptyOwnKey = () => NaturalKeyProbeCompiler.Validate(modelSet, targets, emptyOwnKeys);

        // Swap Session's two reference-site indexes: both stay in range, but each now names the
        // other reference, whose FK column is not the key column.
        var schoolKey = NaturalKeyProbeTestSupport.Key(
            sessionOwnKey.KeyColumns,
            "$.schoolReference.schoolId"
        );
        var schoolYearKey = NaturalKeyProbeTestSupport.Key(
            sessionOwnKey.KeyColumns,
            "$.schoolYearTypeReference.schoolYear"
        );
        Dictionary<QualifiedResourceName, OwnNaturalKeyProbe> swappedOwnKeys = new(ownKeys)
        {
            [NaturalKeyProbeTestSupport.Session] = sessionOwnKey with
            {
                KeyColumns =
                [
                    .. sessionOwnKey.KeyColumns.Select(key =>
                        key.IdentityJsonPath.Canonical switch
                        {
                            "$.schoolReference.schoolId" => key with { Binding = schoolYearKey.Binding },
                            "$.schoolYearTypeReference.schoolYear" => key with
                            {
                                Binding = schoolKey.Binding,
                            },
                            _ => key,
                        }
                    ),
                ],
            },
        };
        _validateOwnKeyBoundToAnotherReferenceSite = () =>
            NaturalKeyProbeCompiler.Validate(modelSet, targets, swappedOwnKeys);

        var sessionBindingCount = concreteResources[NaturalKeyProbeTestSupport.Session]
            .RelationalModel
            .DocumentReferenceBindings
            .Count;
        Dictionary<QualifiedResourceName, OwnNaturalKeyProbe> outOfRangeOwnKeys = new(ownKeys)
        {
            [NaturalKeyProbeTestSupport.Session] = sessionOwnKey with
            {
                KeyColumns =
                [
                    .. sessionOwnKey.KeyColumns.Select(key =>
                        key.IdentityJsonPath.Canonical == "$.schoolReference.schoolId"
                            ? key with
                            {
                                Binding = new NaturalKeyProbeKeyBinding.DocumentReference(
                                    sessionBindingCount
                                ),
                            }
                            : key
                    ),
                ],
            },
        };
        _validateOwnKeyBoundPastTheLastReferenceSite = () =>
            NaturalKeyProbeCompiler.Validate(modelSet, targets, outOfRangeOwnKeys);
    }

    [Test]
    public void It_accepts_the_compiled_probes()
    {
        _validateCompiled.Should().NotThrow();
    }

    [Test]
    public void It_rejects_a_target_probe_for_a_resource_without_RefKey_inventory()
    {
        _validateTargetForNeverReferencedResource
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Ed-Fi.StudentSchoolAttendanceEvent*no RefKey inventory*");
    }

    [Test]
    public void It_rejects_a_document_reference_key_in_a_target_probe()
    {
        _validateTargetWithDocumentReferenceKey
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Ed-Fi.Session*document-reference key*");
    }

    [Test]
    public void It_rejects_a_target_probe_whose_columns_are_reordered()
    {
        _validateReorderedTarget
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Ed-Fi.Session*no RefKey inventory*");
    }

    [Test]
    public void It_rejects_a_target_probe_with_no_keys()
    {
        _validateEmptyTarget
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*reference-target probe for resource 'Ed-Fi.Session' has no keys*");
    }

    [Test]
    public void It_rejects_an_own_key_probe_with_no_keys()
    {
        _validateEmptyOwnKey
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*own natural-key probe for resource 'Ed-Fi.Session' has no keys*");
    }

    [Test]
    public void It_rejects_an_own_key_document_reference_bound_to_another_reference_site()
    {
        _validateOwnKeyBoundToAnotherReferenceSite
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'Ed-Fi.Session'*does not supply column*");
    }

    [Test]
    public void It_rejects_an_own_key_document_reference_bound_past_the_last_reference_site()
    {
        _validateOwnKeyBoundPastTheLastReferenceSite
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'Ed-Fi.Session'*'$.schoolReference.schoolId'*does not supply column*");
    }

    private static Dictionary<QualifiedResourceName, NaturalKeyProbeTarget> WithTarget(
        IReadOnlyDictionary<QualifiedResourceName, NaturalKeyProbeTarget> targets,
        NaturalKeyProbeTarget target
    ) => new(targets) { [target.Resource] = target };
}

/// <summary>
/// Pins the empty-identity compile guard.
/// </summary>
[TestFixture]
public class Given_A_Resource_Without_Identity
{
    private Action _compileWithoutConcreteIdentity = null!;
    private Action _compileWithoutAbstractIdentity = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(
            Ds52FixtureHelper.FixturePath,
            SqlDialect.Pgsql
        );
        var compiler = new MappingSetCompiler();

        var withoutConcreteIdentity = modelSet with
        {
            ConcreteResourcesInNameOrder =
            [
                .. modelSet.ConcreteResourcesInNameOrder.Select(resource =>
                    resource.RelationalModel.Resource == NaturalKeyProbeTestSupport.Session
                        ? resource with
                        {
                            IdentityJsonPaths = [],
                        }
                        : resource
                ),
            ],
        };
        _compileWithoutConcreteIdentity = () => compiler.Compile(withoutConcreteIdentity);

        var withoutAbstractIdentity = modelSet with
        {
            AbstractIdentityTablesInNameOrder =
            [
                .. modelSet.AbstractIdentityTablesInNameOrder.Select(info =>
                    info.AbstractResourceKey.Resource == NaturalKeyProbeTestSupport.EducationOrganization
                        ? info with
                        {
                            TableModel = info.TableModel with
                            {
                                Columns =
                                [
                                    .. info.TableModel.Columns.Where(column => column.SourceJsonPath is null),
                                ],
                            },
                        }
                        : info
                ),
            ],
        };
        _compileWithoutAbstractIdentity = () => compiler.Compile(withoutAbstractIdentity);
    }

    [Test]
    public void It_fails_compilation_for_a_concrete_resource_without_identity_paths()
    {
        _compileWithoutConcreteIdentity
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Cannot compile mapping set: resource 'Ed-Fi.Session' has no identity JSON paths*");
    }

    [Test]
    public void It_fails_compilation_for_an_abstract_identity_table_without_identity_columns()
    {
        _compileWithoutAbstractIdentity
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile mapping set: abstract identity table '*' for resource 'Ed-Fi.EducationOrganization' has no identity columns*"
            );
    }
}

/// <summary>
/// The every-resource parity guard: compiled probes must reproduce the live referential-identity trigger,
/// <c>UX_&lt;R&gt;_NK</c> and <c>UX_&lt;R&gt;_RefKey</c> derivations. Removed together with the RI triggers.
/// </summary>
[TestFixture("ds-5.2", SqlDialect.Pgsql)]
[TestFixture("ds-5.2", SqlDialect.Mssql)]
[TestFixture("ds-5.2-tpdm", SqlDialect.Pgsql)]
[TestFixture("ds-5.2-tpdm", SqlDialect.Mssql)]
[TestFixture("sample", SqlDialect.Pgsql)]
[TestFixture("sample", SqlDialect.Mssql)]
public class Given_Natural_Key_Probes_Match_Legacy_Derivations(string fixtureName, SqlDialect dialect)
{
    private const string Ds52InputPath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string TpdmInputPath =
        "../Fixtures/authoritative/ds-5.2-tpdm/inputs/tpdm-api-schema-authoritative.json";
    private const string SampleInputPath =
        "../Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json";

    private DerivedRelationalModelSet _modelSet = null!;
    private MappingSet _mappingSet = null!;
    private ConcreteResourceModel[] _relationalResources = [];
    private Dictionary<
        QualifiedResourceName,
        TriggerKindParameters.ReferentialIdentityMaintenance
    > _riByResource = [];

    [OneTimeSetUp]
    public void Setup()
    {
        (string, bool)[] inputs = fixtureName switch
        {
            "ds-5.2" => [(Ds52InputPath, false)],
            "ds-5.2-tpdm" => [(Ds52InputPath, false), (TpdmInputPath, true)],
            "sample" => [(Ds52InputPath, false), (SampleInputPath, true)],
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, null),
        };

        _modelSet = RuntimePlanFixtureModelSetBuilder.Build(inputs, dialect);
        _mappingSet = new MappingSetCompiler().Compile(_modelSet);
        _relationalResources =
        [
            .. _modelSet.ConcreteResourcesInNameOrder.Where(resource =>
                resource.StorageKind is ResourceStorageKind.RelationalTables
            ),
        ];
        _riByResource = _modelSet
            .TriggersInCreateOrder.Select(trigger => trigger.Parameters)
            .OfType<TriggerKindParameters.ReferentialIdentityMaintenance>()
            .ToDictionary(ri => new QualifiedResourceName(ri.ProjectName, ri.ResourceName));
    }

    [Test]
    public void It_compiles_exactly_the_expected_probes()
    {
        List<string> violations = [];

        foreach (var resource in _relationalResources)
        {
            var name = resource.RelationalModel.Resource;
            var hasRefKey = resource
                .RelationalModel.Root.Constraints.OfType<TableConstraint.Unique>()
                .Any(unique => unique.Columns[^1] == RelationalNameConventions.DocumentIdColumnName);

            if (!_mappingSet.OwnNaturalKeyProbesByResource.ContainsKey(name))
            {
                violations.Add($"{name}: no own-key probe");
            }

            if (_mappingSet.NaturalKeyProbeTargets.ContainsKey(name) != hasRefKey)
            {
                violations.Add($"{name}: target probe present = {!hasRefKey}, RefKey present = {hasRefKey}");
            }
        }

        foreach (var abstractTable in _modelSet.AbstractIdentityTablesInNameOrder)
        {
            if (
                !_mappingSet.NaturalKeyProbeTargets.TryGetValue(
                    abstractTable.AbstractResourceKey.Resource,
                    out var target
                )
                || target.Kind != NaturalKeyProbeTargetKind.Abstract
            )
            {
                violations.Add($"{abstractTable.AbstractResourceKey.Resource}: no abstract probe");
            }
        }

        _mappingSet.OwnNaturalKeyProbesByResource.Should().HaveCount(_relationalResources.Length);
        violations.Should().BeEmpty();
    }

    [Test]
    public void It_matches_the_referential_identity_trigger_elements()
    {
        List<string> violations = [];

        foreach (var resource in _relationalResources)
        {
            var name = resource.RelationalModel.Resource;

            if (!_riByResource.TryGetValue(name, out var ri))
            {
                violations.Add($"{name}: no referential-identity trigger");
                continue;
            }

            var elements = ri.IdentityElements;
            var ownKeys = _mappingSet.OwnNaturalKeyProbesByResource[name].KeyColumns;
            CheckPaths(name, "own-key", ownKeys, elements, violations);

            for (var index = 0; index < Math.Min(ownKeys.Count, elements.Count); index++)
            {
                CheckOwnKeyEntry(resource, ownKeys[index], elements[index], violations);
            }

            if (_mappingSet.NaturalKeyProbeTargets.TryGetValue(name, out var target))
            {
                CheckPaths(name, "target", target.KeyColumns, elements, violations);

                for (var index = 0; index < Math.Min(target.KeyColumns.Count, elements.Count); index++)
                {
                    var key = target.KeyColumns[index];
                    var element = elements[index];
                    var storageColumn = StorageColumn(resource.RelationalModel.Root, element.Column);

                    if (key.Column != storageColumn)
                    {
                        violations.Add(
                            $"{name} target {element.IdentityJsonPath}: column {key.Column.Value}, "
                                + $"storage-resolved element column {storageColumn.Value}"
                        );
                    }

                    CheckValueBinding(name, "target", key, element, violations);
                }
            }
        }

        _riByResource.Should().NotBeEmpty();
        violations.Should().BeEmpty();
    }

    [Test]
    public void It_matches_each_own_key_probe_to_the_natural_key_constraint()
    {
        List<string> violations = [];

        foreach (var resource in _relationalResources)
        {
            var name = resource.RelationalModel.Resource;
            var columns = _mappingSet
                .OwnNaturalKeyProbesByResource[name]
                .KeyColumns.Select(key => key.Column)
                .Distinct()
                .ToArray();

            if (
                !resource
                    .RelationalModel.Root.Constraints.OfType<TableConstraint.Unique>()
                    .Any(unique => unique.Columns.SequenceEqual(columns))
            )
            {
                violations.Add(
                    $"{name}: no root unique constraint on ({string.Join(", ", columns.Select(column => column.Value))})"
                );
            }
        }

        violations.Should().BeEmpty();
    }

    [Test]
    public void It_matches_each_superclass_alias_to_the_abstract_probe_paths()
    {
        List<string> violations = [];
        var aliasesChecked = 0;

        foreach (var ri in _riByResource.Values)
        {
            if (ri.SuperclassAlias is not { } alias)
            {
                continue;
            }

            var abstractResource = new QualifiedResourceName(alias.ProjectName, alias.ResourceName);

            if (
                !_mappingSet.NaturalKeyProbeTargets.TryGetValue(abstractResource, out var target)
                || target.Kind != NaturalKeyProbeTargetKind.Abstract
            )
            {
                continue;
            }

            aliasesChecked++;
            string[] aliasPaths = [.. alias.IdentityElements.Select(element => element.IdentityJsonPath)];

            if (!aliasPaths.SequenceEqual(NaturalKeyProbeTestSupport.Paths(target.KeyColumns)))
            {
                violations.Add(
                    $"{ri.ProjectName}.{ri.ResourceName} alias of {abstractResource}: "
                        + $"[{string.Join(", ", aliasPaths)}] vs "
                        + $"[{string.Join(", ", NaturalKeyProbeTestSupport.Paths(target.KeyColumns))}]"
                );
            }
        }

        aliasesChecked.Should().BePositive();
        violations.Should().BeEmpty();
    }

    private static void CheckPaths(
        QualifiedResourceName name,
        string probeKind,
        IReadOnlyList<NaturalKeyProbeKeyColumn> keys,
        IReadOnlyList<IdentityElementMapping> elements,
        List<string> violations
    )
    {
        string[] elementPaths = [.. elements.Select(element => element.IdentityJsonPath)];

        if (!elementPaths.SequenceEqual(NaturalKeyProbeTestSupport.Paths(keys)))
        {
            violations.Add(
                $"{name} {probeKind} paths [{string.Join(", ", NaturalKeyProbeTestSupport.Paths(keys))}] "
                    + $"vs trigger [{string.Join(", ", elementPaths)}]"
            );
        }
    }

    /// <summary>
    /// A document-reference entry is checked structurally: a descriptor-valued reference part, such as
    /// the Grade grading-period descriptor, is correctly a document reference, so descriptor and
    /// scalar-type parity apply only to value entries.
    /// </summary>
    private static void CheckOwnKeyEntry(
        ConcreteResourceModel resource,
        NaturalKeyProbeKeyColumn key,
        IdentityElementMapping element,
        List<string> violations
    )
    {
        var name = resource.RelationalModel.Resource;

        if (key.Binding is not NaturalKeyProbeKeyBinding.DocumentReference(var index))
        {
            if (key.Column != element.Column)
            {
                violations.Add(
                    $"{name} own-key {element.IdentityJsonPath}: column {key.Column.Value}, "
                        + $"element column {element.Column.Value}"
                );
            }

            CheckValueBinding(name, "own-key", key, element, violations);
            return;
        }

        var bindings = resource.RelationalModel.DocumentReferenceBindings;

        if (index < 0 || index >= bindings.Count)
        {
            violations.Add($"{name} own-key {element.IdentityJsonPath}: binding index {index} out of range");
            return;
        }

        var binding = bindings[index];

        if (binding.FkColumn != key.Column)
        {
            violations.Add(
                $"{name} own-key {element.IdentityJsonPath}: column {key.Column.Value}, "
                    + $"binding FK column {binding.FkColumn.Value}"
            );
        }

        if (
            !binding.IdentityBindings.Any(identityBinding =>
                identityBinding.ReferenceJsonPath.Canonical == element.IdentityJsonPath
                && identityBinding.Column == element.Column
            )
        )
        {
            violations.Add(
                $"{name} own-key {element.IdentityJsonPath}: binding {index} has no identity binding "
                    + $"for column {element.Column.Value}"
            );
        }
    }

    private static void CheckValueBinding(
        QualifiedResourceName name,
        string probeKind,
        NaturalKeyProbeKeyColumn key,
        IdentityElementMapping element,
        List<string> violations
    )
    {
        var matches = key.Binding switch
        {
            NaturalKeyProbeKeyBinding.Descriptor => element.IsDescriptorReference,
            NaturalKeyProbeKeyBinding.Scalar(var scalarType) => !element.IsDescriptorReference
                && scalarType == element.ScalarType,
            _ => false,
        };

        if (!matches)
        {
            violations.Add(
                $"{name} {probeKind} {element.IdentityJsonPath}: binding {key.Binding}, element "
                    + $"descriptor = {element.IsDescriptorReference}, scalar type = {element.ScalarType}"
            );
        }
    }

    private static DbColumnName StorageColumn(DbTableModel root, DbColumnName columnName) =>
        NaturalKeyProbeTestSupport.Column(root, columnName.Value).Storage is ColumnStorage.UnifiedAlias alias
            ? alias.CanonicalColumn
            : columnName;
}
