// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

internal static class ParameterValidationMetadataTestHelpers
{
    public static string[] WireNames(this ParameterValidationMetadata metadata) =>
        metadata.Parameters.Select(p => p.WireName).ToArray();

    public static Func<string, bool> ParserFor(this ParameterValidationMetadata metadata, string wireName) =>
        metadata
            .Parameters.Single(p => p.WireName.Equals(wireName, StringComparison.OrdinalIgnoreCase))
            .IsBindable;
}

[TestFixture]
public class Given_Parameter_Validation_Metadata_For_A_Vendor_Query
{
    private ParameterValidationMetadata _metadata = null!;

    [SetUp]
    public void Setup() => _metadata = ParameterValidationMetadata.ForQueryType(typeof(FrontendVendorQuery));

    [Test]
    public void It_collects_offset_limit_and_the_integer_id_filter_in_order()
    {
        // string filters (company, contactName, ...) are excluded; only integer query params remain.
        _metadata.WireNames().Should().Equal("offset", "limit", "id");
    }

    [Test]
    public void It_deduplicates_hidden_inherited_offset_and_limit_to_a_single_entry()
    {
        _metadata
            .Parameters.Count(p => p.WireName.Equals("offset", StringComparison.OrdinalIgnoreCase))
            .Should()
            .Be(1);
        _metadata
            .Parameters.Count(p => p.WireName.Equals("limit", StringComparison.OrdinalIgnoreCase))
            .Should()
            .Be(1);
    }

    [TestCase("0", true)]
    [TestCase("-1", true)]
    [TestCase("+3", true)]
    [TestCase("  5  ", true)] // surrounding whitespace allowed (NumberStyles.Integer)
    [TestCase("abc", false)]
    [TestCase("", false)]
    [TestCase("1.5", false)]
    [TestCase("1,000", false)] // group separator not allowed under invariant Integer parsing
    [TestCase("9999999999", false)] // overflows Int32
    public void It_parses_integer_parameters_with_minimal_api_invariant_semantics(string value, bool expected)
    {
        _metadata.ParserFor("offset")(value).Should().Be(expected);
    }

    [TestCase("9999999999", true)] // fits Int64
    [TestCase("-9999999999", true)]
    [TestCase("abc", false)]
    [TestCase("1.5", false)]
    public void It_parses_long_parameters_with_minimal_api_invariant_semantics(string value, bool expected)
    {
        // Vendor 'id' is a long? filter.
        _metadata.ParserFor("id")(value).Should().Be(expected);
    }
}

[TestFixture]
public class Given_Parameter_Validation_Metadata_For_An_Api_Client_Query
{
    [Test]
    public void It_uses_the_from_query_wire_name()
    {
        var metadata = ParameterValidationMetadata.ForQueryType(typeof(FrontendApiClientQuery));

        // ApiClient declares [FromQuery(Name = "applicationid")] (lowercase wire name).
        metadata.WireNames().Should().Equal("offset", "limit", "applicationid");
    }
}

[TestFixture]
public class Given_Parameter_Validation_Metadata_For_An_Application_Query
{
    [Test]
    public void It_excludes_string_query_parameters_including_the_ids_csv()
    {
        var metadata = ParameterValidationMetadata.ForQueryType(typeof(FrontendApplicationQuery));

        // 'ids' (string CSV) and other string filters are excluded; only integer 'id' remains.
        metadata.WireNames().Should().Equal("offset", "limit", "id");
    }
}

[TestFixture]
public class Given_Parameter_Validation_Metadata_For_A_Query_Without_Integer_Filters
{
    [Test]
    public void It_returns_only_offset_and_limit()
    {
        var metadata = ParameterValidationMetadata.ForQueryType(typeof(FrontendResourceClaimActionQuery));

        metadata.WireNames().Should().Equal("offset", "limit");
    }
}

[TestFixture]
public class Given_Parameter_Validation_Metadata_For_A_Query_With_Multiple_Integer_Filters
{
    [Test]
    public void It_orders_remaining_integer_parameters_by_wire_name_ordinal_ignore_case()
    {
        var metadata = ParameterValidationMetadata.ForQueryType(typeof(SortProbeQuery));

        // offset, limit first; then the remaining integer parameters sorted ordinal-ignore-case.
        metadata.WireNames().Should().Equal("offset", "limit", "alpha", "Zebra");
    }

    // Probe DTO exercising the deterministic ordering of multiple non-offset/limit integer parameters.
    private sealed class SortProbeQuery : FrontendPagingQuery
    {
        [FromQuery(Name = "Zebra")]
        public int? Zebra { get; set; }

        [FromQuery(Name = "alpha")]
        public long? Alpha { get; set; }
    }
}

[TestFixture]
public class Given_Parameter_Validation_Metadata_For_A_Query_With_Shadowed_Properties
{
    private ParameterValidationMetadata _metadata = null!;

    [SetUp]
    public void Setup() => _metadata = ParameterValidationMetadata.ForQueryType(typeof(ShadowDerivedProbe));

    [Test]
    public void It_deduplicates_the_shadowed_property_case_insensitively_to_a_single_entry()
    {
        _metadata
            .Parameters.Count(p => p.WireName.Equals("dup", StringComparison.OrdinalIgnoreCase))
            .Should()
            .Be(1);
    }

    [Test]
    public void It_keeps_the_most_derived_wire_name()
    {
        // The derived declaration's wire name ("DUP") wins over the base ("dup").
        _metadata.WireNames().Should().Contain("DUP");
    }

    [Test]
    public void It_uses_the_most_derived_property_parser()
    {
        // Derived 'Dup' is int?; base 'Dup' is long?. A value that overflows Int32 but fits Int64 must be
        // rejected, proving the derived (int) parser won rather than the base (long) parser.
        _metadata.ParserFor("dup")("9999999999").Should().BeFalse();
    }

    // Base declares a long? 'dup'; the derived hides it with an int? whose wire name differs only by case.
    private class ShadowBaseProbe : FrontendPagingQuery
    {
        [FromQuery(Name = "dup")]
        public long? Dup { get; set; }
    }

    private sealed class ShadowDerivedProbe : ShadowBaseProbe
    {
        [FromQuery(Name = "DUP")]
        public new int? Dup { get; set; }
    }
}
