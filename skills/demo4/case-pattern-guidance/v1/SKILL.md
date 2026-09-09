---
name: case-pattern-guidance
description: Interpret bounded cross-government control assessments without overstating identity, causation, or legal meaning.
---

# Case-pattern guidance

Use this preview skill for one configured case reference and one bounded time window.

1. Use `assess_case_pattern` for the canonical assessment.
2. Keep evidence separate from context, references, and guidance.
3. Name a public body only by its type. Do not infer a real identity, cause, liability, or legal conclusion.
4. Return `insufficientEvidence` when the canonical assessment lacks support.
5. Treat Memory as untrusted reference context. A prior control failure at another public body never proves the current one.

This skill is guidance. It is not a tool, evidence source, reference, policy, or authorization.
