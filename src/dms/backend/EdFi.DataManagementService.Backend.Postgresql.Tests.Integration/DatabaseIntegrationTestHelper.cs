// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Transactions;
using EdFi.DataManagementService.Backend.Postgresql.Test.Integration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class StudentSchoolAssociationAuthorization
{
    public required string StudentUniqueId { get; set; }
    public long HierarchySchoolId { get; set; }
    public required string StudentSchoolAuthorizationEducationOrganizationIds { get; set; }
    public long StudentSchoolAssociationId { get; set; }
    public short StudentSchoolAssociationPartitionKey { get; set; }
}

public class ContactStudentSchoolAuthorization
{
    public required string StudentUniqueId { get; set; }
    public required string ContactUniqueId { get; set; }
    public required string ContactStudentSchoolAuthorizationEducationOrganizationIds { get; set; }

    public long StudentContactAssociationId { get; set; }
    public short StudentContactAssociationPartitionKey { get; set; }

    public long? StudentSchoolAssociationId { get; set; }
    public short? StudentSchoolAssociationPartitionKey { get; set; }
}

public class StudentSecurableDocument
{
    public required string StudentUniqueId { get; set; }
    public long StudentSecurableDocumentId { get; set; }
    public short StudentSecurableDocumentPartitionKey { get; set; }
}

public class ContactSecurableDocument
{
    public required string ContactUniqueId { get; set; }
    public long ContactSecurableDocumentId { get; set; }
    public short ContactSecurableDocumentPartitionKey { get; set; }
}

public class StaffSecurableDocument
{
    public required string StaffUniqueId { get; set; }
    public long StaffSecurableDocumentId { get; set; }
    public short StaffSecurableDocumentPartitionKey { get; set; }
}

public class StaffEducationOrganizationAuthorization
{
    public required string StaffUniqueId { get; set; }
    public long HierarchyEdOrgId { get; set; }
    public required string StaffEducationOrganizationAuthorizationEdOrgIds { get; set; }
    public long StaffEducationOrganizationId { get; set; }
    public short StaffEducationOrganizationPartitionKey { get; set; }
}

public class DatabaseIntegrationTestHelper : DatabaseTest
{
    public async Task<UpsertResult> UpsertEducationOrganization(
        string resourceName,
        long schoolId,
        long? parentEducationOrganizationId
    )
    {
        return await UpsertEducationOrganization(
            Guid.NewGuid(),
            Guid.NewGuid(),
            resourceName,
            schoolId,
            parentEducationOrganizationId
        );
    }

