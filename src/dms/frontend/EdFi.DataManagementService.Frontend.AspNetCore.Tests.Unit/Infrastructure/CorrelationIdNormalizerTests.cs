// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
[Parallelizable]
public class CorrelationIdNormalizerTests
{
    [TestFixture]
    public class Given_A_Well_Formed_Correlation_Id : CorrelationIdNormalizerTests
    {
        private const string WellFormed = "test-correlationId";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(
                WellFormed,
                AppSettings.DefaultCorrelationIdMaxLength
            );
        }

        [Test]
        public void It_passes_the_value_through_unchanged()
        {
            _result.Should().Be(WellFormed);
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_From_An_Upstream_Identifier_Scheme : CorrelationIdNormalizerTests
    {
        // FR-LOG-3 forbids narrowing the allowlist to alphanumerics: these characters are
        // ordinary in upstream identifier schemes and the stricter Method/Path allowlist
        // would strip every one of them.
        private const string UpstreamId = "a+b=c{d}e@f|g,h#i(j)k[l]m<n>o\"p'q";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(
                UpstreamId,
                AppSettings.DefaultCorrelationIdMaxLength
            );
        }

        [Test]
        public void It_preserves_printable_punctuation()
        {
            _result.Should().Be(UpstreamId);
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Containing_Control_Characters : CorrelationIdNormalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(
                "trace\r\nid\twith\0control",
                AppSettings.DefaultCorrelationIdMaxLength
            );
        }

        [Test]
        public void It_removes_every_control_character()
        {
            _result.Should().Be("traceidwithcontrol");
        }

        [Test]
        public void It_cannot_forge_an_additional_log_line()
        {
            _result.Should().NotContain("\r").And.NotContain("\n");
        }
    }

    [TestFixture]
    public class Given_An_Over_Length_Correlation_Id : CorrelationIdNormalizerTests
    {
        private string _resultAtDefaultBound = string.Empty;
        private string _resultAtConfiguredBound = string.Empty;

        [SetUp]
        public void Setup()
        {
            _resultAtDefaultBound = CorrelationIdNormalizer.Normalize(
                new string('a', AppSettings.DefaultCorrelationIdMaxLength + 45),
                AppSettings.DefaultCorrelationIdMaxLength
            );
            _resultAtConfiguredBound = CorrelationIdNormalizer.Normalize(new string('b', 100), 16);
        }

        [Test]
        public void It_truncates_to_the_default_maximum_length()
        {
            _resultAtDefaultBound.Should().Be(new string('a', AppSettings.DefaultCorrelationIdMaxLength));
        }

