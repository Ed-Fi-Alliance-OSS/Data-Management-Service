// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.OdsParity;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Tests.OdsParity;

/// <summary>
/// Fail-closed guardrails over the committed ODS 7.3.2 comparison definitions. Pure JSON reading: no
/// database is leased and no request is issued.
/// </summary>
/// <remarks>
/// The expected id lists below are explicit rather than derived from the files they check. A guardrail
/// that recomputed its expectations from the same data could not notice the data shrinking, which is the
/// failure this exists to catch: a case quietly deleted, or an approved difference quietly dropped,
/// would otherwise leave a green suite that no longer covers what the epic approved.
/// <para>
/// Carries the three CI-selection categories so both API integration lanes select it, exactly as
/// <c>Given_The_Api_Parity_Catalog_Resolution</c> does, while inheriting no database base.
/// </para>
/// </remarks>
[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_The_Ods_Comparison_Case_Definitions
{
    /// <summary>
    /// Every row of the epic's ODS Precedence Comparison table, in the order the table states them.
    /// </summary>
    private static readonly string[] _expectedPrecedenceCaseIds =
    [
        "cursor-precedence-01",
        "cursor-precedence-02",
        "cursor-precedence-03",
        "cursor-precedence-04",
        "cursor-precedence-05",
        "cursor-precedence-06",
        "cursor-precedence-07",
        "cursor-precedence-08",
        "cursor-precedence-09",
        "cursor-precedence-10",
        "cursor-precedence-11",
        "cursor-precedence-12",
        "cursor-precedence-13",
    ];

    /// <summary>
    /// Every bullet of the epic's Approved Intentional ODS Differences list, in the order that list
    /// states them.
    /// </summary>
    private static readonly string[] _expectedDifferenceIds =
    [
        "reject-limit-with-cursor-parameters",
        "totalcount-rejected-at-validation",
        "cursor-key-presence-is-significant",
        "number-reserved-from-filter-matching",
        "number-non-numeric-range-message",
        "number-blank-range-message",
        "cursor-parameters-rejected-on-change-queries",
        "partition-reserved-parameters-unsupported",
        "ods-only-partition-parameters-rejected",
        "true-ceiling-at-most-requested-count",
        "partitions-enforce-profile-method-usage",
        "header-gated-on-selected-keyset-maximum",
        "offset-message-text-retained",
        "limit-message-text-retained",
        "maximum-page-size-is-the-omitted-default",
        "openapi-publishes-runtime-page-size-bounds",
        "openapi-publishes-runtime-default-partition-count",
        "write-only-profile-omits-partitions-path",
        "int64-document-id-bounds",
        "no-header-overflow-at-int64-max",
        "strict-base64url-and-decimal-decoder",
    ];

    /// <summary>
    /// Every group a case may belong to, and therefore every group a fixture must bind. A case in a
    /// group nothing runs would never be executed, and a bound group with no cases would assert nothing.
    /// </summary>
    private static readonly string[] _expectedGroups =
    [
        "validation",
        "omitted-limit-default",
        "sizing",
        "number-collision",
        "int64-bounds",
        "identity-maximum",
        "empty-hydration",
        "profile",
        "metadata",
        "profile-metadata",
        "sizing-default-count",
    ];

    /// <summary>
    /// Every decoder case the approved comparison matrix requires: the two accepted transport forms,
    /// the forbidden alphabet, malformed padding, invalid UTF-8, an extra field, and the two
    /// non-canonical decimal forms. Pinned so a whole category cannot quietly disappear while the
    /// catalog entry stays referenced by the remaining ones.
    /// </summary>
    private static readonly string[] _expectedDecoderCaseIds =
    [
        "strict-decoder-canonical-unpadded",
        "strict-decoder-correctly-padded",
        "strict-decoder-extra-field",
        "strict-decoder-forbidden-alphabet",
        "strict-decoder-invalid-padding",
        "strict-decoder-invalid-utf8",
        "strict-decoder-leading-sign-decimal",
        "strict-decoder-whitespace-decimal",
    ];

    private static readonly string[] _expectedExecutors =
    [
        "collection-get",
        "partitions-get",
        "change-query-get",
        "served-document",
        "sizing-true-ceiling",
        "number-collision",
        "empty-hydration",
        "identity-maximum",
        "profile-partitions-get",
        "profile-document-partitions-omission",
    ];

    [Test]
    public void It_records_the_reference_version_and_its_source_authorities()
    {
        var metadata = OdsComparisonCatalog.Definitions.Metadata;

        metadata["odsVersion"]!.GetValue<string>().Should().Be("7.3.2");
        metadata["live"]!
            .GetValue<bool>()
            .Should()
            .BeFalse("no ODS instance is stood up; the ODS column is static expected data");
        metadata["liveReason"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        metadata["derivedFrom"]!
            .GetValue<string>()
            .Should()
            .Be("reference/design/backend-redesign/epics/20-partitioned-cursor-paging/EPIC.md");
        metadata["odsSourceAuthorities"]!
            .AsArray()
            .Should()
            .HaveCount(6, "the epic's Compatibility Baseline pins six ODS 7.3.2 sources");
    }

    [Test]
    public void It_contains_every_precedence_row_exactly_once_and_in_order()
    {
        string[] precedenceCaseIds =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    comparisonCase.SourceFile == "cursor-precedence-cases.json"
                )
                .Select(comparisonCase => comparisonCase.Id),
        ];

        precedenceCaseIds
            .Should()
            .Equal(
                _expectedPrecedenceCaseIds,
                "every row of the epic's precedence table is a case, in the order the table states them"
            );
    }

    [Test]
    public void It_contains_every_approved_difference_exactly_once_and_in_order()
    {
        OdsComparisonCatalog
            .Definitions.Catalog.Select(entry => entry.Id)
            .Should()
            .Equal(
                _expectedDifferenceIds,
                "the catalog mirrors the epic's approved-difference list one for one"
            );

        OdsComparisonCatalog
            .Definitions.Catalog.Select(entry => entry.Bullet)
            .Should()
            .Equal(Enumerable.Range(1, _expectedDifferenceIds.Length), "bullets are numbered in order");
    }

    [Test]
    public void It_declares_every_approved_difference_executable()
    {
        OdsComparisonCatalog
            .Definitions.Catalog.Where(entry => !entry.Executable)
            .Select(entry => entry.Id)
            .Should()
            .BeEmpty("every approved difference is observable from a DMS target and is executed here");
    }

    [Test]
    public void It_gives_every_case_a_unique_id()
    {
        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Id)
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Test]
    public void It_resolves_every_named_approved_difference()
    {
        string[] catalogIds = [.. OdsComparisonCatalog.Definitions.Catalog.Select(entry => entry.Id)];

        string[] unresolved =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    comparisonCase.ApprovedDifference is not null
                    && !catalogIds.Contains(comparisonCase.ApprovedDifference, StringComparer.Ordinal)
                )
                .Select(comparisonCase => $"{comparisonCase.Id} -> {comparisonCase.ApprovedDifference}"),
        ];

        unresolved.Should().BeEmpty("a case may only name a difference the catalog declares");
    }

    [Test]
    public void It_references_every_approved_difference_from_at_least_one_case()
    {
        string[] referenced =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Select(comparisonCase => comparisonCase.ApprovedDifference)
                .Where(difference => difference is not null)
                .Select(difference => difference!)
                .Distinct(StringComparer.Ordinal),
        ];

        OdsComparisonCatalog
            .Definitions.Catalog.Select(entry => entry.Id)
            .Except(referenced, StringComparer.Ordinal)
            .Should()
            .BeEmpty("an approved difference nothing executes is not evidence of anything");
    }

    [Test]
    public void It_requires_every_difference_case_to_name_one()
    {
        OdsComparisonCatalog
            .Definitions.Cases.Where(comparisonCase =>
                comparisonCase.DeclaresDifference
                && string.IsNullOrWhiteSpace(comparisonCase.ApprovedDifference)
            )
            .Select(comparisonCase => comparisonCase.Id)
            .Should()
            .BeEmpty("an unmapped difference is exactly what this suite exists to fail on");

        OdsComparisonCatalog
            .Definitions.Cases.Where(comparisonCase =>
                !comparisonCase.DeclaresDifference && comparisonCase.ApprovedDifference is not null
            )
            .Select(comparisonCase => comparisonCase.Id)
            .Should()
            .BeEmpty("a case declaring parity cannot also name a difference");
    }

    [Test]
    public void It_uses_only_declared_outcomes_groups_and_executors()
    {
        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Outcome)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(new[] { "parity", "difference" });

        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Group)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(_expectedGroups, "a case in an unbound group would never be executed");

        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Executor)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(_expectedExecutors, "an unknown executor throws rather than skipping the case");
    }

    [Test]
    public void It_binds_every_declared_group_and_executor()
    {
        string[] usedGroups =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Select(comparisonCase => comparisonCase.Group)
                .Distinct(StringComparer.Ordinal),
        ];

        _expectedGroups
            .Should()
            .BeSubsetOf(usedGroups, "a declared group with no case would run an empty comparison");

        string[] usedExecutors =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Select(comparisonCase => comparisonCase.Executor)
                .Distinct(StringComparer.Ordinal),
        ];

        _expectedExecutors
            .Should()
            .BeSubsetOf(usedExecutors, "an executor nothing uses is dead comparison machinery");
    }

    [Test]
    public void It_covers_every_decoder_category()
    {
        string[] decoderCaseIds =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    comparisonCase.Id.StartsWith("strict-decoder-", StringComparison.Ordinal)
                )
                .Select(comparisonCase => comparisonCase.Id)
                .Order(StringComparer.Ordinal),
        ];

        decoderCaseIds
            .Should()
            .Equal(
                _expectedDecoderCaseIds,
                "the approved decoder comparison matrix covers every transport and decimal category"
            );
    }

    [Test]
    public void It_never_lets_a_successful_outcome_declare_errors()
    {
        string[] offending =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    (comparisonCase.Dms.Status < 400 && comparisonCase.Dms.Errors is not null)
                    || (comparisonCase.Ods.Status < 400 && comparisonCase.Ods.Errors is not null)
                )
                .Select(comparisonCase => comparisonCase.Id),
        ];

        offending
            .Should()
            .BeEmpty(
                "a successful response carries no error list, so recording one would make the outcome "
                    + "unreachable and the comparison unfalsifiable"
            );
    }

    /// <summary>
    /// The guard that keeps a declared difference falsifiable: rendering the recorded ODS outcome as the
    /// observation that would produce it must match that recorded outcome, and must not match the DMS
    /// outcome. The first half proves the ODS side is reachable at all; the second proves the difference
    /// is one in compared fields rather than in prose.
    /// </summary>
    /// <remarks>
    /// This is the executable form of "if DMS converged on the recorded ODS behavior, the case would
    /// fail". The executing scenario asks exactly this comparer whether an observation matches the ODS
    /// side, so an ODS outcome no observation could equal would declare a difference forever.
    /// </remarks>
    [Test]
    public void It_keeps_every_declared_difference_falsifiable()
    {
        List<string> unfalsifiable = [];
        List<string> notMateriallyDifferent = [];

        foreach (
            var comparisonCase in OdsComparisonCatalog.Definitions.Cases.Where(comparisonCase =>
                comparisonCase.DeclaresDifference
            )
        )
        {
            ObservedOutcome asOds = OdsOutcomeComparer.AsObservation(comparisonCase.Ods);

            if (!OdsOutcomeComparer.Matches(asOds, comparisonCase.Ods))
            {
                unfalsifiable.Add(comparisonCase.Id);
            }

            if (OdsOutcomeComparer.Matches(asOds, comparisonCase.Dms))
            {
                notMateriallyDifferent.Add(comparisonCase.Id);
            }
        }

        unfalsifiable
            .Should()
            .BeEmpty(
                "a recorded ODS outcome no observation could equal would declare a difference forever, "
                    + "masking convergence instead of detecting it"
            );
        notMateriallyDifferent
            .Should()
            .BeEmpty(
                "a case declaring a difference must differ from the DMS outcome in a field the "
                    + "comparison actually reads"
            );
    }

    [Test]
    public void It_keeps_every_parity_case_comparable()
    {
        string[] offending =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    !comparisonCase.DeclaresDifference
                    && !OdsOutcomeComparer.Matches(
                        OdsOutcomeComparer.AsObservation(comparisonCase.Ods),
                        comparisonCase.Dms
                    )
                )
                .Select(comparisonCase => comparisonCase.Id),
        ];

        offending
            .Should()
            .BeEmpty(
                "a parity case records the same outcome on both sides, so the recorded ODS outcome must "
                    + "satisfy the DMS expectation it claims to equal"
            );
    }

    [Test]
    public void It_pins_the_seeded_expectations_that_carry_a_boundary()
    {
        var omittedLimit = OdsComparisonCatalog.Definitions.Cases.Single(comparisonCase =>
            comparisonCase.Id == "omitted-limit-uses-configured-maximum"
        );

        omittedLimit
            .Seed.Should()
            .Be(26, "the seed sits one document past the published Ed-Fi default of twenty-five");
        omittedLimit.Dms.Expect!["documentCount"]!
            .GetValue<int>()
            .Should()
            .Be(26, "DMS returns the whole seed because its configured maximum is well above 25");
        omittedLimit.Ods.Expect!["documentCount"]!
            .GetValue<int>()
            .Should()
            .Be(25, "the published default stops one document short, which is the difference under test");

        var defaultCount = OdsComparisonCatalog.Definitions.Cases.Single(comparisonCase =>
            comparisonCase.Id == "partition-sizing-omitted-count-true-ceiling"
        );

        defaultCount.Seed.Should().Be(105, "the recorded arithmetic is stated for this candidate count");
        defaultCount
            .Executor.Should()
            .Be(
                "sizing-true-ceiling",
                "the omitted-count case must run through the executor that decodes every token and "
                    + "proves the ranges tile the seed, not through a plainer request executor"
            );

        OdsComparisonCatalog
            .Definitions.Cases.Single(comparisonCase => comparisonCase.Id == "partition-sizing-true-ceiling")
            .Executor.Should()
            .Be("sizing-true-ceiling", "both sizing halves share the same proving executor");
        defaultCount.Dms.Expect!["tokenCount"]!
            .GetValue<int>()
            .Should()
            .Be(10, "a true ceiling hands out at most the configured default partition count");
        defaultCount.Ods.Expect!["tokenCount"]!
            .GetValue<int>()
            .Should()
            .Be(11, "a floored size leaves one more starting id, and no cap applies when number is omitted");
    }
}
