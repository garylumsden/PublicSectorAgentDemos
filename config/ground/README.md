# Ground configuration

`agent-definitions.v1.json` contains the two pre-provisioned agent definitions.

Both definitions read one canonical base prompt. Only the Ground definition adds `foundryIq` and the grounding prompt extension.

Both use `gpt-5-mini` with high reasoning. MPM-005 is the primary Presenter comparison.
Ground must quote decoded source text verbatim in Markdown blockquotes and explain the application.
The rehearsal validates citation identity and quotation integrity. It does not prove factual accuracy.
See the [comparison runbook](../../docs/runbooks/ground-comparison.md) for the prompt, source grounds, and saved results.
