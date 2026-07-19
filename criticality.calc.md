# How NuGet Audit calculates Risk and Alert scores

This document explains **Risk** and **Alert** (the numbers and Low / Medium / High / Critical bands in the tool). It is written for readers who are **not** assumed to know CVE databases, CVSS, or formal vulnerability-management programs.

## What you are looking at

- **Health** (text like `vulnerable:High`, `deprecated`, `ok`) is a **separate** summary of NuGet.org metadata. It answers “what did the feed say?” in plain words.
- **Risk** and **Alert** are **scores** built inside NuGet Audit by combining several questions into one number each. They help **sort and color rows** when many packages are listed.

NuGet.org attaches **severity labels** to known issues (for example *Moderate* or *High*). Those labels are **not** the same as a full “CVE score” workflow; they are **categories** published with the advisory. This tool **maps those categories to numbers** and mixes them with other context (direct vs transitive dependency, how many projects use the package, etc.).

Implementation references:

- Raw 0–10 inputs: `src/NuGetAudit.Core/PackageCriticalityAssessor.cs`
- Weights and formulas: `src/NuGetAudit.Intelligence/CriticalityWeights.cs`, `CriticalityCalculator.cs`
- When scores are computed: `src/NuGetAudit.Core/PackageKnowledgeTimelineBuilder.cs`

---

## Step 1 — Ten questions, each answered with a number from 0 to 10

Think of a **scorecard** with ten sliders. Each slider is **clamped** to **0–10** before any math.

| Question (factor) | Idea | Typical source |
|-------------------|------|----------------|
| **Vulnerability severity** | How bad is the **worst** known advisory for this **exact package version**? | NuGet catalog severity → `PackageVulnerabilitySeverity` |
| **Exploitability** | If there is a vulnerability, add emphasis from severity (not a formal “exploit prediction”). | Same severity, different scale |
| **Dependency ownership** | Did **your** project reference the package directly, or only transitively? | Project graph |
| **Reach** | In **this solution snapshot**, how many **projects** use this **same package id + resolved version**? | Count across snapshot |
| **Production relevance** | Rough guess: is this dependency in a “production-like” context vs test-only heuristic? | Target framework hint in pipeline |
| **Lifecycle state** | Deprecated, outdated, abandoned, removed, etc. | NuGet registration + enricher rules |
| **Remediation difficulty** | Does fixing look like “bump version”, “migrate package”, or “unclear”? | Remediation advice fields |
| **Age** | How long has this package **version** been a known issue in **stored knowledge**? | First observed vs current run time |
| **Fix availability** | Do we already know a **recommended version** or **alternate package**? | Remediation advice |
| **Introduction recency** | Is this the **first time** we stored this issue, or an **ongoing** one? | Knowledge timeline |

### Severity → numbers (vulnerability severity factor)

These are the **raw** values for **Vulnerability severity** (and a related scale is used for **Exploitability** when vulnerable):

| NuGet severity label | Raw score (0–10) |
|----------------------|------------------|
| None | 0 |
| Low | 2 |
| Moderate | 5 |
| High | 8 |
| Critical | 10 |

So if the catalog says **Critical**, the “how bad is it?” slider is set to **10**. The tool is **not** recomputing CVSS; it is **trusts the label** for scoring purposes.

### Other factors (selected examples)

- **Dependency ownership:** direct reference **8**, transitive **4**.
- **Reach** (project count for this id+version): 1 → **2**, 2 → **4**, 3–4 → **6**, 5–8 → **8**, 9+ → **10**.
- **Production relevance:** treated as production-like **8**, otherwise **3** (heuristic in code).
- **Lifecycle** uses **first matching rule**: removed **10**; obsolete and vulnerable **10**; obsolete **8**; abandoned **7**; deprecated **6**; outdated **4**; else **0**.
- **Introduction recency:** first time in knowledge **9**, later updates **4**.
- **Age** (days since first stored observation): &lt;1 day **2**, &lt;7 **4**, &lt;30 **6**, &lt;90 **8**, else **10**.
- **Remediation difficulty** and **fix availability** depend on whether **RecommendedVersion**, **AlternatePackageId**, or only a **Summary** is present (see source for exact numbers).

---

## Step 2 — Each answer is multiplied by a weight

Default weights are in `CriticalityWeights.cs` (they sum to **100**):

