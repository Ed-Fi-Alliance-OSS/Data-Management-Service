# API Schema Documentation

The `ApiSchema.json` file represents the API schema for the Ed-Fi DMS. It is an
interpretation of the Ed-Fi Data Standard, providing options and rules that help
in building a REST API definition consistent with the API definition generated
out of the original Ed-FI ODS/API. Embedded within this file, each domain entity
has a [JSON Schema](https://json-schema.org/) definition that can be used for
level 1 validation of incoming `POST` and `PUT` requests.

The file contains two main sections:

1. `projectSchemas`
2. `projectNameMapping`

This file is embedded in the DLL provided by NuGet package
`EdFi.DataStandard51.ApiSchema`.

## projectSchemas

This section is a collection of ProjectNamespaces mapped to ProjectSchema
objects. It contains the detailed schema information for each
project in the API.

```json
{
  "projectSchemas": {
    "[ProjectNamespace]": {
      // ProjectSchema object
    }
  }
}
```

## projectNameMapping

This section is a collection of MetaEdProjectNames mapped to ProjectNamespaces.
It contains the mapping between the project names used in MetaEd and the actual
project namespaces in the API.

```json
{
  "projectNameMapping": {
    "[MetaEdProjectName]": "[ProjectNamespace]"
  }
}
```

For example, the core Ed-Fi Data Standard project maps `"Ed-Fi": "ed-fi"`. Thus
all domain entities in the core Data Standard map to REST API endpoints at
`/ed-fi/[entityName]`.

## ProjectSchema Object

Each project in the projectSchemas section of ApiSchema.json is represented by a
ProjectSchema object with the following properties:

* `projectName`: The MetaEd project name (e.g., "EdFi" for a data standard
  entity).
* `projectVersion`: The version of the project, represented as a semantic
  version (SemVer).
* `isExtensionProject`: A boolean indicating whether this is an extension
  project.
* `description`: A string describing the project.
* `resourceSchemas`: A collection of EndpointNames mapped to ResourceSchema
  objects. These define the characteristics of each resource.
* `resourceNameMapping`: A collection of ResourceNames mapped to EndpointNames.
* `caseInsensitiveEndpointNameMapping`: A collection of lowercased EndpointNames
  mapped to correct-cased EndpointNames, used for case-insensitive endpoint
  access.
* `schoolYearEnumeration`: An optional ResourceSchema for the
  SchoolYearEnumeration (which is not a resource but has a ResourceSchema).
* `abstractResources`: A collection of ResourceNames of abstract resources (that
  don't materialize to endpoints) mapped to AbstractResourceInfos.
* `coreOpenApiSpecification` The core OpenApi specification DMS will use as a
  starting point. This is only present if `isExtensionProject` is `false`.
* `openApiExtensionFragments` The extension OpenApi fragments DMS needs to
  incorporate into its final OpenApi spec. This is only present if
  `isExtensionProject` is `true`.

```json
{
  "projectSchemas": {
    "[ProjectNamespace]": {
      "projectName": "EdFi",
      "projectVersion": "5.1.0",
      "isExtensionProject": false,
      "description": "Ed-Fi Core API",
      "resourceSchemas": {
        // EndpointNames mapped to ResourceSchema objects
      },
      "resourceNameMapping": {
        // ResourceNames mapped to EndpointNames
      },
      "caseInsensitiveEndpointNameMapping": {
        // Lowercased EndpointNames mapped to correct-cased EndpointNames
      },
      "schoolYearEnumeration": {
        // Optional ResourceSchema for SchoolYearEnumeration
      },
      "abstractResources": {
        // ResourceNames of abstract resources mapped to AbstractResourceInfos
      }
      "coreOpenApiSpecification": {
        // OpenAPI specification for a Data Standard. Mutually exclusive
        // with openApiExtensionFragments
      }
      "openApiExtensionFragments": {
        // OpenAPI fragments for an extension project. Mutually exclusive
        // with coreOpenApiSpecification
      }
    }
  }
}
```

## AbstractResourceInfos Object

These represent abstract resources that don't materialize to endpoints in the
API. They have an `identityPathOrder` property which is a list that represents
the elements of the resource's identity in the correct order.

Example:

```json
"abstractResources": {
  "EducationOrganization": {
    "identityPathOrder": [
      "educationOrganizationId"
    ]
  },
  "Person": {
    "identityPathOrder": [
      "personId"
    ]
  }
}
```

## CoreOpenApiSpecification Object

This is the core OpenApi specification for a Data Standard project. When DMS
is configured with extension projects, DMS will use this as a starting point.
This is a complete and valid OpenApi specification, but leaves blank any data
that can only be determined at runtime, for example server URLs.

This is only present if `isExtensionProject` is `false`.

Heavily truncated example:

```json
"coreOpenApiSpecification": {
  "components": {
    "parameters": {
      "limit": {
        "description": "Indicates the maximum number of items that should be returned in the results.",
        "in": "query",
        "name": "limit",
        "schema": {
          "default": 25,
          "format": "int32",
          "maximum": 500,
          "minimum": 0,
          "type": "integer"
        }
      }
    },
    "responses": {
      "Created": {
        "description": "The resource was created.  An ETag value is available in the ETag header..."
      }
    },
    "schemas": {
      "EdFi_AcademicWeek": {
        "description": "This entity represents the academic weeks for a school year...",
        "properties": {
          "beginDate": {
            "description": "The start date for the academic week.",
            "format": "date",
            "type": "string"
          },
          "endDate": {
            "description": "The end date for the academic week.",
            "format": "date",
            "type": "string"
          }
        },
        "required": [
          "beginDate",
          "endDate"
        ],
        "type": "object"
      },
      "EdFi_AcademicWeek_Reference": {
        "properties": {
          "schoolId": {
            "description": "The identifier assigned to a school. It must be distinct from ...",
            "type": "integer"
          },
          "weekIdentifier": {
            "description": "The school label for the week.",
            "maxLength": 80,
            "minLength": 5,
            "type": "string"
          }
        },
        "required": [
          "schoolId",
          "weekIdentifier"
        ],
        "type": "object"
      }
    }
  },
  "info": {
    "contact": {
      "url": "https://www.ed-fi.org/what-is-ed-fi/contact/"
    },
    "description": "",
    "title": "Ed-Fi Data Management Service API",
    "version": "1"
  },
  "openapi": "3.0.0",
  "paths": {
    "/ed-fi/academicWeeks": {
      "get": {},
      "post": {}
    },
    "/ed-fi/academicWeeks/{id}": {
      "delete": {},
      "put": {}
    },
  },
  "servers": [
    {
      "url": ""
    }
  ],
  "tags": [
    {
      "description": "This entity represents the academic weeks for a school year...",
      "name": "academicWeeks"
    }
  ]
}
```

## OpenApiExtensionFragments Object

These are OpenApi fragments for an extension project. When DMS
is configured with extension projects, DMS will use the
CoreOpenApiSpecification as a starting point, and insert extension fragments
into the appropriate locations.

This is only present if `isExtensionProject` is `true`.

The overall structure of this object is made up of four sections, and
looks like:

```json
 "openApiExtensionFragments": {
    "exts": {
    },
    "newPaths": {
    },
    "newSchemas": {
    },
    "newTags": [
    ]
 },
```

### Exts

The `exts` section contains OpenAPI fragments of extensions to an existing
Data Standard document. These fragments are inserted by DMS into the
existing document under an `_ext` document node.

An example of extensions to the Data Standard documents `Credential`,
`School` and `SurveyResponse`:

Example:

```json
"exts": {
    "EdFi_Credential": {
    "description": "",
    "properties": {
        "personReference": {
        "$ref": "#/components/schemas/EdFi_Person_Reference"
        }
    }
    "type": "object"
    },
    "EdFi_School": {
    "description": "",
    "properties": {
        "postSecondaryInstitutionReference": {
        "$ref": "#/components/schemas/EdFi_PostSecondaryInstitution_Reference"
        }
    },
    "type": "object"
    },
    "EdFi_SurveyResponse": {
    "description": "",
    "properties": {
        "personReference": {
        "$ref": "#/components/schemas/EdFi_Person_Reference"
        }
    },
    "type": "object"
    }
},
```

### NewPaths

The `newPaths` section contains OpenAPI fragments of new endpoint paths created
by the extension. These are added into the Data Standard OpenAPI specification
under `$.paths`.

Example:

```json
"paths": {
  "/tpdm/candidateEducatorPreparationProgramAssociations": {
     "get": {
       "description": "This GET operation provides access to resources...",
       "operationId": "getCandidateEducatorPreparationProgramAssociation",
       "parameters": [
         {
           "$ref": "#/components/parameters/offset"
         },
         {
           "$ref": "#/components/parameters/limit"
         },
         {
           "$ref": "#/components/parameters/totalCount"
         },
         {
           "description": "The begin date for the association.",
           "in": "query",
           "name": "beginDate",
           "schema": {
             "format": "date",
             "type": "string"
           },
           "x-Ed-Fi-isIdentity": true
         },
         {
           "description": "The end date for the association.",
           "in": "query",
           "name": "endDate",
           "schema": {
             "format": "date",
             "type": "string"
           }
         }
       ],
       "responses": {
         "200": {
           "content": {
             "application/json": {
               "schema": {
                 "items": {
                   "$ref": "#/components/schemas/TPDM_CandidateEducatorPreparationProgramAssociation"
                 },
                 "type": "array"
               }
             }
           },
           "description": "The requested resource was successfully retrieved."
         }
       },
       "summary": "Retrieves specific resources using...",
       "tags": [
         "academicWeeks"
       ]
     },
     "post": {
       "description": "The POST operation can be used to create...",
       "operationId": "postCandidateEducatorPreparationProgramAssociation",
       "requestBody": {
         "content": {
           "application/json": {
             "schema": {
               "$ref": "#/components/schemas/TPDM_CandidateEducatorPreparationProgramAssociation"
             }
           }
         },
         "description": "The JSON representation of the CandidateEducatorPreparationProgramAssociation...",
         "required": true,
         "x-bodyName": "CandidateEducatorPreparationProgramAssociation"
       },
       "responses": {
         "200": {
           "$ref": "#/components/responses/Updated"
         }
       },
       "summary": "Creates or updates resources based on the natural key values of the supplied resource.",
         "tags": [
           "candidateEducatorPreparationProgramAssociations"
         ]
       }
    }
 }
```

### NewSchemas

The `newSchemas` section contains OpenAPI fragments of new document schemas created
by the extension. These are added into the Data Standard OpenAPI specification
under `$.components.schemas`.

Example:

```json
"newSchemas": {   
  "TPDM_Candidate": {
    "description": "A candidate is both a person enrolled in a...",
    "properties": {
      "birthDate": {
        "description": "The month, day, and year on which an individual was born.",
        "format": "date",
        "type": "string"
      },
      "candidateIdentifier": {
        "description": "A unique alphanumeric code assigned to a candidate.",
        "maxLength": 32,
        "minLength": 1,
        "type": "string"
      },
      "firstName": {
        "description": "A name given to an individual at birth...",
        "maxLength": 75,
        "type": "string"
      },
      "lastSurname": {
        "description": "The name borne in common by members of a family.",
        "maxLength": 75,
        "type": "string"
      }
    },
    "required": [
      "candidateIdentifier",
      "firstName",
      "lastSurname",
      "birthDate"
    ],
    "type": "object"
  }
}
```

### NewTags

The `newTags` section contains OpenAPI the global tags needed for new endpoint paths created
by the extension. These are added into the Data Standard OpenAPI specification
under `$.tags`.

Example:

```json
"newTags": [
  {
    "description": "A candidate is both a person enrolled in a...",
    "name": "candidates"
  },
  {
    "description": "The Educator Preparation Program is designed to...",
    "name": "educatorPreparationPrograms"
  },
]
 ```

## ResourceSchemas

The `resourceSchemas` property in `ProjectSchema` is of type
`ResourceSchemaMapping`, which is a collection of `EndpointNames` mapped to
`ResourceSchema` objects. Each `ResourceSchema` object contains detailed
information about a specific API resource.

A `ResourceSchema` can be one of three types:

* Base Resource Schema
* Association Subclass Resource Schema
* Domain Entity Subclass Resource Schema

All types share common properties from the Base Resource Schema, with additional
properties for subclass types.

### Common Properties

* `resourceName`: The name of the resource (typically the entity metaEdName).
* `isDescriptor`: Boolean indicating if the resource is a descriptor.
* `isSchoolYearEnumeration`: Boolean indicating if the resource is a school year
  enumeration.
* `allowIdentityUpdates`: Boolean indicating if API clients can modify the
  identity of an existing resource document.
* `jsonSchemaForInsert`: The JSON Schema for this resource, used for validation
  when inserting or updating a resource document.
* `equalityConstraints`: A list of EqualityConstraint objects (source/target
  JsonPath pairs), used for validating the equality of two document elements
  that must be the same.
* `identityJsonPaths`: A list of JsonPaths that are part of the resource's
  identity.
* `booleanJsonPaths`: A list of JsonPaths for boolean fields (used in type
  coercion).
* `numericJsonPaths`: A list of JsonPaths for numeric fields (used in type
  coercion).
* `documentPathsMapping`: A collection of MetaEd property fullnames mapped to
  DocumentPaths objects.
* `queryFieldMapping`: A mapping of API query term strings to the JsonPaths in
  the document for querying, used for building search engine API query
  expressions.

### Additional Properties for Subclass Resources

For resources that are subclasses (`isSubclass: true`), there are additional
properties that describe the subclass's relationship to its superclass.

#### Association Subclass

* `superclassProjectName`: The project name of the superclass.
* `superclassResourceName`: The resource name of the superclass.

#### Domain Entity Subclass

Includes all properties of Association Subclass, plus:

* `superclassIdentityJsonPath`: The JsonPath for the superclass identity (used
  for identity renaming in Domain Entities).

Example:

```json
{
  "resourceSchemas": {
    "students": {
      "resourceName": "Student",
      "isDescriptor": false,
      "isSchoolYearEnumeration": false,
      "allowIdentityUpdates": false,
      "jsonSchemaForInsert": {
        // JSON schema object
      },
      "equalityConstraints": [
        // List of EqualityConstraint objects
      ],
      "identityJsonPaths": [
        // List of JsonPath strings
      ],
      "booleanJsonPaths": [
        // List of JsonPath strings
      ],
      "numericJsonPaths": [
        // List of JsonPath strings
      ],
      "documentPathsMapping": {
        // MetaEd property fullnames mapped to DocumentPaths objects
      },
      "queryFieldMapping": {
        // API query term strings mapped to JsonPaths
      },
      "isSubclass": false
    }
    // More resources...
  }
}
```

## DocumentPathsMappings

The `documentPathsMapping` property in `ResourceSchema` is a collection of
MetaEd property fullnames mapped to `DocumentPaths` objects, which provide
information on how to access different properties within a resource document,
including both scalar values and references to other resources.

A `DocumentPaths` can be one of three types:

* `DocumentReferencePaths`
* `DescriptorReferencePath`
* `ScalarPath`

### DocumentReferencePaths

Used for references to non-descriptor resources.

* `isReference: true` Boolean indicating path is a reference.
* `isDescriptor: false` Boolean indicating path is a not descriptor reference.
* `projectName`: The MetaEd project name of the referenced resource.
* `resourceName`: The name of the referenced API resource.
* `referenceJsonPaths`: An array of ReferenceJsonPaths objects, providing
  information on how to map reference fields in the current document to identity
  fields in the referenced document.
* `referenceJsonPath`: The JsonPath to the reference in the current document.
* `identityJsonPath`: The corresponding JsonPath in the referenced document.

### DescriptorReferencePath

Used for references to descriptor resources.

* `isReference: true` Boolean indicating path is a reference.
* `isDescriptor: true` Boolean indicating path is a descriptor reference.
* `projectName`: The MetaEd project name of the descriptor.
* `resourceName`: The name of the descriptor resource.
* `path`: The JsonPath to the descriptor value in the document.

### ScalarPath

Used for scalar (non-reference) properties.

* `isReference: false` Boolean indicating path is a not reference.
* `path`: The JsonPath to the scalar value in the document.

### DocumentPathsMappings Example

Here's an example of what a `documentPathsMapping` might look like for a
`Student` resource. Here `studentUniqueId`, `firstName`, `lastSurname`, and
`birthDate` are scalar properties, `schoolReference` is a reference property to
a `School` resource, and `sexDescriptor` is a descriptor reference property to a
`SexDescriptor` resource.

```json
{
  "studentUniqueId": {
    "isReference": false,
    "path": "$.studentUniqueId"
  },
  "firstName": {
    "isReference": false,
    "path": "$.firstName"
  },
  "lastSurname": {
    "isReference": false,
    "path": "$.lastSurname"
  },
  "birthDate": {
    "isReference": false,
    "path": "$.birthDate"
  },
  "schoolReference": {
    "isReference": true,
    "projectName": "EdFi",
    "resourceName": "School",
    "isDescriptor": false,
    "referenceJsonPaths": [
      {
        "referenceJsonPath": "$.schoolReference.schoolId",
        "identityJsonPath": "$.schoolId"
      }
    ]
  },
  "sexDescriptor": {
    "isReference": true,
    "projectName": "EdFi",
    "resourceName": "SexDescriptor",
    "isDescriptor": true,
    "path": "$.sexDescriptor"
  }
}
```

## ReferenceJsonPaths

The `referenceJsonPaths` property is an array of `ReferenceJsonPaths` objects.
It provides information about how to map reference fields in the current
document to identity fields in the referenced document.

* `referenceJsonPath`: The JsonPath to the reference field in the current
  document.
* `identityJsonPath`: The corresponding JsonPath to the identity field in the
  referenced document.

The `referenceJsonPaths` array serves several important purposes:

* **Reference Construction**: It allows API implementations to correctly
  construct document references by matching the naming conventions between the
  referencing and referenced documents.
* **Identity Mapping**: It provides a clear mapping between the identity fields
  in the referenced document and how they appear in the referencing document.
* **Ordered Identity**: The array is ordered correctly for constructing an
  identity hash, which is crucial for maintaining consistency in references.

Here's an example of `referenceJsonPaths` for a `CourseOffering` reference on a
`Section` resource:

```json
[
  {
    "identityJsonPath": "$.localCourseCode",
    "referenceJsonPath": "$.courseOfferingReference.localCourseCode"
  },
  {
    "identityJsonPath": "$.schoolReference.schoolId",
    "referenceJsonPath": "$.courseOfferingReference.schoolId"
  },
  {
    "identityJsonPath": "$.sessionReference.schoolYear",
    "referenceJsonPath": "$.courseOfferingReference.schoolYear"
  },
  {
    "identityJsonPath": "$.sessionReference.sessionName",
    "referenceJsonPath": "$.courseOfferingReference.sessionName"
  }
]
```

## EqualityConstraints

An EqualityConstraint array represents pairs of JsonPaths within a resource
document that must have equal values.

* `sourceJsonPath`: A JsonPath pointing to a value in the resource document.
* `targetJsonPath`: Another JsonPath in the same document that should have the
  same value as the source.

Equality constraints maintain data integrity within a single resource document.
For example, a `schoolYear` must be the same for all `GradingPeriod` references
in a `Session` document.

Here's an example of an EqualityConstraint for a `Session` resource:

```json
"equalityConstraints": [
  {
    "sourceJsonPath": "$.gradingPeriods[*].gradingPeriodReference.schoolYear",
    "targetJsonPath": "$.schoolYearTypeReference.schoolYear"
  },
  {
    "sourceJsonPath": "$.gradingPeriods[*].gradingPeriodReference.schoolId",
    "targetJsonPath": "$.schoolReference.schoolId"
  },
  {
    "sourceJsonPath": "$.schoolReference.schoolId",
    "targetJsonPath": "$.academicWeeks[*].academicWeekReference.schoolId"
  }
]
```
