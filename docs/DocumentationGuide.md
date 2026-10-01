# Documentation guide

Read the [index](README.md) before investigating an existing behavior. Use the topic document
and its source references to decide which evidence is already available. Existing `README.md`,
`LESSONS.md` and troubleshooting material remain the sources for the subjects they cover.

Preserve information that saves a future investigation: an observed failure's cause, a tested
repair, an interaction between system components, or a storage or runtime contract. Do not add
general API tutorials, unexplained file inventories, plans, temporary status or tool transcripts.
Choose subjects from the application's actual work, not from a predefined folder template.

Distinguish source inspection from execution. Cite the responsible file and member for a code
contract, and identify the relevant test or observed output for an executed claim. Do not call
an untested method verified. Reuse valid evidence; documentation does not require repeating
unchanged tests. Include conditions and limits needed to apply a lesson correctly.

Keep one authoritative explanation per topic and link from other documents. When behavior
changes, update that explanation and its index entry instead of creating a conflicting copy.
When adding a subject, add a short link saying when to read it. Use English consistently with
the source and existing project documentation; interface language strings retain their languages.

Generated probes, logs, screenshots and release preparation belong under ignored `build/` or
the established publish output. Do not commit user settings, personal bridges, signing keys or
credentials. A small illustrative artifact may be kept only when it explains a durable lesson.

Before finishing a documentation edit, check that its links exist, that the claims match their
evidence, and that a reader can reach the needed lesson from the index without reading unrelated
subjects. Remove repetition and unsupported assertions. New knowledge is recorded as part of
development; a task with no new knowledge does not need a new document.
