# Design 009 — Dual-Calendar Timeline (Galactic + Real World)

**Status:** Analysis / not yet implemented
**Date:** 2026-04-05
**Related:** [001-temporal-facets](001-temporal-facets.md), [002-ai-agent-toolkits](../adr/002-ai-agent-toolkits.md)

## Problem

The AI agent cannot satisfy prompts like *"Render the real-world publication history of the canonical Star Wars franchise across all media"* in timeline mode. The agent is forced to fall back to a markdown bullet list because every layer of the Timeline feature — data, API, component, and AI tool — is hardcoded to the in-universe galactic calendar (BBY/ABY).

Real-world dates (book releases, film premieres, TV air dates, actor birthdays) **already exist** in the KG as [`TemporalFacet`](../../src/StarWarsData.Models/Timeline/TemporalFacet.cs) records with `Calendar = "real"`, but they are filtered out by [`KgTimelineBuilderService`](../../src/StarWarsData.Services/Timeline/KgTimelineBuilderService.cs) before reaching `timeline.*` collections.

## Current state (where the galactic assumption is baked in)

- **Model** — [`TimelineEvent`](../../src/StarWarsData.Models/Timeline/TimelineEvent.cs) has `float? Year` (magnitude) + `Demarcation { Aby, Bby }`. No calendar field.
- **ETL** — [`KgTimelineBuilderService.cs`](../../src/StarWarsData.Services/Timeline/KgTimelineBuilderService.cs) explicitly skips `Calendar != "galactic"` when emitting `timeline.{NodeType}` collections.
- **Service** — [`TimelineService.cs`](../../src/StarWarsData.Services/Timeline/TimelineService.cs) computes `_linearYear = (Demarcation=="Bby") ? -Year : Year` for range filtering. The range model has no place for a CE year.
- **API** — [`TimelineController.cs`](../../src/StarWarsData.ApiService/Features/Timeline/TimelineController.cs) / `TimelineQueryParams` accepts `YearFromDemarcation` / `YearToDemarcation` only.
- **Frontend page** — [`Timeline.razor`](../../src/StarWarsData.Frontend/Components/Pages/Timeline.razor) displays an explicit alert: *"The Real World realm toggle has no effect on this page."*
- **Components** — [`EventTimeline.razor`](../../src/StarWarsData.Frontend/Components/Shared/EventTimeline.razor) and [`AskTimelineView.razor`](../../src/StarWarsData.Frontend/Components/Shared/AskTimelineView.razor) group by a formatted galactic year string ("4 ABY") and use eras scoped to the galactic calendar.
- **AI tool** — [`ChartToolKit.RenderTimeline`](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs) takes `yearFromDemarcation`/`yearToDemarcation` as BBY/ABY strings. No real-world path exists.
- **Global filter** — [`GlobalFilterService`](../../src/StarWarsData.Frontend/Services/GlobalFilterService.cs) already carries a `Realm` ({ `Starwars`, `Real`, `Unknown` }) but Timeline intentionally ignores it.

## What is already usable

- `TemporalFacet.Calendar` distinguishes `galactic` vs `real` at the KG level.
- `Realm` enum and `GlobalFilterService` already propagate a per-request realm selection end-to-end.
- Real-world facets already carry a CE `Year` (positive int) and a `Semantic` tag (`publication.release`, `lifespan.start`, …).
- Continuity (Canon/Legends) is orthogonal to calendar and applies to both modes (e.g. *"Canon novels released 2014–2020"*).

## Options

### Option A — Single unified component/toolkit, calendar is a parameter

One `EventTimeline` / one `AskTimelineView` / one `RenderTimeline` tool. A `Calendar` field is added to `TimelineEvent` and flows as a query parameter; the component conditionally formats years, picks category sets, and suppresses era overlays when in real-world mode.

**Pros**
- No duplicated Razor component. `EventTimeline` already has non-trivial infinite-scroll, color hashing, and reference resolution logic — duplicating it guarantees drift.
- `GlobalFilterService.Realm` already exists; the unified path just honours it instead of ignoring it.
- Mixed queries stay possible at the service layer (*"books released during the Galactic Civil War"* could be two parallel requests rendered on one axis if ever wanted).
- Single AI tool signature — the agent picks a calendar argument instead of choosing between two tools. Fewer tools == less routing error in the agent.
- Continuity filter, category color palette, reference rendering, pagination state are shared for free.

**Cons**
- `TimelineEvent` grows a second year representation (CE int vs float+Demarcation). Risk of callers using the wrong one.
- Category list and era overlay must be computed per mode — conditional branches in the component.
- Year-axis formatting (`"4 ABY"` vs `"1983"`) and sort order (BBY descending→ABY ascending vs CE ascending) have to switch on calendar.
- Test surface doubles for `TimelineService` range queries.

### Option B — Two components and two toolkits, one per realm

`EventTimeline` stays galactic-only. New `RealWorldTimeline` component + `RealWorldTimelineService` + `render_real_world_timeline` AI tool. Separate route (`/timeline/real-world`).

**Pros**
- No enum/model contamination. `TimelineEvent` stays a pure galactic record; a new `RealWorldTimelineEvent` holds CE years.
- No conditional branches in the Razor component — each is internally consistent.
- AI tool names become self-describing; the agent cannot accidentally ask for `yearFromDemarcation="BBY"` with a real-world category.
- Easier to evolve the two independently (e.g. real-world mode could introduce a month/day axis later without risking the galactic path).

