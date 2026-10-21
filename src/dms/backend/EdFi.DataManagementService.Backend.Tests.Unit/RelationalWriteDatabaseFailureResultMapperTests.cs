// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteDatabaseFailureResultMapper
{
    private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
    private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
    private CapturingLogger _logger = null!;
    private RelationalWriteDatabaseFailureResultMapper _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
        _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
        _logger = new CapturingLogger();
        _sut = new RelationalWriteDatabaseFailureResultMapper(
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _logger
        );
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public void It_maps_unresolved_unique_violations_to_non_unique_values_and_logs_only_the_constraint_name(
        RelationalWriteOperationKind operationKind
    )
    {
        var exception = new StubDbException(
            "Cannot insert duplicate key in object 'edfi.SchoolAddress'. The duplicate key value is (Austin)."
        );
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation("UX_SchoolAddress");
        _writeConstraintResolver.ResolutionToReturn = new RelationalWriteConstraintResolution.Unresolved(
            "UX_SchoolAddress"
        );

        var isMapped = _sut.TryBuild(CreateRequest(operationKind), exception, out var result);

        isMapped.Should().BeTrue();
        result.Should().BeEquivalentTo(NonUniqueValuesResult(operationKind));
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
        entry.Message.Should().Contain("UX_SchoolAddress").And.NotContain("Austin");
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public void It_maps_unique_violations_without_a_parseable_constraint_name_to_non_unique_values(
        RelationalWriteOperationKind operationKind
    )
    {
        var exception = new StubDbException("localized unique violation for the value (Austin)");
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;
        _writeExceptionClassifier.IsUniqueConstraintViolationToReturn = true;

        var isMapped = _sut.TryBuild(CreateRequest(operationKind), exception, out var result);

        isMapped.Should().BeTrue();
        result.Should().BeEquivalentTo(NonUniqueValuesResult(operationKind));
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
        entry.Message.Should().Contain("(not parsed)").And.NotContain("Austin");
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public void It_keeps_unrecognized_non_unique_failures_as_unknown_failures(
        RelationalWriteOperationKind operationKind
    )
    {
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;

        _sut.TryBuild(CreateRequest(operationKind), new StubDbException("provider failure"), out var result);

        result
            .Should()
            .BeEquivalentTo(UnknownFailureResult(operationKind, "an unrecognized final write failure"));
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public void It_keeps_unresolved_foreign_key_violations_as_unknown_failures(
        RelationalWriteOperationKind operationKind
    )
    {
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation("FK_Unknown");
        _writeConstraintResolver.ResolutionToReturn = new RelationalWriteConstraintResolution.Unresolved(
            "FK_Unknown"
        );

        _sut.TryBuild(CreateRequest(operationKind), new StubDbException("fk violation"), out var result);

        result
            .Should()
            .BeEquivalentTo(UnknownFailureResult(operationKind, "a non-user-facing constraint violation"));
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public void It_prefers_the_root_natural_key_identity_conflict_over_the_non_unique_values_fallback(
        RelationalWriteOperationKind operationKind
    )
    {
        ConfigureRootNaturalKeyUniqueViolation();
        _writeExceptionClassifier.IsUniqueConstraintViolationToReturn = true;

        _sut.TryBuild(CreateRequest(operationKind), new StubDbException("unique violation"), out var result);

        KeyValuePair<string, string>[] duplicateIdentityValues = [new("schoolId", "255901")];
        result
            .Should()
            .BeEquivalentTo(
                operationKind == RelationalWriteOperationKind.Post
                    ? (RelationalWriteExecutorResult)
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureIdentityConflict(
                                new ResourceName("School"),
                                duplicateIdentityValues
                            )
                        )
                    : new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureIdentityConflict(
                            new ResourceName("School"),
                            duplicateIdentityValues
                        )
                    )
            );
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public void It_prefers_a_transient_classification_over_a_unique_violation(
        RelationalWriteOperationKind operationKind
    )
    {
        _writeExceptionClassifier.IsTransientFailureToReturn = true;
        _writeExceptionClassifier.IsUniqueConstraintViolationToReturn = true;
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation("UX_SchoolAddress");

        _sut.TryBuild(CreateRequest(operationKind), new StubDbException("deadlock"), out var result);

        result
            .Should()
            .BeEquivalentTo(
                operationKind == RelationalWriteOperationKind.Post
                    ? (RelationalWriteExecutorResult)
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict()
                        )
                    : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureWriteConflict())
            );
    }

    private static RelationalWriteExecutorResult NonUniqueValuesResult(
        RelationalWriteOperationKind operationKind
    ) =>
        operationKind == RelationalWriteOperationKind.Post
            ? new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureNonUniqueValues())
            : new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNonUniqueValues());

    private static RelationalWriteExecutorResult UnknownFailureResult(
        RelationalWriteOperationKind operationKind,
        string reason
    )
    {
        var message =
            $"Relational write failed for resource 'Ed-Fi.School' because the database reported {reason}.";

        return operationKind == RelationalWriteOperationKind.Post
            ? new RelationalWriteExecutorResult.Upsert(new UpsertResult.UnknownFailure(message))
            : new RelationalWriteExecutorResult.Update(new UpdateResult.UnknownFailure(message));
    }

    [Test]
    public void It_maps_late_document_reference_foreign_key_violations_to_missing_reference_failures()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference("$.schoolReference");
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference]
        );
        var exception = new StubDbException("foreign key violation");
        var violation = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
            "FK_School_SchoolReference"
        );
        _writeExceptionClassifier.ClassificationToReturn = violation;
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                violation.ConstraintName,
                RelationalWriteReferenceKind.Document,
                new JsonPathExpression(
                    "$.schoolReference",
                    [new JsonPathSegment.Property("schoolReference")]
                ),
                new QualifiedResourceName("Ed-Fi", "School")
            );

        var isMapped = _sut.TryBuild(request, exception, out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureReference(
                        [
                            DocumentReferenceFailure.From(
                                documentReference,
                                DocumentReferenceFailureReason.Missing
                            ),
                        ],
                        []
                    )
                )
            );
        _writeExceptionClassifier.CapturedException.Should().BeSameAs(exception);
        _writeConstraintResolver.CapturedRequest!.Violation.Should().BeSameAs(violation);
        _writeConstraintResolver
            .CapturedRequest!.ReferenceResolutionRequest.Should()
            .BeSameAs(request.ReferenceResolutionRequest);
    }

    [Test]
    public void It_does_not_fabricate_reference_failures_without_a_matching_request_reference()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        var violation = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
            "FK_School_SchoolReference"
        );
        _writeExceptionClassifier.ClassificationToReturn = violation;
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                violation.ConstraintName,
                RelationalWriteReferenceKind.Document,
                new JsonPathExpression(
                    "$.schoolReference",
                    [new JsonPathSegment.Property("schoolReference")]
                ),
                new QualifiedResourceName("Ed-Fi", "School")
            );

        var isMapped = _sut.TryBuild(request, new StubDbException("foreign key violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UnknownFailure(
                        "Relational write failed for resource 'Ed-Fi.School' because the database reported a non-user-facing constraint violation."
                    )
                )
            );
    }

    [Test]
    public void It_maps_unrecognized_final_database_write_failures_to_unknown_failures()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;

        var isMapped = _sut.TryBuild(request, new StubDbException("provider write failure"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UnknownFailure(
                        "Relational write failed for resource 'Ed-Fi.School' because the database reported an unrecognized final write failure."
                    )
                )
            );
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
    }

    [Test]
    public void It_maps_transient_post_database_failures_to_retryable_write_conflicts()
    {
        var exception = new StubDbException("deadlock");
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var isMapped = _sut.TryBuild(
            CreateRequest(RelationalWriteOperationKind.Post),
            exception,
            out var result
        );

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
        _writeExceptionClassifier.CapturedTransientException.Should().BeSameAs(exception);
        _writeExceptionClassifier.CapturedException.Should().BeNull();
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
    }

    [Test]
    public void It_maps_transient_put_database_failures_to_retryable_write_conflicts()
    {
        var exception = new StubDbException("lock timeout");
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var isMapped = _sut.TryBuild(
            CreateRequest(RelationalWriteOperationKind.Put),
            exception,
            out var result
        );

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureWriteConflict())
            );
        _writeExceptionClassifier.CapturedTransientException.Should().BeSameAs(exception);
        _writeExceptionClassifier.CapturedException.Should().BeNull();
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
    }

    [Test]
    public void It_leaves_unclassified_database_exceptions_unmapped()
    {
        var exception = new StubDbException("provider failure");

        var isMapped = _sut.TryBuild(
            CreateRequest(RelationalWriteOperationKind.Put),
            exception,
            out var result
        );

        isMapped.Should().BeFalse();
        result.Should().BeNull();
        _writeExceptionClassifier.CapturedTransientException.Should().BeSameAs(exception);
        _writeExceptionClassifier.CapturedException.Should().BeSameAs(exception);
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
    }

    [Test]
    public void It_maps_root_create_races_to_identity_conflicts_without_a_precondition()
    {
        ConfigureRootNaturalKeyUniqueViolation();
        var request = CreateRequest(RelationalWriteOperationKind.Post);

        var isMapped = _sut.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "255901")]
                    )
                ),
                options => options.RespectingRuntimeTypes()
            );
    }

    [Test]
    public void It_maps_root_create_races_to_identity_conflicts_when_an_IfMatch_precondition_is_present()
    {
        ConfigureRootNaturalKeyUniqueViolation();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfMatch("\"5-client-tag\"")
        );

        var isMapped = _sut.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "255901")]
                    )
                ),
                options => options.RespectingRuntimeTypes()
            );
    }

    [Test]
    public void It_reports_a_missing_own_natural_key_probe_for_an_identity_conflict()
    {
        ConfigureRootNaturalKeyUniqueViolation();
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var requestWithoutProbe = request with
        {
            MappingSet = request.MappingSet with
            {
                OwnNaturalKeyProbesByResource = new Dictionary<QualifiedResourceName, OwnNaturalKeyProbe>(),
            },
        };

        var act = () => _sut.TryBuild(requestWithoutProbe, new StubDbException("unique violation"), out _);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Mapping set 'schema-hash/Pgsql/v1' is missing the own natural-key probe for resource 'Ed-Fi.School'."
            );
    }

    [Test]
    public void It_maps_abstract_education_organization_identity_unique_violations_to_identity_conflicts()
    {
        // Drive the real resolver through the mapper so this covers mapping
        // UX_EducationOrganizationIdentity_NK to the literal 409 identity-conflict result, with the
        // duplicate values taken from the concrete School request body rather than the abstract column.
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                "UX_EducationOrganizationIdentity_NK"
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );
        var request = CreateSchoolAbstractIdentityRequest(RelationalWriteOperationKind.Post);

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "155901")]
                    )
                ),
                options => options.RespectingRuntimeTypes()
            );
    }

    [Test]
    public void It_maps_abstract_education_organization_identity_unique_violations_on_put_to_identity_conflicts()
    {
        // The same abstract-identity natural-key violation on a PUT must surface the Update identity-conflict
        // result, with the duplicate values still taken from the concrete School request body.
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                "UX_EducationOrganizationIdentity_NK"
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );
        var request = CreateSchoolAbstractIdentityRequest(RelationalWriteOperationKind.Put);

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "155901")]
                    )
                ),
                options => options.RespectingRuntimeTypes()
            );
    }

    [Test]
    public void It_reports_the_writing_subclass_identity_for_abstract_education_organization_identity_conflicts()
    {
        // A non-School EducationOrganization subclass colliding on the shared abstract identity table must
        // report its own identity element (localEducationAgencyId), proving the duplicate values are taken from
        // the concrete resource's own natural-key probe rather than a hard-coded School identity.
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                "UX_EducationOrganizationIdentity_NK"
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );
        var request = CreateLocalEducationAgencyAbstractIdentityRequest();

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("LocalEducationAgency"),
                        [new KeyValuePair<string, string>("localEducationAgencyId", "155901")]
                    )
                ),
                options => options.RespectingRuntimeTypes()
            );
    }

    [Test]
    public void It_resolves_nested_reference_backed_identity_values_for_abstract_identity_conflicts()
    {
        // A reference-backed concrete identity element (a GeneralStudentProgramAssociation member's
        // $.programReference.educationOrganizationId) is addressed by a multi-segment JSONPath. The mapper
        // must read its duplicate value by walking the nested request body, and report it under its full
        // property path (programReference.educationOrganizationId), proving the abstract-identity conflict
        // path is not limited to top-level scalar identities and keeps the reference that owns the value.
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                AbstractIdentitySchoolTestData.GeneralStudentProgramAssociationNaturalKeyConstraintName
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );
        var request = CreateReferenceBackedSubclassAbstractIdentityRequest();

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("StudentProgramAssociation"),
                        [
                            new KeyValuePair<string, string>(
                                "programReference.educationOrganizationId",
                                "255901001"
                            ),
                        ]
                    )
                ),
                options => options.RespectingRuntimeTypes()
            );
    }

    [TestFixture("PK_ReferentialIdentity")]
    [TestFixture("PK_EducationOrganizationIdToEducationOrganizationId")]
    public class Given_A_School_Identity_Conflict_Reported_By_An_Identity_Maintenance_Primary_Key(
        string constraintName
    )
    {
        private RelationalWriteExecutorResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // On SQL Server the School AFTER triggers fire in an undefined order, so a duplicate education
            // organization id can be reported by the referential identity or auth hierarchy primary key instead
            // of UX_EducationOrganizationIdentity_NK. Drive the real resolver so the client still gets the
            // identity conflict for the concrete School body.
            var classifier = new RecordingRelationalWriteExceptionClassifier
            {
                ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                    constraintName
                ),
            };
            var mapper = new RelationalWriteDatabaseFailureResultMapper(
                classifier,
                new RelationalWriteConstraintResolver()
            );

            mapper.TryBuild(
                CreateSchoolAbstractIdentityRequest(RelationalWriteOperationKind.Post),
                new StubDbException("unique violation"),
                out var result
            );
            _result = result!;
        }

        [Test]
        public void It_reports_an_identity_conflict_with_the_school_identity()
        {
            _result
                .Should()
                .BeEquivalentTo(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureIdentityConflict(
                            new ResourceName("School"),
                            [new KeyValuePair<string, string>("schoolId", "155901")]
                        )
                    ),
                    options => options.RespectingRuntimeTypes()
                );
        }
    }

    [TestFixture(RelationalWriteOperationKind.Post)]
    [TestFixture(RelationalWriteOperationKind.Put)]
    public class Given_Repeated_Identity_Leaf_Names_With_Different_Values(
        RelationalWriteOperationKind operationKind
    )
    {
        private bool _isMapped;
        private RelationalWriteExecutorResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Two reference-backed identity elements end in the same property (educationOrganizationId) and
            // carry different values. Each must be reported under its own reference path so the two values
            // cannot be confused, in referential-identity metadata order (which the test data makes
            // non-alphabetical).
            var request = CreateRepeatedIdentityLeafRequest(
                operationKind,
                """{"educationOrganizationReference":{"educationOrganizationId":255901001},"programReference":{"educationOrganizationId":255901002},"beginDate":"2024-08-01"}"""
            );

            _isMapped = MapRepeatedIdentityLeafConflict(request, out var result);
            _result = result!;
        }

        [Test]
        public void It_maps_the_database_failure()
        {
            _isMapped.Should().BeTrue();
        }

        [Test]
        public void It_reports_the_concrete_resource_name()
        {
            GetRepeatedIdentityLeafConflict(_result, operationKind)
                .ResourceName.Should()
                .Be(new ResourceName("StudentProgramAssociation"));
        }

        [Test]
        public void It_qualifies_each_repeated_leaf_with_its_reference_path_in_metadata_order()
        {
            GetRepeatedIdentityLeafConflict(_result, operationKind)
                .DuplicateIdentityValues.Select(static pair => (pair.Key, pair.Value))
                .Should()
                .Equal(
                    ("programReference.educationOrganizationId", "255901002"),
                    ("educationOrganizationReference.educationOrganizationId", "255901001")
                );
        }
    }

    [TestFixture]
    public class Given_A_Repeated_Identity_Leaf_Value_Missing_From_The_Request_Body
    {
        private bool _isMapped;
        private RelationalWriteExecutorResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // A value absent from the selected body is still omitted rather than reported empty; the value that
            // is present keeps its full reference path.
            var request = CreateRepeatedIdentityLeafRequest(
                RelationalWriteOperationKind.Post,
                """{"educationOrganizationReference":{"educationOrganizationId":255901001},"beginDate":"2024-08-01"}"""
            );

            _isMapped = MapRepeatedIdentityLeafConflict(request, out var result);
            _result = result!;
        }

        [Test]
        public void It_maps_the_database_failure()
        {
            _isMapped.Should().BeTrue();
        }

        [Test]
        public void It_omits_the_missing_value_and_qualifies_the_rest()
        {
            GetRepeatedIdentityLeafConflict(_result, RelationalWriteOperationKind.Post)
                .DuplicateIdentityValues.Select(static pair => (pair.Key, pair.Value))
                .Should()
                .Equal(("educationOrganizationReference.educationOrganizationId", "255901001"));
        }
    }

    // Drives the real resolver through the mapper for the repeated-leaf model.
    private static bool MapRepeatedIdentityLeafConflict(
        RelationalWriteExecutorRequest request,
        out RelationalWriteExecutorResult? result
    )
    {
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                AbstractIdentitySchoolTestData.GeneralStudentProgramAssociationNaturalKeyConstraintName
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );

        return mapper.TryBuild(request, new StubDbException("unique violation"), out result);
    }

    // Returns the identity-conflict payload for either operation kind, failing the test if the result is not an
    // identity conflict.
    private static (
        ResourceName ResourceName,
        IEnumerable<KeyValuePair<string, string>> DuplicateIdentityValues
    ) GetRepeatedIdentityLeafConflict(
        RelationalWriteExecutorResult? result,
        RelationalWriteOperationKind operationKind
    )
    {
        if (operationKind == RelationalWriteOperationKind.Put)
        {
            var updateConflict = result
                .Should()
                .BeOfType<RelationalWriteExecutorResult.Update>()
                .Which.Result.Should()
                .BeOfType<UpdateResult.UpdateFailureIdentityConflict>()
                .Which;
            return (updateConflict.ResourceName, updateConflict.DuplicateIdentityValues);
        }

        var upsertConflict = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureIdentityConflict>()
            .Which;
        return (upsertConflict.ResourceName, upsertConflict.DuplicateIdentityValues);
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        RelationalWriteOperationKind operationKind,
        IReadOnlyList<DocumentReference>? documentReferences = null,
        WritePrecondition? writePrecondition = null
    )
    {
        var rootWritePlan = Given_Relational_Write_Guarded_No_Op.CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(rootWritePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootWritePlan]);
        var mappingSet = CreateMappingSet(resourceModel, resourceWritePlan);
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var updateDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(updateDocumentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                    createDocumentUuid
                ),
            resourceWritePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            allowIdentityUpdates: false,
            new TraceId("database-failure-result-mapper-test"),
            new ReferenceResolverRequest(
                mappingSet,
                resourceWritePlan.Model.Resource,
                documentReferences ?? [],
                DescriptorReferences: []
            ),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid),
            writePrecondition: writePrecondition
        );
    }

    private void ConfigureRootNaturalKeyUniqueViolation()
    {
        const string constraintName = "UK_School_NaturalKey";
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(constraintName);
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RootNaturalKeyUnique(constraintName);
    }

    private static RelationalResourceModel CreateRelationalResourceModel(DbTableModel rootTable)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static MappingSet CreateMappingSet(
        RelationalResourceModel resourceModel,
        ResourceWritePlan resourceWritePlan
    )
    {
        var resource = resourceModel.Resource;
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
        var identityColumn = resourceModel.Root.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.schoolId"
        );
        var identityJsonPath =
            identityColumn.SourceJsonPath
            ?? throw new InvalidOperationException("Expected the School identity source path.");

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        resourceKey,
                        ResourceStorageKind.RelationalTables,
                        resourceModel
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = resourceWritePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        )
        {
            OwnNaturalKeyProbesByResource = new Dictionary<QualifiedResourceName, OwnNaturalKeyProbe>
            {
                [resource] = new(
                    resource,
                    resourceModel.Root.Table,
                    [
                        new NaturalKeyProbeKeyColumn(
                            identityJsonPath,
                            identityColumn.ColumnName,
                            new NaturalKeyProbeKeyBinding.Scalar(
                                identityColumn.ScalarType
                                    ?? throw new InvalidOperationException(
                                        "Expected the School identity column to have a scalar type."
                                    )
                            )
                        ),
                    ],
                    new DbColumnName("DocumentId")
                ),
            },
        };
    }

    // Dedicated, isolated builder for the abstract-identity end-to-end tests, parameterized by operation kind
    // so both the POST and PUT identity-conflict paths are covered. Shares its table shapes with the
    // constraint-resolver tests via AbstractIdentitySchoolTestData.
    private static RelationalWriteExecutorRequest CreateSchoolAbstractIdentityRequest(
        RelationalWriteOperationKind operationKind
    )
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel();
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var updateDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(updateDocumentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                    createDocumentUuid
                ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"schoolId":155901,"nameOfInstitution":"School Test"}""")!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    // Builds a POST request for a non-School EducationOrganization subclass (LocalEducationAgency) so the
    // abstract-identity conflict path is exercised against a different concrete identity element.
    private static RelationalWriteExecutorRequest CreateLocalEducationAgencyAbstractIdentityRequest()
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildLocalEducationAgencyWriteModel();
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                createDocumentUuid
            ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"localEducationAgencyId":155901,"nameOfInstitution":"Grand Bend ISD"}""")!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-lea-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    // Builds a POST request for a concrete GeneralStudentProgramAssociation member whose identity element is
    // reference-backed, so the mapper's nested-path identity-value resolution is exercised through the
    // abstract-identity conflict path. The body nests the duplicate value under $.programReference.
    private static RelationalWriteExecutorRequest CreateReferenceBackedSubclassAbstractIdentityRequest()
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildReferenceBackedSubclassWriteModel();
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                createDocumentUuid
            ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse(
                """{"programReference":{"educationOrganizationId":255901001},"beginDate":"2024-08-01"}"""
            )!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-reference-backed-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    // Builds a POST or PUT request for the StudentProgramAssociation model whose two reference-backed identity
    // elements share the trailing property educationOrganizationId, with the supplied request body.
    private static RelationalWriteExecutorRequest CreateRepeatedIdentityLeafRequest(
        RelationalWriteOperationKind operationKind,
        string requestBody
    )
    {
        var (writePlan, mappingSet) =
            AbstractIdentitySchoolTestData.BuildRepeatedIdentityLeafSubclassWriteModel();
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
            JsonNode.Parse(requestBody)!,
            allowIdentityUpdates: false,
            new TraceId("repeated-identity-leaf-conflict-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    internal sealed class RecordingRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public DbException? CapturedException { get; private set; }

        public DbException? CapturedTransientException { get; private set; }

        public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

        public bool IsTransientFailureToReturn { get; set; }

        public bool IsUniqueConstraintViolationToReturn { get; set; }

        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            CapturedException = exception;
            classification = ClassificationToReturn;
            return classification is not null;
        }

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => IsUniqueConstraintViolationToReturn;

        public bool IsTransientFailure(DbException exception)
        {
            CapturedTransientException = exception;
            return IsTransientFailureToReturn;
        }
    }

    private sealed class RecordingRelationalWriteConstraintResolver : IRelationalWriteConstraintResolver
    {
        public int ResolveCallCount { get; private set; }

        public RelationalWriteConstraintResolutionRequest? CapturedRequest { get; private set; }

        public RelationalWriteConstraintResolution ResolutionToReturn { get; set; } =
            new RelationalWriteConstraintResolution.Unresolved("UNCONFIGURED");

        public RelationalWriteConstraintResolution Resolve(RelationalWriteConstraintResolutionRequest request)
        {
            ResolveCallCount++;
            CapturedRequest = request;
            return ResolutionToReturn;
        }
    }

    internal sealed class StubDbException(string message) : DbException(message);

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}

