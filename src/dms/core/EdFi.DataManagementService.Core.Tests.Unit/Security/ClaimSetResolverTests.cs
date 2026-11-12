// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class ClaimSetResolverTests
{
    private const char ListSeparator = '|';

    private static string[] Split(string list) =>
        list.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries);

    private static ClientAuthorizations CreateAuthorizations(
        string[] scopeValues,
        string claimSetName = ""
    ) =>
        new(
            TokenId: "token-id",
            ClientId: "client-id",
            ClaimSetName: claimSetName,
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        )
        {
            ScopeValues = scopeValues,
        };

    private static List<ClaimSet> CreateClaimSets(string metadataNames) =>
        [.. Split(metadataNames).Select(name => new ClaimSet(name, []))];

    /// <summary>
    /// Scope values are given as a '|'-separated list so a case can state tokens that contain
    /// commas or tabs literally. Each row is token list, metadata names, expected selected name.
    /// </summary>
    [TestFixture("alpha|profile", "Alpha", "alpha")]
    [TestFixture("profile|Alpha", "Alpha", "Alpha")]
    [TestFixture("Alpha|alpha", "Alpha", "Alpha")]
    [TestFixture("alpha|Alpha", "Alpha", "Alpha")]
    [TestFixture("Alpha|Alpha", "Alpha", "Alpha")]
    [TestFixture("alpha|ALPHA|Alpha", "Alpha", "Alpha")]
    [TestFixture("Alpha|unknown", "Alpha|Beta", "Alpha")]
    [TestFixture("unknown|beta", "Alpha|Beta", "beta")]
    public class Given_ClaimSet_Resolution(string scopeValues, string metadataNames, string expectedName)
    {
        private List<ClaimSet> _claimSets = null!;
        private ClaimSetResolution _result = null!;

        [SetUp]
        public void Setup()
        {
            _claimSets = CreateClaimSets(metadataNames);

            _result = ClaimSetResolver.Resolve(CreateAuthorizations(Split(scopeValues)), _claimSets);
        }

        [Test]
        public void It_succeeds_with_the_expected_name() =>
            _result
                .Should()
                .BeOfType<ClaimSetResolution.Success>()
                .Which.ClaimSetName.Should()
                .Be(expectedName);

        [Test]
        public void It_returns_the_one_matching_metadata_record() =>
            _result
                .Should()
                .BeOfType<ClaimSetResolution.Success>()
                .Which.ClaimSet.Should()
                .BeSameAs(
                    _claimSets.Single(claimSet =>
                        string.Equals(
                            claimSet.Name,
                            expectedName,
                            StringComparison.InvariantCultureIgnoreCase
                        )
                    )
                );
    }

    [TestFixture("Alpha|Beta", "Alpha|Beta")]
    [TestFixture("Beta|Alpha", "Alpha|Beta")]
    [TestFixture("Alpha|profile|Beta", "Beta|Alpha|Gamma")]
    [TestFixture("alpha|BETA", "Alpha|Beta")]
    public class Given_Two_Recognized_Claim_Sets(string scopeValues, string metadataNames)
    {
        private ClaimSetResolution _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ClaimSetResolver.Resolve(
                CreateAuthorizations(Split(scopeValues)),
                CreateClaimSets(metadataNames)
            );

        [Test]
        public void It_is_ambiguous() => _result.Should().BeOfType<ClaimSetResolution.Ambiguous>();
    }

    [TestFixture("Alpha,Beta", "Alpha|Beta")]
    [TestFixture("Alpha\tBeta", "Alpha|Beta")]
    [TestFixture("Alpha Beta", "Alpha|Beta")]
    [TestFixture("", "Alpha|Beta")]
    [TestFixture("unknown|other", "Alpha|Beta")]
    [TestFixture("Alpha", "")]
    [TestFixture("Alpha", "Beta|beta")]
    public class Given_No_Recognized_Claim_Set(string scopeValues, string metadataNames)
    {
        private ClaimSetResolution _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ClaimSetResolver.Resolve(
                CreateAuthorizations(Split(scopeValues)),
                CreateClaimSets(metadataNames)
            );

        [Test]
        public void It_matches_no_claim_set() => _result.Should().BeOfType<ClaimSetResolution.NoMatch>();
    }

    [TestFixture("Alpha", "Alpha|alpha")]
    [TestFixture("Alpha", "Alpha|Alpha")]
    [TestFixture("Alpha|alpha", "ALPHA|Alpha")]
    [TestFixture("Alpha|Beta", "Alpha|alpha|Beta")]
    [TestFixture("Beta|Alpha", "Beta|Alpha|ALPHA")]
    public class Given_Duplicate_Matching_Claim_Set_Metadata(string scopeValues, string metadataNames)
    {
        private ClaimSetResolution _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ClaimSetResolver.Resolve(
                CreateAuthorizations(Split(scopeValues)),
                CreateClaimSets(metadataNames)
            );

        [Test]
        public void It_reports_invalid_metadata_before_ambiguity() =>
            _result.Should().BeOfType<ClaimSetResolution.InvalidMetadata>();
    }

    [TestFixture]
    public class Given_Duplicate_Names_Alongside_Another_Recognized_Name
    {
        private ClaimSetResolution _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ClaimSetResolver.Resolve(
                CreateAuthorizations(["Alpha", "Beta"]),
                CreateClaimSets("Alpha|alpha|Beta")
            );

        [Test]
        public void It_identifies_only_the_duplicate_metadata_names() =>
            _result
                .Should()
                .BeOfType<ClaimSetResolution.InvalidMetadata>()
                .Which.DuplicateNames.Should()
                .Equal("Alpha", "alpha");
    }

    [TestFixture]
    public class Given_Duplicate_Metadata_Names_That_Match_No_Scope
    {
        private ClaimSetResolution _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ClaimSetResolver.Resolve(
                CreateAuthorizations(["Alpha"]),
                CreateClaimSets("Alpha|Beta|beta")
            );

        [Test]
        public void It_ignores_the_unrelated_duplicates() =>
            _result.Should().BeOfType<ClaimSetResolution.Success>().Which.ClaimSetName.Should().Be("Alpha");
    }

    /// <summary>
    /// A nonempty ScopeValues collection decides the result even when the legacy name is already
    /// present, and the legacy name only stands in when no scope values were carried.
    /// </summary>
    [TestFixture]
    public class Given_Authoritative_Scope_Values
    {
        private ClaimSetResolution _scopeValuesWinResult = null!;
        private ClaimSetResolution _staleNameOnlyResult = null!;
        private ClaimSetResolution _legacyNameResult = null!;
        private ClaimSetResolution _legacyNameNoRecordResult = null!;
        private ClaimSetResolution _legacyNameNotSplitResult = null!;
        private ClaimSetResolution _emptyEverythingResult = null!;

        [SetUp]
        public void Setup()
        {
            List<ClaimSet> bothRecords = CreateClaimSets("Alpha|Beta");

            _scopeValuesWinResult = ClaimSetResolver.Resolve(
                CreateAuthorizations(["profile", "Alpha"], claimSetName: "Beta"),
                bothRecords
            );
            _staleNameOnlyResult = ClaimSetResolver.Resolve(
                CreateAuthorizations(["profile"], claimSetName: "Beta"),
                bothRecords
            );
            _legacyNameResult = ClaimSetResolver.Resolve(
                CreateAuthorizations([], claimSetName: "Alpha"),
                CreateClaimSets("Alpha")
            );
            _legacyNameNoRecordResult = ClaimSetResolver.Resolve(
                CreateAuthorizations([], claimSetName: "Gamma"),
                bothRecords
            );
            _legacyNameNotSplitResult = ClaimSetResolver.Resolve(
                CreateAuthorizations([], claimSetName: "Alpha Beta"),
                bothRecords
            );
            _emptyEverythingResult = ClaimSetResolver.Resolve(CreateAuthorizations([]), bothRecords);
        }

        [Test]
        public void It_ignores_a_stale_name_when_scope_values_resolve() =>
            _scopeValuesWinResult
                .Should()
                .BeOfType<ClaimSetResolution.Success>()
                .Which.ClaimSetName.Should()
                .Be("Alpha");

        [Test]
        public void It_does_not_fall_back_to_the_name_when_scope_values_match_nothing() =>
            _staleNameOnlyResult.Should().BeOfType<ClaimSetResolution.NoMatch>();

        [Test]
        public void It_uses_the_legacy_name_when_no_scope_values_were_carried() =>
            _legacyNameResult
                .Should()
                .BeOfType<ClaimSetResolution.Success>()
                .Which.ClaimSetName.Should()
                .Be("Alpha");

        [Test]
        public void It_reports_no_match_for_an_unknown_legacy_name() =>
            _legacyNameNoRecordResult.Should().BeOfType<ClaimSetResolution.NoMatch>();

        [Test]
        public void It_treats_the_legacy_name_as_one_literal_candidate() =>
            _legacyNameNotSplitResult.Should().BeOfType<ClaimSetResolution.NoMatch>();

        [Test]
        public void It_reports_no_match_without_scope_values_or_a_name() =>
            _emptyEverythingResult.Should().BeOfType<ClaimSetResolution.NoMatch>();
    }

    [TestFixture]
    public class Given_Case_Variants_Of_One_Record_In_Different_Orders
    {
        private string _forwardName = null!;
        private string _reverseName = null!;

        [SetUp]
        public void Setup()
        {
            List<ClaimSet> claimSets = CreateClaimSets("Alpha");

            _forwardName = ClaimSetResolver
                .Resolve(CreateAuthorizations(["Alpha", "alpha"]), claimSets)
                .Should()
                .BeOfType<ClaimSetResolution.Success>()
                .Which.ClaimSetName;
            _reverseName = ClaimSetResolver
                .Resolve(CreateAuthorizations(["alpha", "Alpha"]), claimSets)
                .Should()
                .BeOfType<ClaimSetResolution.Success>()
                .Which.ClaimSetName;
        }

        [Test]
        public void It_uses_the_metadata_spelling_regardless_of_order()
        {
            _forwardName.Should().Be("Alpha");
            _reverseName.Should().Be("Alpha");
        }
    }
}
