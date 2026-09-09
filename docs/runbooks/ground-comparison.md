# Foundation and Ground: MPM-005

## Exact prompt

Copy this prompt unchanged into a fresh conversation with each agent:

```text
A department proposes a non-statutory letter of comfort supporting a private provider. Maximum exposure is GBP 240,000, within its GBP 500,000 delegation. The letter is not a type routinely used in commercial business. The team says its non-binding wording and the amount being below GBP 300,000 remove the need for Treasury approval and Parliamentary notification. They also treat the Contingent Liability approval framework checklist as having the same threshold. Under Managing Public Money, is that analysis valid? Distinguish Treasury approval, Parliamentary notification, and the mandatory checklist submission threshold. Apply each rule to these facts.
```

The amounts and departmental delegation are stipulated demonstration facts, not claimed MPM thresholds.
Only the cited MPM rules support the assessment.
The question requires three different controls, not merely recognition of the word "novel".

## Correct source-based assessment

**Route: treasury-consent. Do not issue the letter under the stated delegation.**

| Control | Pinned source | Application |
|---|---|---|
| Treasury approval | A5.4.23, page 231 | Treasury approval is essential for letters of comfort. Non-binding wording and GBP 240,000 do not remove it. |
| Parliamentary notification | A5.4.26-A5.4.27, page 232 | The non-routine instrument triggers an exception to the general under-GBP-300,000 exemption. Notify Parliament. |
| Mandatory checklist submission | A5.4.19, pages 230-231 | The threshold is GBP 3 million of maximum exposure. GBP 240,000 does not meet it. |

Treasury may still request supporting papers. A checklist threshold does not grant spending authority.
Do not assume that "not routine commercially" proves every separate novel, contentious, or repercussive classification.
The express letter-of-comfort provisions already establish the required route.

### Original source extracts

These are source excerpts, not generated policy. PDF line breaks are joined with spaces.

A5.4.23:

> Letters of comfort, however vague, give rise to moral and sometimes legal obligations.

> Treasury approval is essential.

[HM Treasury, Managing Public Money, April 2026, page 231](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=231)

A5.4.27 says Parliament must be notified even when an A5.4.26 exemption applies.
Its first listed exception includes this wording:

> arises as a result of a specific guarantee, indemnity or letter of comfort where the guarantee is not of a type routinely used in commercial business dealings

[HM Treasury, Managing Public Money, April 2026, page 232](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=232)

A5.4.19 specifies the checklist threshold:

> maximum exposure of £3 million or more.

[HM Treasury, Managing Public Money, April 2026, pages 230-231](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=230)

These excerpts explain why the GBP 240,000 amount has different effects on the three controls.
The low amount does not remove approval or notification. It does not trigger the mandatory GBP 3 million checklist rule.

## Saved live comparison

**Saved result, not a live run.** Recorded on 6 September 2026.
Both agents used `gpt-5-mini`, high reasoning, the same base instructions, and the exact prompt above.
Foundation version `7` had no tools. Ground version `13` used the pinned Foundry IQ source.
Each invocation started without conversation history. No failed response was silently retried.

### Foundation excerpt

This is generated model output, not an authoritative policy quotation:

> Checklist submission threshold — the Contingent Liability Approval Framework checklist must be completed when seeking Treasury consent or where Treasury rules require it; it is not simply interchangeable with the team’s cited monetary cut‑off and should be submitted as part of any request for consent. (provisional)

Foundation did not identify the GBP 3 million threshold.
It treated checklist submission as part of any consent request.
It also left the Parliamentary notification decision to Treasury instead of applying the explicit exception.
This is a substantive difference, not merely a difference in answer length.

### Ground excerpts

The following excerpts preserve the saved response wording.
Public source links replace the service markers for this offline document. The model's wording is not corrected.

**Assessment**

Route: treasury-consent — Provisional: Treasury consent required; Parliamentary notification required; CLAF checklist not mandatory (threshold £3 million)

**Evidence**

> Treasury approval must be sought for all contingent liabilities that are novel, contentious or repercussive.

[A5.4.19, page 230](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=230)

> Letters of comfort, however vague, give rise to moral and sometimes legal obligations.

