// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Text;
using System.Text;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_Cursor_Token_Construction
{
    [Test]
    public void It_round_trips_a_from_token_as_a_document_id_range_unbounded_above()
    {
        string token = PerfCursorTokens.DocumentIdRangeFrom(249_988);

        PerfCursorTokens
            .TryDecodeDocumentIdRange(token, out long inclusiveMinimum, out long inclusiveMaximum)
            .Should()
            .BeTrue();
        inclusiveMinimum.Should().Be(249_988);
        inclusiveMaximum.Should().Be(long.MaxValue);
    }

    [Test]
    public void It_emits_distinct_tokens_for_distinct_starts()
    {
        PerfCursorTokens.DocumentIdRangeFrom(1).Should().NotBe(PerfCursorTokens.DocumentIdRangeFrom(2));
    }

    [Test]
    public void It_emits_url_safe_unpadded_token_text()
    {
        string token = PerfCursorTokens.DocumentIdRangeFrom(555_561);

        token.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Test]
    public void It_decodes_a_bounded_server_issued_range()
    {
        string token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("d,10,99"));

        PerfCursorTokens
            .TryDecodeDocumentIdRange(token, out long inclusiveMinimum, out long inclusiveMaximum)
            .Should()
            .BeTrue();
        inclusiveMinimum.Should().Be(10);
        inclusiveMaximum.Should().Be(99);
    }

    [Test]
    public void It_rejects_a_change_version_anchored_token()
    {
        string token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("c,10,99"));

        PerfCursorTokens.TryDecodeDocumentIdRange(token, out _, out _).Should().BeFalse();
    }

    [Test]
    public void It_rejects_malformed_token_text()
    {
        PerfCursorTokens.TryDecodeDocumentIdRange("not a token!", out _, out _).Should().BeFalse();
        PerfCursorTokens.TryDecodeDocumentIdRange(null, out _, out _).Should().BeFalse();
        PerfCursorTokens.TryDecodeDocumentIdRange(string.Empty, out _, out _).Should().BeFalse();
    }
}
