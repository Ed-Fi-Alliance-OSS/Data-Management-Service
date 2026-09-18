// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Validation;

/// <summary>
/// Pins the query parameter names DMS consumes as control parameters, and therefore removes from
/// resource-filter matching, to the names MetaEd refuses as property names.
/// </summary>
/// <remarks>
/// The upstream guard is the NoPagingPropertyNames validator in
/// <c>MetaEd-js/packages/metaed-plugin-edfi-unified/src/validator/CrossProperty</c>, which refuses these
/// names for a model targeting Ed-Fi API 8.1 or later (DMS-1442). It carries its own copy of the list,
/// because a MetaEd build cannot read DMS constants. A name added here, removed here, or re-spelled here
/// without the matching upstream change leaves an extension author free to declare a property that is then
/// unfilterable on the operations reserving it, with no warning anywhere. Asserting constants against
/// literals is the point: this test exists to fail when the two sides drift, and its failure message is the
/// reminder to change MetaEd in the same release.
/// </remarks>
[TestFixture]
[Parallelizable]
public class Given_The_Reserved_Query_Parameter_Names
{
    /// <summary>
    /// Every name DMS reserves, read from the validators that own the spellings rather than re-typed. The
    /// same name is reserved by more than one operation, so this carries duplicates by construction.
    /// </summary>
    private static readonly string[] _reservedByDms =
    [
        CursorRequestValidator.LimitParameter,
        CursorRequestValidator.OffsetParameter,
        CursorRequestValidator.TotalCountParameter,
        .. CursorRequestValidator.CursorParameters,
        .. ChangeVersionParameterValidator.ReservedParameterNames,
        .. PartitionRequestValidator.ReservedParameters,
        PartitionRequestValidator.NumberParameter,
    ];

    /// <summary>
    /// The names the upstream MetaEd validator refuses, spelled as they are published.
    /// </summary>
    private static readonly string[] _refusedByMetaEd =
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

    [Test]
    public void It_is_exactly_the_set_the_upstream_validator_refuses()
    {
        _reservedByDms.Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(_refusedByMetaEd);
    }

    [Test]
    public void It_has_one_lower_cased_name_per_reserved_name()
    {
        _reservedByDms
            .Select(name => name.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Should()
            .HaveCount(
                _refusedByMetaEd.Length,
                "the upstream validator compares lower-cased names, so two reserved names differing only "
                    + "in case could not both be refused"
            );
    }
}
