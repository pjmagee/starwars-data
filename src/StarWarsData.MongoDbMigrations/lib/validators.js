// Shared $jsonSchema validator definitions for kg.edges and kg.nodes.
// Used by migration 0003 and utils/verify-validators.js.
//
// These must stay in sync with the C# model in StarWarsData.Models.

"use strict";

const REALM_ENUM = ["Starwars", "Real", "Unknown"];
const CONTINUITY_ENUM = ["Canon", "Legends", "Both", "Unknown"];
const CALENDAR_ENUM = ["galactic", "real", "unknown"];

const edgeValidator = {
  $jsonSchema: {
    bsonType: "object",
    required: [
      "fromId", "fromName", "fromType",
      "toId",   "toName",   "toType",
      "label",  "weight",   "continuity",
      "sourcePageId", "createdAt",
    ],
    properties: {
      fromId:       { bsonType: "int", minimum: 1, description: "source PageId" },
      fromName:     { bsonType: "string", minLength: 1 },
      fromType:     { bsonType: "string", minLength: 1 },
      fromRealm:    { enum: REALM_ENUM, description: "denormalized realm from source node" },
      toId:         { bsonType: "int", minimum: 1, description: "target PageId" },
      toName:       { bsonType: "string", minLength: 1 },
      toType:       { bsonType: "string" }, // allow "" during ETL
      toRealm:      { enum: REALM_ENUM, description: "denormalized realm from target node" },
      label:        { bsonType: "string", minLength: 1 },
      reverseLabel: { bsonType: ["string", "null"], description: "reverse form from FieldSemantics" },
      weight:       { bsonType: "double", minimum: 0, maximum: 1 },
      evidence:     { bsonType: "string" },
      sourcePageId: { bsonType: "int", minimum: 1 },
      continuity:   { enum: CONTINUITY_ENUM },
      createdAt:    { bsonType: "date" },
      fromYear:     { bsonType: ["int", "null"] },
      toYear:       { bsonType: ["int", "null"] },
      pairId:       { bsonType: ["objectId", "null"], description: "LLM dual-write pair link" },
      meta: {
        bsonType: "object",
        additionalProperties: false,
        properties: {
          qualifier: { bsonType: ["string", "null"] },
          rawValue:  { bsonType: ["string", "null"] },
          order:     { bsonType: ["int", "null"] },
        },
      },
    },
  },
};

const nodeValidator = {
  $jsonSchema: {
    bsonType: "object",
    required: ["_id", "name", "type", "continuity", "realm", "processedAt"],
    properties: {
      _id:           { bsonType: "int", minimum: 1 },
      name:          { bsonType: "string", minLength: 1 },
      type:          { bsonType: "string", minLength: 1 },
      continuity:    { enum: CONTINUITY_ENUM },
      realm:         { enum: REALM_ENUM },
      properties:    { bsonType: "object" },
      imageUrl:      { bsonType: ["string", "null"] },
      wikiUrl:       { bsonType: "string" },
      startYear:     { bsonType: ["int", "null"] },
      endYear:       { bsonType: ["int", "null"] },
      startDateText: { bsonType: ["string", "null"] },
      endDateText:   { bsonType: ["string", "null"] },
      temporalFacets: {
        bsonType: "array",
        items: {
          bsonType: "object",
          required: ["field", "semantic", "calendar", "text"],
          properties: {
            field:    { bsonType: "string" },
            semantic: { bsonType: "string" },
            calendar: { enum: CALENDAR_ENUM },
            year:     { bsonType: ["int", "null"] },
            text:     { bsonType: "string" },
            order:    { bsonType: ["int", "null"] },
          },
        },
      },
      lineages:    { bsonType: "object", description: "precomputed transitive closures" },
      contentHash: { bsonType: ["string", "null"] },
      processedAt: { bsonType: "date" },
    },
  },
};

// ── Phase 2: Holocron enrichments ─────────────────────────────────────────
// See eng/design/018-kg-enrichments-architecture.md.

const ENRICHMENT_STATUS_ENUM = ["Active", "Superseded", "Stale", "Rejected"];
// v1 policy (Design-018): infobox is canonical truth. Agent only adds —
// no Refine. Add (new value), Augment (append to list, deduped),
// FillGap (fill null sub-property of existing entity).
const ENRICHMENT_OPERATION_ENUM = ["Add", "Augment", "FillGap"];
const HOLOCRON_EVENT_TYPE_ENUM = [
  "EnrichmentCreated",
  "EdgeEnrichmentCreated",
  "EnrichmentSuperseded",
  "EnrichmentMarkedStale",
  "EnrichmentRejected",
  "EnrichmentOrphaned",
  "HolocronPassStarted",
  "HolocronPassCompleted",
];

