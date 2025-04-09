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
/// Initializes the AuthorizationPathways in the PipelineContext.
/// </summary>
internal class ProvideAuthorizationPathwayMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            $"Entering {nameof(ProvideAuthorizationPathwayMiddleware)} - {{TraceId}}",
            context.FrontendRequest.TraceId.Value
        );

        context.AuthorizationPathways = context
            .ResourceSchema.AuthorizationPathways.Select(authorizationPathway =>
                authorizationPathway switch
                {
                    "StudentSchoolAssociationAuthorization" =>
                        BuildStudentSchoolAssociationAuthorizationPathway(
                            context.DocumentSecurityElements,
                            context.Method
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
                !documentSecurityElements.StudentUsi.Any()
                || !documentSecurityElements.EducationOrganization.Any()
            )
        )
        {
            throw new InvalidOperationException(
                "The StudentId and/or SchoolId are missing from the request body."
            );
        }

        return new AuthorizationPathway.StudentSchoolAssociation(
            documentSecurityElements.StudentUsi.FirstOrDefault(),
            documentSecurityElements.EducationOrganization.FirstOrDefault()?.Id ?? default
        );
    }
}
