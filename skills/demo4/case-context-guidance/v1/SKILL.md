---
name: case-context-guidance
description: Use the internal fixture-backed context provider while preserving preview data and evidence boundaries.
---

# Case context guidance

Use this preview skill only with the configured Demo 4 fixture.

1. Keep the preview label visible on every context result.
2. Use `search_case_context` only for bounded framing.
3. Use canonical evidence only from `assess_case_pattern`.
4. Do not describe fixture data as live, personal, external, or production data.
5. Fail closed when the provider result is unavailable or invalid.

This skill is guidance. It cannot add facts or change a canonical assessment.
