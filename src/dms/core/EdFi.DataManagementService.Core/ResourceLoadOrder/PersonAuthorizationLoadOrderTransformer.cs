// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

/// <summary>
/// Splits the <see cref="LoadOrder"/> of People resources into two entries:
/// The first entry is updated to only have the "Create" operation.
/// A second entry with the "Update" operation is added right after its corresponding Association.
/// </summary>
internal class PersonAuthorizationLoadOrderTransformer(
    IApiSchemaProvider apiSchemaProvider,
    ILogger<PersonAuthorizationLoadOrderTransformer> logger
) : IResourceLoadOrderTransformer
{
    private readonly string _coreProjectEndpointName = new ApiSchemaDocuments(
        apiSchemaProvider.GetApiSchemaNodes(),
        logger
    )
        .GetCoreProjectSchema()
        .ProjectEndpointName.Value;

    public void Transform(IList<LoadOrder> resources)
    {
        ApplyStudentTransformation(resources);
        ApplyStaffTransformation(resources);
        ApplyContactTransformation(resources);
    }

    /// <summary>
    /// Splits the <see cref="LoadOrder"/> of Student into two entries:
    /// The first entry is updated to only have the "Create" operation.
    /// A second entry with the "Update" operation is added right after StudentSchoolAssociation.
    /// </summary>
    private void ApplyStudentTransformation(IList<LoadOrder> resources)
    {
        var student = resources.SingleOrDefault(r => r.Resource == $"/{_coreProjectEndpointName}/students");

        if (student == null)
        {
            return;
        }

        var studentSchoolAssociation = resources.Single(r =>
            r.Resource == $"/{_coreProjectEndpointName}/studentSchoolAssociations"
        );

        var studentWithUpdate = new LoadOrder(
            student.Resource,
            studentSchoolAssociation.Group + 1,
            ["Update"]
        );

        student.Operations.Remove("Update");

        resources.Insert(
            resources.IndexOf(
                resources.FirstOrDefault(r => r.Group == studentWithUpdate.Group) ?? resources[^1]
            ),
            studentWithUpdate
        );
    }

    /// <summary>
    /// Splits the <see cref="LoadOrder"/> of Staff into two entries:
    /// The first entry is updated to only have the "Create" operation.
    /// A second entry with the "Update" operation is added right after
    /// StaffEducationOrganizationEmploymentAssociation and StaffEducationOrganizationAssignmentAssociation.
    /// </summary>
    private void ApplyStaffTransformation(IList<LoadOrder> resources)
    {
        // StaffEducationOrganizationEmploymentAssociation or StaffEducationOrganizationAssignmentAssociation
        // must be created before Staff can be updated.
        var staff = resources.SingleOrDefault(r => r.Resource == $"/{_coreProjectEndpointName}/staffs");

        if (staff == null)
        {
            return;
        }

        var staffEducationOrganizationEmploymentAssociation = resources.Single(r =>
            r.Resource == $"/{_coreProjectEndpointName}/staffEducationOrganizationEmploymentAssociations"
        );

        var staffEducationOrganizationAssignmentAssociation = resources.Single(r =>
            r.Resource == $"/{_coreProjectEndpointName}/staffEducationOrganizationAssignmentAssociations"
        );

        int highestGroup = Math.Max(
            staffEducationOrganizationAssignmentAssociation.Group,
            staffEducationOrganizationEmploymentAssociation.Group
        );

        var staffWithUpdate = new LoadOrder(staff.Resource, highestGroup + 1, ["Update"]);

        staff.Operations.Remove("Update");

        resources.Insert(
            resources.IndexOf(
                resources.FirstOrDefault(r => r.Group == staffWithUpdate.Group) ?? resources[^1]
            ),
            staffWithUpdate
        );
    }

    /// <summary>
    /// Splits the <see cref="LoadOrder"/> of Contact into two entries:
    /// The first entry is updated to only have the "Create" operation.
    /// A second entry with the "Update" operation is added right after StudentContactAssociation
    /// and StudentSchoolAssociation.
    /// </summary>
    private void ApplyContactTransformation(IList<LoadOrder> resources)
    {
        // StudentContactAssociation must be created before Contacts can be updated.
        // StudentSchoolAssociation must be created before Contacts can be updated.
        // StudentSchoolAssociation must be created before StudentContactAssociations can be updated.
        var contact = resources.SingleOrDefault(r =>
            r.Resource == $"/{_coreProjectEndpointName}/parents"
            || r.Resource == $"/{_coreProjectEndpointName}/contacts"
        );

        if (contact == null)
        {
            return;
        }

        var studentContactAssociation = resources.Single(r =>
            r.Resource == $"/{_coreProjectEndpointName}/studentParentAssociations"
            || r.Resource == $"/{_coreProjectEndpointName}/studentContactAssociations"
        );

        var studentSchoolAssociation = resources.Single(r =>
            r.Resource == $"/{_coreProjectEndpointName}/studentSchoolAssociations"
        );

        int higherGroup = Math.Max(studentContactAssociation.Group, studentSchoolAssociation.Group);

        var contactWithUpdate = new LoadOrder(contact.Resource, higherGroup + 1, ["Update"]);

        contact.Operations.Remove("Update");

        resources.Insert(
            resources.IndexOf(
                resources.FirstOrDefault(r => r.Group == contactWithUpdate.Group) ?? resources[^1]
            ),
            contactWithUpdate
        );
    }
}
