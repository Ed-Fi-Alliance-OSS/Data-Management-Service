// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Validation;

/// <summary>
/// What the reserved query parameter catalog declares, and that it still agrees with the arrays the
/// request pipeline excludes from filter matching today.
/// </summary>
/// <remarks>
/// The exclusion assertions are the load-bearing ones. They are written against the constants the
/// validators own rather than against literals, so they fail if the catalog and the served pipeline
/// ever describe different sets of reserved names - which is the drift this catalog exists to make
/// impossible.
/// </remarks>
[TestFixture]
[Parallelizable]
public class ReservedQueryParametersTests
{
    /// <summary>
    /// The wire spellings, pinned as literals. A reserved name is part of the published API contract,
    /// so renaming one is a protocol change that should be a deliberate, visible test diff rather than
    /// a silent consequence of editing a constant.
    /// </summary>
    private static readonly string[] _pinnedNames =
    [
        "limit",
        "offset",
        "totalCount",
        "pageToken",
        "pageSize",
        "minChangeVersion",
        "maxChangeVersion",
        "number",
    ];

    /// <summary>
    /// The ordinal names <see cref="EdFi.DataManagementService.Core.Middleware.ValidateQueryMiddleware" />
    /// removes from filter matching today, spelled from the same constants its own private array is
    /// spelled from: its three traditional paging names followed by the two cursor names.
    /// </summary>
    private static readonly string[] _collectionGetOrdinalExclusionsToday =
    [
        CursorRequestValidator.LimitParameter,
        CursorRequestValidator.OffsetParameter,
        CursorRequestValidator.TotalCountParameter,
        .. CursorRequestValidator.CursorParameters,
    ];

    /// <summary>
    /// The ordinal names
    /// <see cref="EdFi.DataManagementService.Core.Middleware.ValidatePartitionQueryMiddleware" />
    /// removes from filter matching today.
    /// </summary>
    private static readonly string[] _partitionsOrdinalExclusionsToday =
    [
        .. PartitionRequestValidator.ReservedParameters,
        PartitionRequestValidator.NumberParameter,
    ];

    public static IEnumerable<string> AllReservedNames() =>
        ReservedQueryParameters.All.Select(reserved => reserved.Name);

    [TestFixture]
    [Parallelizable]
    public class Given_The_Catalog : ReservedQueryParametersTests
    {
        [Test]
        public void It_pins_every_reserved_name_in_canonical_order()
        {
            AllReservedNames()
                .Should()
                .Equal(
                    _pinnedNames,
                    "a reserved name is published API contract, and adding one must be a visible diff "
                        + "that forces the collision question to be asked"
                );
        }

        [Test]
        public void It_reserves_every_entry_somewhere()
        {
            ReservedQueryParameters
                .All.Where(reserved => reserved.ReservedOn == ReservedQueryParameterOperations.None)
                .Should()
                .BeEmpty("an entry reserved on no operation would refuse a schema for nothing");
        }

        [Test]
        public void It_gives_every_entry_a_purpose()
        {
            ReservedQueryParameters
                .All.Where(reserved => string.IsNullOrWhiteSpace(reserved.Purpose))
                .Should()
                .BeEmpty("a diagnostic states what the name is taken for, not only that it is taken");
        }

        [Test]
        public void It_names_every_entry_uniquely_ignoring_case()
        {
            AllReservedNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Should()
                .HaveCount(
                    ReservedQueryParameters.All.Count,
                    "two entries differing only in case would be one name to every comparison that "
                        + "matters, so one of them would silently never be reported"
                );
        }

        [Test]
        public void It_reserves_the_partition_count_on_partitions_alone()
        {
            ReservedQueryParameters
                .All.Single(reserved => reserved.Name == PartitionRequestValidator.NumberParameter)
                .ReservedOn.Should()
                .Be(
                    ReservedQueryParameterOperations.Partitions,
                    "canonicalizing or reserving the count key on a collection GET would change "
                        + "filtering and unknown-field error text on every collection"
                );
        }