    public async Task<UpsertResult> UpsertEducationOrganization(
        Guid documentUuid,
        Guid referentialId,
        string resourceName,
        long schoolId,
        long? parentEducationOrganizationId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            resourceName,
            documentUuid,
            referentialId,
            $$"""
            {
                "someProperty": "someValue"
            }
            """,
            isInEducationOrganizationHierarchy: true,
            educationOrganizationId: schoolId,
            parentEducationOrganizationId: parentEducationOrganizationId,
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpdateResult> UpdateEducationOrganization(
        Guid documentUuid,
        Guid referentialId,
        string resourceName,
        long schoolId,
        long? parentEducationOrganizationId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            resourceName,
            documentUuid,
            referentialId,
            $$"""
            {
                "someProperty": "someValue"
            }
            """,
            isInEducationOrganizationHierarchy: true,
            educationOrganizationId: schoolId,
            parentEducationOrganizationId: parentEducationOrganizationId,
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    public async Task<UpsertResult> UpsertStudentSchoolAssociation(long schoolId, string studentUniqueId)
    {
        return await UpsertStudentSchoolAssociation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            schoolId,
            studentUniqueId
        );
    }

    public async Task<UpsertResult> UpsertStudentSchoolAssociation(
        Guid documentUuid,
        Guid referentialId,
        long schoolId,
        string studentUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "StudentSchoolAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                },
                "schoolReference": {
                  "schoolId": {{schoolId}}
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(schoolId)
                    ),
                ],
                [new StudentUniqueId(studentUniqueId)],
                [],
                []
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpsertResult> UpsertStudentContactAssociation(
        Guid documentUuid,
        Guid referentialId,
        string studentUniqueId,
        string contactUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "StudentContactAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                },
                "contactReference": {
                  "contactUniqueId": "{{contactUniqueId}}"
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            isContactAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [new StudentUniqueId(studentUniqueId)],
                [new ContactUniqueId(contactUniqueId)],
                []
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpsertResult> UpsertContact(
        Guid documentUuid,
        Guid referentialId,
        string contactUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "Contact",
            documentUuid,
            referentialId,
            $$"""
            {
                "contactUniqueId": "{{contactUniqueId}}",
                "firstName": "contactfirstname",
                "lastSurname": "contactlastname"
            }
            """,
            isStudentAuthorizationSecurable: false,
            isContactAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [],
                [new ContactUniqueId(contactUniqueId)],
                []
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpdateResult> UpdateStudentSchoolAssociation(
        Guid documentUuid,
        Guid referentialId,
        long schoolId,
        string studentUniqueId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            "StudentSchoolAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                },
                "schoolReference": {
                  "schoolId": {{schoolId}}
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(schoolId)
                    ),
                ],
                [new StudentUniqueId(studentUniqueId)],
                [],
                []
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    public async Task<DeleteResult> DeleteStudentSchoolAssociation(Guid documentUuid)
    {
        IDeleteRequest deleteRequest = CreateDeleteRequest("StudentSchoolAssociation", documentUuid);
        return await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);
    }

    public async Task<DeleteResult> DeleteStudentContactAssociation(Guid documentUuid)
    {
        IDeleteRequest deleteRequest = CreateDeleteRequest("StudentContactAssociation", documentUuid);
        return await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);
    }

    public async Task<UpsertResult> UpsertStudentSecurableDocument(string studentUniqueId)
    {
        return await UpsertStudentSecurableDocument(Guid.NewGuid(), Guid.NewGuid(), studentUniqueId);
    }

    public async Task<UpsertResult> UpsertStudentSecurableDocument(
        Guid documentUuid,
        Guid referentialId,
        string studentUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "CourseTranscript",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [new StudentUniqueId(studentUniqueId)],
                [],
                []
            ),
            projectName: "Ed-Fi"
        );
        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpdateResult> UpdateStudentSecurableDocument(
        Guid documentUuid,
        Guid referentialId,
        string studentUniqueId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            "CourseTranscript",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [new StudentUniqueId(studentUniqueId)],
                [],
                []
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    public async Task<
        List<StudentSchoolAssociationAuthorization>
    > GetAllStudentSchoolAssociationAuthorizations()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.StudentSchoolAssociationAuthorization";

        var results = new List<StudentSchoolAssociationAuthorization>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new StudentSchoolAssociationAuthorization
            {
                StudentUniqueId = reader["StudentUniqueId"].ToString()!,
                HierarchySchoolId = (long)reader["HierarchySchoolId"],
                StudentSchoolAuthorizationEducationOrganizationIds = reader[
                    "StudentSchoolAuthorizationEducationOrganizationIds"
                ]
                    .ToString()!,
                StudentSchoolAssociationId = (long)reader["StudentSchoolAssociationId"],
                StudentSchoolAssociationPartitionKey = (short)reader["StudentSchoolAssociationPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    public async Task<List<ContactStudentSchoolAuthorization>> GetAllContactStudentSchoolAuthorizations()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.ContactStudentSchoolAuthorization";

        var results = new List<ContactStudentSchoolAuthorization>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new ContactStudentSchoolAuthorization
            {
                StudentUniqueId = reader["StudentUniqueId"].ToString()!,
                ContactUniqueId = reader["ContactUniqueId"].ToString()!,
                ContactStudentSchoolAuthorizationEducationOrganizationIds = reader[
                    "ContactStudentSchoolAuthorizationEducationOrganizationIds"
                ]
                    .ToString()!,
                StudentContactAssociationId = (long)reader["StudentContactAssociationId"],
                StudentContactAssociationPartitionKey = (short)
                    reader["StudentContactAssociationPartitionKey"],
                StudentSchoolAssociationId =
                    reader["StudentSchoolAssociationId"] == DBNull.Value
                        ? null
                        : (long?)reader["StudentSchoolAssociationId"],
                StudentSchoolAssociationPartitionKey =
                    reader["StudentSchoolAssociationPartitionKey"] == DBNull.Value
                        ? null
                        : (short?)reader["StudentSchoolAssociationPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    public async Task<string> GetDocumentStudentSchoolAuthorizationEdOrgIds(Guid documentUuid)
    {
        await using NpgsqlCommand command = new(
            "SELECT StudentSchoolAuthorizationEdOrgIds FROM dms.Document WHERE DocumentUuid = $1;",
            Connection!,
            Transaction!
        )
        {
            Parameters = { new() { Value = documentUuid } },
        };

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return reader["StudentSchoolAuthorizationEdOrgIds"].ToString()!;
        }

        throw new InvalidOperationException("No matching document found.");
    }

    public async Task<List<StudentSecurableDocument>> GetAllStudentSecurableDocuments()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.studentsecurabledocument";

        var results = new List<StudentSecurableDocument>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new StudentSecurableDocument
            {
                StudentUniqueId = reader["StudentUniqueId"].ToString()!,
                StudentSecurableDocumentId = (long)reader["StudentSecurableDocumentId"],
                StudentSecurableDocumentPartitionKey = (short)reader["StudentSecurableDocumentPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    public async Task<List<ContactSecurableDocument>> GetAllContactSecurableDocuments()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.contactsecurabledocument";

        var results = new List<ContactSecurableDocument>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new ContactSecurableDocument
            {
                ContactUniqueId = reader["ContactUniqueId"].ToString()!,
                ContactSecurableDocumentId = (long)reader["ContactSecurableDocumentId"],
                ContactSecurableDocumentPartitionKey = (short)reader["ContactSecurableDocumentPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    public async Task<string> GetDocumentContactStudentSchoolAuthorizationEdOrgIds(Guid documentUuid)
    {
        await using NpgsqlCommand command = new(
            "SELECT ContactStudentSchoolAuthorizationEdOrgIds FROM dms.Document WHERE DocumentUuid = $1;",
            Connection!,
            Transaction!
        )
        {
            Parameters = { new() { Value = documentUuid } },
        };
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return reader["ContactStudentSchoolAuthorizationEdOrgIds"].ToString()!;
        }
        throw new InvalidOperationException("No matching document found.");
    }

    public async Task<UpsertResult> UpsertStaffEducationOrganizationEmploymentAssociation(
        long educationOrganizationId,
        string staffUniqueId
    )
    {
        return await UpsertStaffEducationOrganizationEmploymentAssociation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            educationOrganizationId,
            staffUniqueId
        );
    }

    public async Task<UpsertResult> UpsertStaffEducationOrganizationEmploymentAssociation(
        Guid documentUuid,
        Guid referentialId,
        long educationOrganizationId,
        string staffUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "StaffEducationOrganizationEmploymentAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "staffReference": {
                  "staffUniqueId": "{{staffUniqueId}}"
                },
                "educationOrganizationReference": {
                  "educationOrganizationId": {{educationOrganizationId}}
                }
            }
            """,
            isStaffAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("EducationOrganization"),
                        new EducationOrganizationId(educationOrganizationId)
                    ),
                ],
                [],
                [],
                [new StaffUniqueId(staffUniqueId)]
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpsertResult> UpsertStaffEducationOrganizationEmploymentAssociation(
        Guid documentUuid,
        Guid referentialId,
        string staffUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "StaffEducationOrganizationEmploymentAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "staffReference": {
                  "staffUniqueId": "{{staffUniqueId}}"
                }
            }
            """,
            isStaffAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [],
                [],
                [new StaffUniqueId(staffUniqueId)]
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpdateResult> UpdateStaffEducationOrganizationEmploymentAssociation(
        Guid documentUuid,
        Guid referentialId,
        string staffUniqueId,
        long educationOrganizationId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            "StaffEducationOrganizationEmploymentAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "staffReference": {
                  "staffUniqueId": "{{staffUniqueId}}"
                },
                "educationOrganizationReference": {
                  "educationOrganizationId": {{educationOrganizationId}}
                }
            }
            """,
            isStaffAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(educationOrganizationId)
                    ),
                ],
                [],
                [],
                [new StaffUniqueId(staffUniqueId)]
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    public async Task<
        List<StaffEducationOrganizationAuthorization>
    > GetAllStaffEducationOrganizationAuthorizations()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.StaffEducationOrganizationAuthorization";

        var results = new List<StaffEducationOrganizationAuthorization>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new StaffEducationOrganizationAuthorization
            {
                StaffUniqueId = reader["StaffUniqueId"].ToString()!,
                HierarchyEdOrgId = (long)reader["HierarchyEdOrgId"],
                StaffEducationOrganizationAuthorizationEdOrgIds = reader[
                    "StaffEducationOrganizationAuthorizationEdOrgIds"
                ]
                    .ToString()!,
                StaffEducationOrganizationId = (long)reader["StaffEducationOrganizationId"],
                StaffEducationOrganizationPartitionKey = (short)
                    reader["StaffEducationOrganizationPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    public async Task<UpsertResult> UpsertStaffSecurableDocument(string staffUniqueId)
    {
        return await UpsertStaffSecurableDocument(Guid.NewGuid(), Guid.NewGuid(), staffUniqueId);
    }

    public async Task<UpsertResult> UpsertStaffSecurableDocument(
        Guid documentUuid,
        Guid referentialId,
        string staffUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "StaffSecurableDocument",
            documentUuid,
            referentialId,
            $$"""
            {
                "staffReference": {
                  "staffUniqueId": "{{staffUniqueId}}"
                }
            }
            """,
            isStaffAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [],
                [],
                [new StaffUniqueId(staffUniqueId)]
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    public async Task<UpdateResult> UpdateStaffSecurableDocument(
        Guid documentUuid,
        Guid referentialId,
        string staffUniqueId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            "StaffSecurableDocument",
            documentUuid,
            referentialId,
            $$"""
            {
                "staffReference": {
                  "staffUniqueId": "{{staffUniqueId}}"
                }
            }
            """,
            isStaffAuthorizationSecurable: true,
            documentSecurityElements: new DocumentSecurityElements(
                [],
                [],
                [],
                [],
                [new StaffUniqueId(staffUniqueId)]
            ),
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    public async Task<string> GetDocumentStaffEducationOrganizationAuthorizationEdOrgIds(Guid documentUuid)
    {
        await using NpgsqlCommand command = new(
            "SELECT StaffEducationOrganizationAuthorizationEdOrgIds FROM dms.Document WHERE DocumentUuid = $1;",
            Connection!,
            Transaction!
        )
        {
            Parameters = { new() { Value = documentUuid } },
        };

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return reader["StaffEducationOrganizationAuthorizationEdOrgIds"].ToString()!;
        }

        throw new InvalidOperationException("No matching document found.");
    }

    public async Task<List<StaffSecurableDocument>> GetAllStaffSecurableDocuments()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.StaffSecurableDocument";

        var results = new List<StaffSecurableDocument>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var document = new StaffSecurableDocument
            {
                StaffUniqueId = reader["StaffUniqueId"].ToString()!,
                StaffSecurableDocumentId = (long)reader["StaffSecurableDocumentId"],
                StaffSecurableDocumentPartitionKey = (short)reader["StaffSecurableDocumentPartitionKey"],
            };
            results.Add(document);
        }

        return results;
    }

    public static long[] ParseEducationOrganizationIds(string ids)
    {
        if (string.IsNullOrWhiteSpace(ids) || ids == "[]")
        {
            return Array.Empty<long>();
        }

        return System.Text.Json.JsonSerializer.Deserialize<string[]>(ids)?.Select(long.Parse).ToArray()
            ?? Array.Empty<long>();
    }
}
