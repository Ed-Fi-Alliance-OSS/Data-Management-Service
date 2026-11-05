// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsTargetDatabaseNameTests
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        ["EdFi_Admin"]
    );

    private static DmsTargetNameValidation Validate(string? candidate) =>
        DmsTargetDatabaseName.TryCreate(candidate, _protectedNames, out _);

    private static Dictionary<string, DmsTargetNameValidation> ValidateAll(params string[] candidates) =>
        candidates.ToDictionary(candidate => candidate, Validate, StringComparer.Ordinal);

    [TestFixture]
    public class Given_a_valid_name
    {
        private DmsTargetNameValidation _verdict;
        private DmsTargetDatabaseName _name;
        private DmsTargetNameValidation _longestVerdict;

        [SetUp]
        public void Setup()
        {
            _verdict = DmsTargetDatabaseName.TryCreate("EdFi_Ods_District1", _protectedNames, out _name);
            _longestVerdict = Validate("E" + new string('d', 62));
        }

        [Test]
        public void It_is_valid() => _verdict.Should().Be(DmsTargetNameValidation.Valid);

        [Test]
        public void It_keeps_the_candidate_exactly() => _name.Value.Should().Be("EdFi_Ods_District1");

        [Test]
        public void It_accepts_sixty_three_characters() =>
            _longestVerdict.Should().Be(DmsTargetNameValidation.Valid);

        [Test]
        public void It_compares_by_value()
        {
            DmsTargetDatabaseName.TryCreate(
                "EdFi_Ods_District1",
                _protectedNames,
                out DmsTargetDatabaseName other
            );
            other.Should().Be(_name);
        }

        [Test]
        public void It_converts_to_a_plain_database_name()
        {
            DmsDatabaseName plain = _name;
            plain.Value.Should().Be("EdFi_Ods_District1");
        }
    }

    [TestFixture]
    public class Given_a_missing_or_whitespace_wrapped_candidate
    {
        private Dictionary<string, DmsTargetNameValidation> _verdicts = [];
        private DmsTargetNameValidation _fromNull;
        private DmsTargetDatabaseName _rejectedName;

        [SetUp]
        public void Setup()
        {
            _fromNull = Validate(null);
            _verdicts = ValidateAll("", " ", "EdFi_Ods ", "\tEdFi_Ods", "EdFi_Ods\n", "\r\nEdFi_Ods");
            DmsTargetDatabaseName.TryCreate(" EdFi_Ods", _protectedNames, out _rejectedName);
        }

        [Test]
        public void It_rejects_null_as_empty() => _fromNull.Should().Be(DmsTargetNameValidation.Empty);

        [Test]
        public void It_rejects_an_empty_string() => _verdicts[""].Should().Be(DmsTargetNameValidation.Empty);

        [Test]
        public void It_rejects_whitespace_only() =>
            _verdicts[" "].Should().Be(DmsTargetNameValidation.SurroundingWhitespace);

        [Test]
        public void It_rejects_a_trailing_space_instead_of_trimming() =>
            _verdicts["EdFi_Ods "].Should().Be(DmsTargetNameValidation.SurroundingWhitespace);

        [Test]
        public void It_rejects_a_leading_tab() =>
            _verdicts["\tEdFi_Ods"].Should().Be(DmsTargetNameValidation.SurroundingWhitespace);

        [Test]
        public void It_rejects_a_trailing_newline() =>
            _verdicts["EdFi_Ods\n"].Should().Be(DmsTargetNameValidation.SurroundingWhitespace);

        [Test]
        public void It_rejects_a_leading_line_break_pair() =>
            _verdicts["\r\nEdFi_Ods"].Should().Be(DmsTargetNameValidation.SurroundingWhitespace);

        [Test]
        public void It_leaves_the_out_name_unset()
        {
            Action read = () => _ = _rejectedName.Value;
            read.Should().Throw<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class Given_a_candidate_that_breaks_the_grammar
    {
        private Dictionary<string, DmsTargetNameValidation> _verdicts = [];

        [SetUp]
        public void Setup() =>
            _verdicts = ValidateAll(
                "E" + new string('d', 63),
                "1EdFi_Ods",
                "_EdFi_Ods",
                "Edfi-Ods",
                "EdFi Ods",
                "EdFi_Ödsé",
                "EdFi\"Ods",
                "EdFi]Ods",
                "EdFi;Ods",
                "EdFi--Ods",
                "EdFi\nOds",
                "EdFi.Ods"
            );

        [Test]
        public void It_rejects_sixty_four_characters() =>
            _verdicts["E" + new string('d', 63)].Should().Be(DmsTargetNameValidation.TooLong);

        [Test]
        public void It_rejects_a_leading_digit() =>
            _verdicts["1EdFi_Ods"].Should().Be(DmsTargetNameValidation.InvalidFirstCharacter);

        [Test]
        public void It_rejects_a_leading_underscore() =>
            _verdicts["_EdFi_Ods"].Should().Be(DmsTargetNameValidation.InvalidFirstCharacter);

        [Test]
        public void It_rejects_a_hyphen() =>
            _verdicts["Edfi-Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_an_inner_space() =>
            _verdicts["EdFi Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_non_ascii_letters() =>
            _verdicts["EdFi_Ödsé"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_a_double_quote() =>
            _verdicts["EdFi\"Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_a_closing_bracket() =>
            _verdicts["EdFi]Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_a_semicolon() =>
            _verdicts["EdFi;Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_a_comment_marker() =>
            _verdicts["EdFi--Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_an_inner_newline() =>
            _verdicts["EdFi\nOds"].Should().Be(DmsTargetNameValidation.InvalidCharacter);

        [Test]
        public void It_rejects_a_dot() =>
            _verdicts["EdFi.Ods"].Should().Be(DmsTargetNameValidation.InvalidCharacter);
    }

    [TestFixture]
    public class Given_a_reserved_system_database_name_in_any_case
    {
        private Dictionary<string, DmsTargetNameValidation> _verdicts = [];

        [SetUp]
        public void Setup() =>
            _verdicts = ValidateAll(
                "postgres",
                "POSTGRES",
                "Postgres",
                "template0",
                "Template0",
                "template1",
                "TEMPLATE1",
                "master",
                "Master",
                "model",
                "MODEL",
                "msdb",
                "MsDb",
                "tempdb",
                "TempDB"
            );

        [Test]
        public void It_rejects_every_variant_as_reserved() =>
            _verdicts.Values.Should().AllBeEquivalentTo(DmsTargetNameValidation.ReservedName);

        [Test]
        public void It_covers_both_engines_regardless_of_provider() =>
            _verdicts
                .Keys.Should()
                .Contain(["postgres", "template0", "template1", "master", "model", "msdb", "tempdb"]);
    }

    [TestFixture]
    public class Given_a_protected_deployment_name_in_any_case
    {
        private Dictionary<string, DmsTargetNameValidation> _verdicts = [];

        [SetUp]
        public void Setup() =>
            _verdicts = ValidateAll(
                "edfi_configurationservice",
                "EDFI_CONFIGURATIONSERVICE",
                "EdFi_Admin",
                "edfi_admin",
                "EdFi_Admin2"
            );

        [Test]
        public void It_rejects_the_catalog_database() =>
            _verdicts["edfi_configurationservice"].Should().Be(DmsTargetNameValidation.ProtectedName);

        [Test]
        public void It_rejects_the_catalog_database_case_insensitively() =>
            _verdicts["EDFI_CONFIGURATIONSERVICE"].Should().Be(DmsTargetNameValidation.ProtectedName);

        [Test]
        public void It_rejects_an_operator_listed_name() =>
            _verdicts["EdFi_Admin"].Should().Be(DmsTargetNameValidation.ProtectedName);

        [Test]
        public void It_rejects_an_operator_listed_name_case_insensitively() =>
            _verdicts["edfi_admin"].Should().Be(DmsTargetNameValidation.ProtectedName);

        [Test]
        public void It_matches_whole_names_only() =>
            _verdicts["EdFi_Admin2"].Should().Be(DmsTargetNameValidation.Valid);
    }

    [TestFixture]
    public class Given_a_provisioner_generated_prefix
    {
        private Dictionary<string, DmsTargetNameValidation> _verdicts = [];

        [SetUp]
        public void Setup() =>
            _verdicts = ValidateAll(
                "edfi_dms_provision_x",
                "EDFI_DMS_PROVISION_stage_0123456789ab",
                "edfi_dms_provisioning",
                "edfi_dms_provisio"
            );

        [Test]
        public void It_rejects_the_staging_prefix() =>
            _verdicts["edfi_dms_provision_x"].Should().Be(DmsTargetNameValidation.ReservedPrefix);

        [Test]
        public void It_rejects_the_staging_prefix_case_insensitively() =>
            _verdicts["EDFI_DMS_PROVISION_stage_0123456789ab"]
                .Should()
                .Be(DmsTargetNameValidation.ReservedPrefix);

        [Test]
        public void It_rejects_longer_names_sharing_the_prefix() =>
            _verdicts["edfi_dms_provisioning"].Should().Be(DmsTargetNameValidation.ReservedPrefix);

        [Test]
        public void It_accepts_a_name_that_stops_short_of_the_prefix() =>
            _verdicts["edfi_dms_provisio"].Should().Be(DmsTargetNameValidation.Valid);
    }

    [TestFixture]
    public class Given_a_default_initialized_name
    {
        private Action _read = () => { };

        [SetUp]
        public void Setup() => _read = () => _ = default(DmsTargetDatabaseName).Value;

        [Test]
        public void It_refuses_to_expose_a_value() => _read.Should().Throw<InvalidOperationException>();
    }
}
