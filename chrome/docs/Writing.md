# Documentation guide

Before writing, identify the real extension task the information will help with, what would
otherwise need rediscovery, its evidence, its applicable conditions, and the existing topic it
belongs to. Keep one authoritative explanation; link to it rather than maintaining copies.

Use English, the existing `docs` directory, and topic names chosen from actual work. Preserve
working documents. Do not populate a template tree with empty categories or enumerate source
files without explaining their relationship.

Write a verified method with its prerequisites, actions, expected outcome, and known limits.
For an architectural topic, explain ownership and the flow from input to result. For a failure,
preserve the distinguishing symptom, established cause, and tested fix. Include the relevant
source symbol, official document, or reproducible test. Do not describe source-only reasoning as
an executed test, and do not document an untried method as a working procedure.

Add new topics to the nearest index with a short description of when to read them. Keep the
README as the user entry point. Update outdated behavior and incoming links when changing or
moving a document; do not create a conflicting second explanation.

Keep raw outputs, screenshots used only for testing, temporary experiments, and browser profiles
under `build`, which Git ignores. Do not put secrets, temporary progress, proposed designs, or
future work lists in this topic library. Do not turn a one-time user instruction into a permanent
restriction or record unspoken user approval.

Before finishing a change, document a new reusable lesson if one was learned, correct affected
claims, and check the new links. Walk the relevant index path to confirm another contributor can
find the answer without repeating the investigation. Reuse existing evidence instead of rerunning
tests only to write a document. If there is no new knowledge, no new document is needed.
