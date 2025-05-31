Feature: Authorization Metadata endpoints

    Background:
        Given valid credentials
        And token received

    Scenario: 01 Ensure clients can GET authorization metadata for a specific claim set
        When a GET request is made to "/authorizationMetadata?claimSetName=AssessmentRead"
        Then it should respond with 200
        And the response body is an array with one object where
          | property       | value / condition |
          | claimSetName   | "AssessmentRead"  |
          | claims         | non-empty array   |
          | authorizations | non-empty array   |

    Scenario: 02 Ensure clients can GET authorization metadata for all claim sets when claimSetName is not specified
        When a GET request is made to "/authorizationMetadata"
        Then it should respond with 200
        And the response body is an array with more than one object where each object
          | property       | value / condition |
          | claimSetName   | non-empty string  |

    Scenario: 03 Ensure clients can GET authorization metadata for a non-existing claim set
        When a GET request is made to "/authorizationMetadata?claimSetName=NotThere"
        Then it should respond with 200
        And the response body is
        """
        []
        """
