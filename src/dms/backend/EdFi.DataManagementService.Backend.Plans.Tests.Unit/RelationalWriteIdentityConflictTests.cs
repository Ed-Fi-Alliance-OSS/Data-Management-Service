// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Classifies root natural-key violations and builds duplicate-identity values from the compiled authoritative
/// DS 5.2 mapping set with all trigger metadata withheld, so both read only the own natural-key probes.
/// ClassPeriod and School use the values pinned by the IdentityConflictValidation E2E bodies. Grade's natural
/// key puts its descriptor column first while the table declares it after both reference columns, collapses
/// its 4- and 7-part references to one column each, and carries a descriptor-valued reference part.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_Ds52_Identity_Conflicts_Without_Trigger_Metadata(SqlDialect dialect)
{
    private const string ClassPeriodNaturalKey = "UX_ClassPeriod_NK";
    private const string EducationOrganizationIdentityNaturalKey = "UX_EducationOrganizationIdentity_NK";
    private const string GradeNaturalKey = "UX_Grade_NK";
    private const string GradeTypeDescriptorUri = "uri://ed-fi.org/GradeTypeDescriptor#Final";
    private const string GradingPeriodDescriptorUri =
        "uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks";

    private static readonly QualifiedResourceName ClassPeriodResource = new("Ed-Fi", "ClassPeriod");
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName GradeResource = new("Ed-Fi", "Grade");

    private RelationalWriteConstraintResolution _classPeriodResolution = null!;
    private RelationalWriteConstraintResolution _schoolResolution = null!;
    private RelationalWriteConstraintResolution _gradeResolution = null!;
    private RelationalWriteExecutorResult? _classPeriodResult;
    private RelationalWriteExecutorResult? _schoolResult;
    private RelationalWriteExecutorResult? _gradeResult;
    private int _resourcesSwept;
    private int _ownNaturalKeyProbeCount;
    private readonly List<string> _naturalKeyClassificationMismatches = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var (_, compiledMappingSet) = Ds52FixtureHelper.BuildAndCompile(dialect);
        var mappingSet = compiledMappingSet with
        {
            Model = compiledMappingSet.Model with { TriggersInCreateOrder = [] },
        };

        SweepRootNaturalKeyClassification(mappingSet);

        var classPeriodRequest = CreateRequest(
            mappingSet,
            ClassPeriodResource,
            RelationalWriteOperationKind.Put,
            """{"classPeriodName":"First","schoolReference":{"schoolId":255901001}}"""
        );
        var schoolRequest = CreateRequest(
            mappingSet,
            SchoolResource,
            RelationalWriteOperationKind.Post,
            """{"schoolId":155901,"nameOfInstitution":"School Test"}"""
        );
        // The body lists gradeTypeDescriptor last, so the reported order cannot come from the body.
        var gradeRequest = CreateRequest(
            mappingSet,
            GradeResource,
            RelationalWriteOperationKind.Put,
            $$"""
            {
              "gradingPeriodReference": {
                "gradingPeriodDescriptor": "{{GradingPeriodDescriptorUri}}",
                "gradingPeriodName": "GP1",
                "schoolId": 255901001,
                "schoolYear": 2022
              },
              "studentSectionAssociationReference": {
                "beginDate": "2021-08-23",
                "localCourseCode": "ALG-1",
                "schoolId": 255901001,
                "schoolYear": 2022,
                "sectionIdentifier": "ALG-1-01",
                "sessionName": "2021-2022 Fall Semester",
                "studentUniqueId": "604822"
              },
              "gradeTypeDescriptor": "{{GradeTypeDescriptorUri}}"
            }
            """
        );

        var resolver = new RelationalWriteConstraintResolver();
        _classPeriodResolution = Resolve(resolver, classPeriodRequest, ClassPeriodNaturalKey);
        _schoolResolution = Resolve(resolver, schoolRequest, EducationOrganizationIdentityNaturalKey);
        _gradeResolution = Resolve(resolver, gradeRequest, GradeNaturalKey);

        _classPeriodResult = MapUniqueViolation(resolver, classPeriodRequest, ClassPeriodNaturalKey);
        _schoolResult = MapUniqueViolation(resolver, schoolRequest, EducationOrganizationIdentityNaturalKey);
        _gradeResult = MapUniqueViolation(resolver, gradeRequest, GradeNaturalKey);
    }

    [Test]
    public void It_classifies_the_ClassPeriod_natural_key_violation_as_a_root_natural_key_conflict()
    {
        _classPeriodResolution
            .Should()
            .Be(new RelationalWriteConstraintResolution.RootNaturalKeyUnique(ClassPeriodNaturalKey));
    }

    [Test]
    public void It_reports_the_ClassPeriod_put_identity_conflict_values()
    {
        var conflict = _classPeriodResult
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureIdentityConflict>()
            .Subject;

        conflict.ResourceName.Should().Be(new ResourceName("ClassPeriod"));
        conflict
            .DuplicateIdentityValues.Should()
            .Equal(
                new KeyValuePair<string, string>("classPeriodName", "First"),
                new KeyValuePair<string, string>("schoolId", "255901001")
            );
    }

    [Test]
    public void It_classifies_the_School_abstract_identity_violation_as_a_root_natural_key_conflict()
    {
        _schoolResolution
            .Should()
            .Be(
                new RelationalWriteConstraintResolution.RootNaturalKeyUnique(
                    EducationOrganizationIdentityNaturalKey
                )
            );
    }

    [Test]
    public void It_reports_the_School_post_identity_conflict_values()
    {
        var conflict = _schoolResult
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureIdentityConflict>()
            .Subject;

        conflict.ResourceName.Should().Be(new ResourceName("School"));
        conflict
            .DuplicateIdentityValues.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new KeyValuePair<string, string>("schoolId", "155901"));
    }

    [Test]
    public void It_classifies_the_Grade_natural_key_violation_as_a_root_natural_key_conflict()
    {
        _gradeResolution
            .Should()
            .Be(new RelationalWriteConstraintResolution.RootNaturalKeyUnique(GradeNaturalKey));
    }

    [Test]
    public void It_reports_the_Grade_identity_conflict_values_in_identity_order()
    {
        var conflict = _gradeResult
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureIdentityConflict>()
            .Subject;

        conflict.ResourceName.Should().Be(new ResourceName("Grade"));
        conflict
            .DuplicateIdentityValues.Should()
            .Equal(
                new KeyValuePair<string, string>("gradeTypeDescriptor", GradeTypeDescriptorUri),
                new KeyValuePair<string, string>("gradingPeriodDescriptor", GradingPeriodDescriptorUri),
                new KeyValuePair<string, string>("gradingPeriodName", "GP1"),
                new KeyValuePair<string, string>("schoolId", "255901001"),
                new KeyValuePair<string, string>("schoolYear", "2022"),
                new KeyValuePair<string, string>("beginDate", "2021-08-23"),
                new KeyValuePair<string, string>("localCourseCode", "ALG-1"),
                new KeyValuePair<string, string>("schoolId", "255901001"),
                new KeyValuePair<string, string>("schoolYear", "2022"),
                new KeyValuePair<string, string>("sectionIdentifier", "ALG-1-01"),
                new KeyValuePair<string, string>("sessionName", "2021-2022 Fall Semester"),
                new KeyValuePair<string, string>("studentUniqueId", "604822")
            );
    }

    // The every-resource check that root natural-key classification depends on: the own-key probe's distinct
    // columns must equal the emitted UX_<R>_NK columns, in order, without reading trigger metadata.
    [Test]
    public void It_classifies_every_root_natural_key_constraint_as_a_root_natural_key_conflict()
    {
        _resourcesSwept.Should().BePositive().And.Be(_ownNaturalKeyProbeCount);
        _naturalKeyClassificationMismatches.Should().BeEmpty();
    }

    private void SweepRootNaturalKeyClassification(MappingSet mappingSet)
    {
        var resolver = new RelationalWriteConstraintResolver();
        var relationalResources = mappingSet
            .Model.ConcreteResourcesInNameOrder.Where(resource =>
                resource.StorageKind is ResourceStorageKind.RelationalTables
            )
            .Select(resource => resource.RelationalModel.Resource)
            .ToArray();

        foreach (var resource in relationalResources)
        {
            var writePlan = mappingSet.WritePlansByResource[resource];
            var referenceResolutionRequest = new ReferenceResolverRequest(
                mappingSet,
                resource,
                [],
                DescriptorReferences: []
            );
            string[] naturalKeyConstraints =
            [
                .. writePlan
                    .Model.Root.Constraints.OfType<TableConstraint.Unique>()
                    .Where(unique =>
                        resolver.Resolve(
                            new RelationalWriteConstraintResolutionRequest(
                                writePlan,
                                referenceResolutionRequest,
                                new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                                    unique.Name
                                )
                            )
                        ) is RelationalWriteConstraintResolution.RootNaturalKeyUnique
                    )
                    .Select(unique => unique.Name),
            ];

            if (naturalKeyConstraints.Length != 1)
            {
                _naturalKeyClassificationMismatches.Add(
                    $"{resource.ProjectName}.{resource.ResourceName}: "
                        + $"[{string.Join(", ", naturalKeyConstraints)}]"
                );
            }
        }

        _resourcesSwept = relationalResources.Length;
        _ownNaturalKeyProbeCount = mappingSet.OwnNaturalKeyProbesByResource.Count;
    }

    private static RelationalWriteConstraintResolution Resolve(
        RelationalWriteConstraintResolver resolver,
        RelationalWriteExecutorRequest request,
        string constraintName
    ) =>
        resolver.Resolve(
            new RelationalWriteConstraintResolutionRequest(
                request.WritePlan,
                request.ReferenceResolutionRequest,
                new RelationalWriteExceptionClassification.UniqueConstraintViolation(constraintName)
            )
        );

    private static RelationalWriteExecutorResult? MapUniqueViolation(
        RelationalWriteConstraintResolver resolver,
        RelationalWriteExecutorRequest request,
        string constraintName
    )
    {
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            new UniqueViolationClassifier(constraintName),
            resolver
        );

        mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        return result;
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteOperationKind operationKind,
        string body
    )
    {
        var writePlan = mappingSet.WritePlansByResource[resource];
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var updateDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(updateDocumentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                    createDocumentUuid
                ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse(body)!,
            allowIdentityUpdates: false,
            new TraceId("ds52-identity-conflict-test"),
            new ReferenceResolverRequest(mappingSet, resource, [], DescriptorReferences: []),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    private sealed class UniqueViolationClassifier(string constraintName)
        : IRelationalWriteExceptionClassifier
    {
        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            classification = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                constraintName
            );
            return true;
        }

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => true;

        public bool IsTransientFailure(DbException exception) => false;
    }

    private sealed class StubDbException(string message) : DbException(message);
}
