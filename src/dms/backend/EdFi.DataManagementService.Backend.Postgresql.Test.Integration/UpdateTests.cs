// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

public class UpdateTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    private static TraceId traceId = new("");

    [TestFixture]
    public class Given_An_Update_Of_A_Nonexistent_Document : UpdateTests
    {
        private UpdateResult? _updateResult;
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                Guid.NewGuid(),
                Guid.NewGuid(),
                _edFiDocString
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_be_a_failed_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        }

        [Test]
        public async Task It_should_not_be_found_updated_by_get()
        {
            var getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
            getResult.Should().BeOfType<GetResult.GetFailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_An_Existing_Document : UpdateTests
    {
        private UpdateResult? _updateResult;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString1
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString2
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_found_updated_by_get()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString2);
            (_getResult! as GetResult.GetSuccess)!.LastModifiedTraceId.Should().Be(traceId.Value);
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_An_Existing_Document_With_Different_ReferentialId : UpdateTests
    {
        private UpdateResult? _updateResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid1 = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid2 = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid1,
                _edFiDocString1
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid2,
                _edFiDocString2
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_not_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }

        [Test]
        public async Task It_should_not_have_changed_the_document()
        {
            var getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
            (getResult as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Contain("\"abc\":1");
            (getResult! as GetResult.GetSuccess)!.LastModifiedTraceId.Should().Be(traceId.Value);
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_An_Existing_Document_With_Different_ReferentialId_Allow_Identity_Update
        : UpdateTests
    {
        private UpdateResult? _updateResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid1 = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid2 = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        private static readonly string _referencingResourceName = "ReferencingResource";

        [SetUp]
        public async Task Setup()
        {
            // Create referenced document
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid1,
                _edFiDocString1,
                allowIdentityUpdates: true
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId);

            // Create referencing document referencing the first
            _ = CreateUpsertRequest(
                _referencingResourceName,
                Guid.NewGuid(),
                Guid.NewGuid(),
                """{"xyz":1}""",
                CreateDocumentReferences([new(_referencingResourceName, _referentialIdGuid1)])
            );

            // Update the first document's referential id, allow identity updates
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid2,
                _edFiDocString2,
                allowIdentityUpdates: true
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public async Task It_should_have_changed_the_document()
        {
            var getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
            (getResult as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Contain("\"abc\":2");
            (getResult! as GetResult.GetSuccess)!.LastModifiedTraceId.Should().Be(traceId.Value);
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_The_Same_Document_With_Two_Overlapping_Request : UpdateTests
    {
        private UpdateResult? _updateResult1;
        private UpdateResult? _updateResult2;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";

        [SetUp]
        public async Task Setup()
        {
            (_updateResult1, _updateResult2) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    // Insert the original document
                    await CreateUpsert()
                        .Upsert(
                            CreateUpsertRequest(
                                _defaultResourceName,
                                _documentUuidGuid,
                                _referentialIdGuid,
                                _edFiDocString1
                            ),
                            connection,
                            transaction,
                            traceId
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction, traceId);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString3
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction, traceId);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _updateResult1!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_conflict_failure_for_2nd_transaction()
        {
            _updateResult2!.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        }

        [Test]
        public async Task It_should_be_the_1st_update_found_by_get()
        {
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            GetResult? _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);

            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString2);
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_A_Document_To_Reference_A_Non_Existent_Document : UpdateTests
    {
        private UpdateResult? _updateResult;

        private static readonly Guid _referencedReferentialIdGuid = Guid.NewGuid();

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _referencingDocumentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referencingReferentialIdGuid = Guid.NewGuid();
        private static readonly Guid _invalidReferentialIdGuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            // Referenced document
            IUpsertRequest refUpsertRequest = CreateUpsertRequest(
                "ReferencedResource",
                Guid.NewGuid(),
                _referencedReferentialIdGuid,
                """{"abc":1}"""
            );
            await CreateUpsert().Upsert(refUpsertRequest, Connection!, Transaction!, traceId);

            // Document with valid reference
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _referencingDocumentUuidGuid,
                _referencingReferentialIdGuid,
                """{"abc":2}""",
                CreateDocumentReferences([new(_referencingResourceName, _referencedReferentialIdGuid)])
            );

            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId);

            // Update with invalid reference
            string updatedReferencedDocString = """{"abc":3}""";
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _referencingResourceName,
                _referencingDocumentUuidGuid,
                _referencingReferentialIdGuid,
                updatedReferencedDocString,
                CreateDocumentReferences([new(_referencingResourceName, _invalidReferentialIdGuid)])
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_be_an_update_failure_reference()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateFailureReference>();
        }

        [Test]
        public void Needs_to_assert_expected_ReferencingDocumentInfo()
        {
            var failureResult = _updateResult as UpdateResult.UpdateFailureReference;
            failureResult!.ReferencingDocumentInfo[0].Value.Should().Be(_referencingResourceName);
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_A_Document_To_Reference_An_Existing_Document : UpdateTests
    {
        private UpdateResult? _updateResult;

        private static readonly string _referencedResourceName = "ReferencedResource";
        private static readonly Guid _resourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _referencedRefIdGuid = Guid.NewGuid();
        private static readonly string _referencedDocString = """{"abc":1}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // First, insert the referenced document
            IUpsertRequest refUpsertRequest = CreateUpsertRequest(
                _referencedResourceName,
                _resourcedDocUuidGuid,
                _referencedRefIdGuid,
                _referencedDocString
            );
            var upsertResult1 = await CreateUpsert()
                .Upsert(refUpsertRequest, Connection!, Transaction!, traceId);
            upsertResult1.Should().BeOfType<UpsertResult.InsertSuccess>();

            // Then, insert the referencing document without a reference
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString
            );

            var upsertResult2 = await CreateUpsert()
                .Upsert(upsertRequest, Connection!, Transaction!, traceId);
            upsertResult2.Should().BeOfType<UpsertResult.InsertSuccess>();

            // Update the referencing document, adding the reference
            string updatedReferencingDocString = """{"abc":3}""";
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                updatedReferencingDocString,
                CreateDocumentReferences([new(_referencingResourceName, _referencedRefIdGuid)])
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_A_Document_With_One_Existing_And_One_Non_Existent_Reference : UpdateTests
    {
        private UpdateResult? _updateResult;

        private static readonly string _existingReferencedResourceName = "ExistingReferencedResource";
        private static readonly Guid _existingResourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _existingReferencedRefIdGuid = Guid.NewGuid();
        private static readonly string _existingReferencedDocString = """{"abc":1}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // First, insert the existing referenced document
            IUpsertRequest existingRefUpsertRequest = CreateUpsertRequest(
                _existingReferencedResourceName,
                _existingResourcedDocUuidGuid,
                _existingReferencedRefIdGuid,
                _existingReferencedDocString
            );
            var upsertResult1 = await CreateUpsert()
                .Upsert(existingRefUpsertRequest, Connection!, Transaction!, traceId);
            upsertResult1.Should().BeOfType<UpsertResult.InsertSuccess>();

            // Then, insert the referencing document with no references yet
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString
            );

            var upsertResult2 = await CreateUpsert()
                .Upsert(upsertRequest, Connection!, Transaction!, traceId);
            upsertResult2.Should().BeOfType<UpsertResult.InsertSuccess>();

            // One existing and one non-existent reference
            Reference[] references =
            [
                new(_existingReferencedResourceName, _existingReferencedRefIdGuid),
                new("Nonexistent", Guid.NewGuid()),
            ];

            // Update the referencing document to refer to both existing and non-existent documents
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                """{"abc":3}""",
                CreateDocumentReferences(references)
            );
            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_be_a_update_failure_reference()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateFailureReference>();
        }

        [Test]
        public void Needs_to_assert_expected_ReferencingDocumentInfo()
        {
            var failureResult = _updateResult as UpdateResult.UpdateFailureReference;
            failureResult!.ReferencingDocumentInfo[0].Value.Should().Be("Nonexistent");
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_A_Subclass_Document_Referenced_By_An_Existing_Document_As_A_Superclass
        : UpdateTests
    {
        private UpdateResult? _updateResult;
        private static readonly string _superclassResourceName = "EducationOrganization";
        private static readonly Guid _superclassDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _superclassRefIdGuid = Guid.NewGuid();
        private static readonly string _superclassDocString =
            """{"schoolId": 123, "nameOfInstitution" : "Test" }""";

        private static readonly string _subclassResourceName = "AcademicWeek";
        private static readonly Guid _subclassDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _subclassRefIdGuid = Guid.NewGuid();
        private static readonly string _subclassDocString = """{"weekIdentifier": "One"}""";
        private static readonly string _subclassDocStringUpdate =
            """{"weekIdentifier": "One", "schoolReference":[{"schoolId": 123}]}""";

        [SetUp]
        public async Task Setup()
        {
            // The document that will be referenced (EducationOrganization)
            IUpsertRequest superclassUpsertRequest = CreateUpsertRequest(
                _superclassResourceName,
                _superclassDocUuidGuid,
                _superclassRefIdGuid,
                _superclassDocString
            );
            var upsertResult1 = await CreateUpsert()
                .Upsert(superclassUpsertRequest, Connection!, Transaction!, traceId);
            upsertResult1.Should().BeOfType<UpsertResult.InsertSuccess>();

            // The original document with no reference (AcademicWeek)
            IUpsertRequest referencingUpsertRequest = CreateUpsertRequest(
                _subclassResourceName,
                _subclassDocUuidGuid,
                _subclassRefIdGuid,
                _subclassDocString
            );

            var upsertResult2 = await CreateUpsert()
                .Upsert(referencingUpsertRequest, Connection!, Transaction!, traceId);
            upsertResult2.Should().BeOfType<UpsertResult.InsertSuccess>();

            // The updated document with reference as superclass (an AcademicWeek reference an EducationOrganization)
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _subclassResourceName,
                _subclassDocUuidGuid,
                _subclassRefIdGuid,
                _subclassDocStringUpdate,
                CreateDocumentReferences([new(_subclassResourceName, _superclassRefIdGuid)])
            );

            _updateResult = await CreateUpdate()
                .UpdateById(updateRequest, Connection!, Transaction!, traceId);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_The_Same_Document_With_Two_Overlapping_Request_But_Also_With_Different_References
        : UpdateTests
    {
        private UpdateResult? _updateResult1;
        private UpdateResult? _updateResult2;

        private static readonly string _existingReferencedResourceName = "ExistingReferencedResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly string _refEdFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            (_updateResult1, _updateResult2) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    // Insert the original document
                    await CreateUpsert()
                        .Upsert(
                            CreateUpsertRequest(
                                _defaultResourceName,
                                _documentUuidGuid,
                                _referentialIdGuid,
                                _edFiDocString1
                            ),
                            connection,
                            transaction,
                            traceId
                        );

                    // Add references: one existing and one non-existent
                    Reference[] references = { new(_existingReferencedResourceName, _documentUuidGuid) };

                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _referencingResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _refEdFiDocString,
                        CreateDocumentReferences(references)
                    );

                    await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction, traceId);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString3
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction, traceId);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _updateResult1!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_conflict_failure_for_2nd_transaction()
        {
            _updateResult2!.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_A_Document_Cascade_To_Parents_With_Recursion : UpdateTests
    {
        private static readonly Guid _sessionDocumentUuid = Guid.NewGuid();
        private static readonly Guid _sessionReferentialIdUuid = Guid.NewGuid();

        private static readonly Guid _courseOfferingDocumentUuid = Guid.NewGuid();
        private static readonly Guid _courseOfferingReferentialIdUuid = Guid.NewGuid();

        private static readonly Guid _section1DocumentUuid = Guid.NewGuid();
        private static readonly Guid _section1ReferentialIdUuid = Guid.NewGuid();

        private static readonly Guid _section2DocumentUuid = Guid.NewGuid();
        private static readonly Guid _section2ReferentialIdUuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            //The document that will be referenced "Session"
            IUpsertRequest sessionUpsertRequest = CreateUpsertRequest(
                "Session",
                _sessionDocumentUuid,
                _sessionReferentialIdUuid,
                """
                {
                    "sessionName": "Third Quarter"
                }
                """,
                allowIdentityUpdates: true
            );
            var upsert = CreateUpsert();
            var sessionUpsertResult = await upsert.Upsert(
                sessionUpsertRequest,
                Connection!,
                Transaction!,
                traceId
            );
            sessionUpsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            IUpsertRequest courseOfferingUpsertRequest = CreateUpsertRequest(
                "CourseOffering",
                _courseOfferingDocumentUuid,
                _courseOfferingReferentialIdUuid,
                """
                {
                    "localCourseCode": "ABC",
                    "sessionReference": {
                        "sessionName": "Third Quarter"
                    }
                }
                """,
                CreateDocumentReferences(
                    [new("CourseOffering", sessionUpsertRequest.DocumentInfo.ReferentialId.Value)]
                )
            );

            var courseOfferingUpsertResult = await CreateUpsert()
                .Upsert(courseOfferingUpsertRequest, Connection!, Transaction!, traceId);
            courseOfferingUpsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            IUpsertRequest section1UpsertRequest = CreateUpsertRequest(
                "Section",
                _section1DocumentUuid,
                _section1ReferentialIdUuid,
                """
                {
                    "sectionName": "SECTION 1",
                    "courseOfferingReference": {
                        "localCourseCode": "ABC",
                        "sessionName": "Third Quarter"
                    }
                }
                """,
                CreateDocumentReferences(
                    [new("Section", courseOfferingUpsertRequest.DocumentInfo.ReferentialId.Value)]
                )
            );

            var section1UpsertResult = await CreateUpsert()
                .Upsert(section1UpsertRequest, Connection!, Transaction!, traceId);
            section1UpsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            IUpsertRequest section2UpsertRequest = CreateUpsertRequest(
                "Section",
                _section2DocumentUuid,
                _section2ReferentialIdUuid,
                """
                {
                    "sectionName": "SECTION 2",
                    "courseOfferingReference": {
                        "localCourseCode": "ABC",
                        "sessionName": "Third Quarter"
                    }
                }
                """,
                CreateDocumentReferences(
                    [new("Section", courseOfferingUpsertRequest.DocumentInfo.ReferentialId.Value)]
                )
            );

            var section2UpsertResult = await CreateUpsert()
                .Upsert(section2UpsertRequest, Connection!, Transaction!, traceId);
            section2UpsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            var documentIdentityElement = new DocumentIdentityElement(
                new JsonPath("$.sessionName"),
                "Fourth Quarter"
            );
            IUpdateRequest sessionUpdateRequest = CreateUpdateRequest(
                "Session",
                _sessionDocumentUuid,
                Guid.NewGuid(),
                """
                {
                    "sessionName": "Fourth Quarter"
                }
                """,
                documentIdentityElements: [documentIdentityElement],
                allowIdentityUpdates: true
            );

            var sessionUpdateResult = await CreateUpdate()
                .UpdateById(sessionUpdateRequest, Connection!, Transaction!, traceId);

            sessionUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public async Task It_should_update_the_body_of_the_referencing_document()
        {
            var getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest("CourseOffering", _courseOfferingDocumentUuid),
                    Connection!,
                    Transaction!
                );

            getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_courseOfferingDocumentUuid);
            (getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Contain("Fourth Quarter");
            (getResult! as GetResult.GetSuccess)!.LastModifiedTraceId.Should().Be(traceId.Value);
        }

        [Test]
        public async Task It_should_update_the_body_of_the_second_level_referencing_document()
        {
            var section1GetResult = await CreateGetById()
                .GetById(CreateGetRequest("Section", _section1DocumentUuid), Connection!, Transaction!);

            section1GetResult!.Should().BeOfType<GetResult.GetSuccess>();
            (section1GetResult! as GetResult.GetSuccess)!
                .DocumentUuid.Value.Should()
                .Be(_section1DocumentUuid);
            (section1GetResult! as GetResult.GetSuccess)!
                .EdfiDoc.ToJsonString()
                .Should()
                .Contain("SECTION 1");
            (section1GetResult! as GetResult.GetSuccess)!
                .EdfiDoc.ToJsonString()
                .Should()
                .Contain("Fourth Quarter");

            var section2GetResult = await CreateGetById()
                .GetById(CreateGetRequest("Section", _section2DocumentUuid), Connection!, Transaction!);

            section1GetResult!.Should().BeOfType<GetResult.GetSuccess>();
            (section2GetResult! as GetResult.GetSuccess)!
                .DocumentUuid.Value.Should()
                .Be(_section2DocumentUuid);
            (section2GetResult! as GetResult.GetSuccess)!
                .EdfiDoc.ToJsonString()
                .Should()
                .Contain("SECTION 2");
            (section2GetResult! as GetResult.GetSuccess)!
                .EdfiDoc.ToJsonString()
                .Should()
                .Contain("Fourth Quarter");
        }
    }
    // Future tests - from Meadowlark

    // given an update of a document that tries to reference an existing descriptor

    // given an update of a document that tries to reference a nonexisting descriptor

    // Future tests - new concurrency-based

    // given an update of a document that tries to reference an existing document that is concurrently deleted
}
