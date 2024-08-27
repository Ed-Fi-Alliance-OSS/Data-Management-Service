// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit
{
    [TestFixture]
    public class DatabaseOptionsTests
    {
        private readonly DatabaseOptions _databaseOptions = new();
        private readonly IsolationLevel _isolationLevel = IsolationLevel.Chaos;

        [SetUp]
        public void Setup()
        {
            _databaseOptions.IsolationLevel = _isolationLevel;
        }

        [Test]
        public void Should_read_write_isolation_level()
        {
            _databaseOptions.IsolationLevel.Should().Be(_isolationLevel);
        }
    }
}
