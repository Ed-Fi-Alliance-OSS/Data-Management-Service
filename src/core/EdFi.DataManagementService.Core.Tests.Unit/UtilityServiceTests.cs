// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;
using EdFi.DataManagementService.Core.External.Model;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
public class UtilityServiceTests
{
    [Test]
    public void PathExpressionRegex_ShouldMatchValidPath()
    {
        var regex = PathExpressionRegex();
        string validPath = "/namespace/endpointName/documentUuid";

        var match = regex.Match(validPath);

        match.Success.Should().BeTrue();
        match.Groups["projectNamespace"].Value.Should().Be("namespace");
        match.Groups["endpointName"].Value.Should().Be("endpointName");
        match.Groups["documentUuid"].Value.Should().Be("documentUuid");
    }

    [Test]
    public void PathExpressionRegex_ShouldNotMatchInvalidPath()
    {
        var regex = PathExpressionRegex();
        string invalidPath = "/namespace//documentUuid";

        var match = regex.Match(invalidPath);

        match.Success.Should().BeFalse();
    }

    [Test]
    public void Uuid4Regex_ShouldMatchValidUuid()
    {
        var regex = Uuid4Regex();
        string validUuid = Guid.NewGuid().ToString();

        var match = regex.Match(validUuid);

        match.Success.Should().BeTrue();
    }

    [Test]
    public void Uuid4Regex_ShouldNotMatchInvalidUuid()
    {
        var regex = Uuid4Regex();
        string invalidUuid = "123e4567-e89b-12d3-a456-42661417400G"; // Invalid character 'G'

        var match = regex.Match(invalidUuid);

        match.Success.Should().BeFalse();
    }

    [Test]
    public void SerializeBody_ShouldSerializeObjectToJson()
    {
        var obj = new { Name = "Test", Value = 123 };

        string json = SerializeBody(obj);

        json.Should().Be("{\"Name\":\"Test\",\"Value\":123}");
    }

    [Test]
    public void ToJsonError_ShouldReturnCorrectJsonFormat()
    {
        string errorInfo = "Some error occurred";
        TraceId traceId = new ("trace-123");

        string jsonError = ToJsonError(errorInfo, traceId);

        jsonError.Should().Be("{\"error\":\"Some error occurred\",\"correlationId\":{\"Value\":\"trace-123\"}}");
    }
    
    [Test]
    public void ToJsonError_ShouldHandleEmptyErrorInfo()
    {
        string errorInfo = "";
        TraceId traceId = new("trace-123");

        string jsonError = ToJsonError(errorInfo, traceId);

        jsonError.Should().Be("{\"error\":\"\",\"correlationId\":{\"Value\":\"trace-123\"}}");
    }
}

