// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Initializes the AuthorizationPathways in the RequestInfo.
/// </summary>
internal class ProvideAuthorizationPathwayMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            $"Entering {nameof(ProvideAuthorizationPathwayMiddleware)} - {{TraceId}}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.AuthorizationPathways = requestInfo
            .ResourceSchema.AuthorizationPathways.Select(authorizationPathway =>
                authorizationPathway switch
                {
                    "StudentSchoolAssociationAuthorization" =>
                        BuildStudentSchoolAssociationAuthorizationPathway(
                            requestInfo.DocumentSecurityElements,
                            requestInfo.Method
                        ),
                    "StudentEducationOrganizationResponsibilityAssociationAuthorization" =>
                        BuildStudentEducationOrganizationResponsibilityAssociationAuthorizationPathway(
                            requestInfo.DocumentSecurityElements,
                            requestInfo.Method
                        ),
                    "ContactStudentSchoolAuthorization" =>
                        (AuthorizationPathway)BuildStudentContactAssociationAuthorizationPathway(
                            requestInfo.DocumentSecurityElements,
                            requestInfo.Method
                        ),
                    "StaffEducationOrganizationAuthorization" =>
                        BuildStaffEducationOrganizationAuthorizationPathway(
                            requestInfo.DocumentSecurityElements,
                            requestInfo.Method
                        ),

                    _ => throw new InvalidOperationException(
                        $"Unrecognized Authorization Pathway '{authorizationPathway}'."
                    ),
                }
            )
            .ToList();

        await next();
    }

    /// <summary>
    /// Builds the StudentSchoolAssociation AuthorizationPathway from the DocumentSecurityElements.
    /// </summary>
    private static AuthorizationPathway.StudentSchoolAssociation BuildStudentSchoolAssociationAuthorizationPathway(
        DocumentSecurityElements documentSecurityElements,
        RequestMethod requestMethod
    )
    {
        if (
            requestMethod is RequestMethod.POST or RequestMethod.PUT
            && (
                documentSecurityElements.Student.Length == 0
                || documentSecurityElements.EducationOrganization.Length == 0
            )
        )
        {
            throw new InvalidOperationException(
                "The StudentUniqueId and/or SchoolId are missing from the request body."
            );
        }

        return new AuthorizationPathway.StudentSchoolAssociation(
            documentSecurityElements.Student.FirstOrDefault(),
            documentSecurityElements.EducationOrganization.FirstOrDefault()?.Id ?? default
        );
    }

    /// <summary>
    /// Builds the StudentContactAssociation AuthorizationPathway from the DocumentSecurityElements.
    /// </summary>
    private static AuthorizationPathway.StudentContactAssociation BuildStudentContactAssociationAuthorizationPathway(
        DocumentSecurityElements documentSecurityElements,
        RequestMethod requestMethod
    )
    {
        if (
            requestMethod is RequestMethod.POST or RequestMethod.PUT
            && (documentSecurityElements.Student.Length == 0 || documentSecurityElements.Contact.Length == 0)
        )
        {
            throw new InvalidOperationException(
                "The StudentUniqueId and/or ContactUniqueId are missing from the request body."
            );
        }

        return new AuthorizationPathway.StudentContactAssociation(
            documentSecurityElements.Student.FirstOrDefault(),
            documentSecurityElements.Contact.FirstOrDefault()
        );
    }

    /// <summary>
    /// Builds the StudentEducationOrganizationResponsibilityAssociation AuthorizationPathway from the DocumentSecurityElements.
    /// </summary>
    private static AuthorizationPathway.StudentEducationOrganizationResponsibilityAssociation BuildStudentEducationOrganizationResponsibilityAssociationAuthorizationPathway(
        DocumentSecurityElements documentSecurityElements,
        RequestMethod requestMethod
    )
    {
        if (
            requestMethod is RequestMethod.POST or RequestMethod.PUT
            && (
                documentSecurityElements.Student.Length == 0
                || documentSecurityElements.EducationOrganization.Length == 0
            )
        )
        {
            throw new InvalidOperationException(
                "The StudentUniqueId and/or EducationOrganizationId are missing from the request body."
            );
        }

        return new AuthorizationPathway.StudentEducationOrganizationResponsibilityAssociation(
            documentSecurityElements.Student.FirstOrDefault(),
            documentSecurityElements.EducationOrganization.FirstOrDefault()?.Id ?? default
        );
    }

    private static AuthorizationPathway.StaffEducationOrganizationAssociation BuildStaffEducationOrganizationAuthorizationPathway(
        DocumentSecurityElements documentSecurityElements,
        RequestMethod requestMethod
    )
    {
        if (
            requestMethod is RequestMethod.POST or RequestMethod.PUT
            && (
                documentSecurityElements.Staff.Length == 0
                || documentSecurityElements.EducationOrganization.Length == 0
            )
        )
        {
            throw new InvalidOperationException(
                "The StaffUniqueId and/or EducationOrganizationId are missing from the request body."
            );
        }

        return new AuthorizationPathway.StaffEducationOrganizationAssociation(
            documentSecurityElements.Staff.FirstOrDefault(),
            documentSecurityElements.EducationOrganization.FirstOrDefault()?.Id ?? default
        );
    }
}
