# Dossier: Cross-Government Grants Fraud Analytics Exchange

> **Entirely fictional demonstration material.** Every organisation, person, service, figure, and event in this Dossier is invented. It does not describe or resemble a real department, programme, public body, or supplier.

## Submission

**Proposal:** Establish a shared analytics service that identifies possible fraud across grant programmes run by eight fictional public bodies.

**Sponsor:** Public Funding Integrity Directorate (fictional)

**Named accountable officer:** Elias March, Government Grants Assurance Director (fictional)

**Decision sought after assurance:** Permission to connect the first four grant programmes and begin live risk scoring.

## Current problem

The eight bodies award about GBP 6.4 billion each year through 37 grant schemes. Each body checks applications within its own systems. Investigators report repeated bank accounts, directors, addresses, and supporting documents across programmes.

The bodies exchanged information in 64 serious cases last year. The process required manual legal review and took an average of 18 working days. Four payments were made before the review finished.

## Proposed exchange

The proposed **GrantShield** service will receive applicant, payment, director, address, device, and document metadata. It will create links across programmes and assign a risk score before payment.

Scores above 85 will pause payment for investigator review. Scores from 60 to 84 will create an alert but allow payment. The service will not make a final fraud finding. However, a paused payment can affect payroll, service delivery, and the survival of a small organisation.

The model combines fixed rules with machine-learning signals. The supplier will explain the fixed rules but treats some model features and weightings as commercial intellectual property. Investigators can see contributing factors but cannot reproduce the full score.

## Evidence

A retrospective test used 210,000 applications. GrantShield identified 78% of cases later confirmed as fraud. It also marked 6.8% of all applications as high risk.

The test assumes that historic investigation outcomes are correct. It does not include cases where an organisation withdrew after a delay, failed because payment was paused, or was cleared without a recorded reason. Small charities, new organisations, and applicants from shared workspaces received higher average scores.

The programme estimates that 430 payments each month will be paused. Current investigator capacity can review 170 cases within five working days. The business case assumes automation will reduce each review from six hours to 90 minutes, but this has not been tested with live evidence.

## Legal authority and governance

Each body has powers to prevent fraud in its own scheme. Legal advice is divided on whether all proposed data can be pooled for general pattern discovery. Two bodies can share information only when they already have reasonable suspicion.

The proposal names the Public Funding Integrity Directorate as service operator. It does not decide whether that directorate can instruct a body to pause payment. Each body will remain responsible for its award decision, but investigators will use a common score and central case record.

Applicants will receive a general fraud-prevention notice. They will not receive the score or full contributing factors. The proposed appeal route begins only after a formal rejection. There is no expedited route for a payment paused before a decision.

## Cost and benefits

The four-year cost is GBP 38 million. Claimed benefits are GBP 96 million:

| Benefit | Estimate |
| --- | ---: |
| Prevented fraudulent payments | GBP 63 million |
| Reduced duplicate investigation | GBP 18 million |
| Faster recovery action | GBP 9 million |
| Reduced manual data matching | GBP 6 million |

The prevented-loss estimate treats every historic high-risk match as avoidable loss. Independent review estimates that between GBP 24 million and GBP 71 million is supportable.

## Security and resilience

GrantShield will contain a cross-government network of organisations, people, addresses, accounts, and devices. A false link can propagate to several programmes.

The design encrypts data and logs investigator access. It does not define how a corrected link will be removed from prior scores. The central platform has a four-hour recovery target. Bodies can continue processing during an outage, but they cannot perform cross-programme checks.

The threat model covers external attackers. It does not cover a participating body submitting excessive data, searching competitors, or using the exchange for eligibility checks outside fraud prevention.

## User and staff evidence

Investigators support faster cross-programme matching. Grant managers want clear authority to release an urgent payment despite a high score.

Research with 31 grant recipients found strong support for fraud prevention. Small organisations were concerned that unexplained delays would cause immediate financial harm. Participants expected a named person to review contested matches and correct shared records.

## Open matters for assessment

The accountable officer asks the Board to assess whether live scoring should begin, which data-sharing powers are sufficient, whether payment pauses are proportionate, what explanation and correction routes are required, and how much false-positive risk may be accepted.