| Factor | Default weight |
|--------|----------------|
| Vulnerability severity | 20 |
| Exploitability | 15 |
| Dependency ownership | 8 |
| Reach | 12 |
| Production relevance | 10 |
| Lifecycle state | 8 |
| Remediation difficulty | 8 |
| Age | 6 |
| Fix availability | 7 |
| Introduction recency | 6 |

Higher weight = that slider moves the final score **more**.

---

## Step 3 — Risk score

For each factor:

`contribution = (raw score 0–10) × weight`

**Risk** = **sum of all contributions ÷ 10**, rounded to two decimals.

Intuition: if every raw score were **10**, the weighted sum would be **100 × 10 = 1000**, and **1000 / 10 = 100** (the top of the scale).

---

## Step 4 — Alert score

**Alert** starts from the **same** weighted base as risk (before the final risk rounding, the code uses the same products).

Then it adds an **extra bump** so that “new issues”, “known fixes”, and “exploitability emphasis” surface more loudly:

`alertBoost = (introduction raw × 1.2) + (fix availability raw × 0.7) + (exploitability raw × 0.9)`

**Alert** = **min(100, riskBase + alertBoost)**, rounded to two decimals.

So **Alert** is **≥ Risk** in typical cases, and is capped at **100**.

---

## Step 5 — Bands (Low / Medium / High / Critical)

Both **Risk** and **Alert** are classified with the **same cut-offs**:

| Score range | Band |
|-------------|------|
| 0 ≤ score &lt; 25 | Low |
| 25 ≤ score &lt; 50 | Medium |
| 50 ≤ score &lt; 75 | High |
| 75 ≤ score | Critical |

---

## Worked examples (illustrative)

The **exact** numbers for your repo depend on remediation text, reach, age, and first-seen flags. Below are **simplified** patterns to build intuition.

### Example A — No known vulnerability, healthy lifecycle

- **Vulnerability severity** raw **0**, **Exploitability** **0**.
- Suppose other context is mild: direct dependency, a few projects, production-like, no lifecycle warnings, moderate remediation/fix fields, not brand-new.

Then most **high-weight** terms are small. **Risk** often lands in **Low** or low **Medium**, and **Alert** gets a small extra bump from **introduction** and **fix availability** if those raw scores are non-zero.

### Example B — Same package, catalog reports “Critical”

- **Vulnerability severity** raw **10** (weight **20**) → contribution **200**.
- **Exploitability** raw **8** (weight **15**) → **120**.

Just those two sum to **320** before other factors. After adding typical ownership/reach/production values, **Risk** often jumps into **Medium** or **High** quickly. **Alert** rises further because **exploitability** is part of `alertBoost`.

This illustrates: **the severity label is the dominant knob**, as intended.

### Example C — “Moderate” severity

- **Vulnerability severity** raw **5** (weight **20**) → **100**.
- **Exploitability** raw **4** (weight **15**) → **60**.

Base **160** from security alone before lifecycle/reach/etc., so **Risk** tends toward **mid-teens to twenties** from security alone → often **Low** or crossing into **Medium** once other factors add up.

### Example D — No vulnerability, but package is **deprecated**

- **Vulnerability** and **exploitability** stay **0**.
- **Lifecycle** raw **6** (deprecated path) with weight **8** → **48** contribution.

That alone is **4.8** points toward Risk after division by 10; combined with ownership/reach/production, you can reach **Low** or **Medium** without any CVE-style severity.

### Example E — High **reach** (many projects on same version)

If **10 projects** share the package version, **Reach** raw **10** (weight **12**) → **120** → **12** points toward Risk after ÷10. That rewards “this issue is **widespread** in this solution.”

### Example F — First time seen vs ongoing

**Introduction recency** raw **9** (first observation) vs **4** (ongoing). With weight **6**, contributions are **54** vs **24** → **5.4** vs **2.4** after ÷10, and the **Alert** formula multiplies introduction by **1.2** again in the boost. New findings are meant to **ping louder** on **Alert**.

---

## Relationship to “CVE” and CVSS

- **CVE** (Common Vulnerabilities and Exposures) is an **identifier** for a public security issue.
- **CVSS** is one **family** of formulas that can produce a **0–10 technical score** from many inputs.

NuGet Audit **does not** run CVSS math in `CriticalityCalculator`. It uses **NuGet’s published severity buckets** plus **project context** to produce **internal** Risk/Alert scores for **triage in this tool**. For legal or compliance decisions, use the **linked advisory** and your organization’s process.

---

## See also

- XML documentation on types and methods in `NuGetAudit.Intelligence` and `PackageCriticalityAssessor`.
- This file: `criticality.calc.md` (repository root).
