// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsOperationIdTests
{
    [TestFixture]
    public class Given_a_generated_operation_id
    {
        private DmsOperationId _first;
        private DmsOperationId _second;
        private DmsTargetNameValidation _stagingAsTarget;

        [SetUp]
        public void Setup()
        {
            _first = DmsOperationId.New();
            _second = DmsOperationId.New();
            _stagingAsTarget = DmsTargetDatabaseName.TryCreate(
                _first.StagingDatabaseName.Value,
                ProtectedDatabaseNames.Create("edfi_configurationservice", []),
                out _
            );
        }

        [Test]
        public void It_is_twelve_lowercase_hex_characters()
        {
            _first.Value.Should().HaveLength(12);
            _first.Value.Should().MatchRegex("^[0-9a-f]{12}$");
        }

        [Test]
        public void It_differs_between_calls() => _second.Should().NotBe(_first);

        [Test]
        public void It_round_trips_through_parse()
        {
            DmsOperationId.TryParse(_first.Value, out DmsOperationId parsed).Should().BeTrue();
            parsed.Should().Be(_first);
        }

        [Test]
        public void It_names_the_staging_database_from_the_reserved_prefix() =>
            _first.StagingDatabaseName.Value.Should().Be("edfi_dms_provision_stage_" + _first.Value);

        [Test]
        public void It_makes_the_staging_name_unselectable_as_a_target() =>
            _stagingAsTarget.Should().Be(DmsTargetNameValidation.ReservedPrefix);

        [Test]
        public void It_names_the_holder_session_within_twenty_characters()
        {
            _first.HolderApplicationName.Should().Be("dmsprov:" + _first.Value);
            _first.HolderApplicationName.Should().HaveLength(20);
        }

        [Test]
        public void It_names_the_restore_session() =>
            _first.RestoreApplicationName.Should().Be("dms-provision:" + _first.Value);

        [Test]
        public void It_tags_physical_files() => _first.PhysicalFileTag.Should().Be("__" + _first.Value);
    }

    [TestFixture]
    public class Given_candidate_ids_to_parse
    {
        private readonly Dictionary<string, bool> _parsed = [];
        private bool _fromNull;

        [SetUp]
        public void Setup()
        {
            foreach (
                string candidate in new[]
                {
                    "0123456789ab",
                    "0123456789AB",
                    "0123456789a",
                    "0123456789abc",
                    "0123456789ag",
                }
            )
            {
                _parsed[candidate] = DmsOperationId.TryParse(candidate, out _);
            }

            _fromNull = DmsOperationId.TryParse(null, out _);
        }

        [Test]
        public void It_accepts_twelve_lowercase_hex_characters() => _parsed["0123456789ab"].Should().BeTrue();

        [Test]
        public void It_rejects_uppercase_hex() => _parsed["0123456789AB"].Should().BeFalse();

        [Test]
        public void It_rejects_eleven_characters() => _parsed["0123456789a"].Should().BeFalse();

        [Test]
        public void It_rejects_thirteen_characters() => _parsed["0123456789abc"].Should().BeFalse();

        [Test]
        public void It_rejects_non_hex_characters() => _parsed["0123456789ag"].Should().BeFalse();

        [Test]
        public void It_rejects_null() => _fromNull.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_default_initialized_id
    {
        private Action _read = () => { };

        [SetUp]
        public void Setup() => _read = () => _ = default(DmsOperationId).Value;

        [Test]
        public void It_refuses_to_expose_a_value() => _read.Should().Throw<InvalidOperationException>();
    }
}
