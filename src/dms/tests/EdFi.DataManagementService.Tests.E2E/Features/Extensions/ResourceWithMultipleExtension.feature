Feature: Resource with multiple extensions

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://tpdm.ed-fi.org"
            And the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/OperationalStatusDescriptor#Active                                 |
                  | uri://ed-fi.org/PostSecondaryInstitutionLevelDescriptor#Four or more years         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                 |

        Scenario: 01 Ensure clients can create a resource with tpdm extension reference
             When a POST request is made to "/ed-fi/postSecondaryInstitutions" with
                  """
                 {
                    "postSecondaryInstitutionId": 6000203,
                    "nameOfInstitution": "The University of Texas at Austin",
                    "operationalStatusDescriptor": "uri://ed-fi.org/OperationalStatusDescriptor#Active",
                    "shortNameOfInstitution": "UT-Austin",
                    "postSecondaryInstitutionLevelDescriptor": "uri://ed-fi.org/PostSecondaryInstitutionLevelDescriptor#Four or more years",
                    "categories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                      }
                    ]
                  }
                  """
              When a POST request is made to "/sample/buses" with
                  """
                 {
                    "busId": "bus123"
                 }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/schools" with
             """
             {
              "schoolId": "3",
              "nameOfInstitution": "Extension Test Community College",
              "educationOrganizationCategories": [
                {
                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                }
              ],
              "gradeLevels": [
                {
                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                }
              ],
              "_ext": {
                 "sample": {
                    "directlyOwnedBuses": [{
                        "directlyOwnedBusReference":{
                        "busId":"bus123"
                        }
                    }]
                },
                "tpdm": {
                  "postSecondaryInstitutionReference": {
                    "postSecondaryInstitutionId": 6000203
                  }
                }
                }
              }
             """
             Then it should respond with 201
             And the record can be retrieved with a GET request
                  """
                  {
                     "id": "{id}",
                     "schoolId": 3,
                      "nameOfInstitution": "Extension Test Community College",
                      "educationOrganizationCategories": [
                        {
                          "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                      ],
                      "gradeLevels": [
                        {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                      ],
                      "_ext": {
                      "sample": {
                            "directlyOwnedBuses": [{
                                "directlyOwnedBusReference":{
                                "busId":"bus123"
                                }
                            }]
                        },
                       "tpdm": {
                          "postSecondaryInstitutionReference": {
                            "postSecondaryInstitutionId": 6000203
                          }
                        }
                        }
                  }
                  """

    @ignore
    Scenario: 02 Ensure clients can not create a resource when tpdm extension reference is unavailable
             When a POST request is made to "/ed-fi/schools" with
             """
             {
              "schoolId": "4",
              "nameOfInstitution": "Extension Test Community College",
              "educationOrganizationCategories": [
                {
                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                }
              ],
              "gradeLevels": [
                {
                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                }
              ],
              "_ext": {
                "tpdm": {
                  "postSecondaryInstitutionReference": {
                    "postSecondaryInstitutionId": 6000204
                  }
                }
                }
              }
             """
             Then it should respond with 409

      @ignore
      Scenario: 03 Ensure clients can not create a resource when sample extension reference is unavailable
             When a POST request is made to "/ed-fi/schools" with
             """
             {
              "schoolId": "4",
              "nameOfInstitution": "Extension Test Community College",
              "educationOrganizationCategories": [
                {
                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                }
              ],
              "gradeLevels": [
                {
                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                }
              ],
              "_ext": {
                 "sample": {
                            "directlyOwnedBuses": [{
                                "directlyOwnedBusReference":{
                                "busId":"bus098"
                                }
                            }]
                        }
              }
             """
             Then it should respond with 409
