// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Api.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Api.Tests.E2E.StepDefinitions;

[Binding]
public class EqualityConstraintValidationStepDefinitions(PlaywrightContext _playwrightContext)
{
    private IAPIResponse _apiResponse = null!;

    [Given("a post to the bellschedules endpoint where the referenced school id and all class period school ids match")]
    public async Task GivenAPostToTheBellschedulesEndpointWhereTheReferencedSchoolIdAndAllClassPeriodSchoolIdsMatch()
    {
        const string ValidBellScheduleJson = """

                                       {
                                           "schoolReference": {
                                             "schoolId": 255901001
                                           },
                                           "bellScheduleName": "Test Schedule",
                                           "totalInstructionalTime": 325,
                                           "classPeriods": [
                                             {
                                               "classPeriodReference": {
                                                 "classPeriodName": "01 - Traditional",
                                                 "schoolId": 255901001
                                               }
                                             },
                                             {
                                               "classPeriodReference": {
                                                 "classPeriodName": "02 - Traditional",
                                                 "schoolId": 255901001
                                               }
                                             }
                                           ],
                                           "dates": [],
                                           "gradeLevels": []
                                         }

                                       """;

        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync("ed-fi/bellschedules", new() { Data = ValidBellScheduleJson })!;
    }

    [Then("receive created response")]
    public void ThenReceiveCreatedResponse()
    {
        _apiResponse.Status.Should().Be((int)HttpStatusCode.Created);
    }

    [Given("a post to the bellschedules endpoint where the referenced school id and all class period school ids do not match")]
    public async Task GivenAPostToTheBellschedulesEndpointWhereTheReferencedSchoolIdAndAllClassPeriodSchoolIdsDoNotMatch()
    {
        const string InvalidBellScheduleJson = """

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
                                                       "schoolId": 2
                                                     }
                                                   },
                                                   {
                                                     "classPeriodReference": {
                                                       "classPeriodName": "02 - Traditional",
                                                       "schoolId": 2
                                                     }
                                                   }
                                                 ],
                                                 "dates": [],
                                                 "gradeLevels": []
                                               }

                                             """;

        _apiResponse = await _playwrightContext.ApiRequestContext?.PostAsync("ed-fi/bellschedules", new() { Data = InvalidBellScheduleJson })!;
    }

    [Then("receive bad request response")]
    public void ThenReceiveBadRequestResponse()
    {
        _apiResponse.Status.Should().Be((int)HttpStatusCode.BadRequest);
    }
}
