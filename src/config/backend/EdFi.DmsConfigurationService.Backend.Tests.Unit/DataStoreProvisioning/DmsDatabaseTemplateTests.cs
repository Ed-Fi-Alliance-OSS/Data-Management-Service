// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsDatabaseTemplateTests
{
    private static DmsDatabaseTemplate? Resolve(string? wireValue) =>
        DmsDatabaseTemplate.TryFromWireValue(wireValue, out DmsDatabaseTemplate? template) ? template : null;

    [TestFixture]
    public class Given_the_exact_wire_values
    {
        private DmsDatabaseTemplate? _minimal;
        private DmsDatabaseTemplate? _sample;

        [SetUp]
        public void Setup()
        {
            _minimal = Resolve("Minimal");
            _sample = Resolve("Sample");
        }

        [Test]
        public void It_resolves_Minimal_to_the_Minimal_template() =>
            _minimal.Should().BeSameAs(DmsDatabaseTemplate.Minimal);

        [Test]
        public void It_resolves_Sample_to_the_Sample_template() =>
            _sample.Should().BeSameAs(DmsDatabaseTemplate.Sample);

        [Test]
        public void It_maps_Minimal_to_the_Minimal_manifest_kind() =>
            _minimal!.TemplateKind.Should().Be("Minimal");

        [Test]
        public void It_maps_Sample_to_the_Populated_manifest_kind() =>
            _sample!.TemplateKind.Should().Be("Populated");

        [Test]
        public void It_keeps_the_wire_values() =>
            DmsDatabaseTemplate
                .All.Select(template => template.WireValue)
                .Should()
                .Equal("Minimal", "Sample");
    }

    [TestFixture]
    public class Given_wire_values_that_are_not_an_exact_ordinal_match
    {
        private readonly Dictionary<string, DmsDatabaseTemplate?> _resolved = [];
        private DmsDatabaseTemplate? _fromNull;

        [SetUp]
        public void Setup()
        {
            foreach (
                string candidate in new[]
                {
                    "minimal",
                    "MINIMAL",
                    "Populated",
                    " Minimal",
                    "Minimal ",
                    "sample",
                    "",
                }
            )
            {
                _resolved[candidate] = Resolve(candidate);
            }

            _fromNull = Resolve(null);
        }

        [Test]
        public void It_rejects_lowercase_minimal() => _resolved["minimal"].Should().BeNull();

        [Test]
        public void It_rejects_uppercase_minimal() => _resolved["MINIMAL"].Should().BeNull();

        [Test]
        public void It_rejects_the_manifest_kind_Populated() => _resolved["Populated"].Should().BeNull();

        [Test]
        public void It_rejects_a_leading_space() => _resolved[" Minimal"].Should().BeNull();

        [Test]
        public void It_rejects_a_trailing_space() => _resolved["Minimal "].Should().BeNull();

        [Test]
        public void It_rejects_lowercase_sample() => _resolved["sample"].Should().BeNull();

        [Test]
        public void It_rejects_an_empty_value() => _resolved[""].Should().BeNull();

        [Test]
        public void It_rejects_null() => _fromNull.Should().BeNull();
    }
}
