// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public static class OpenApiDocumentTestBase
{
    public static JsonNode CoreSchemaRootNode()
    {
        JsonObject descriptorSchemas = new()
        {
            ["EdFi_AbsenceEventCategoryDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AcademicHonorCategoryDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AcademicSubjectDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AccommodationDescriptor"] = new JsonObject
            {
                ["description"] = "An Ed-Fi Descriptor",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
        };

        JsonObject schemas = new()
        {
            ["EdFi_AcademicWeek"] = new JsonObject
            {
                ["description"] = "AcademicWeek description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_AccountabilityRating"] = new JsonObject
            {
                ["description"] = "AccountabilityRating description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_School"] = new JsonObject
            {
                ["description"] = "School description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
            ["EdFi_SurveyResponse"] = new JsonObject
            {
                ["description"] = "SurveyResponse description",
                ["properties"] = new JsonObject(),
                ["type"] = "string",
            },
        };

        JsonObject paths = new()
        {
            ["/ed-fi/academicWeeks"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek get description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
                ["post"] = new JsonObject
                {
                    ["description"] = "academicWeek post description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
            },
            ["/ed-fi/academicWeeks/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek id get description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
                ["delete"] = new JsonObject
                {
                    ["description"] = "academicWeek delete description",
                    ["tags"] = new JsonArray("academicWeeks"),
                },
            },
        };

        JsonObject descriptorsPaths = new()
        {
            ["/ed-fi/accommodationDescriptors"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors get description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
                ["post"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors post description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
            },
            ["/ed-fi/accommodationDescriptors/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors id get description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
                ["delete"] = new JsonObject
                {
                    ["description"] = "accommodationDescriptors delete description",
                    ["tags"] = new JsonArray("accommodationDescriptors"),
                },
            },
        };

        JsonArray tags = [];
        tags.Add(
            new JsonObject { ["name"] = "academicWeeks", ["description"] = "AcademicWeeks Description" }
        );
        tags.Add(
            new JsonObject
            {
                ["name"] = "accountabilityRating",
                ["description"] = "AccountabilityRatings Description",
            }
        );

        JsonArray descriptorsTags = [];
        descriptorsTags.Add(
            new JsonObject
            {
                ["name"] = "academicSubjectDescriptors",
                ["description"] = "AcademicSubjects Descriptors Description",
            }
        );
        descriptorsTags.Add(
            new JsonObject
            {
                ["name"] = "accommodationDescriptors",
                ["description"] = "Accommodations Descriptors Description",
            }
        );

        var builder = new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: new JsonObject
                {
                    ["openapi"] = "3.0.1",
                    ["info"] = new JsonObject { ["title"] = "Ed-Fi Resources API", ["version"] = "5.0.0" },
                    ["components"] = new JsonObject { ["schemas"] = schemas },
                    ["paths"] = paths,
                    ["tags"] = tags,
                },
                descriptorsDoc: new JsonObject
                {
                    ["openapi"] = "3.0.1",
                    ["info"] = new JsonObject { ["title"] = "Ed-Fi Descriptors API", ["version"] = "5.0.0" },
                    ["components"] = new JsonObject { ["schemas"] = descriptorSchemas },
                    ["paths"] = descriptorsPaths,
                    ["tags"] = descriptorsTags,
                }
            );

        // Add resources for each schema
        builder.WithSimpleResource("AcademicWeek", false, schemas["EdFi_AcademicWeek"]);
        builder.WithSimpleResource("AccountabilityRating", false, schemas["EdFi_AccountabilityRating"]);

        // Add descriptors
        builder.WithSimpleDescriptor(
            "AbsenceEventCategoryDescriptor",
            descriptorSchemas["EdFi_AbsenceEventCategoryDescriptor"]
        );
        builder.WithSimpleDescriptor(
            "AcademicHonorCategoryDescriptor",
            descriptorSchemas["EdFi_AcademicHonorCategoryDescriptor"]
        );
        builder.WithSimpleDescriptor(
            "AcademicSubjectDescriptor",
            descriptorSchemas["EdFi_AcademicSubjectDescriptor"]
        );
        builder.WithSimpleDescriptor(
            "AccommodationDescriptor",
            descriptorSchemas["EdFi_AccommodationDescriptor"]
        );

        return builder.WithEndProject().AsSingleApiSchemaRootNode();
    }

    public static JsonNode FirstExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_AcademicWeek"] = new JsonObject
            {
                ["description"] = "ext AcademicWeek description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/credentials"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential get" },
                ["post"] = new JsonObject { ["description"] = "credential post" },
            },
        };

        JsonObject descriptorNewPaths = new()
        {
            ["/tpdm/credentialDescriptor"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "credential descriptor get" },
                ["post"] = new JsonObject { ["description"] = "credential descriptor post" },
            },
        };

        JsonObject newSchemas = new()
        {
            ["TPDM_Credential"] = new JsonObject
            {
                ["description"] = "TPDM credential description",
                ["type"] = "string",
            },
        };

        JsonObject descriptorNewSchemas = new()
        {
            ["TPDM_CredentialDescriptor"] = new JsonObject
            {
                ["description"] = "TPDM credential descriptor description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName1",
                ["description"] = "First Extension Description1",
            }
        );
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName2",
                ["description"] = "First Extension Description2",
            }
        );

        JsonArray descriptorNewTags = [];
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName1",
                ["description"] = "First Extension Descriptor Description1",
            }
        );
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName2",
                ["description"] = "First Extension Descriptor Description2",
            }
        );

        var builder = new ApiSchemaBuilder().WithStartProject("tpdm", "5.0.0");

        // Add resource extension (exts)
        builder
            .WithStartResource("AcademicWeekExtension", isResourceExtension: true)
            .WithResourceExtensionFragments("resources", exts)
            .WithEndResource();

        // Add new extension resource
        builder
            .WithStartResource("Credential", isDescriptor: false)
            .WithNewExtensionResourceFragments("resources", newSchemas, newPaths, newTags)
            .WithEndResource();

        // Add new extension descriptor
        builder
            .WithStartResource("CredentialDescriptor", isDescriptor: true)
            .WithNewExtensionResourceFragments(
                "descriptors",
                descriptorNewSchemas,
                descriptorNewPaths,
                descriptorNewTags
            )
            .WithEndResource();

        return builder.WithEndProject().AsSingleApiSchemaRootNode();
    }

    public static JsonNode SecondExtensionSchemaRootNode()
    {
        JsonObject exts = new()
        {
            ["EdFi_School"] = new JsonObject
            {
                ["description"] = "ext School description",
                ["type"] = "string",
            },
        };

        JsonObject newPaths = new()
        {
            ["/tpdm/candidates/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate id get" },
                ["delete"] = new JsonObject { ["description"] = "candidate delete" },
            },
        };

        JsonObject descriptorNewPaths = new()
        {
            ["/tpdm/candidateDescriptor/{id}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "candidate descriptor id get" },
                ["delete"] = new JsonObject { ["description"] = "candidate descriptor delete" },
            },
        };

        JsonObject newSchemas = new()
        {
            ["TPDM_Candidate"] = new JsonObject
            {
                ["description"] = "TPDM candidate description",
                ["type"] = "string",
            },
        };

        JsonObject descriptorNewSchemas = new()
        {
            ["TPDM_CandidateDescriptor"] = new JsonObject
            {
                ["description"] = "TPDM candidate descriptor description",
                ["type"] = "string",
            },
        };

        JsonArray newTags = [];
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName3",
                ["description"] = "Second Extension Description3",
            }
        );
        newTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName4",
                ["description"] = "Second Extension Description4",
            }
        );

        JsonArray descriptorNewTags = [];
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName3",
                ["description"] = "Second Extension Descriptor Description3",
            }
        );
        descriptorNewTags.Add(
            new JsonObject
            {
                ["name"] = "ExtensionTagName4",
                ["description"] = "Second Extension Descriptor Description4",
            }
        );

        var builder = new ApiSchemaBuilder().WithStartProject("tpdm", "5.0.0");

        // Add resource extension (exts)
        builder
            .WithStartResource("SchoolExtension", isResourceExtension: true)
            .WithResourceExtensionFragments("resources", exts)
            .WithEndResource();

        // Add new extension resource
        builder
            .WithStartResource("Candidate", isDescriptor: false)
            .WithNewExtensionResourceFragments("resources", newSchemas, newPaths, newTags)
            .WithEndResource();

        // Add new extension descriptor
        builder
            .WithStartResource("CandidateDescriptor", isDescriptor: true)
            .WithNewExtensionResourceFragments(
                "descriptors",
                descriptorNewSchemas,
                descriptorNewPaths,
                descriptorNewTags
            )
            .WithEndResource();

        return builder.WithEndProject().AsSingleApiSchemaRootNode();
    }

    public static JsonNode CoreSchemaWithContactAndAddress()
    {
        JsonObject contactAddressSchema = new()
        {
            ["description"] = "Contact Address",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["streetNumberName"] = new JsonObject { ["type"] = "string" },
                ["city"] = new JsonObject { ["type"] = "string" },
            },
        };

        JsonObject contactSchema = new()
        {
            ["description"] = "Contact description",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/EdFi_Contact_Address" },
                },
            },
        };

        JsonObject schemas = new()
        {
            ["EdFi_Contact"] = contactSchema,
            ["EdFi_Contact_Address"] = contactAddressSchema,
        };

        var builder = new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: new JsonObject
                {
                    ["openapi"] = "3.0.1",
                    ["info"] = new JsonObject { ["title"] = "Ed-Fi Resources API", ["version"] = "5.0.0" },
                    ["components"] = new JsonObject { ["schemas"] = schemas },
                    ["paths"] = new JsonObject
                    {
                        ["/ed-fi/contacts"] = new JsonObject
                        {
                            ["get"] = new JsonObject
                            {
                                ["description"] = "contacts get",
                                ["tags"] = new JsonArray("contacts"),
                            },
                        },
                    },
                    ["tags"] = new JsonArray(
                        new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                    ),
                }
            )
            .WithSimpleResource("Contact", false)
            .WithEndProject();

        return builder.AsSingleApiSchemaRootNode();
    }

    public static JsonNode CoreSchemaWithContactOnly()
    {
        JsonObject contactSchema = new()
        {
            ["description"] = "Contact description",
            ["type"] = "object",
            ["properties"] = new JsonObject { ["contactUniqueId"] = new JsonObject { ["type"] = "string" } },
        };

        JsonObject schemas = new() { ["EdFi_Contact"] = contactSchema };

        var builder = new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: new JsonObject
                {
                    ["openapi"] = "3.0.1",
                    ["info"] = new JsonObject { ["title"] = "Ed-Fi Resources API", ["version"] = "5.0.0" },
                    ["components"] = new JsonObject { ["schemas"] = schemas },
                    ["paths"] = new JsonObject
                    {
                        ["/ed-fi/contacts"] = new JsonObject
                        {
                            ["get"] = new JsonObject
                            {
                                ["description"] = "contacts get",
                                ["tags"] = new JsonArray("contacts"),
                            },
                        },
                    },
                    ["tags"] = new JsonArray(
                        new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                    ),
                }
            )
            .WithSimpleResource("Contact", false)
            .WithEndProject();

        return builder.AsSingleApiSchemaRootNode();
    }
}
