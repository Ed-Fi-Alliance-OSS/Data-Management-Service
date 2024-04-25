@SchoolYear
Feature: School Year Reference Validation
  Reference validation on School Years

  """
  @StudentEducationOrganizationAssociation Hook that will create the following data for the required tests
  createStudent(studentUniqueId)
  createSchool(schoolId)
  createCourse(courseCode, schoolId)
  createSession(schoolId, schoolYear, sessionName)
  createCourseOffering(localCourseCode, courseCode, schoolId, schoolYear, sessionName)
  createSection(localCourseCode, schoolId, schoolYear, sessionName, sectionIdentifier)
  """

  @Ignore @StudentEducationOrganizationAssociation
  Scenario: Post valid request using a existing year and make sure response is correctly
    Given a existing school year
    When sending a POST request to "/ed-fi/studentSectionAssociation" with body
    """
        {
        "beginDate": "2024-04-25",
        "sectionReference": {
            "localCourseCode": "string",
            "schoolId": 0,
            "schoolYear": 0,
            "sectionIdentifier": "string",
            "sessionName": "string",
        },
        "studentReference": {
            "studentUniqueId": "string",
        }
        }
    """
    Then the response code is 200
    And the response body is
    """
    """

  @Ignore @StudentEducationOrganizationAssociation
  Scenario: Post invalid request using a non existing year and make sure to receive an appropiate error message
    When sending a POST request to "/ed-fi/studentSectionAssociation" with body
    """
        {
        "beginDate": "2024-04-25",
        "sectionReference": {
            "localCourseCode": "string",
            "schoolId": 0,
            "schoolYear": 0,
            "sectionIdentifier": "string",
            "sessionName": "string",
        },
        "studentReference": {
            "studentUniqueId": "string",
        }
        }
    """
    Then the response code is 404

  #Course Offering / School Year
  @Ignore
  Scenario: Post a valid request using an existing CourseOffering
    Given a existing CourseOffering
    When sending a POST request to "/ed-fi/courseOfferings" with body
    """
        {
        "localCourseCode": "string",
        "courseReference": {
            "courseCode": "string",
            "educationOrganizationId": 0
        },
        "schoolReference": {
            "schoolId": 0
        },
        "sessionReference": {
            "schoolId": 0,
            "schoolYear": 0,
            "sessionName": "string"
        }
        }
    """
    Then the response code is 200
    And the response body is
    """
    """

  @Ignore
  Scenario: Post a valid request using a non existing CourseOffering and API will handle this correctly
    When sending a POST request to "/ed-fi/courseOfferings" with body
    """
        {
        "localCourseCode": "string",
        "courseReference": {
            "courseCode": "string",
            "educationOrganizationId": 0
        },
        "schoolReference": {
            "schoolId": 0
        },
        "sessionReference": {
            "schoolId": 0,
            "schoolYear": 0,
            "sessionName": "string"
        }
        }
    """
    Then the response code is 400


  #The cohort year scenario is interesting because it is testing a situation where a resource has a collection of SchoolYear references.
  #The StudentEducationOrganizationAssociation has this collection, via CohortYears.
  @Ignore
  Scenario: Handling the array with two valid cohorts
    Verify that the API correctly processes the request when both CohortYears are valid.
    Given 2 valid CohortYears
    When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
    """
    {
        "educationOrganizationReference": {
            "educationOrganizationId": 0
        },
        "studentReference": {
            "studentUniqueId": "string"
        },
        "cohortYears": [
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            },
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            }
        ]
    }
    """
    Then the response code is 200

  @Ignore
  Scenario: Handling the array with 2 cohorts (1st valid / 2nd invalid)
    Ensure clients can not post when 1 of the CohortYears is invalid
    Given 2 CohortYears invalid and valid
    When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
    """
    {
        "educationOrganizationReference": {
            "educationOrganizationId": 0
        },
        "studentReference": {
            "studentUniqueId": "string"
        },
        "cohortYears": [
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            },
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            }
        ]
    }
    """
    Then the response code is 400

  @Ignore
  Scenario: Handling the array with 2 cohorts (1st invalid / 2nd valid)
    Ensure clients can not post when 1 of the CohortYears is invalid
    Given 2 CohortYears valid and invalid
    When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
    """
    {
        "educationOrganizationReference": {
            "educationOrganizationId": 0
        },
        "studentReference": {
            "studentUniqueId": "string"
        },
        "cohortYears": [
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            },
            {
            "cohortYearTypeDescriptor": "string",
            "termDescriptor": "string",
            "schoolYearTypeReference": {
                "schoolYear": 0
            }
            }
        ]
    }
    """
    Then the response code is 400
