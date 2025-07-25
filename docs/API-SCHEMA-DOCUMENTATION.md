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
* `openApiBaseDocuments`: The base OpenAPI documents for resources and descriptors.
  Contains two base OpenAPI specifications: one for resources and one for descriptors.

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
      },
      "openApiBaseDocuments": {
        "resources": {
          // Complete OpenAPI specification for resources
        },
        "descriptors": {
          // Complete OpenAPI specification for descriptors
        }
      }
    }
  }
}
```

## AbstractResources Object

These represent abstract resources that don't materialize to endpoints in the
API. Each abstract resource has:

* `identityPathOrder`: A list that represents the elements of the resource's 
  identity in the correct order.
* `openApiFragment`: An OpenAPI fragment that defines the OpenAPI schema for
  this abstract resource.

Example:

```json
"abstractResources": {
  "EducationOrganization": {
    "identityPathOrder": [
      "educationOrganizationId"
    ],
    "openApiFragment": {
      "components": {
        "schemas": {
          "EdFi_EducationOrganization": {
            "description": "This entity represents any public or private institution...",
            "properties": {
              "educationOrganizationId": {
                "description": "The identifier assigned to an education organization.",
                "type": "integer"
              }
            },
            "required": ["educationOrganizationId"],
            "type": "object"
          }
        }
      }
    }
  },
  "Person": {
    "identityPathOrder": [
      "personId"
    ]
  }
}
```

## OpenApiBaseDocuments Object

The `openApiBaseDocuments` property contains the base OpenAPI specifications for
a project. DMS will use these as starting points and merge in OpenAPI resource-level fragments.
These starting points leave blank any data that can only be
determined at runtime, for example server URLs.

The object contains two properties:
* `resources`: Complete OpenAPI specification for resource endpoints
* `descriptors`: Complete OpenAPI specification for descriptor endpoints

Example:

```json
"openApiBaseDocuments": {
  "resources": {
    "openapi": "3.0.1",
    "info": {
      "title": "Ed-Fi Resources API",
      "version": "5.0.0",
      "contact": {
        "url": "https://www.ed-fi.org/what-is-ed-fi/contact/"
      }
    },
    "servers": [
      {
        "url": ""
      }
    ],
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
      }
    }
  },
  "descriptors": {
    "openapi": "3.0.1",
    "info": {
      "title": "Ed-Fi Descriptors API",
      "version": "5.0.0"
    },
    "servers": [
      {
        "url": ""
      }
    ]
  }
}
```

## OpenAPI Fragments in ResourceSchema

OpenAPI fragments are now defined at the resource level
within each resource's schema definition. DMS will use the OpenApiBaseDocuments 
as a starting point, and merge in the fragments from each resource.

The `openApiFragments` property exists within each resource in the `resourceSchemas`
collection. This property contains fragments for both resources and descriptors:

* `resources`: OpenAPI fragments for resource endpoints
* `descriptors`: OpenAPI fragments for descriptor endpoints

Each fragment can contain the following sections:

```json
"resourceSchemas": {
  "candidates": {
    "openApiFragments": {
      "resources": {
        "exts": {
          // Extensions to existing resources
        },
        "components": {
          "schemas": {
            // New schemas for this resource
          }
        },
        "paths": {
          // New paths for this resource
        },
        "tags": [
          // New tags for this resource
        ]
      }
    }
    // Other resource properties...
  }
}
```

### Exts

The `exts` section contains OpenAPI fragments of extensions to an existing
Data Standard document. These fragments are inserted by DMS into the
existing document under an `_ext` document node.

An example exts for an extension to the Data Standard document `Credential`:

```json
"exts": {
  "EdFi_Credential": {
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

### Paths

The `paths` section contains OpenAPI fragments of endpoint paths created
by the resource. These are merged into the base OpenAPI
document's paths section.

Example:

```json
"paths": {
  "/tpdm/candidateEducatorPreparationProgramAssociations": {
     "get": {
       "description": "This GET operation provides access to resources...",
       "operationId": "getCandidateEducatorPreparationProgramAssociation",
       "parameters": [
          ...
       ],
       "responses": {
          ...
       },
       "summary": "Retrieves specific resources using...",
       "tags": [
         "academicWeeks"
       ]
     }
   }
 }
```

### Schemas (within components)

The `schemas` section within `components` contains OpenAPI fragments of 
schemas created by the resource. These are merged into the appropriate 
base OpenAPI document under `$.components.schemas`.

Example:

```json
"components": {
  "schemas": {   
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
}
```

### Tags

The `tags` section contains OpenAPI tags needed for endpoints created
by the resource. These are merged into the appropriate base OpenAPI global
tags array.

Example:

```json
"tags": [
  {
    "description": "A candidate is both a person enrolled in a...",
    "name": "candidates"
  },
  {
    "description": "The Educator Preparation Program is designed to...",
    "name": "educatorPreparationPrograms"
  }
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
* `isResourceExtension`: Boolean indicating if the resource extends an existing
  Ed-Fi resource (used in extension projects).
* `openApiFragments`: OpenAPI fragments for extension resources. Contains
  `resources` and `descriptors` subdocuments with fragments to be merged into the
  base OpenAPI documents.

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
      "isSubclass": false,
      "isResourceExtension": false
      "openApiFragments": {
        "resources": {
          // OpenAPI fragments for resource endpoints
        },
        "descriptors": {
          // OpenAPI fragments for descriptor endpoints  
        }
      }
    },
    "candidates": {
      "resourceName": "Candidate",
      "isDescriptor": false,
      "isResourceExtension": false,
      // Other properties...
      "openApiFragments": {
        "resources": {
          // OpenAPI fragments for resource endpoints
        },
        "descriptors": {
          // OpenAPI fragments for descriptor endpoints  
        }
      }
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