/// <summary>
/// A root natural-key violation on a resource whose semantic identity order (sessionName, termDescriptor,
/// schoolReference.schoolId) differs from ordinal path order, ordinal column-name order, and the root table's
/// column order. Every authoritative identity list is path-sorted, so only a permuted fixture can catch a
/// derivation that sorts. The mapping set carries no trigger metadata.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Root_Natural_Key_Violation_With_A_Non_Ordinal_Identity
{
    private const string NaturalKeyConstraintName = "UX_Session_NK";
    private const string TermDescriptorUri = "uri://ed-fi.org/TermDescriptor#Fall Semester";

    private static readonly QualifiedResourceName SessionResource = new("Ed-Fi", "Session");
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName TermDescriptorResource = new("Ed-Fi", "TermDescriptor");

    private RelationalWriteExecutorResult? _fullBodyResult;
    private RelationalWriteExecutorResult? _bodyWithoutTermDescriptorResult;

    [SetUp]
    public void Setup()
    {
        var sut = new RelationalWriteDatabaseFailureResultMapper(
            new Given_RelationalWriteDatabaseFailureResultMapper.RecordingRelationalWriteExceptionClassifier
            {
                ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                    NaturalKeyConstraintName
                ),
            },
            new RelationalWriteConstraintResolver()
        );

        sut.TryBuild(
            CreateRequest(
                $$$"""{"sessionName":"Fall","termDescriptor":"{{{TermDescriptorUri}}}","schoolReference":{"schoolId":255901}}"""
            ),
            new Given_RelationalWriteDatabaseFailureResultMapper.StubDbException("unique violation"),
            out _fullBodyResult
        );
        sut.TryBuild(
            CreateRequest("""{"sessionName":"Fall","schoolReference":{"schoolId":255901}}"""),
            new Given_RelationalWriteDatabaseFailureResultMapper.StubDbException("unique violation"),
            out _bodyWithoutTermDescriptorResult
        );
    }

    [Test]
    public void It_classifies_the_violation_as_a_root_natural_key_identity_conflict()
    {
        _fullBodyResult
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureIdentityConflict>()
            .Which.ResourceName.Should()
            .Be(new ResourceName("Session"));
    }

    [Test]
    public void It_reports_the_duplicate_identity_values_in_semantic_order()
    {
        DuplicateIdentityValuesOf(_fullBodyResult)
            .Should()
            .Equal(
                new KeyValuePair<string, string>("sessionName", "Fall"),
                new KeyValuePair<string, string>("termDescriptor", TermDescriptorUri),
                new KeyValuePair<string, string>("schoolReference.schoolId", "255901")
            );
    }

    [Test]
    public void It_omits_an_identity_value_the_body_does_not_carry()
    {
        DuplicateIdentityValuesOf(_bodyWithoutTermDescriptorResult)
            .Should()
            .Equal(
                new KeyValuePair<string, string>("sessionName", "Fall"),
                new KeyValuePair<string, string>("schoolReference.schoolId", "255901")
            );
    }

    private static IEnumerable<KeyValuePair<string, string>> DuplicateIdentityValuesOf(
        RelationalWriteExecutorResult? result
    ) =>
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureIdentityConflict>()
            .Which.DuplicateIdentityValues;

    private static RelationalWriteExecutorRequest CreateRequest(string body)
    {
        var (writePlan, mappingSet) = CreateWriteModel();
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                createDocumentUuid
            ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse(body)!,
            allowIdentityUpdates: false,
            new TraceId("non-ordinal-identity-conflict-test"),
            new ReferenceResolverRequest(mappingSet, SessionResource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    private static (ResourceWritePlan WritePlan, MappingSet MappingSet) CreateWriteModel()
    {
        var sessionNamePath = new JsonPathExpression(
            "$.sessionName",
            [new JsonPathSegment.Property("sessionName")]
        );
        var termDescriptorPath = new JsonPathExpression(
            "$.termDescriptor",
            [new JsonPathSegment.Property("termDescriptor")]
        );
        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );
        var schoolIdPath = new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );

        // The table declares the identity columns in a third order: descriptor, reference, then scalar.
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Session"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Session",
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
                    new DbColumnName("Term_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    termDescriptorPath,
                    TermDescriptorResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    schoolReferencePath,
                    SchoolResource,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    schoolIdPath,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SessionName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 60),
                    false,
                    sessionNamePath,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    NaturalKeyConstraintName,
                    [
                        new DbColumnName("SessionName"),
                        new DbColumnName("Term_DescriptorId"),
                        new DbColumnName("School_DocumentId"),
                    ]
                ),
            ]
        );

        var schoolReferenceBinding = new DocumentReferenceBinding(
            true,
            schoolReferencePath,
            rootTable.Table,
            new DbColumnName("School_DocumentId"),
            SchoolResource,
            [
                new ReferenceIdentityBinding(
                    new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                    schoolIdPath,
                    new DbColumnName("SchoolReference_SchoolId")
                ),
            ],
            IsRequired: true
        );

        var resourceModel = new RelationalResourceModel(
            SessionResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [schoolReferenceBinding],
            [
                new DescriptorEdgeSource(
                    true,
                    termDescriptorPath,
                    rootTable.Table,
                    new DbColumnName("Term_DescriptorId"),
                    TermDescriptorResource,
                    IsRequired: true
                ),
            ]
        );

        var writePlan = new ResourceWritePlan(resourceModel, []);

        var sessionKey = new ResourceKeyEntry(1, SessionResource, "1.0.0", false);
        var schoolKey = new ResourceKeyEntry(2, SchoolResource, "1.0.0", false);
        var termDescriptorKey = new ResourceKeyEntry(3, TermDescriptorResource, "1.0.0", false);

        var mappingSet = new MappingSet(
            new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    "1.0",
                    "v1",
                    "schema-hash",
                    3,
                    [1, 2, 3],
                    [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                    [sessionKey, schoolKey, termDescriptorKey]
                ),
                SqlDialect.Pgsql,
                [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
                [new ConcreteResourceModel(sessionKey, ResourceStorageKind.RelationalTables, resourceModel)],
                [],
                [],
                [],
                []
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan> { [SessionResource] = writePlan },
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>
            {
                [SessionResource] = sessionKey.ResourceKeyId,
                [SchoolResource] = schoolKey.ResourceKeyId,
                [TermDescriptorResource] = termDescriptorKey.ResourceKeyId,
            },
            new Dictionary<short, ResourceKeyEntry>
            {
                [sessionKey.ResourceKeyId] = sessionKey,
                [schoolKey.ResourceKeyId] = schoolKey,
                [termDescriptorKey.ResourceKeyId] = termDescriptorKey,
            },
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        )
        {
            OwnNaturalKeyProbesByResource = new Dictionary<QualifiedResourceName, OwnNaturalKeyProbe>
            {
                [SessionResource] = new(
                    SessionResource,
                    rootTable.Table,
                    [
                        new NaturalKeyProbeKeyColumn(
                            sessionNamePath,
                            new DbColumnName("SessionName"),
                            new NaturalKeyProbeKeyBinding.Scalar(
                                new RelationalScalarType(ScalarKind.String, 60)
                            )
                        ),
                        new NaturalKeyProbeKeyColumn(
                            termDescriptorPath,
                            new DbColumnName("Term_DescriptorId"),
                            new NaturalKeyProbeKeyBinding.Descriptor(TermDescriptorResource)
                        ),
                        new NaturalKeyProbeKeyColumn(
                            schoolIdPath,
                            new DbColumnName("School_DocumentId"),
                            new NaturalKeyProbeKeyBinding.DocumentReference(0)
                        ),
                    ],
                    new DbColumnName("DocumentId")
                ),
            },
        };

        return (writePlan, mappingSet);
    }
}
