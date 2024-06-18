Feature: Subclass resources validation

        Background:
            Given the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | localEducationAgencyCategoryDescriptor                             | categories                                                                                                                                    |
                  | 255901                 | Grand Bend ISD    | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider" }] |

        Scenario: Ensure client can't get create a school with the same ID as another Subclass
             When a POST request is made to "ed-fi/schools" with
                  """
                  {
                      "schoolId": 255901,
                      "nameOfInstitution": "School Test",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                      "type": "urn:ed-fi:api:identity-conflict",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": null,
                      "errors": [
                      "A natural key conflict occurred when attempting to create a new resource School with a duplicate key. The duplicate keys and values are (schoolId = 255901)"
                      ]
                  }
                  """
