// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class SecretReferenceTokensTests
{
    private static void ShouldFind(string value, params SecretReferenceToken[] expected) =>
        SecretReferenceTokens.Find(value).Should().Equal(expected);

    private static void ShouldFindNone(string value) => SecretReferenceTokens.Find(value).Should().BeEmpty();

    [TestFixture]
    public class Given_a_name_built_from_each_permitted_character : SecretReferenceTokensTests
    {
        [TestCase("_")]
        [TestCase("-")]
        [TestCase(".")]
        [TestCase("/")]
        [TestCase(":")]
        [TestCase("@")]
        [TestCase("+")]
        [TestCase("AZ")]
        [TestCase("az")]
        [TestCase("09")]
        public void It_is_a_token_carrying_the_whole_name(string permitted)
        {
            string name = $"a{permitted}b";
            ShouldFind($"Password=${{secret:{name}}};", new SecretReferenceToken(9, 10 + name.Length, name));
        }

        [TestCase("_")]
        [TestCase("-")]
        [TestCase(".")]
        [TestCase("/")]
        [TestCase(":")]
        [TestCase("@")]
        [TestCase("+")]
        public void It_is_a_token_when_the_name_is_only_that_character(string permitted) =>
            ShouldFind($"${{secret:{permitted}}}", new SecretReferenceToken(0, 11, permitted));

        [Test]
        public void It_accepts_a_vault_path() =>
            ShouldFind(
                "Password=${secret:prod/dms/ds-2026}",
                new SecretReferenceToken(9, 26, "prod/dms/ds-2026")
            );
    }

    [TestFixture]
    public class Given_a_name_containing_an_excluded_character : SecretReferenceTokensTests
    {
        [TestCase("{")]
        [TestCase("$")]
        [TestCase(";")]
        [TestCase("=")]
        [TestCase("\"")]
        [TestCase("'")]
        [TestCase(" ", TestName = "It_is_not_a_token(space)")]
        [TestCase("\t", TestName = "It_is_not_a_token(tab)")]
        [TestCase("\n", TestName = "It_is_not_a_token(newline)")]
        [TestCase(",")]
        [TestCase("#")]
        [TestCase("%")]
        [TestCase("&")]
        [TestCase("*")]
        [TestCase("!")]
        [TestCase("?")]
        [TestCase("\\")]
        [TestCase("(")]
        [TestCase(")")]
        [TestCase("[")]
        [TestCase("]")]
        [TestCase("<")]
        [TestCase(">")]
        [TestCase("|")]
        [TestCase("~")]
        [TestCase("^")]
        [TestCase("`")]
        [TestCase("é", TestName = "It_is_not_a_token(non-ASCII letter)")]
        [TestCase("٣", TestName = "It_is_not_a_token(non-ASCII digit)")]
        public void It_is_not_a_token(string excluded) =>
            ShouldFindNone($"Password=${{secret:a{excluded}b}};");

        [Test]
        public void It_ends_the_name_at_a_closing_brace_and_leaves_the_rest_as_text() =>
            ShouldFind("${secret:a}b}", new SecretReferenceToken(0, 11, "a"));
    }

    [TestFixture]
    public class Given_a_malformed_reference : SecretReferenceTokensTests
    {
        [TestCase("${secret:}", TestName = "It_is_not_a_token(empty name)")]
        [TestCase("${secret:a b}", TestName = "It_is_not_a_token(embedded space)")]
        [TestCase("${secret:a{b}", TestName = "It_is_not_a_token(embedded brace)")]
        [TestCase("${secret:", TestName = "It_is_not_a_token(unterminated, no name)")]
        [TestCase("Password=${secret:abc", TestName = "It_is_not_a_token(unterminated, with name)")]
        [TestCase("${secret:abc;Host=db", TestName = "It_is_not_a_token(unterminated, followed by text)")]
        [TestCase("${Secret:x}", TestName = "It_is_not_a_token(capitalized prefix)")]
        [TestCase("${SECRET:x}", TestName = "It_is_not_a_token(upper-case prefix)")]
        [TestCase("${secret :x}", TestName = "It_is_not_a_token(space before colon)")]
        [TestCase("$ {secret:x}", TestName = "It_is_not_a_token(space after dollar)")]
        [TestCase("{secret:x}", TestName = "It_is_not_a_token(no dollar)")]
        [TestCase("$(secret:x)", TestName = "It_is_not_a_token(parentheses)")]
        [TestCase("${secrets:x}", TestName = "It_is_not_a_token(other prefix)")]
        [TestCase("${x}", TestName = "It_is_not_a_token(no prefix)")]
        [TestCase("Password=p${ss}word", TestName = "It_is_not_a_token(password containing a dollar brace)")]
        public void It_is_not_a_token(string value) => ShouldFindNone(value);
    }

    [TestFixture]
    public class Given_a_malformed_reference_followed_by_a_token : SecretReferenceTokensTests
    {
        [Test]
        public void It_finds_the_token_after_an_unterminated_opening() =>
            ShouldFind("${secret:${secret:b}", new SecretReferenceToken(9, 11, "b"));

        [Test]
        public void It_finds_the_token_after_an_empty_name() =>
            ShouldFind("${secret:}${secret:b}", new SecretReferenceToken(10, 11, "b"));

        [Test]
        public void It_finds_the_token_after_an_embedded_space() =>
            ShouldFind("${secret:a b}${secret:c}", new SecretReferenceToken(13, 11, "c"));

        [Test]
        public void It_finds_the_token_inside_a_case_mismatched_opening() =>
            ShouldFind("${Secret:${secret:b}}", new SecretReferenceToken(9, 11, "b"));
    }

    [TestFixture]
    public class Given_no_escape_sequence : SecretReferenceTokensTests
    {
        [Test]
        public void It_treats_a_doubled_dollar_as_text_before_a_token() =>
            ShouldFind("$${secret:x}", new SecretReferenceToken(1, 11, "x"));

        [Test]
        public void It_treats_a_backslash_as_text_before_a_token() =>
            ShouldFind("\\${secret:x}", new SecretReferenceToken(1, 11, "x"));
    }

    [TestFixture]
    public class Given_more_than_one_token : SecretReferenceTokensTests
    {
        [Test]
        public void It_finds_adjacent_tokens_independently() =>
            ShouldFind(
                "${secret:a}${secret:bc}",
                new SecretReferenceToken(0, 11, "a"),
                new SecretReferenceToken(11, 12, "bc")
            );

        [Test]
        public void It_finds_tokens_between_literal_text() =>
            ShouldFind(
                "pre${secret:a}mid${secret:b/c}post",
                new SecretReferenceToken(3, 11, "a"),
                new SecretReferenceToken(17, 13, "b/c")
            );

        [Test]
        public void It_finds_a_repeated_name_at_each_position() =>
            ShouldFind(
                "Username=${secret:x};Password=${secret:x}",
                new SecretReferenceToken(9, 11, "x"),
                new SecretReferenceToken(30, 11, "x")
            );
    }

    [TestFixture]
    public class Given_a_single_token : SecretReferenceTokensTests
    {
        [Test]
        public void It_finds_a_token_that_is_the_whole_value() =>
            ShouldFind("${secret:x}", new SecretReferenceToken(0, 11, "x"));

        [Test]
        public void It_finds_a_token_at_the_start() =>
            ShouldFind("${secret:x}tail", new SecretReferenceToken(0, 11, "x"));

        [Test]
        public void It_finds_a_token_at_the_end() =>
            ShouldFind("head${secret:x}", new SecretReferenceToken(4, 11, "x"));
    }

    [TestFixture]
    public class Given_no_token : SecretReferenceTokensTests
    {
        [TestCase("")]
        [TestCase("Host=db;Username=edfi;Password=secret")]
        [TestCase("secret:x")]
        public void It_finds_nothing(string value) => ShouldFindNone(value);
    }
}
