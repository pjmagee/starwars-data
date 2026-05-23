# Feature Specification: Node-Detail "Enhance with Holocron" Control

**Feature Branch**: `001-node-detail-holocron-button`

**Created**: 2026-05-23

**Status**: Shipped

**Input**: User description: "Add an 'Enhance with Holocron' button to the /knowledge-graph/nodes/[node-id] node-detail page in the Frontend. The button is currently missing from this page. An equivalent control exists on at least one other page in the Frontend (per the Holocron async pipeline shipped in Design-020 — global + per-node UI + dialog). The new button should trigger the same Holocron enhancement workflow for the currently displayed node, surface progress/results consistently with the existing per-node UI, and respect the dev-auth bypass already in place. Goal: parity with the per-node Holocron control wherever it already exists, so users can kick off enrichment directly from the node-detail page."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Trigger Holocron enrichment from the node I'm looking at (Priority: P1)

An administrator is browsing the knowledge graph and lands on a specific node's detail page
to inspect its data. They notice that the node looks under-enriched or out of date and want
to trigger a Holocron enrichment pass for it. Today they must leave the page and use the
per-node control surface that lives elsewhere in the Frontend; this story lets them trigger
enrichment directly from the node they are already viewing.

**Why this priority**: This is the only user story in scope. The feature exists solely to
close a parity gap — every other page that surfaces a single node already exposes this
control. Until the node-detail page does too, admins have to context-switch for the most
natural place to kick off enrichment.

**Independent Test**: An admin opens any node-detail page, sees the enrichment control,
clicks it, and observes that the same progress feedback and completion behaviour they
would see on the existing per-node surface occurs on the node-detail page. The enriched
data appears on the node afterwards without leaving the page.

**Acceptance Scenarios**:

1. **Given** an admin is viewing a node's detail page and Holocron is enabled,
   **When** the page renders, **Then** an "Enhance with Holocron" control is visible
   in a discoverable location on the page.
2. **Given** the control is visible, **When** the admin activates it,
   **Then** an enrichment workflow starts for that specific node and progress feedback
   appears in-place, matching the visual conventions of the equivalent control elsewhere.
3. **Given** an enrichment is already running for the node being viewed,
   **When** the admin opens the node-detail page, **Then** the control reflects the
   in-progress state and cannot be triggered a second time for the same node.
4. **Given** the enrichment completes successfully, **When** the workflow finishes,
   **Then** the node-detail page reflects the newly enriched data (or links to it)
   without requiring a manual refresh or a navigation away.
5. **Given** the enrichment fails, **When** the workflow errors,
   **Then** the failure is surfaced on the node-detail page using the same convention as
   the equivalent control, with enough context for the admin to decide whether to retry.

### Edge Cases

- What happens when Holocron is globally disabled? The control must not be triggerable;
  the user should understand why it is unavailable.
- What happens when the viewer lacks permission to trigger enrichment? The control must
  not be reachable for them.
- What happens when the displayed node has no enrichable content (e.g. a stub node with
  empty source data)? The control should reflect that state cleanly rather than starting
  a workflow that has nothing to do.
- What happens when the admin navigates away from the node-detail page mid-enrichment?
  The workflow must continue to completion and its outcome must remain observable from
  the existing per-node surfaces.
- What happens when an admin triggers enrichment from the node-detail page and from the
  other per-node surface simultaneously? The system must not run duplicate workflows for
  the same node.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The node-detail page MUST display an "Enhance with Holocron" control when
  the viewer has permission to trigger Holocron enrichment and the global Holocron
  capability is enabled.
- **FR-002**: Activating the control MUST initiate the same Holocron enrichment workflow
  that the existing per-node control elsewhere in the Frontend triggers, scoped to the
  currently displayed node.
- **FR-003**: The control's appearance, label, placement convention, and interaction
  feedback (progress, success, error) MUST match the equivalent per-node control already
  shipped in the Frontend. Any drift between the two MUST be considered a defect.
- **FR-004**: While an enrichment is in progress for the displayed node, the control
  MUST reflect that state visually and MUST NOT allow a second trigger for the same node.
- **FR-005**: When the global Holocron capability is disabled, the control MUST NOT be
  invocable on this page; the user MUST understand from the page why it is unavailable.
- **FR-006**: Enrichment results — success and failure — MUST be surfaced on the
  node-detail page in-place, using the same presentation conventions as the existing
  per-node control. The admin MUST NOT have to leave the page to learn the outcome.
- **FR-007**: Permission checks for the control MUST follow the same rules as the
  existing per-node control, including the development-environment auth bypass already in
  place for the Frontend.
- **FR-008**: When the displayed node has no enrichable content, the control MUST reflect
  that state (for example by being disabled with an explanation) rather than initiating
  a workflow that would produce no change.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An admin can trigger a Holocron enrichment for the displayed node from its
  detail page in a single interaction, without navigating away.
- **SC-002**: On node-detail page loads where the viewer has permission and Holocron is
  enabled, the control is present and operable 100% of the time.
- **SC-003**: An enrichment triggered from the node-detail page produces the same data
  changes and the same observable user-facing outcome as an enrichment triggered from the
  existing per-node control for the same node.
- **SC-004**: After this feature ships, no page that previously offered the per-node
  control regresses (no duplicate-trigger races, no UI conflicts, no auth drift).
- **SC-005**: An admin can determine the outcome of an enrichment they triggered (running,
  succeeded, failed) without leaving the node-detail page.

## Assumptions

- The existing per-node Holocron control in the Frontend is the canonical reference for
  this feature's UX, auth gating, in-progress handling, and result presentation. This
  spec deliberately defers to it rather than re-specifying its behaviour.
- The Frontend's existing development-environment auth bypass — which surfaces a
  synthetic admin principal locally — applies to this control unchanged.
- The global Holocron on/off capability is the authoritative gate; this feature does
  not introduce a new gate.
- A node always corresponds to a single, identifiable entity in the knowledge graph,
  and the node-detail page is the canonical view for it.
- **Out of scope**: redesigning the existing per-node Holocron control, changing the
  Holocron enrichment pipeline itself, surfacing Holocron status on pages other than
  the node-detail page, and any change to how Holocron's daily scheduled job runs.
