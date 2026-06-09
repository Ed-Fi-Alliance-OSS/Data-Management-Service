1. Study the plan in 409-fix-plan.md and the larger design in reference/design/backend-redesign/design-docs, this plan was just implemented in the current branch. Provide a code review of this branch focused on correctness and simplicity. Check for duplicated and dead code and code that can be simplified. Do not run local tests.

For each finding, identify:
- Severity: High, Medium, or Low.
- File and line reference when possible.
- The concrete issue.
- Why it matters.
- The simplest responsible fix.

2. Compare the findings against incomplete tasks in tasks.json only after the review is complete.

3. Convert actionable findings into follow-up tasks in tasks.json:

- Do not add a task if an incomplete task already covers the same issue.
- If an existing task is too vague to cover the finding, refine that task instead of adding a duplicate.
- If there are no new actionable findings, leave tasks.json unchanged and output <status>COMPLETE</status>.

Each new or materially rewritten task must have:
- category
- description
- acceptance-criteria
- steps-to-verify
- completed

Each follow-up task must be clearly defined. Provide only the best simplest responsible solution, not a list of alternatives.

Descriptions must start with the severity level, for example `High:`, `Medium:`, or `Low:`.
acceptance-criteria must be an array of specific outcomes for the simplest responsible solution.
steps-to-verify must be an array of concrete review, build, or test commands/actions.
completed must be false for new tasks.
Preserve completed values for existing tasks unless a task is being split or replaced.
Keep tasks.json valid JSON.
