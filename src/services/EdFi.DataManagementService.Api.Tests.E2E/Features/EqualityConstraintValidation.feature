Feature: Equality Constraint Validation
    Equality constraints on the resource describe values that must be equal when posting a resource.

Scenario: Post a valid bell schedule
    Given a post to the bellschedules endpoint where the referenced school id and all class period school ids match
    Then receive created response

Scenario: Post an invalid bell schedule
    Given a post to the bellschedules endpoint where the referenced school id and all class period school ids do not match
    Then receive bad request response
