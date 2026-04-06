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

// Support both require() (Node.js, utils) and load() (mongosh migrations)
if (typeof module !== "undefined") {
  module.exports = { edgeValidator, nodeValidator };
}
globalThis.__validators = { edgeValidator, nodeValidator };
