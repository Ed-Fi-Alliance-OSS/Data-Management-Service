// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

public class DataStoreDerivativeTypeTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Recognized_Derivative_Type_Name
    {
        [Test]
        public void It_should_recognize_ReadReplica()
        {
            DataStoreDerivativeTypeNames
                .TryParseExact("ReadReplica", out DataStoreDerivativeType type)
                .Should()
                .BeTrue();

            type.Should().Be(DataStoreDerivativeType.ReadReplica);
        }

        [Test]
        public void It_should_recognize_Snapshot()
        {
            DataStoreDerivativeTypeNames
                .TryParseExact("Snapshot", out DataStoreDerivativeType type)
                .Should()
                .BeTrue();

            type.Should().Be(DataStoreDerivativeType.Snapshot);
        }
    }

    /// <summary>
    /// Recognition is ordinal and exact. The Configuration Service validates and stores exactly two
    /// spellings, so accepting a case variant or a padded value here would silently widen a contract
    /// the configuration database itself does not permit.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unrecognized_Derivative_Type_Name
    {
        [TestCase("SNAPSHOT")]
        [TestCase("snapshot")]
        [TestCase("SnapShot")]
        [TestCase("READREPLICA")]
        [TestCase("readreplica")]
        [TestCase("readReplica")]
        [TestCase(" Snapshot")]
        [TestCase("Snapshot ")]
        [TestCase("Read Replica")]
        [TestCase("Replica")]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void It_should_not_recognize_the_value(string? value)
        {
            DataStoreDerivativeTypeNames
                .TryParseExact(value, out DataStoreDerivativeType type)
                .Should()
                .BeFalse();

            type.Should().Be(default(DataStoreDerivativeType));
        }
    }
}
