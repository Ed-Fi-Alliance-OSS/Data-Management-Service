// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// GET dispatch consumes the same path classification the pipeline's own path parsing does, so the
/// shape a request is dispatched as and the shape it is later parsed as cannot drift apart. Each
/// case below asserts the pipeline that already served that path.
/// </summary>
[TestFixture]
[Parallelizable]
public class ApiServiceGetDispatchTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Collection_Path : ApiServiceGetDispatchTests
    {
        [TestCase("/ed-fi/endpointName")]
        [TestCase("/ed-fi/endpointName/")]
        public void It_dispatches_to_the_query_pipeline(string path)
        {
            ApiService
                .SelectGetPipelineKind(ResourcePathParser.Parse(path))
                .Should()
                .Be(ApiService.GetPipelineKind.Query);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_By_Id_Path : ApiServiceGetDispatchTests
    {
        [Test]
        public void It_dispatches_to_the_get_by_id_pipeline()
        {
            ApiService
                .SelectGetPipelineKind(
                    ResourcePathParser.Parse("/ed-fi/endpointName/7825fba8-0b3d-4fc9-ae72-5ad8194d3ce2")
                )
                .Should()
                .Be(ApiService.GetPipelineKind.GetById);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Partitions_Path : ApiServiceGetDispatchTests
    {
        [TestCase("/ed-fi/endpointName/partitions")]
        [TestCase("/ed-fi/endpointName/PARTITIONS")]
        public void It_dispatches_to_the_pipeline_that_declines_to_serve_it(string path)
        {
            ApiService
                .SelectGetPipelineKind(ResourcePathParser.Parse(path))
                .Should()
                .Be(
                    ApiService.GetPipelineKind.GetById,
                    "the partitions pipeline does not exist yet, so the operation is answered with "
                        + "the existing invalid-identifier response rather than an incomplete surface"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unrecognized_Third_Segment : ApiServiceGetDispatchTests
    {
        [Test]
        public void It_dispatches_to_the_get_by_id_pipeline()
        {
            ApiService
                .SelectGetPipelineKind(ResourcePathParser.Parse("/ed-fi/endpointName/invalidId"))
                .Should()
                .Be(ApiService.GetPipelineKind.GetById);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_Without_The_Resource_Path_Shape : ApiServiceGetDispatchTests
    {
        [TestCase("")]
        [TestCase("badpath")]
        [TestCase("/ed-fi/endpointName/partitions/extra")]
        public void It_dispatches_to_the_query_pipeline(string path)
        {
            ApiService
                .SelectGetPipelineKind(ResourcePathParser.Parse(path))
                .Should()
                .Be(ApiService.GetPipelineKind.Query);
        }
    }
}
