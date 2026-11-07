// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// Step 3.9 (6.1 step 3): an admin connection built over a source reads the current resolved value at every
/// use, so a secret rotated between operations reaches the next connection, with the provisioning overrides
/// applied to each value; one built over a fixed value still refuses a value the driver cannot read at
/// construction.
/// </summary>
public class PostgresqlAdminConnectionTests
{
    [TestFixture]
    public class Given_a_source_whose_value_rotates
    {
        private NpgsqlConnectionStringBuilder _before = null!;
        private NpgsqlConnectionStringBuilder _after = null!;
        private LibpqConnectionSettings _libpqAfter = null!;

        [SetUp]
        public void Setup()
        {
            string current = "Host=db.example;Username=dms_admin;Password=before;Pooling=true";
            PostgresqlAdminConnection connection = new(() => current, TimeSpan.FromSeconds(30));
            _before = new(connection.ConnectionStringFor("postgres", "dmsprov-session"));
            current = "Host=db.example;Username=dms_admin;Password=after;Pooling=true";
            _after = new(connection.ConnectionStringFor("postgres", "dmsprov-session"));
            _libpqAfter = connection.ToLibpqSettings();
        }

        [Test]
        public void It_connects_with_the_value_current_at_each_use() =>
            (_before.Password, _after.Password, _libpqAfter.Password)
                .Should()
                .Be(("before", "after", "after"));

        [Test]
        public void It_applies_the_provisioning_overrides_to_the_rotated_value() =>
            (_after.Pooling, _after.TcpKeepAlive, _after.CommandTimeout).Should().Be((false, true, 30));
    }

    [TestFixture]
    public class Given_a_fixed_value_the_driver_cannot_read
    {
        [Test]
        public void It_refuses_it_at_construction() =>
            FluentActions
                .Invoking(() => new PostgresqlAdminConnection("Port=not-a-number", TimeSpan.FromSeconds(30)))
                .Should()
                .Throw<Exception>();
    }
}
