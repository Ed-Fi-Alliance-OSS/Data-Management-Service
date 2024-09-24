// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class InjectLastModifiedDateToEdFiDocumentMiddlewareTests
{
    // Middleware to test
    internal static IPipelineStep Middleware()
    {
        return new InjectLastModifiedDateToEdFiDocumentMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Valid_Request_Body : InjectLastModifiedDateToEdFiDocumentMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private readonly string _pattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$";
        private readonly string _propertyName = "_lastModifiedDate";

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                    "schoolReference": {
                        "schoolId": 1
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "01 - Traditional",
                                "schoolId": 1
                            }
                        }
                    ],
                    "dates": [],
                    "gradeLevels": []
                }
                """;

            var parsedBody = JsonNode.Parse(jsonBody);

            _context.ParsedBody = parsedBody!;

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_should_not_have_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_should_have_parsed_body_with_formatted_lastmodifieddate()
        {
            var lastModifiedDate = _context?.ParsedBody[_propertyName]?.AsValue();
            lastModifiedDate.Should().NotBeNull();
            var IsValid = Regex.IsMatch(lastModifiedDate!.ToString(), _pattern);
            IsValid.Should().BeTrue();
        }
    }
}