[A5.4.23, page 231](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=231)

> maximum exposure of £3 million or more.

[A5.4.19, page 231](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=231)

> not of a type routinely used in commercial business dealings

[A5.4.27, page 232](https://assets.publishing.service.gov.uk/media/69e0b17861d2e8e9b9e42e13/Managing_Public_Money_-_April_2026.pdf#page=232)

**Application excerpt**

Checklist threshold — the Contingent Liability approval framework checklist is required for maximum exposures of £3 million or more (A5.4.19); £240,000 is below that threshold, so checklist submission is not mandatory.

The saved response cited the retrieved snippets for these statements. Use the source links above to read the original context.

## Measured results and limits

| Final comparison | Result |
|---|---|
| Attempted paired trials | 4: the original three and one follow-up |
| Completed pairs accepted by the source and quotation gate | 3/4 |
| Completed Ground answers resolving all three requested controls | 3/3 |
| Completed Foundation answers identifying the GBP 3 million threshold | 0/3 |
| Completed Ground answers with verified, cited blockquotes and an application explanation | 3/3 |
| Pair durations, including the failed run | 201.36, 225.33, 181.28, and 165.82 seconds |

The second trial failed before a paired response file was saved.
The earlier quiet runner did not retain its exception. A request timeout is suspected, not confirmed.
The runner now records partial results and the exception type for request failures.
Its request timeout remains three minutes. It does not retry an answer.
The follow-up used the same published versions and completed without error.
It confirmed the substantive contrast, but did not establish the cause of the earlier failure.

**One of four final comparison pairs did not complete.** Do not describe this as a fully reliable live demonstration.
Use this saved comparison if a request fails or takes too long.
Foundation can answer correctly on another run. Ground can still misinterpret a correctly quoted source.

Development included 21 exploratory paired trials, four control pairs, and the four final pairs above.
Early trials tested a useful-life equipment gift and a more complex letter-of-comfort case.
The complex case caused Ground to misread a remote-liability clause.
Low and medium reasoning also produced merged quotations, omitted conditions, and literal JSON escape sequences.
Those failures led to shorter excerpts, decoded snippet matching, and source-boundary checks.
Historical gate scores are not comparable because the gate changed during development.

All four existing control scenarios passed once before the final high-reasoning change.
They are not evidence of a repeated high-reasoning reliability rate.
The final result table refers only to the four explicitly identified trials of the final published versions.

## Fresh deployment follow-up

The `cgaiid-fresh-260906` deployment was rehearsed separately on 6 September 2026.
Do not combine these outcomes with the earlier saved comparison rate.

The first fresh pair produced the correct three-control conclusion, but one Ground blockquote omitted its trailing qualifying clause.
The unchanged quotation gate rejected that response.
Ground version 2 added clearer instructions to preserve qualifiers and avoid opening fragments of longer rules.
The next paired request timed out while waiting for Ground.
That timeout provides no evidence that the new quotation instructions passed.

Both attempts remain recorded in the private rehearsal output.
The live comparison remains variable and slow; use the saved comparison when a live response fails the evidence checks or takes too long.

## Presenter fallback

1. State: "This is a saved comparison from 6 September 2026."
2. Read the Foundation checklist excerpt.
3. Show the Ground blockquotes and application excerpt.
4. Open the pinned source pages for the three controls.
5. State the three-completed-pairs-out-of-four result and the latency limit.
6. Do not approve the proposal or claim that grounding guarantees correctness.

## Source attribution and reproducibility

Source: [HM Treasury, Managing Public Money, April 2026](https://www.gov.uk/government/publications/managing-public-money).
Contains public sector information licensed under the [Open Government Licence v3.0](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).
Crown copyright 2026. Source excerpts preserve the published wording.

The pinned PDF is `data\ground\v1\knowledge\managing-public-money-april-2026.pdf`.
Its SHA-256 is `d763aa52d5fcd2cf4f7b011d57d7149cfdcd82a85adb5f9414054cffa86476f5`.
The fixture contains the review rubric. The Presenter contains the same prompt.
Private endpoints, environment identifiers, credentials, and raw service traces are not included in this document.
