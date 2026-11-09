// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Numerics;
using System.Text;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Paging.CursorPagingPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
[Category("CursorPagingProperty")]
public class Given_Generated_Page_Tokens
{
    [Test]
    public void It_matches_the_wire_oracle_and_round_trips_exact_bounds(
        [Values] PageOrderingMode mode,
        [Values("arbitrary", "equal", "inverted", "minimum", "maximum", "extremes")] string shape
    )
    {
        Check(
            Generate(shape),
            bounds =>
            {
                var range = new CursorRange(bounds.Minimum, bounds.Maximum);
                var token = PageTokenCodec.Encode(range, mode);
                token.Should().Be(Token(bounds, mode));
                AssertDecoded(token, range, mode);
                AssertDecoded(token.PadRight((token.Length + 3) / 4 * 4, '='), range, mode);
            },
            $"mode={mode}, shape={shape}"
        );
    }

    [Test]
    public void It_accepts_empty_maxima_and_noncanonical_signed_decimal_spellings(
        [Values] PageOrderingMode mode
    )
    {
        Check(
            Generate(),
            bounds =>
            {
                var minimum = Decimal(bounds.Minimum);
                var withZeros = minimum.StartsWith('-') ? "-000" + minimum[1..] : "000" + minimum;
                AssertDecoded(
                    Token($"{Marker(mode)},{withZeros},"),
                    new(bounds.Minimum, long.MaxValue),
                    mode
                );
                AssertDecoded(
                    Token($"{Marker(mode)},-0,{Decimal(bounds.Maximum)}"),
                    new(0, bounds.Maximum),
                    mode
                );
            },
            $"mode={mode}, empty maximum/leading zeros/signed zero"
        );
    }

    [Test]
    public void It_rejects_each_corrupted_grammar_component(
        [Values] PageOrderingMode mode,
        [Values("marker", "fields", "minimum", "maximum", "overflow", "base64", "padding", "utf8")]
            string corruption
    )
    {
        Check(
            Generate(),
            bounds =>
            {
                var marker = Marker(mode);
                var minimum = Decimal(bounds.Minimum);
                var maximum = Decimal(bounds.Maximum);
                var token = Token(bounds, mode);
                string[] invalid = corruption switch
                {
                    "marker" =>
                    [
                        Token($"{marker.ToUpperInvariant()},{minimum},{maximum}"),
                        Token($"x,{minimum},{maximum}"),
                        Token($",{minimum},{maximum}"),
                    ],
                    "fields" =>
                    [
                        Token($"{marker},{minimum}"),
                        Token($"{minimum},{maximum}"),
                        Token($"{marker},{minimum},{maximum},0"),
                    ],
                    "minimum" =>
                    [
                        .. new[]
                        {
                            "",
                            "-",
                            "+1",
                            " " + minimum,
                            minimum + " ",
                            minimum + ".0",
                            "١",
                            "−1",
                            minimum + "e0",
                        }.Select(value => Token($"{marker},{value},{maximum}")),
                    ],
                    "maximum" =>
                    [
                        .. new[]
                        {
                            "-",
                            "+1",
                            " " + maximum,
                            maximum + " ",
                            maximum + ".0",
                            "١",
                            "−1",
                            maximum + "e0",
                        }.Select(value => Token($"{marker},{minimum},{value}")),
                    ],
                    "overflow" =>
                    [
                        Token($"{marker},9223372036854775808,{maximum}"),
                        Token($"{marker},-9223372036854775809,{maximum}"),
                        Token($"{marker},{minimum},9223372036854775808"),
                        Token($"{marker},{minimum},-9223372036854775809"),
                    ],
                    "base64" =>
                    [
                        token.Insert(1, "+"),
                        token.Insert(1, "/"),
                        token.Insert(1, " "),
                        token + "\n",
                        "A",
                    ],
                    "padding" =>
                    [
                        token.Insert(1, "="),
                        token.PadRight((token.Length + 3) / 4 * 4, '=') + "=",
                        token + "===",
                        // Force a payload requiring two padding characters, then supply only one.
                        Token($"{marker},100,2000") + "=",
                    ],
                    "utf8" =>
                    [
                        .. new byte[][]
                        {
                            [0x80],
                            [0xC0, 0xAF],
                            [0xE2, 0x82],
                            [0xED, 0xA0, 0x80],
                            [0xF4, 0x90, 0x80, 0x80],
                            [0xFF],
                        }.Select(bytes =>
                            Token([
                                .. Encoding.UTF8.GetBytes($"{marker},{minimum},"),
                                .. bytes,
                                .. Encoding.UTF8.GetBytes(maximum),
                            ])
                        ),
                    ],
                    _ => throw new InvalidOperationException($"Unknown case: {corruption}"),
                };
                foreach (var malformed in invalid)
                {
                    PageTokenCodec
                        .TryDecode(malformed, out var decoded, out _)
                        .Should()
                        .BeFalse($"corruption={corruption}, token={malformed}");
                    decoded.Should().BeNull();
                }
            },
            $"mode={mode}, corruption={corruption}"
        );
    }

    [Test]
    public void It_advances_exactly_one_without_overflow_and_preserves_partition_maxima(
        [Values] PageOrderingMode mode,
        [Values("generated", "minimum", "negative-one", "zero", "one", "penultimate", "maximum")]
            string anchorClass
    )
    {
        Check(
            Generate(),
            bounds =>
            {
                var anchor = anchorClass switch
                {
                    "generated" => bounds.Minimum,
                    "minimum" => long.MinValue,
                    "negative-one" => -1,
                    "zero" => 0,
                    "one" => 1,
                    "penultimate" => long.MaxValue - 1,
                    "maximum" => long.MaxValue,
                    _ => throw new InvalidOperationException($"Unknown case: {anchorClass}"),
                };
                // Exercise an arbitrary ceiling, a partition's terminal page, and an unbounded walk.
                foreach (var maximum in new[] { bounds.Maximum, anchor, long.MaxValue })
                {
                    var successor = (BigInteger)anchor + 1;
                    var created = PageTokenCodec.TryCreateNextPageToken(anchor, maximum, mode, out var token);
                    created.Should().Be(successor <= long.MaxValue);
                    if (successor > long.MaxValue)
                    {
                        token.Should().BeNull();
                    }
                    else
                    {
                        token.Should().Be(Token(new Bounds((long)successor, maximum), mode));
                        AssertDecoded(token!, new((long)successor, maximum), mode);
                    }
                }
            },
            $"mode={mode}, anchor={anchorClass}"
        );
    }

    private static void AssertDecoded(string token, CursorRange expected, PageOrderingMode mode)
    {
        PageTokenCodec.TryDecode(token, out var decoded, out var decodedMode).Should().BeTrue();
        decoded.Should().Be(expected);
        decodedMode.Should().Be(mode);
    }
}
