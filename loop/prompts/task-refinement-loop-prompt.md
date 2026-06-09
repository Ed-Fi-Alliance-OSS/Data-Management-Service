You are refining the implementation task breakdown for the plan at 409-fix-plan.md,
It is part of the larger design found in reference/design/backend-redesign.
The implementation task breakdown belongs in tasks.json in the repo root folder.

1. Study the story, its epic siblings, and the larger design.

2. If tasks.json does not exist in the repo root folder, create it with an initial structured task breakdown for the story:
- The file must be valid JSON.
- The root value must be an array of task objects.
- Each task must have category, description, acceptance-criteria, steps-to-verify, and completed.
- description must describe the work to do.
- acceptance-criteria must be an array of specific outcomes that determine whether the task is complete.
- steps-to-verify must be an array of concrete review, build, or test commands/actions.
- completed must be false.
- Each task should be small enough to complete within one agent context window without triggering compaction.
- Tasks should cover implementation, tests, integration points, refactoring needs, and risky unknowns implied by the story.
- Tasks must stay within the story scope, adding work only when required by the story or integration with previous work.
- After creating tasks.json, output <status>TASKS_CREATED</status> and stop.

3. If tasks.json already exists, study the current tasks.json and validate it for quality:
- Completeness against the story and relevant design docs.
- Sufficient detail in every task description and acceptance-criteria array.
- Clear separation between the task description and acceptance criteria.
- Clear, concrete steps to verify for each task.
- Appropriate scope: no task should drift outside the story, and missing work should be added only when required by the story or integration with previous work.
- Appropriate size: each task should be completable within one agent context window without triggering compaction.
- Useful sequencing and dependency boundaries.
- Avoidance of duplicated tasks, vague tasks, and implementation tasks that are really review-only placeholders.
- Coverage of tests, integration points, refactoring needs, and risky unknowns implied by the story.

4. Apply your recommended improvements directly to tasks.json:
- If tasks.json already looks excellent and further changes would be low value, leave it unchanged and output <status>COMPLETE</status>.
- Otherwise, edit tasks.json so it is a better implementation plan for the story.
- For new or rewritten tasks, each task must have category, description, acceptance-criteria, steps-to-verify, and completed.
- For existing tasks that embed acceptance criteria in description, separate them into the acceptance-criteria array.
- Descriptions must describe the work to do without embedding acceptance criteria.
- acceptance-criteria must be an array of specific outcomes that determine whether the task is complete.
- steps-to-verify must be an array of concrete review, build, or test commands/actions.
- completed must be false for new tasks.
- Preserve completed values for existing tasks unless the task is being split or replaced because its shape is materially wrong.
- Keep the file valid JSON.

Only create or refine tasks.json. Do not implement story code, do not edit production or test source files, and do not make a git commit.
