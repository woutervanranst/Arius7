---
status: {proposed | accepted | rejected | deprecated | superseded}
date: {YYYY-MM-DD when the decision was last updated}
decision-makers: {list everyone involved in the decision}
consulted: {list everyone whose opinions were sought}
informed: {list everyone who must be kept informed}
confidence: {high | medium | low}
supersedes: {ADR-0000 or remove this line}
superseded-by: {ADR-0000 or remove this line}
---

# {short title naming the problem and chosen direction}

Keep ADRs pithy, assertive, on-topic, and factual. A reader should understand the decision without opening supplemental material.

If one architectural direction has distinct short-term, mid-term, and long-term phases, create one ADR per phase instead of hiding multiple decisions in one record.

## Context and Problem Statement

{Describe the context and the problem in two to five sentences. State the forces that make this decision necessary now. Do not mention the chosen solution here.}

The question for this ADR is {phrase the decision question without assuming the answer}.

## Decision Drivers

* {driver 1: quality attribute, constraint, risk, or stakeholder need}
* {driver 2}
* … <!-- numbers of drivers can vary -->

## Considered Options

* {option 1}
* {option 2}
* … <!-- numbers of options can vary -->

## Decision Outcome

Chosen option: "{option}", because {brief rationale tied to the drivers above}.

Confidence: {high | medium | low}. {Explain why the confidence level is appropriate. If confidence is low, state what evidence could change the decision.}

The practical effect of this decision should be visible at a glance:

Before:

```csharp
{small example of the old structure, behavior, API, configuration, or workflow}
```

After:

```csharp
{small example of the new structure, behavior, API, configuration, or workflow}
```

If code is not the clearest representation, replace the code blocks with concise before/after text, commands, configuration, or diagrams.

### Consequences and Tradeoffs

* Good, because {positive consequence, e.g., improvement of one or more desired qualities, …}
* Bad, because {negative consequence, e.g., compromising one or more desired qualities, …}
* … <!-- numbers of consequences can vary -->

Do not hide important consequences. If a tradeoff mattered during the decision, record it here even when it makes the decision look less clean.

### Confirmation

{Describe how the implementation / compliance of the ADR can/will be confirmed. Is there any automated or manual fitness function? If so, list it and explain how it is applied. Is the chosen design and its implementation in line with the decision? E.g., a design/code review or a test with a library such as ArchUnit can help validate this. Note that although we classify this element as optional, it is included in many ADRs.}

## Pros and Cons of the Options

### {title of option 1}

{example | description | pointer to more information | …}

* Good, because {argument a}
* Good, because {argument b}
<!-- use "neutral" if the given argument weights neither for good nor bad -->
* Neutral, because {argument c}
* Bad, because {argument d}
* … <!-- numbers of pros and cons can vary -->

### {title of other option}

{example | description | pointer to more information | …}

* Good, because {argument a}
* Good, because {argument b}
<!-- use "neutral" if the given argument weights neither for good nor bad -->
* Neutral, because {argument c}
* Bad, because {argument d}
* … <!-- numbers of pros and cons can vary -->

## More Information

{Link to issue, proposal, design notes, benchmark output, or supporting material. Keep the decision clear enough to stand alone without these links. Remove this section when there is nothing useful to link.}