        [Test]
        public void It_truncates_to_the_host_configured_maximum_length()
        {
            _resultAtConfiguredBound.Should().Be(new string('b', 16));
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Exactly_At_The_Maximum_Length : CorrelationIdNormalizerTests
    {
        private const int MaxLength = 16;

        [Test]
        public void It_does_not_truncate_at_the_boundary()
        {
            // The readable statement of the rule - `value.Length > effectiveMaxLength`, so a
            // value of exactly the maximum length passes through whole - but on its own it
            // cannot fail: `value[..MaxLength]` of a MaxLength-long string is that same string,
            // so `>` and `>=` produce an identical result for this input. The test below is the
            // one that discriminates between them.
            string atTheBound = new('c', MaxLength);

            CorrelationIdNormalizer.Normalize(atTheBound, MaxLength).Should().Be(atTheBound);
        }

        [Test]
        public void It_does_not_enter_the_truncation_path_at_the_boundary()
        {
            // The only observable on which the two forms of the length test differ: the reported
            // Truncated fact. For a value of exactly MaxLength code units the two forms return
            // the same string whichever way the comparison goes - `value[..MaxLength]` of a
            // MaxLength-long string is that same string, and the surrogate back-off cannot fire
            // because there is no discarded code unit for it to look at - so the string alone
            // cannot tell them apart. Under the greater-than test the comparison is false and
            // nothing is reported; under a greater-than-or-equal test it is true, and every
            // request whose correlation ID sits exactly at the cap would be reported on the
            // CorrelationIdModified event as truncated when nothing was cut.
            //
            // This assertion previously used a value of exactly MaxLength code units ending in
            // an unpaired high surrogate, which survived under `>` and lost its last unit to the
            // back-off under `>=`. That input no longer discriminates: the allowlist now removes
            // an unpaired surrogate wherever it occurs, so both forms produce the same shorter
            // string. The flag is what is left, and it is the fact the event actually reports.
            string atTheBound = new('c', MaxLength);

            CorrelationIdNormalizer.NormalizeWithDetail(atTheBound, MaxLength).Truncated.Should().BeFalse();
        }

        [Test]
        public void It_truncates_one_character_past_the_boundary()
        {
            // The other side of the same boundary, so the pair pins the comparison rather than
            // just one of its two outcomes.
            CorrelationIdNormalizer
                .Normalize(new string('c', MaxLength + 1), MaxLength)
                .Should()
                .Be(new string('c', MaxLength));
        }
    }

    [TestFixture]
    public class Given_A_Single_Astral_Character_And_A_Maximum_Length_Of_One : CorrelationIdNormalizerTests
    {
        [Test]
        public void It_yields_an_empty_string_rather_than_an_orphaned_high_surrogate()
        {
            // Two UTF-16 code units cut to one lands on the high half, the surrogate guard drops
            // it, and the retained prefix is empty. That empty result is documented behavior, and
            // it is what the ingestion point's fallback to the server-generated identifier then
            // depends on: the alternative - emitting a lone surrogate - would break the
            // byte-identical log/body parity FR-LOG-6 guarantees.
            CorrelationIdNormalizer.Normalize("\U0001F600", 1).Should().Be(string.Empty);
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_That_Already_Contains_A_Lone_Surrogate : CorrelationIdNormalizerTests
    {
        // A surrogate half with no partner. Kestrel's default header decoding cannot produce
        // one, so this is unreachable over a real socket today - but "today" is a host-
        // configuration fact (RequestHeaderEncodingSelector with a non-replacement fallback
        // reopens it), and Normalize is also called on values that never came from a header at
        // all. The contract is therefore unconditional: the result never contains an unpaired
        // surrogate. These pin the half of that promise the allowlist keeps, as distinct from
        // the half the truncation back-off keeps.
        private const string WithLoneSurrogate = "ab\ud83dcd";

        [Test]
        public void It_removes_the_lone_surrogate_rather_than_preserving_it()
        {
            CorrelationIdNormalizer
                .Normalize(WithLoneSurrogate, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("abcd");
        }

        // Two things about this source are deliberate, and both have bitten this fixture.
        //
        // It is a TestCaseSource and not a [TestCase] because a custom attribute argument is
        // stored UTF-8-encoded in the assembly's attribute blob, and an unpaired surrogate is not
        // encodable in UTF-8: the compiler silently substitutes U+FFFD, and the test then runs
        // against a value that has already been repaired - passing whatever the code does. String
        // constants in a method body go through the UTF-16 user-string heap instead and arrive
        // intact.
        //
        // Every case also carries an explicit ASCII SetName. Without one, NUnit derives the case
        // name from the argument, the derived name carries the lone surrogate, and the VSTest
        // adapter then drops the case during discovery - it is not reported as failing or as
        // skipped, it simply never appears. A silently vanishing test is worse than a vacuous
        // one, so the names are pinned rather than derived.
        private static readonly TestCaseData[] EveryPositionAnUnpairedSurrogateCanTake =
        [
            new TestCaseData("\ud83dabcd").SetName("lone high surrogate at the start of the value"),
            new TestCaseData("ab\ud83dcd").SetName("lone high surrogate in the middle of the value"),
            // The decoder's NeedMoreData case, as opposed to InvalidData for the three others.
            new TestCaseData("abcd\ud83d").SetName("lone high surrogate at the end of the value"),
            new TestCaseData("ab\udc00cd").SetName("lone low surrogate in the middle of the value"),
        ];

        [TestCaseSource(nameof(EveryPositionAnUnpairedSurrogateCanTake))]
        public void It_removes_an_unpaired_surrogate_wherever_it_sits(string value)
        {
            // Position matters to the implementation even though it does not to the contract:
            // the decoder reports InvalidData for an interior half and NeedMoreData for a high
            // half that ends the string.
            CorrelationIdNormalizer
                .Normalize(value, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("abcd");
        }

        [Test]
        public void It_still_removes_control_characters_around_it()
        {
            // Removing the lone surrogate is not a reason to give up on the rest of the value:
            // the allowlist still applies to every other character.
            CorrelationIdNormalizer
                .Normalize($"\r{WithLoneSurrogate}\n", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("abcd");
        }

        [Test]
        public void It_reports_the_removal_as_a_character_removal()
        {
            // An unpaired surrogate the client sent is a character the allowlist removed, and is
            // reported as such on the CorrelationIdModified event - not silently, and not as a
            // truncation.
            CorrelationIdNormalizer.NormalizationDetail detail = CorrelationIdNormalizer.NormalizeWithDetail(
                WithLoneSurrogate,
                AppSettings.DefaultCorrelationIdMaxLength
            );

            detail.CharactersRemoved.Should().BeTrue();
            detail.Truncated.Should().BeFalse();
        }

        [Test]
        public void It_yields_an_empty_string_when_the_value_is_only_a_lone_surrogate()
        {
            // Which is what the ingestion point's fallback to the server-generated identifier
            // then acts on, exactly as for a value made up entirely of control characters.
            CorrelationIdNormalizer
                .Normalize("\ud83d", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_keeps_a_well_formed_pair_that_immediately_follows_a_lone_surrogate()
        {
            // Only the unpaired half goes. A genuine astral character adjacent to it is intact
            // Unicode and survives, so the rule cannot be implemented by skipping forward two
            // code units on a decode failure - that would swallow the pair's high half and
            // orphan its low half, turning one bad character into two.
            CorrelationIdNormalizer
                .Normalize("a\ud83d\ud83d\ude00b", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("a\U0001F600b");
        }

        [Test]
        public void It_keeps_a_well_formed_pair_with_no_lone_surrogate_anywhere_near_it()
        {
            CorrelationIdNormalizer
                .Normalize("trace-\U0001F600-id", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("trace-\U0001F600-id");
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_That_Is_Both_Over_Length_And_Hostile : CorrelationIdNormalizerTests
    {
        // The first MaxLength characters contain a control character, which is what makes the
        // two possible orders observably different. Truncate-then-filter yields "abcdefghi"
        // (nine characters); filter-then-truncate would yield "abcdefghij" (ten). This test
        // fails if the order in CorrelationIdNormalizer.Normalize is ever swapped.
        private const int MaxLength = 10;
        private const string Hostile = "ab\ncdefghijklmnopqrstuvwxyz";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(Hostile, MaxLength);
        }

        [Test]
        public void It_truncates_before_it_filters()
        {
            // This single equality is the whole order assertion: it rules out the
            // filter-then-truncate result "abcdefghij" and pins the length at MaxLength - 1.
            // A result shorter than the maximum length is the intended consequence of
            // truncating first, not a defect to compensate for.
            _result.Should().Be("abcdefghi");
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Truncated_Inside_A_Surrogate_Pair : CorrelationIdNormalizerTests
    {
        // Case A of the two truncation-boundary cases: the cut splits a genuine astral
        // character. U+1F600 is a non-BMP code point, so it occupies two UTF-16 code units, and
        // cutting at seven units lands between them.
        private const string WithAstralCharacter = "abcdef\U0001F600ghij";

        private CorrelationIdNormalizer.NormalizationDetail _result;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.NormalizeWithDetail(WithAstralCharacter, 7);
        }

        [Test]
        public void It_drops_the_orphaned_high_surrogate_rather_than_emitting_it()
        {
            // The orphan must not reach the output by either route. System.Text.Json would
            // write U+FFFD into the response body while a log sink received the raw unpaired
            // unit, and the two would no longer be byte-identical - the parity FR-LOG-6
            // guarantees.
            _result.Value.Should().Be("abcdef");
        }

        [Test]
        public void It_does_not_report_the_orphan_as_a_character_the_allowlist_removed()
        {
            // This is what the truncation back-off is still for, now that the allowlist would
            // also delete an orphan it was allowed to create. Both spellings produce "abcdef",
            // so the value alone cannot tell them apart; the reported fact can. The client sent
            // a well-formed astral character and nothing outside the allowlist, so the only
            // adjustment to report is the truncation. Without the back-off the orphaned half
            // would reach the allowlist and be counted as a removal, and the
            // CorrelationIdModified event would tell an operator the client sent something
            // disallowed when it did not.
            _result.CharactersRemoved.Should().BeFalse();
            _result.Truncated.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_With_A_Lone_Surrogate_Exactly_At_The_Truncation_Cut
        : CorrelationIdNormalizerTests
    {
        // Case B, and the reason the back-off tests for a split *pair* rather than merely for a
        // high surrogate at the last retained position. Here the high surrogate at the cut has
        // no low half after it: it was already unpaired in the input, so truncation is not
        // about to break anything, and there is nothing for the back-off to prevent.
        private const string WithLoneSurrogateAtTheCut = "abc\ud83dxyz";

        private CorrelationIdNormalizer.NormalizationDetail _result;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.NormalizeWithDetail(WithLoneSurrogateAtTheCut, 4);
        }

        [Test]
        public void It_still_removes_the_lone_surrogate()
        {
            // Same value as case A, reached the other way: the allowlist removes it after the
            // cut instead of the back-off declining to retain it. A back-off narrowed to split
            // pairs without the allowlist change would emit "abc\ud83d" here, which is the
            // parity break in its purest form.
            _result.Value.Should().Be("abc");
        }

        [Test]
        public void It_reports_the_lone_surrogate_as_a_character_the_allowlist_removed()
        {
            // The mirror of case A, and what pins the back-off's predicate from the other side:
            // a back-off that fired on any high surrogate at the cut would drop this one itself,
            // spending a code unit of the length budget on a character the allowlist was about
            // to remove for free, and reporting a client-supplied disallowed character as
            // nothing more than a truncation.
            _result.CharactersRemoved.Should().BeTrue();
            _result.Truncated.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Ending_Exactly_At_A_Surrogate_Pair_Boundary
        : CorrelationIdNormalizerTests
    {
        private const string WithAstralCharacter = "abcdef\U0001F600ghij";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            // Cutting at eight units keeps the pair whole: the last retained unit is the LOW
            // half. The guard must test only for a HIGH surrogate, so it leaves this alone.
            _result = CorrelationIdNormalizer.Normalize(WithAstralCharacter, 8);
        }

        [Test]
        public void It_preserves_a_complete_pair_that_ends_at_the_boundary()
        {
            // Pins the guard's predicate. Widening char.IsHighSurrogate to char.IsSurrogate
            // would drop the low half here, leaving an orphaned high half that the allowlist
            // then deletes too - so the astral character the client sent disappears entirely
            // instead of being retained whole.
            _result.Should().Be("abcdef\U0001F600");
        }
    }

    [TestFixture]
    public class Given_An_Already_Normalized_Correlation_Id : CorrelationIdNormalizerTests
    {
        private const int MaxLength = 10;

        [TestCase("ab\ncdefghijklmnopqrstuvwxyz")]
        [TestCase("clean-id")]
        [TestCase("\r\n\t\0")]
        [TestCase("0HNCTN1IRQMDG:00000001")]
        [TestCase("")]
        public void It_is_idempotent(string input)
        {
            string once = CorrelationIdNormalizer.Normalize(input, MaxLength);

            CorrelationIdNormalizer.Normalize(once, MaxLength).Should().Be(once);
        }
    }

    [TestFixture]
    public class Given_An_Empty_Or_Whitespace_Correlation_Id : CorrelationIdNormalizerTests
    {
        [Test]
        public void It_returns_empty_for_null_without_throwing()
        {
            CorrelationIdNormalizer
                .Normalize(null, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_for_an_empty_string_without_throwing()
        {
            CorrelationIdNormalizer
                .Normalize(string.Empty, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_when_every_character_is_a_control_character()
        {
            CorrelationIdNormalizer
                .Normalize("\r\n\t\0", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_preserves_a_whitespace_only_value()
        {
            CorrelationIdNormalizer
                .Normalize("   ", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("   ");
        }
    }

    [TestFixture]
    public class Given_A_Non_Positive_Maximum_Length : CorrelationIdNormalizerTests
    {
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void It_falls_back_to_the_documented_default_rather_than_throwing(int maxLength)
        {
            string oversized = new('c', AppSettings.DefaultCorrelationIdMaxLength + 10);

            CorrelationIdNormalizer
                .Normalize(oversized, maxLength)
                .Should()
                .Be(new string('c', AppSettings.DefaultCorrelationIdMaxLength));
        }
    }

    [TestFixture]
    public class Given_Any_Correlation_Id_The_Normalized_Value_Survives_Json_Serialization
        : CorrelationIdNormalizerTests
    {
        // The property the surrogate rules exist for, asserted end to end rather than through a
        // proxy. FR-LOG-6 says the correlationId a client reads from an error response body is
        // the string an operator finds in the logs. The log sink receives the normalized value
        // as-is; the response body receives it through System.Text.Json. So parity holds exactly
        // when the normalized value round-trips through JSON unchanged - which it does for every
        // string that is well-formed UTF-16, and for no string that is not, because the encoder
        // substitutes U+FFFD for each unpaired half.
        //
        // Every one of these inputs fails this round trip before normalization; the assertion is
        // that none of them still does after it.
        // A TestCaseSource for the same reason as the fixture above: an unpaired surrogate
        // written into a [TestCase] attribute is silently replaced with U+FFFD by UTF-8 attribute
        // blob encoding, which would leave these cases asserting nothing about surrogates at all.
        private static readonly TestCaseData[] ValuesThatBreakJsonParityBeforeNormalization =
        [
            new TestCaseData("ab\ud83dcd", 255).SetName("lone high surrogate in the middle"),
            new TestCaseData("\ud83dabcd", 255).SetName("lone high surrogate at the start"),
            new TestCaseData("abcd\ud83d", 255).SetName("lone high surrogate at the end"),
            new TestCaseData("ab\udc00cd", 255).SetName("lone low surrogate"),
            new TestCaseData("a\ud83d\ud83d\ude00b", 255).SetName("well-formed pair after a lone surrogate"),
            new TestCaseData("abc\ud83d\ude00xyz", 4).SetName("cut splits a well-formed pair"),
            new TestCaseData("abc\ud83dxyz", 4).SetName("cut lands on an already-unpaired high surrogate"),
            new TestCaseData("\U0001F600", 1).SetName("single astral character cut to one code unit"),
        ];

        [TestCaseSource(nameof(ValuesThatBreakJsonParityBeforeNormalization))]
        public void It_round_trips_byte_identically_through_System_Text_Json(string raw, int maxLength)
        {
            string normalized = CorrelationIdNormalizer.Normalize(raw, maxLength);

            string logSinkValue = normalized;
            string responseBodyValue = JsonSerializer.Deserialize<string>(
                JsonSerializer.Serialize(normalized)
            )!;

            responseBodyValue.Should().Be(logSinkValue);
            responseBodyValue.Should().NotContain("\uFFFD");
        }

        [Test]
        public void It_is_the_normalization_and_not_the_assertion_that_makes_this_hold()
        {
            // The guard against the round trip above being vacuous. Every input it lists is a
            // string JSON serialization does change, so the assertions are load-bearing rather
            // than true of any string at all.
            const string Raw = "ab\ud83dcd";

            JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(Raw)).Should().NotBe(Raw);
        }
    }

    [TestFixture]
    public class Given_A_Server_Generated_Trace_Identifier : CorrelationIdNormalizerTests
    {
        // The shape ASP.NET Core produces for HttpContext.TraceIdentifier. FR-LOG-5 requires
        // the behavior for system-generated IDs to be unchanged.
        private const string TraceIdentifier = "0HNCTN1IRQMDG:00000001";

        [Test]
        public void It_passes_the_value_through_unchanged()
        {
            CorrelationIdNormalizer
                .Normalize(TraceIdentifier, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(TraceIdentifier);
        }
    }
}