const evidenceItemSchema = {
  bsonType: "object",
  required: ["excerpt"],
  properties: {
    sourcePageId:   { bsonType: ["int", "null"] },
    chunkId:        { bsonType: ["string", "null"] },
    excerpt:        { bsonType: "string", maxLength: 1000 },
    relevanceScore: { bsonType: ["double", "null"] },
  },
};

const nodeEnrichmentValidator = {
  $jsonSchema: {
    bsonType: "object",
    required: [
      "_id", "pageId", "fieldPath", "operation", "value",
      "claim", "evidence", "contentHashAtCreation",
      "status", "createdAt", "agentVersion", "modelId",
    ],
    properties: {
      _id:                   { bsonType: "objectId" },
      pageId:                { bsonType: "int", minimum: 1 },
      fieldPath:             { bsonType: "string", minLength: 1 },
      operation:             { enum: ENRICHMENT_OPERATION_ENUM },
      value:                 {}, // any BSON shape — depends on fieldPath
      claim:                 { bsonType: "string", minLength: 1 },
      evidence:              { bsonType: "array", items: evidenceItemSchema, minItems: 1 },
      llmReasoning:          { bsonType: ["string", "null"] },
      contentHashAtCreation: { bsonType: "string", minLength: 1 },
      status:                { enum: ENRICHMENT_STATUS_ENUM },
      supersededBy:          { bsonType: ["objectId", "null"] },
      createdAt:             { bsonType: "date" },
      appliedAt:             { bsonType: ["date", "null"] },
      agentVersion:          { bsonType: "string", minLength: 1 },
      modelId:               { bsonType: "string" },
    },
  },
};

const edgeEnrichmentValidator = {
  $jsonSchema: {
    bsonType: "object",
    required: [
      "_id", "fromId", "toId", "label", "operation", "value",
      "claim", "evidence", "contentHashAtCreation",
      "status", "createdAt", "agentVersion", "modelId",
    ],
    properties: {
      _id:                   { bsonType: "objectId" },
      fromId:                { bsonType: "int", minimum: 1 },
      toId:                  { bsonType: "int", minimum: 1 },
      label:                 { bsonType: "string", minLength: 1 },
      operation:             { enum: ENRICHMENT_OPERATION_ENUM },
      value:                 { bsonType: "object" },
      claim:                 { bsonType: "string", minLength: 1 },
      evidence:              { bsonType: "array", items: evidenceItemSchema, minItems: 1 },
      llmReasoning:          { bsonType: ["string", "null"] },
      contentHashAtCreation: { bsonType: "string", minLength: 1 },
      status:                { enum: ENRICHMENT_STATUS_ENUM },
      supersededBy:          { bsonType: ["objectId", "null"] },
      createdAt:             { bsonType: "date" },
      appliedAt:             { bsonType: ["date", "null"] },
      agentVersion:          { bsonType: "string", minLength: 1 },
      modelId:               { bsonType: "string" },
    },
  },
};

const holocronEventValidator = {
  $jsonSchema: {
    bsonType: "object",
    required: ["_id", "eventType", "summary", "triggeredBy", "occurredAt", "agentVersion"],
    properties: {
      _id:          { bsonType: "objectId" },
      eventType:    { enum: HOLOCRON_EVENT_TYPE_ENUM },
      enrichmentId: { bsonType: ["objectId", "null"] },
      pageId:       { bsonType: ["int", "null"] },
      fieldPath:    { bsonType: ["string", "null"] },
      fromId:       { bsonType: ["int", "null"] },
      toId:         { bsonType: ["int", "null"] },
      label:        { bsonType: ["string", "null"] },
      summary:      { bsonType: "string", minLength: 1 },
      triggeredBy:  { bsonType: "string", minLength: 1 },
      occurredAt:   { bsonType: "date" },
      agentVersion: { bsonType: "string", minLength: 1 },
    },
  },
};

// Support both require() (Node.js, utils) and load() (mongosh migrations)
if (typeof module !== "undefined") {
  module.exports = {
    edgeValidator,
    nodeValidator,
    nodeEnrichmentValidator,
    edgeEnrichmentValidator,
    holocronEventValidator,
  };
}
globalThis.__validators = {
  edgeValidator,
  nodeValidator,
  nodeEnrichmentValidator,
  edgeEnrichmentValidator,
  holocronEventValidator,
};
