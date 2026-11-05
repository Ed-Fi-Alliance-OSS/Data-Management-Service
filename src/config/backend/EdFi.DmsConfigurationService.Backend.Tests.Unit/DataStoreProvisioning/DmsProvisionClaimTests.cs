// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsProvisionClaimTests
{
    private static readonly Guid _expected = Guid.Parse("8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b");

    private static DmsOperationId Operation(string value) =>
        DmsOperationId.TryParse(value, out DmsOperationId operation)
            ? operation
            : throw new InvalidOperationException("test operation id must parse");

    [TestFixture]
    public class Given_an_operation_and_an_expected_identity
    {
        private DmsProvisionClaim _claim = null!;
        private DmsProvisionClaim? _parsed;

        [SetUp]
        public void Setup()
        {
            _claim = new DmsProvisionClaim(Operation("0123456789ab"), _expected);
            DmsProvisionClaim.TryParse(_claim.Text, out _parsed);
        }

        [Test]
        public void It_writes_the_versioned_comment_text() =>
            _claim
                .Text.Should()
                .Be("edfi-dms-provision:v1:0123456789ab:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b");

        [Test]
        public void It_round_trips_through_parse() => _parsed.Should().Be(_claim);
    }

    [TestFixture]
    public class Given_comment_text_that_is_not_a_claim
    {
        private readonly Dictionary<string, bool> _parsed = [];
        private bool _fromNull;
        private Action _emptyIdentity = () => { };

        [SetUp]
        public void Setup()
        {
            foreach (
                string text in new[]
                {
                    "edfi-dms-provision:v2:0123456789ab:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b",
                    "edfi-dms-provision:v1:0123456789AB:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b",
                    "edfi-dms-provision:v1:0123456789ab:00000000-0000-0000-0000-000000000000",
                    "edfi-dms-provision:v1:0123456789ab:{8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b}",
                    "edfi-dms-provision:v1:0123456789ab:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b:extra",
                    "a database comment",
                    "",
                }
            )
            {
                _parsed[text] = DmsProvisionClaim.TryParse(text, out _);
            }

            _fromNull = DmsProvisionClaim.TryParse(null, out _);
            _emptyIdentity = () => _ = new DmsProvisionClaim(Operation("0123456789ab"), Guid.Empty);
        }

        [Test]
        public void It_rejects_another_version() =>
            _parsed["edfi-dms-provision:v2:0123456789ab:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b"]
                .Should()
                .BeFalse();

        [Test]
        public void It_rejects_an_uppercase_operation_id() =>
            _parsed["edfi-dms-provision:v1:0123456789AB:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b"]
                .Should()
                .BeFalse();

        [Test]
        public void It_rejects_an_all_zero_identity() =>
            _parsed["edfi-dms-provision:v1:0123456789ab:00000000-0000-0000-0000-000000000000"]
                .Should()
                .BeFalse();

        [Test]
        public void It_rejects_a_braced_identity() =>
            _parsed["edfi-dms-provision:v1:0123456789ab:{8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b}"]
                .Should()
                .BeFalse();

        [Test]
        public void It_rejects_trailing_segments() =>
            _parsed["edfi-dms-provision:v1:0123456789ab:8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b:extra"]
                .Should()
                .BeFalse();

        [Test]
        public void It_rejects_an_ordinary_comment() => _parsed["a database comment"].Should().BeFalse();

        [Test]
        public void It_rejects_empty_and_null()
        {
            _parsed[""].Should().BeFalse();
            _fromNull.Should().BeFalse();
        }

        [Test]
        public void It_refuses_to_build_a_claim_for_an_empty_identity() =>
            _emptyIdentity.Should().Throw<ArgumentException>();
    }
}
