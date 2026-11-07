// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>The 6.8 quoting rules: identifiers bracketed with an embedded closing bracket doubled; literals as <c>N'…'</c> with embedded single quotes doubled; nothing else touched.</summary>
public class MssqlIdentifiersTests
{
    [TestFixture]
    public class Given_identifier_and_literal_vectors
    {
        private Dictionary<string, string> _quotedIdentifiers = [];
        private Dictionary<string, string> _literals = [];
        private Action _nullIdentifier = () => { };
        private Action _nullLiteral = () => { };

        [SetUp]
        public void Setup()
        {
            string[] vectors =
            [
                "EdFi_Ods_Test",
                "edfi_dms_provision_stage_0123456789ab",
                "a]b",
                "]]",
                "[x",
                "it's",
                "x]; DROP DATABASE y; --",
                "/var/opt/mssql/data/stage__0123456789ab.mdf",
                "",
            ];
            _quotedIdentifiers = vectors.ToDictionary(vector => vector, MssqlIdentifiers.Quote);
            _literals = vectors.ToDictionary(vector => vector, MssqlIdentifiers.Literal);
            _nullIdentifier = () => MssqlIdentifiers.Quote(null!);
            _nullLiteral = () => MssqlIdentifiers.Literal(null!);
        }

        [Test]
        public void It_wraps_a_plain_identifier() =>
            _quotedIdentifiers["EdFi_Ods_Test"].Should().Be("[EdFi_Ods_Test]");

        [Test]
        public void It_doubles_an_embedded_closing_bracket() =>
            _quotedIdentifiers["a]b"].Should().Be("[a]]b]");

        [Test]
        public void It_doubles_every_closing_bracket() => _quotedIdentifiers["]]"].Should().Be("[]]]]]");

        [Test]
        public void It_leaves_an_opening_bracket_alone() => _quotedIdentifiers["[x"].Should().Be("[[x]");

        [Test]
        public void It_leaves_single_quotes_alone_in_identifiers() =>
            _quotedIdentifiers["it's"].Should().Be("[it's]");

        [Test]
        public void It_keeps_statement_text_inside_the_identifier() =>
            _quotedIdentifiers["x]; DROP DATABASE y; --"].Should().Be("[x]]; DROP DATABASE y; --]");

        [Test]
        public void It_quotes_the_empty_identifier() => _quotedIdentifiers[""].Should().Be("[]");

        [Test]
        public void It_wraps_a_plain_literal_as_unicode() =>
            _literals["EdFi_Ods_Test"].Should().Be("N'EdFi_Ods_Test'");

        [Test]
        public void It_doubles_embedded_single_quotes_in_literals() =>
            _literals["it's"].Should().Be("N'it''s'");

        [Test]
        public void It_leaves_brackets_alone_in_literals() => _literals["a]b"].Should().Be("N'a]b'");

        [Test]
        public void It_keeps_a_server_path_verbatim_in_a_literal() =>
            _literals["/var/opt/mssql/data/stage__0123456789ab.mdf"]
                .Should()
                .Be("N'/var/opt/mssql/data/stage__0123456789ab.mdf'");

        [Test]
        public void It_refuses_a_null_identifier() => _nullIdentifier.Should().Throw<ArgumentNullException>();

        [Test]
        public void It_refuses_a_null_literal() => _nullLiteral.Should().Throw<ArgumentNullException>();
    }
}
