// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

internal abstract class StoredStateProjectorTests
{
    // -----------------------------------------------------------------------
    //  Stub for IStoredSideExistenceLookup
    // -----------------------------------------------------------------------

    private sealed class StubExistenceLookup : IStoredSideExistenceLookup
    {
        public bool VisibleScopeExistsAt(ScopeInstanceAddress address) => false;

        public bool VisibleCollectionRowExistsAt(CollectionRowAddress address) => false;
    }

    // -----------------------------------------------------------------------
    //  1. Given_IncludeAll_Profile_With_Stored_Document
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeAll_Profile_With_Stored_Document : StoredStateProjectorTests
    {
        private ProfileAppliedWriteContext _context = null!;
        private ProfileAppliedWriteRequest _request = null!;
        private ImmutableArray<StoredScopeState> _inputStoredScopes;
        private ImmutableArray<VisibleStoredCollectionRow> _inputStoredRows;

        [SetUp]
        public void Setup()
        {
            // Build classifier from IncludeAll profile
            var classifier = new ProfileVisibilityClassifier(
                ProfileTestFixtures.BuildIncludeAllProfile(),
                ProfileTestFixtures.SharedFixtureScopes
            );

            // Stored document with all fields
            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": {
                        "calendarCode": "2024-01",
                        "calendarTypeDescriptor": "uri://ed-fi.org/CalendarType#IEP"
                    },
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            // Minimal request (body is not used by projector, just passed through)
            _request = new ProfileAppliedWriteRequest(
                WritableRequestBody: JsonNode.Parse("{}")!,
                RootResourceCreatable: true,
                RequestScopeStates: [],
                VisibleRequestCollectionItems: []
            );

            // Pre-computed stored-side classifications
            _inputStoredScopes =
            [
                new StoredScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    []
                ),
            ];

            _inputStoredRows =
            [
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods[*]",
                        new ScopeInstanceAddress("$", []),
                        ImmutableArray.Create(
                            new SemanticIdentityPart(
                                "classPeriodName",
                                JsonNode.Parse("\"Period1\""),
                                IsPresent: true
                            )
                        )
                    ),
                    []
                ),
            ];

            var existenceLookupResult = new StoredSideExistenceLookupResult(
                new StubExistenceLookup(),
                _inputStoredScopes,
                _inputStoredRows
            );

            // Act
            var projector = new StoredStateProjector(storedDocument, classifier);
            _context = projector.ProjectStoredState(_request, existenceLookupResult);
        }

        [Test]
        public void It_should_pass_through_request_unchanged()
        {
            _context.Request.Should().BeSameAs(_request);
        }

        [Test]
        public void It_should_produce_visible_stored_body()
        {
            // IncludeAll keeps all fields — verify entryDate is present
            _context.VisibleStoredBody["entryDate"]!
                .GetValue<string>()
                .Should()
                .Be("2024-08-01");
        }

        [Test]
        public void It_should_pass_through_stored_scope_states()
        {
            _context.StoredScopeStates.Should().Equal(_inputStoredScopes);
        }

        [Test]
        public void It_should_pass_through_visible_stored_collection_rows()
        {
            _context.VisibleStoredCollectionRows.Should().Equal(_inputStoredRows);
        }
    }

    // -----------------------------------------------------------------------
    //  2. Given_IncludeOnly_Profile_Filtering_Stored_Body
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Profile_Filtering_Stored_Body : StoredStateProjectorTests
    {
        private ProfileAppliedWriteContext _context = null!;

        /// <summary>
        /// IncludeOnly profile exposing studentReference, schoolReference at root;
        /// classPeriods with IncludeOnly classPeriodName. Hides entryDate,
        /// entryTypeDescriptor, and classPeriods.officialAttendancePeriod.
        /// </summary>
        private static ContentTypeDefinition BuildIncludeOnlyProfile() =>
            new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference"), new PropertyRule("schoolReference")],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("classPeriodName")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

        [SetUp]
        public void Setup()
        {
            // Build classifier from IncludeOnly profile
            var classifier = new ProfileVisibilityClassifier(
                BuildIncludeOnlyProfile(),
                ProfileTestFixtures.SharedFixtureScopes
            );

            // Stored document with ALL fields including hidden ones
            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "classPeriods": [
                        {
                            "classPeriodName": "Period1",
                            "officialAttendancePeriod": true
                        }
                    ]
                }
                """
            )!;

            // Minimal request
            var request = new ProfileAppliedWriteRequest(
                WritableRequestBody: JsonNode.Parse("{}")!,
                RootResourceCreatable: false,
                RequestScopeStates: [],
                VisibleRequestCollectionItems: []
            );

            // Empty pre-computed arrays — pass-through is tested above
            var existenceLookupResult = new StoredSideExistenceLookupResult(
                new StubExistenceLookup(),
                [],
                []
            );

            // Act
            var projector = new StoredStateProjector(storedDocument, classifier);
            _context = projector.ProjectStoredState(request, existenceLookupResult);
        }

        [Test]
        public void It_should_include_visible_root_members()
        {
            var body = _context.VisibleStoredBody.AsObject();
            body["studentReference"].Should().NotBeNull();
            body["schoolReference"].Should().NotBeNull();
        }

        [Test]
        public void It_should_strip_hidden_root_members()
        {
            var body = _context.VisibleStoredBody.AsObject();
            body["entryDate"].Should().BeNull();
            body["entryTypeDescriptor"].Should().BeNull();
        }

        [Test]
        public void It_should_strip_hidden_collection_item_members()
        {
            var classPeriods = _context.VisibleStoredBody["classPeriods"]!.AsArray();
            classPeriods.Should().HaveCount(1);

            var item = classPeriods[0]!.AsObject();
            item["classPeriodName"]!.GetValue<string>().Should().Be("Period1");
            item["officialAttendancePeriod"].Should().BeNull();
        }
    }
}
