Feature: Validation of an abstract entity

Scenario: Validation of an abstract entity
    When sending a POST request to "/ed-fi/schools" with body
            """
            {
                "educationOrganizationCategories": [
                {
                    "educationOrganizationCategoryDescriptor": ""uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                }
                ],
                "gradeLevels": [
                {
                    "gradeLevelDescriptor": "string"
                }
                ],
                "schoolId": 0,
                "nameOfInstitution": "string"
    	    }
            """
    Then it should respond with 400
        And the response body is  
"""
{
    "detail": "Data validation failed. See 'validationErrors' for details.",
    "type": "urn:ed-fi:api:bad-request:data",
    "title": "Data Validation Failed",
    "status": 400,
    "correlationId": "04603d67-c318-4207-b855-9d86bc7335d1",
    "validationErrors": {
      "$.schoolId": [
        "SchoolId value must be different than 0."
      ]
    }
}
"""

"
Scenario: Validation of an abstract entity
    When sending a POST request to "ed-fi/studentProgramAssociations" with body
            """
            {
                "beginDate": "2024-04-22",
                "educationOrganizationReference": {
                    "educationOrganizationId": 0
                },
                "programReference": {
                    "educationOrganizationId": 0,
                    "programName": "21st CCLC",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                },
                "studentReference": {
                    "studentUniqueId": "604824"
                }
            }
            """
    Then the response code is 400
        And the response body is
"""
{
    "detail": "Data validation failed. See 'validationErrors' for details.",
    "type": "urn:ed-fi:api:bad-request:data",
    "title": "Data Validation Failed",
    "status": 400,
    "correlationId": "11abdedb-351a-48c5-837a-11d8fcb643f3",
    "validationErrors": {
      "$.programReference": [
        "ProgramReference is missing the following properties: EducationOrganizationId"
      ],
      "$.educationOrganizationReference": [
        "EducationOrganizationReference is missing the following properties: EducationOrganizationId"
      ]
    }
}
"""