        [Test]
        public void It_matches_the_change_version_names_case_insensitively()
        {
            ReservedQueryParameters
                .All.Where(reserved =>
                    reserved.RequestMatching == ReservedQueryParameterMatching.OrdinalIgnoreCase
                )
                .Select(reserved => reserved.Name)
                .Should()
                .Equal(
                    ChangeVersionParameterValidator.ReservedParameterNames,
                    "the change-version validator is the only one that looks its parameters up "
                        + "case-insensitively in Core"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Query_Field_Name : ReservedQueryParametersTests
    {
        [TestCaseSource(typeof(ReservedQueryParametersTests), nameof(AllReservedNames))]
        public void It_finds_every_reserved_name(string reservedName)
        {
            ReservedQueryParameters
                .TryGetReserved(reservedName, out ReservedQueryParameter? reserved)
                .Should()
                .BeTrue();

            reserved!.Name.Should().Be(reservedName);
        }

        [TestCase("PageSize", "pageSize")]
        [TestCase("PAGETOKEN", "pageToken")]
        [TestCase("Limit", "limit")]
        [TestCase("MINCHANGEVERSION", "minChangeVersion")]
        [TestCase("Number", "number")]
        public void It_finds_a_reserved_name_declared_in_another_case(
            string declaredName,
            string expectedReservedName
        )
        {
            ReservedQueryParameters
                .TryGetReserved(declaredName, out ReservedQueryParameter? reserved)
                .Should()
                .BeTrue(
                    "a query field is matched against a supplied key case-insensitively, so it is "
                        + "just as unfilterable whichever case it is declared in"
                );

            reserved!.Name.Should().Be(expectedReservedName);
        }

        [TestCase("numberOfPartitions")]
        [TestCase("limits")]
        [TestCase("pageTokens")]
        [TestCase("minChange")]
        [TestCase("schoolId")]
        [TestCase("totalCountOfSomething")]
        [TestCase("")]
        [TestCase(" number")]
        public void It_does_not_find_a_name_that_is_merely_similar(string queryFieldName)
        {
            ReservedQueryParameters
                .TryGetReserved(queryFieldName, out ReservedQueryParameter? reserved)
                .Should()
                .BeFalse("only an exact name, ignoring case, is consumed as a control parameter");

            reserved.Should().BeNull();
        }

        [Test]
        public void It_rejects_a_null_name()
        {
            Action act = () => ReservedQueryParameters.TryGetReserved(null!, out _);

            act.Should().Throw<ArgumentNullException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Collection_Get_Exclusions : ReservedQueryParametersTests
    {
        [Test]
        public void It_matches_the_ordinal_names_the_query_middleware_excludes_today()
        {
            ReservedQueryParameters
                .OrdinalFilterExclusionsOn(ReservedQueryParameterOperations.CollectionGet)
                .Should()
                .Equal(
                    _collectionGetOrdinalExclusionsToday,
                    "deriving the middleware's exclusion list from the catalog must not change which "
                        + "names it excludes"
                );
        }

        [Test]
        public void It_matches_the_case_insensitive_names_the_query_middleware_excludes_today()
        {
            ReservedQueryParameters
                .IgnoreCaseFilterExclusionsOn(ReservedQueryParameterOperations.CollectionGet)
                .Should()
                .Equal(ChangeVersionParameterValidator.ReservedParameterNames);
        }

        [Test]
        public void It_does_not_exclude_the_partition_count()
        {
            ReservedQueryParameters
                .OrdinalFilterExclusionsOn(ReservedQueryParameterOperations.CollectionGet)
                .Should()
                .NotContain(
                    PartitionRequestValidator.NumberParameter,
                    "the count key is a filterable resource property name on a collection GET"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Partitions_Exclusions : ReservedQueryParametersTests
    {
        [Test]
        public void It_matches_the_ordinal_names_the_partition_middleware_excludes_today()
        {
            ReservedQueryParameters
                .OrdinalFilterExclusionsOn(ReservedQueryParameterOperations.Partitions)
                .Should()
                .BeEquivalentTo(
                    _partitionsOrdinalExclusionsToday,
                    "the set must be identical; only the order differs, and filter exclusion is set "
                        + "semantics, so the served unsupported-parameter message order is unaffected"
                );
        }

        [Test]
        public void It_adds_only_the_partition_count_to_the_collection_get_exclusions()
        {
            string[] expected =
            [
                .. ReservedQueryParameters.OrdinalFilterExclusionsOn(
                    ReservedQueryParameterOperations.CollectionGet
                ),
                PartitionRequestValidator.NumberParameter,
            ];

            ReservedQueryParameters
                .OrdinalFilterExclusionsOn(ReservedQueryParameterOperations.Partitions)
                .Should()
                .BeEquivalentTo(expected);
        }

        [Test]
        public void It_matches_the_case_insensitive_names_the_partition_middleware_excludes_today()
        {
            ReservedQueryParameters
                .IgnoreCaseFilterExclusionsOn(ReservedQueryParameterOperations.Partitions)
                .Should()
                .Equal(ChangeVersionParameterValidator.ReservedParameterNames);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Change_Query_Exclusions : ReservedQueryParametersTests
    {
        [Test]
        public void It_matches_the_ordinal_names_the_query_middleware_excludes_today()
        {
            ReservedQueryParameters
                .OrdinalFilterExclusionsOn(ReservedQueryParameterOperations.ChangeQueries)
                .Should()
                .Equal(
                    _collectionGetOrdinalExclusionsToday,
                    "one step serves both pipelines from one exclusion array, so the Change Query "
                        + "operations exclude exactly what the collection GET excludes"
                );
        }

        [Test]
        public void It_matches_the_case_insensitive_names_the_query_middleware_excludes_today()
        {
            ReservedQueryParameters
                .IgnoreCaseFilterExclusionsOn(ReservedQueryParameterOperations.ChangeQueries)
                .Should()
                .Equal(ChangeVersionParameterValidator.ReservedParameterNames);
        }

        [Test]
        public void It_excludes_the_cursor_parameters_the_change_query_step_rejects_by_name()
        {
            ReservedQueryParameters
                .OrdinalFilterExclusionsOn(ReservedQueryParameterOperations.ChangeQueries)
                .Should()
                .Contain(
                    CursorRequestValidator.CursorParameters,
                    "excluding them from filter matching is what lets the Change Query step report "
                        + "them by name instead of as unknown query fields"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Operation_That_Is_Not_Exactly_One : ReservedQueryParametersTests
    {
        private static IEnumerable<ReservedQueryParameterOperations> AmbiguousOperations()
        {
            yield return ReservedQueryParameterOperations.None;
            yield return ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions;
            yield return ReservedQueryParameterOperations.CollectionGet
                | ReservedQueryParameterOperations.Partitions
                | ReservedQueryParameterOperations.ChangeQueries;
            yield return (ReservedQueryParameterOperations)64;
        }

        [TestCaseSource(nameof(AmbiguousOperations))]
        public void It_refuses_to_answer_ordinal_exclusions(ReservedQueryParameterOperations operation)
        {
            Action act = () => ReservedQueryParameters.OrdinalFilterExclusionsOn(operation);

            act.Should()
                .Throw<ArgumentOutOfRangeException>(
                    "a combination has two plausible readings that produce different exclusion sets"
                );
        }

        [TestCaseSource(nameof(AmbiguousOperations))]
        public void It_refuses_to_answer_case_insensitive_exclusions(
            ReservedQueryParameterOperations operation
        )
        {
            Action act = () => ReservedQueryParameters.IgnoreCaseFilterExclusionsOn(operation);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
