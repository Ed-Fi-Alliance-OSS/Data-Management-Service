// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

/// <summary>
/// The page token is the only client-visible form of a cursor range, so its grammar is pinned in both
/// directions: what the encoder emits, and the exact set of inputs the decoder accepts.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Page_Token_Codec
{
    /// <summary>Encodes arbitrary payload text the way a well-formed token encodes its range.</summary>
    private static string TokenFor(string payload) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

    /// <summary>Reads the payload text back out of a token, to assert the wire format itself.</summary>
    private static string PayloadOf(string token) =>
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));

    private static string Padded(string token) => token + new string('=', (4 - (token.Length % 4)) % 4);

    [Test]
    public void It_is_not_visible_outside_Core()
    {
        typeof(PageTokenCodec).IsPublic.Should().BeFalse();
    }

    [Test]
    public void It_is_not_reachable_from_backend_production_code()
    {
        typeof(PageTokenCodec)
            .Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(static attribute => attribute.AssemblyName)
            .Should()
            .NotContain("EdFi.DataManagementService.Backend");
    }

    [Test]
    public void It_encodes_the_marker_and_both_bounds_as_three_comma_separated_fields()
    {
        PayloadOf(PageTokenCodec.Encode(new CursorRange(10, 2509), PageOrderingMode.DocumentId))
            .Should()
            .Be("d,10,2509");
    }

    [Test]
    public void It_encodes_a_content_version_anchored_range_with_the_content_version_marker()
    {
        PayloadOf(PageTokenCodec.Encode(new CursorRange(10, 2509), PageOrderingMode.ContentVersion))
            .Should()
            .Be("c,10,2509");
    }

    [Test]
    public void It_encodes_negative_bounds_with_a_leading_minus()
    {
        PayloadOf(PageTokenCodec.Encode(new CursorRange(-20, -5), PageOrderingMode.DocumentId))
            .Should()
            .Be("d,-20,-5");
    }

    [Test]
    public void It_never_emits_the_empty_maximum_form()
    {
        PayloadOf(PageTokenCodec.Encode(CursorRange.From(5), PageOrderingMode.DocumentId))
            .Should()
            .Be("d,5,9223372036854775807");
    }

    [Test]
    public void It_emits_canonical_unpadded_base64url()
    {
        string token = PageTokenCodec.Encode(new CursorRange(10, 2509), PageOrderingMode.DocumentId);

        token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [TestCase(0L, 0L)]
    [TestCase(1L, 500L)]
    [TestCase(-5L, -1L)]
    [TestCase(2510L, 2509L)]
    [TestCase(long.MinValue, long.MaxValue)]
    public void It_round_trips_a_document_id_anchored_range(long inclusiveMinimum, long inclusiveMaximum)
    {
        CursorRange range = new(inclusiveMinimum, inclusiveMaximum);

        PageTokenCodec
            .TryDecode(
                PageTokenCodec.Encode(range, PageOrderingMode.DocumentId),
                out CursorRange? decoded,
                out PageOrderingMode decodedOrderingMode
            )
            .Should()
            .BeTrue();
        decoded.Should().Be(range);
        decodedOrderingMode.Should().Be(PageOrderingMode.DocumentId);
    }

    [TestCase(0L, 0L)]
    [TestCase(1L, 500L)]
    [TestCase(-5L, -1L)]
    [TestCase(2510L, 2509L)]
    [TestCase(long.MinValue, long.MaxValue)]
    public void It_round_trips_a_content_version_anchored_range(long inclusiveMinimum, long inclusiveMaximum)
    {
        CursorRange range = new(inclusiveMinimum, inclusiveMaximum);

        PageTokenCodec
            .TryDecode(
                PageTokenCodec.Encode(range, PageOrderingMode.ContentVersion),
                out CursorRange? decoded,
                out PageOrderingMode decodedOrderingMode
            )
            .Should()
            .BeTrue();
        decoded.Should().Be(range);
        decodedOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_accepts_a_correctly_padded_token()
    {
        string unpadded = PageTokenCodec.Encode(new CursorRange(10, 2509), PageOrderingMode.DocumentId);

        PageTokenCodec.TryDecode(Padded(unpadded), out CursorRange? decoded, out _).Should().BeTrue();
        decoded.Should().Be(new CursorRange(10, 2509));
    }

    [Test]
    public void It_decodes_an_empty_maximum_as_unbounded_above()
    {
        PageTokenCodec.TryDecode(TokenFor("d,5,"), out CursorRange? decoded, out _).Should().BeTrue();
        decoded.Should().Be(new CursorRange(5, long.MaxValue));
    }

    [Test]
    public void It_decodes_an_empty_maximum_on_a_content_version_token_as_unbounded_above()
    {
        PageTokenCodec
            .TryDecode(TokenFor("c,5,"), out CursorRange? decoded, out PageOrderingMode decodedMode)
            .Should()
            .BeTrue();
        decoded.Should().Be(new CursorRange(5, long.MaxValue));
        decodedMode.Should().Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_accepts_the_signed_extremes()
    {
        PageTokenCodec
            .TryDecode(
                TokenFor("d,-9223372036854775808,9223372036854775807"),
                out CursorRange? decoded,
                out _
            )
            .Should()
            .BeTrue();
        decoded.Should().Be(new CursorRange(long.MinValue, long.MaxValue));
    }

    [TestCase("d,-20,-5", -20L, -5L, TestName = "It_accepts_negative_bounds")]
    [TestCase("d,2510,2509", 2510L, 2509L, TestName = "It_accepts_an_inverted_match_nothing_range")]
    public void It_accepts_a_valid_payload(string payload, long expectedMinimum, long expectedMaximum)
    {
        PageTokenCodec.TryDecode(TokenFor(payload), out CursorRange? decoded, out _).Should().BeTrue();
        decoded.Should().Be(new CursorRange(expectedMinimum, expectedMaximum));
    }

    [TestCase(null, TestName = "It_rejects_a_null_token")]
    [TestCase("", TestName = "It_rejects_an_empty_token")]
    [TestCase("=", TestName = "It_rejects_padding_only")]
    [TestCase("MTAs+jUwOQ", TestName = "It_rejects_the_plus_character")]
    [TestCase("MTAs/jUwOQ", TestName = "It_rejects_the_slash_character")]
    [TestCase("MTAsM", TestName = "It_rejects_an_impossible_base64url_length")]
    // A permissive base64 reader skips whitespace; the token grammar does not allow it anywhere.
    [TestCase("MTAs MjUwOQ", TestName = "It_rejects_embedded_whitespace")]
    [TestCase(" MTAsMjUwOQ", TestName = "It_rejects_leading_whitespace_in_the_token")]
    [TestCase("MTAsMjUwOQ ", TestName = "It_rejects_trailing_whitespace_in_the_token")]
    [TestCase("MTAsMjUw\nOQ", TestName = "It_rejects_an_embedded_newline")]
    public void It_rejects_a_malformed_token(string? pageToken)
    {
        PageTokenCodec.TryDecode(pageToken, out CursorRange? decoded, out _).Should().BeFalse();
        decoded.Should().BeNull();
    }

    [Test]
    public void It_rejects_internal_padding()
    {
        string token = PageTokenCodec.Encode(new CursorRange(10, 2509), PageOrderingMode.DocumentId);
        string withInternalPadding = token[..2] + "=" + token[2..];

        PageTokenCodec.TryDecode(withInternalPadding, out _, out _).Should().BeFalse();
    }

    [Test]
    public void It_rejects_more_padding_than_required()
    {
        string token = PageTokenCodec.Encode(new CursorRange(10, 2509), PageOrderingMode.DocumentId);

        PageTokenCodec.TryDecode(Padded(token) + "=", out _, out _).Should().BeFalse();
    }

    [Test]
    public void It_rejects_incomplete_padding()
    {
        string token = TokenFor("d,100,2000");
        token.Length.Should().Be(14, "this payload encodes to a length requiring two padding characters");

        PageTokenCodec.TryDecode(token + "=", out _, out _).Should().BeFalse();
    }

    [Test]
    public void It_rejects_padding_that_is_not_required()
    {
        string token = TokenFor("d,1,20");
        token.Length.Should().Be(8, "this payload encodes to a length requiring no padding");

        PageTokenCodec.TryDecode(token + "=", out _, out _).Should().BeFalse();
    }

    [Test]
    public void It_rejects_invalid_utf8_payload_bytes()
    {
        string token = Base64Url.EncodeToString([0xFF, 0xFE]);

        PageTokenCodec.TryDecode(token, out _, out _).Should().BeFalse();
    }

    // Marker grammar. A token whose marker cannot be read names no anchor, so its bounds cannot be
    // read against a column at all — the same standing as a malformed bound, not a defaulted anchor.
    [TestCase("d", TestName = "It_rejects_a_marker_only")]
    [TestCase("d,10", TestName = "It_rejects_a_marker_and_one_bound")]
    [TestCase("10,20", TestName = "It_rejects_the_two_field_payload_with_no_marker")]
    [TestCase("d,10,20,30", TestName = "It_rejects_four_fields")]
    [TestCase("x,10,20", TestName = "It_rejects_an_unknown_marker")]
    [TestCase("1,10,20", TestName = "It_rejects_a_numeric_marker")]
    [TestCase("D,10,20", TestName = "It_rejects_an_upper_case_document_id_marker")]
    [TestCase("C,10,20", TestName = "It_rejects_an_upper_case_content_version_marker")]
    [TestCase("dc,10,20", TestName = "It_rejects_a_two_character_marker")]
    [TestCase(",10,20", TestName = "It_rejects_an_empty_marker")]
    [TestCase(" d,10,20", TestName = "It_rejects_leading_whitespace_in_the_marker")]
    [TestCase("d ,10,20", TestName = "It_rejects_trailing_whitespace_in_the_marker")]
    // Bound grammar, re-asserted against the three-field payload.
    [TestCase("d,,20", TestName = "It_rejects_an_empty_minimum")]
    [TestCase("d,,", TestName = "It_rejects_two_empty_bounds")]
    [TestCase("d, 10,20", TestName = "It_rejects_leading_whitespace")]
    [TestCase("d,10 ,20", TestName = "It_rejects_trailing_whitespace_in_the_minimum")]
    [TestCase("d,10, 20", TestName = "It_rejects_leading_whitespace_in_the_maximum")]
    [TestCase("d,10,20 ", TestName = "It_rejects_trailing_whitespace")]
    [TestCase("d,+10,20", TestName = "It_rejects_a_leading_plus_in_the_minimum")]
    [TestCase("d,10,+20", TestName = "It_rejects_a_leading_plus_in_the_maximum")]
    [TestCase("d,-,20", TestName = "It_rejects_a_bare_minus")]
    [TestCase("d,1a,20", TestName = "It_rejects_a_non_digit_in_the_minimum")]
    [TestCase("d,10,2b", TestName = "It_rejects_a_non_digit_in_the_maximum")]
    [TestCase("d,1.0,20", TestName = "It_rejects_a_decimal_point")]
    [TestCase("d,9223372036854775808,1", TestName = "It_rejects_a_minimum_above_Int64")]
    [TestCase("d,-9223372036854775809,1", TestName = "It_rejects_a_minimum_below_Int64")]
    [TestCase("d,1,9223372036854775808", TestName = "It_rejects_a_maximum_above_Int64")]
    [TestCase("c,1a,20", TestName = "It_rejects_a_malformed_bound_on_a_content_version_token")]
    public void It_rejects_a_malformed_payload(string payload)
    {
        PageTokenCodec.TryDecode(TokenFor(payload), out CursorRange? decoded, out _).Should().BeFalse();
        decoded.Should().BeNull();
    }

    /// <summary>
    /// A rejected token reports no anchor even when its marker was the readable part, so a caller that
    /// ignores the return value cannot act on an anchor no valid token supplied.
    /// </summary>
    [Test]
    public void It_reports_no_anchor_for_a_token_whose_marker_parsed_but_whose_bound_did_not()
    {
        PageTokenCodec
            .TryDecode(TokenFor("c,1a,20"), out _, out PageOrderingMode decodedOrderingMode)
            .Should()
            .BeFalse();
        decodedOrderingMode.Should().Be(PageOrderingMode.DocumentId);
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Selected_Page_Below_The_Maximum_Anchor
{
    private bool _created;
    private string? _nextPageToken;

    [SetUp]
    public void Setup()
    {
        _created = PageTokenCodec.TryCreateNextPageToken(
            highestSelectedAnchor: 100,
            maximumAnchor: 2509,
            PageOrderingMode.DocumentId,
            out _nextPageToken
        );
    }

    [Test]
    public void It_creates_a_next_page_token()
    {
        _created.Should().BeTrue();
        _nextPageToken.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void It_advances_past_the_highest_selected_anchor_and_retains_the_maximum()
    {
        PageTokenCodec
            .TryDecode(_nextPageToken, out CursorRange? decoded, out PageOrderingMode decodedOrderingMode)
            .Should()
            .BeTrue();
        decoded.Should().Be(new CursorRange(101, 2509));
        decodedOrderingMode.Should().Be(PageOrderingMode.DocumentId);
    }
}

/// <summary>
/// The continuation of a max-bearing windowed page. Advancing by one is the same predicate as a strict
/// inequality over an integer sequence, which is why the anchor reuses the inclusive bound shape.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Selected_Page_Anchored_On_Content_Version
{
    private string? _nextPageToken;

    [SetUp]
    public void Setup()
    {
        PageTokenCodec.TryCreateNextPageToken(
            highestSelectedAnchor: 100,
            maximumAnchor: 2509,
            PageOrderingMode.ContentVersion,
            out _nextPageToken
        );
    }

    [Test]
    public void It_stamps_the_content_version_anchor_on_the_token()
    {
        PageTokenCodec
            .TryDecode(_nextPageToken, out CursorRange? decoded, out PageOrderingMode decodedOrderingMode)
            .Should()
            .BeTrue();
        decoded.Should().Be(new CursorRange(101, 2509));
        decodedOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Selected_Page_At_A_Bounded_Partition_Upper_Bound
{
    private string? _nextPageToken;

    [SetUp]
    public void Setup()
    {
        PageTokenCodec.TryCreateNextPageToken(
            highestSelectedAnchor: 2509,
            maximumAnchor: 2509,
            PageOrderingMode.DocumentId,
            out _nextPageToken
        );
    }

    [Test]
    public void It_creates_an_inverted_range_that_ends_the_partition_walk()
    {
        PageTokenCodec.TryDecode(_nextPageToken, out CursorRange? decoded, out _).Should().BeTrue();
        decoded.Should().Be(new CursorRange(2510, 2509));
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Selected_Page_Entered_From_An_Unbounded_Walk
{
    private string? _nextPageToken;

    [SetUp]
    public void Setup()
    {
        PageTokenCodec.TryCreateNextPageToken(
            highestSelectedAnchor: 100,
            maximumAnchor: long.MaxValue,
            PageOrderingMode.DocumentId,
            out _nextPageToken
        );
    }

    [Test]
    public void It_stays_unbounded_above()
    {
        PageTokenCodec.TryDecode(_nextPageToken, out CursorRange? decoded, out _).Should().BeTrue();
        decoded.Should().Be(new CursorRange(101, long.MaxValue));
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Selected_Page_At_The_Largest_Anchor_Value
{
    private bool _created;
    private string? _nextPageToken;

    [SetUp]
    public void Setup()
    {
        _created = PageTokenCodec.TryCreateNextPageToken(
            highestSelectedAnchor: long.MaxValue,
            maximumAnchor: long.MaxValue,
            PageOrderingMode.DocumentId,
            out _nextPageToken
        );
    }

    [Test]
    public void It_declines_to_create_a_next_page_token_rather_than_overflowing()
    {
        _created.Should().BeFalse();
    }

    [Test]
    public void It_produces_no_token()
    {
        _nextPageToken.Should().BeNull();
    }
}