**Cons**
- Duplicated Razor component (infinite scroll, sentinel, color hashing, reference popover, category filter, continuity plumbing) — every future fix has to be mirrored.
- Two service methods with near-identical aggregation pipelines; the only real difference is how the year is interpreted.
- Two API endpoints to maintain and document.
- The agent has to choose between two tools for what the user perceives as "one timeline feature", which can produce wrong-tool mistakes on mixed prompts.
- `GlobalFilterService.Realm` becomes redundant with route selection — two mechanisms saying the same thing.

## Recommendation

**Option A — single unified component and single `RenderTimeline` tool with a `calendar` parameter.**

Rationale:

1. The dividing line between the two modes is narrow: a different year encoding, a different category set, and an optional era overlay. Everything else (continuity, search, references, pagination, color hashing, infinite scroll, grouping by year, Blazor wiring) is identical. Option B would duplicate ~600 lines of Razor to avoid ~3 conditional branches.
2. `GlobalFilterService.Realm` is already plumbed; honouring it on Timeline is strictly less code than adding a second route + second component tree.
3. The KG already distinguishes calendars at the facet level — the ETL change is "remove the filter and tag the emitted event", not "build a parallel pipeline".
4. The AI agent benefits from a smaller, more uniform tool surface. A single `RenderTimeline(calendar: "galactic" | "real")` is easier for the planner than two near-synonyms.
5. Future work (hybrid views such as *"books released during the Clone Wars"*) is natural in Option A and awkward in Option B.

The cost — a `Calendar` field on `TimelineEvent` plus conditional year formatting — is contained and type-safe.

## Proposed shape (not a commitment, reference for the implementation plan)

### Model

```csharp
public enum Calendar { Galactic, Real, Unknown }

public class TimelineEvent
{
    // existing
    public float? Year { get; set; }              // magnitude, galactic only
    public Demarcation Demarcation { get; set; }  // Bby/Aby, galactic only

    // new
    public Calendar Calendar { get; set; } = Calendar.Galactic;
    public int? RealYear { get; set; }            // signed CE year, real only (negative = BCE)
}
```

- Invariant: `Calendar == Galactic` ⇒ `Year`/`Demarcation` set, `RealYear` null. `Calendar == Real` ⇒ `RealYear` set, `Year`/`Demarcation` ignored.
- Enforced in the KG builder at emission, not by downstream code.

### ETL

[`KgTimelineBuilderService`](../../src/StarWarsData.Services/Timeline/KgTimelineBuilderService.cs): drop the `Calendar == "galactic"` guard. Emit every facet, tagging `TimelineEvent.Calendar` from `TemporalFacet.Calendar`. Keep writing to the same `timeline.{NodeType}` collections — no separate `timeline.real_*` namespace. Calendar becomes a filterable field on the existing documents.

Reasoning: real-world categories (`Book`, `Film`, `Video game`, `Television series`) largely do not overlap with galactic categories (`Battle`, `War`, `Era`). A mixed collection is still clean in practice and keeps aggregation pipelines uniform.

### Service / API

- `TimelineQueryParams` gains `Calendar? Calendar`. Default: `Galactic` for the page UI, **null (= both)** when the agent omits it.
- `TimelineService.GetTimelineEvents` branches the range-filter `$expr`:
  - Galactic: current `if Demarcation == "Bby" then -Year else Year` path, matched against galactic bounds.
  - Real: straight `$gte/$lte` on `RealYear`, matched against real bounds.
- `GetErasAsync` stays galactic-only (no change); component suppresses era overlay in real mode.
- `/Timeline/categories` becomes calendar-aware so the dropdown shows the right set.

### Frontend

- `EventTimeline.razor`:
  - Bind to `GlobalFilterService.SelectedRealm` and map `Realm.Real → Calendar.Real`, `Realm.Starwars → Calendar.Galactic`, null → null (both).
  - `FormatYear(ev)` switches on `ev.Calendar` (`"4 ABY"` vs `"1983"` vs `"44 BCE"`).
  - Era overlay rendered only when `calendar == Galactic`.
  - Category dropdown refetches when calendar changes.
- `Timeline.razor`: drop the "Real World realm toggle has no effect" alert; the page now honours the global realm toggle.
- `AskTimelineView.razor`: same conditional formatting; descriptor carries the calendar.

### AI toolkit

[`ChartToolKit.RenderTimeline`](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs): add `string? calendar = null` (`"galactic"` | `"real"`; null ⇒ infer from categories, falling back to galactic). `yearFromDemarcation`/`yearToDemarcation` remain for galactic use; for real mode, `yearFrom`/`yearTo` are interpreted as CE ints. Tool description updated with both usage patterns and two examples. `TimelineDescriptor` gains a `Calendar` field so `AskTimelineView` renders correctly.

## Open questions

1. **BCE handling** — are there any KG facets with pre-1 CE years (e.g. historical references)? If not, `RealYear` can be a `uint`; otherwise keep `int` and support negative.
2. **Category naming collisions** — do any real-world and galactic categories share a template name? A quick `distinct kg.nodes.NodeType` group by `TemporalFacet.Calendar` would confirm.
3. **Year axis sparsity** — real-world data is clustered 1977–present; the infinite-scroll sentinel logic should still work, but year-group pagination thresholds may need tuning.
4. **Mixed-calendar queries** — should a future prompt like *"Clone Wars era events and their corresponding publications"* render both on one axis, or two stacked timelines? Out of scope for this design but the unified model does not preclude it.

## Non-goals

- A month/day axis for real-world events (CE year granularity is enough for v1).
- Merging the galactic and real axes into a single visual scale.
- Replacing the existing galactic timeline behaviour — default UX is unchanged unless the user toggles realm.
