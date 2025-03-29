Create a C# class that accepts a JSON object and creates SQL Sever tables. Each
JSON key that has a string, number, or boolean value becomes a column in the
table. Each key with a value that is an object is saved to a linked table, and
each node with a key whose name is suffixed with "Reference" becomes a foreign
key reference. Ignore the `link` nodes.

For example, the JSON object might look like this:

```json
{
    "id": "string",
    "educationOrganizationReference": {
      "educationOrganizationId": 0
    },
    "studentReference": {
      "studentUniqueId": "string"
    },
    "addresses": [
      {
        "addressTypeDescriptor": "string",
        "stateAbbreviationDescriptor": "string",
        "periods": [
          {
            "beginDate": "2025-03-29",
            "endDate": "2025-03-29"
          }
        ]
      }
    ],
    "ancestryEthnicOrigins": [
      {
        "ancestryEthnicOriginDescriptor": "string"
      }
    ],
    "barrierToInternetAccessInResidenceDescriptor": "string",
    "cohortYears": [
      {
        "cohortYearTypeDescriptor": "string",
        "termDescriptor": "string",
        "schoolYearTypeReference": {
          "schoolYear": 0
        }
      }
    ]
}
```

And this document would create the following tables

- StudentEducationOrganizationAssociation
  - with foreign keys to:
    - EducationOrganization
    - Student
- StudentEducationOrganizationAssociationAddress
- StudentEducationOrganizationAssociationAddressPeriod
- StudentEducationOrganizationAssociationAncestryEthnicOrigin
- StudentEducationOrganizationAssociationCohortYear
