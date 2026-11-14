// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>The 6.8 quoting rules: identifiers double-quoted with embedded quotes doubled; literals single-quoted with embedded quotes doubled; nothing else touched.</summary>
public class PostgresqlIdentifiersTests
{
    [TestFixture]
    public class Given_identifier_and_literal_vectors
    {
        private Dictionary<string, string> _quotedIdentifiers = [];
        private Dictionary<string, string> _literals = [];
        private Action _nullIdentifier = () => { };

        [SetUp]
        public void Setup()
        {
            string[] vectors =
            [
                "EdFi_Ods_Test",
                "edfi_dms_provision_stage_0123456789ab",
                "a\"b",
                "\"\"",
                "it's",
                "x; DROP DATABASE y",
                "back\\slash",
                "",
            ];
            _quotedIdentifiers = vectors.ToDictionary(vector => vector, PostgresqlIdentifiers.Quote);
            _literals = vectors.ToDictionary(vector => vector, PostgresqlIdentifiers.Literal);
            _nullIdentifier = () => PostgresqlIdentifiers.Quote(null!);
        }

        [Test]
        public void It_wraps_a_plain_identifier() =>
            _quotedIdentifiers["EdFi_Ods_Test"].Should().Be("\"EdFi_Ods_Test\"");

        [Test]
        public void It_doubles_embedded_double_quotes() =>
            _quotedIdentifiers["a\"b"].Should().Be("\"a\"\"b\"");

        [Test]
        public void It_doubles_every_double_quote() => _quotedIdentifiers["\"\""].Should().Be("\"\"\"\"\"\"");

        [Test]
        public void It_leaves_single_quotes_alone_in_identifiers() =>
            _quotedIdentifiers["it's"].Should().Be("\"it's\"");

        [Test]
        public void It_keeps_statement_text_inside_the_identifier() =>
            _quotedIdentifiers["x; DROP DATABASE y"].Should().Be("\"x; DROP DATABASE y\"");

        [Test]
        public void It_leaves_backslashes_alone() =>
            _quotedIdentifiers["back\\slash"].Should().Be("\"back\\slash\"");

        [Test]
        public void It_quotes_the_empty_identifier() => _quotedIdentifiers[""].Should().Be("\"\"");

        [Test]
        public void It_wraps_a_plain_literal() => _literals["EdFi_Ods_Test"].Should().Be("'EdFi_Ods_Test'");

        [Test]
        public void It_doubles_embedded_single_quotes_in_literals() =>
            _literals["it's"].Should().Be("'it''s'");

        [Test]
        public void It_leaves_double_quotes_alone_in_literals() => _literals["a\"b"].Should().Be("'a\"b'");

        [Test]
        public void It_leaves_backslashes_alone_in_literals() =>
            _literals["back\\slash"].Should().Be("'back\\slash'");

        [Test]
        public void It_refuses_a_null_identifier() => _nullIdentifier.Should().Throw<ArgumentNullException>();
    }
}
