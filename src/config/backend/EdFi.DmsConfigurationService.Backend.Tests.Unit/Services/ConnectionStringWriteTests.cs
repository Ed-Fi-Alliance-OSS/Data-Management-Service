// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class ConnectionStringWriteTests
{
    [TestFixture]
    public class Given_no_connection_string_was_provided : ConnectionStringWriteTests
    {
        [Test]
        public void It_preserves_the_stored_value() =>
            ConnectionStringWrite.PreservesExistingValue(null).Should().BeTrue();
    }

    /// <summary>
    /// Only a missing value preserves. The empty and whitespace rows are here to pin that: the API
    /// rejects them before an update reaches a repository, and they must never be read as an
    /// instruction to leave the stored value alone.
    /// </summary>
    [TestFixture]
    public class Given_a_connection_string_was_provided : ConnectionStringWriteTests
    {
        [TestCase("Server=localhost;Database=TestDb;")]
        [TestCase("host=localhost;port=5432;username=postgres;database=edfi_dms")]
        [TestCase("", TestName = "It_writes_the_submitted_value(empty)")]
        [TestCase("   ", TestName = "It_writes_the_submitted_value(whitespace)")]
        public void It_writes_the_submitted_value(string submitted) =>
            ConnectionStringWrite.PreservesExistingValue(submitted).Should().BeFalse();
    }
}
