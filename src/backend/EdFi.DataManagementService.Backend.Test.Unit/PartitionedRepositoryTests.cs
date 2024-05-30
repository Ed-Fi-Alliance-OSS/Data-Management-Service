// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Model;
using ImpromptuInterface;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend
{
    [TestFixture]
    public class PartitionedRepositoryTests
    {
        private class TestPartitionedRepository : PartitionedRepository
        {
            public int GetPartition(IDocumentUuid documentUuid)
            {
                return PartitionKeyFor(documentUuid);
            }
        }

        [TestFixture]
        public class Given_A_Partitioned_Repository : PartitionedRepositoryTests
        {
            private string _randomDocumentUuid = Guid.Empty.ToString();
            private readonly TestPartitionedRepository _testPartitionedRepository = new();

            [SetUp]
            public void Setup()
            {
                _randomDocumentUuid = Guid.NewGuid().ToString();
            }

            [TestCase('0', ExpectedResult = 0)]
            [TestCase('1', ExpectedResult = 1)]
            [TestCase('2', ExpectedResult = 2)]
            [TestCase('3', ExpectedResult = 3)]
            [TestCase('4', ExpectedResult = 4)]
            [TestCase('5', ExpectedResult = 5)]
            [TestCase('6', ExpectedResult = 6)]
            [TestCase('7', ExpectedResult = 7)]
            [TestCase('8', ExpectedResult = 8)]
            [TestCase('9', ExpectedResult = 9)]
            [TestCase('A', ExpectedResult = 10)]
            [TestCase('B', ExpectedResult = 11)]
            [TestCase('C', ExpectedResult = 12)]
            [TestCase('D', ExpectedResult = 13)]
            [TestCase('E', ExpectedResult = 14)]
            [TestCase('F', ExpectedResult = 15)]
            public int All_possible_last_bytes_return_correct_partition(char lastByte)
            {
                return _testPartitionedRepository.GetPartition(
                    (new { Value = _randomDocumentUuid[..35] + lastByte }).ActLike<IDocumentUuid>()
                );
            }
        }
    }
}
