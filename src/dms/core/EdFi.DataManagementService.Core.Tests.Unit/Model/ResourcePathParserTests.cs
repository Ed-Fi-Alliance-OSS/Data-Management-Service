// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
[Parallelizable]
public class ResourcePathParserTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_Without_A_Third_Segment : ResourcePathParserTests
    {
        private ResourcePathParseResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourcePathParser.Parse("/ed-fi/endpointName");
        }

        [Test]
        public void It_recognizes_the_collection_operation()
        {
            _result
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.PathComponents.Operation.Should()
                .BeOfType<ResourcePathOperation.Collection>();
        }

        [Test]
        public void It_supplies_no_operation_segment()
        {
            _result
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.SuppliedOperationSegment.Should()
                .BeNull();
        }

        [Test]
        public void It_provides_the_project_and_endpoint_names()
        {
            var recognized = (ResourcePathParseResult.Recognized)_result;

            recognized.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            recognized.PathComponents.EndpointName.Value.Should().Be("endpointName");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_With_A_Trailing_Slash : ResourcePathParserTests
    {
        [Test]
        public void It_recognizes_the_collection_operation()
        {
            ResourcePathParser
                .Parse("/ed-fi/endpointName/")
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.PathComponents.Operation.Should()
                .BeOfType<ResourcePathOperation.Collection>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_With_A_Well_Formed_Document_Uuid : ResourcePathParserTests
    {
        private const string DocumentUuid = "7825fba8-0b3d-4fc9-ae72-5ad8194d3ce2";

        private ResourcePathParseResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourcePathParser.Parse($"/ed-fi/endpointName/{DocumentUuid}");
        }

        [Test]
        public void It_recognizes_the_by_id_operation_carrying_the_uuid()
        {
            _result
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.PathComponents.Operation.Should()
                .BeOfType<ResourcePathOperation.ById>()
                .Which.DocumentUuid.Value.Should()
                .Be(new Guid(DocumentUuid));
        }

        [Test]
        public void It_exposes_the_uuid_through_path_components()
        {
            var recognized = (ResourcePathParseResult.Recognized)_result;

            recognized.PathComponents.DocumentUuid.Value.Should().Be(new Guid(DocumentUuid));
        }

        [Test]
        public void It_supplies_the_raw_segment()
        {
            _result
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.SuppliedOperationSegment.Should()
                .Be(DocumentUuid);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_Naming_The_Partitions_Operation : ResourcePathParserTests
    {
        [TestCase("partitions")]
        [TestCase("PARTITIONS")]
        [TestCase("Partitions")]
        public void It_recognizes_the_partitions_operation_regardless_of_case(string segment)
        {
            ResourcePathParser
                .Parse($"/ed-fi/endpointName/{segment}")
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.PathComponents.Operation.Should()
                .BeOfType<ResourcePathOperation.Partitions>();
        }

        [TestCase("partitions")]
        [TestCase("PARTITIONS")]
        [TestCase("Partitions")]
        public void It_preserves_the_segment_as_supplied(string segment)
        {
            ResourcePathParser
                .Parse($"/ed-fi/endpointName/{segment}")
                .Should()
                .BeOfType<ResourcePathParseResult.Recognized>()
                .Which.SuppliedOperationSegment.Should()
                .Be(segment);
        }

        [Test]
        public void It_carries_no_document_uuid()
        {
            var recognized = (ResourcePathParseResult.Recognized)
                ResourcePathParser.Parse("/ed-fi/endpointName/partitions");

            recognized.PathComponents.DocumentUuid.Should().Be(No.DocumentUuid);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_With_An_Unrecognized_Third_Segment : ResourcePathParserTests
    {
        [TestCase("invalidId")]
        [TestCase("ffc0a272")]
        [TestCase("partition")]
        [TestCase("partitionss")]
        public void It_reports_an_invalid_identifier_carrying_the_supplied_segment(string segment)
        {
            ResourcePathParser
                .Parse($"/ed-fi/endpointName/{segment}")
                .Should()
                .BeOfType<ResourcePathParseResult.InvalidIdentifier>()
                .Which.SuppliedSegment.Should()
                .Be(segment);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_Without_The_Resource_Path_Shape : ResourcePathParserTests
    {
        [TestCase("")]
        [TestCase("badpath")]
        [TestCase("/ed-fi")]
        [TestCase("/ed-fi/endpointName/11111111-1111-1111-1111-111111111111/extra")]
        [TestCase("/ed-fi/endpointName/partitions/extra")]
        public void It_reports_the_path_as_unmatched(string path)
        {
            ResourcePathParser.Parse(path).Should().BeOfType<ResourcePathParseResult.Unmatched>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_With_A_Mixed_Case_Project_Namespace : ResourcePathParserTests
    {
        [Test]
        public void It_lower_cases_only_the_project_endpoint_name()
        {
            var recognized = (ResourcePathParseResult.Recognized)
                ResourcePathParser.Parse("/Ed-Fi/endpointName");

            recognized.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            recognized.PathComponents.EndpointName.Value.Should().Be("endpointName");
        }
    }

    /// <summary>
    /// The project namespace is a fixed protocol token, so which spellings the parser accepts must not
    /// depend on the server's culture. The Turkish locale lowercases <c>I</c> to a dotless <c>ı</c>,
    /// which does not match the ordinal comparison the project lookup performs, so a culture-sensitive
    /// fold answers not found for a spelling that resolves everywhere else.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Turkish_Culture_And_An_Upper_Case_Project_Namespace : ResourcePathParserTests
    {
        private CultureInfo _originalCulture = null!;
        private ResourcePathParseResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Scoped to the thread the parse runs on, so a parallel sibling fixture cannot observe it.
            _originalCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            _result = ResourcePathParser.Parse("/ED-FI/endpointName");
        }

        [TearDown]
        public void TearDown()
        {
            CultureInfo.CurrentCulture = _originalCulture;
        }

        [Test]
        public void It_lower_cases_the_project_endpoint_name_invariantly()
        {
            var recognized = (ResourcePathParseResult.Recognized)_result;

            recognized.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
        }
    }
}
