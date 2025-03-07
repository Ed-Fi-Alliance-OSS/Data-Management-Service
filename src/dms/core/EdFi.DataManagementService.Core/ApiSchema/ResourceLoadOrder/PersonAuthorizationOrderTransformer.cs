// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema.ResourceLoadOrder
{
    internal class PersonAuthorizationOrderTransformer : IOrderTransformer
    {
        public void Transform(IList<LoadOrder> resources)
        {
            ApplyStudentTransformation(resources);
            ApplyStaffTransformation(resources);
            ApplyContactTransformation(resources);
        }

        private void ApplyContactTransformation(IList<LoadOrder> resources)
        {
            // StudentContactAssociation must be created before Contacts can be updated.
            // StudentSchoolAssociation must be created before Contacts can be updated.
            // StudentSchoolAssociation must be created before StudentContactAssociations can be updated.
            var contactCreate = resources
                .SingleOrDefault(r => r.Resource is "/ed-fi/parents" or "/ed-fi/contacts");

            if (contactCreate == null)
            {
                return;
            }

            var studentContactAssociation = resources
                .Single(r =>
                    r.Resource is "/ed-fi/studentParentAssociations" or "/ed-fi/studentContactAssociations");

            var studentSchoolAssociation = resources
                .Single(r => r.Resource == "/ed-fi/studentSchoolAssociations");

            int higherOrder = Math.Max(studentContactAssociation.Order, studentSchoolAssociation.Order);

            var contactUpdate = new LoadOrder
            {
                Resource = contactCreate.Resource,
                Order = higherOrder + 1,
                Operations = new List<string> { "Update" }
            };

            contactCreate.Operations.Remove("Update");

            resources.Insert(
                resources.IndexOf(resources.FirstOrDefault(r => r.Order == contactUpdate.Order) ??
                                  resources[^1])
                , contactUpdate
            );
        }

        private void ApplyStaffTransformation(IList<LoadOrder> resources)
        {
            // StaffEducationOrganizationEmploymentAssociation or StaffEducationOrganizationAssignmentAssociation
            //must be created before Staff can be updated.
            var staffCreate = resources
                .SingleOrDefault(r => r.Resource == "/ed-fi/staffs");

            if (staffCreate == null)
            {
                return;
            }

            var staffEducationOrganizationEmploymentAssociation = resources
                .Single(r => r.Resource == "/ed-fi/staffEducationOrganizationEmploymentAssociations");

            var staffEducationOrganizationAssignmentAssociation = resources
                .Single(r => r.Resource == "/ed-fi/staffEducationOrganizationAssignmentAssociations");

            int highestOrder = Math.Max(
                staffEducationOrganizationAssignmentAssociation.Order,
                staffEducationOrganizationEmploymentAssociation.Order);

            var staffUpdate = new LoadOrder()
            {
                Resource = staffCreate.Resource,
                Order = highestOrder + 1,
                Operations = new List<string> { "Update" }
            };

            staffCreate.Operations.Remove("Update");

            resources.Insert(
                resources.IndexOf(
                    resources.FirstOrDefault(r => r.Order == staffUpdate.Order) ?? resources[^1])
                , staffUpdate
            );
        }

        private void ApplyStudentTransformation(IList<LoadOrder> resources)
        {
            var studentCreate = resources
                .SingleOrDefault(r => r.Resource == "/ed-fi/students");

            if (studentCreate == null)
            {
                return;
            }


            var studentSchoolAssociation = resources
                .Single(r => r.Resource == "/ed-fi/studentSchoolAssociations");

            var studentUpdate = new LoadOrder()
            {
                Resource = studentCreate.Resource,
                Order = studentSchoolAssociation.Order + 1,
                Operations = new List<string> { "Update" }
            };

            studentCreate.Operations.Remove("Update");

            resources.Insert(
                resources.IndexOf(resources.FirstOrDefault(r => r.Order == studentUpdate.Order) ??
                                  resources[^1])
                , studentUpdate
            );
        }
    }
}
