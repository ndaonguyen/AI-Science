# content-search-service — MongoDB schema

Source of truth: `source/content-search-service/source/ContentSearchService.Infrastructure/Models/`.
Bson conventions: **camelCase field names**, **enums as strings**, **GUIDs as standard binary**, and crucially **every model has a `BsonDocument extraElements` field** so unknown / future fields are preserved on round-trips. This service is downstream of authoring-service — it consumes Kafka events and projects activity / block state into its own DB + OpenSearch index.

## Collections

| Name | Backed by |
|---|---|
| `activities` | `ActivityModel` |
| `blocks` | `BlockModel` (abstract; concrete: `ComponentBlockModel`, `SectionBlockModel`) |

Both are also indexed in OpenSearch — see "OpenSearch projection" at the bottom.

The `blocks` collection uses a **discriminator `_t`** with values `"ComponentBlockModel"` or `"SectionBlockModel"`.

---

## Collection: `activities`

Cross-references with `authoring-service.activities` but with **extra lifecycle fields** (binning, deletion, lifecycle status). Some fields are typed differently — most notably `tags` is `UUID[]` here (tag IDs) vs `string[]` in authoring-service (tag names).

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `_id` | UUID | required | |
| `state` | `State` | required | enum: `Draft`, `Published` |
| `sourceId` | UUID | yes | original this was branched from |
| `name` | string | yes | |
| `description` | string | yes | |
| `tags` | UUID[] | default `[]` | **tag IDs**, not strings (different from authoring-service) |
| `alignmentTags` | `AlignmentTagModel[]` | default `[]` | |
| `createdBy` | UUID | required | |
| `createdAt` | ISODate | required | |
| `updatedBy` | UUID | yes | |
| `updatedAt` | ISODate | yes | |
| `organizationId` | UUID | yes | |
| `blocks` | UUID[] | default `[]` | **stored as STRINGS** (`[BsonRepresentation(BsonType.String)]`) — different from rest of file |
| `versions` | `VersionsModel` | yes | |
| `binnedAt` | ISODate | yes | timestamp of move to bin |
| `deletedBy` | UUID | yes | |
| `deletedAt` | ISODate | yes | hard-delete timestamp |
| `lifeCycleStatus` | `LifeCycleStatus` | required | enum: `Active`, `InBin`, `Deleted` (default: `Active`) |
| `isLatest` | bool | required | |
| `isEpContent` | bool | required | |
| `extraElements` | BsonDocument | default `{}` | catches unknown fields on round-trip |

---

## Collection: `blocks`

### Common to all blocks (`BlockModel`)

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `_id` | UUID | required | |
| `_t` | string \| string[] | required | **Shape depends on whether `BlockModel` is registered as a root class.** If `[BsonDiscriminator(RootClass = true)]` is on the base, the driver writes the full hierarchy as an array (`["BlockModel", "ComponentBlockModel"]`); otherwise it writes just the leaf as a string (`"ComponentBlockModel"`). Authoring-service uses RootClass; content-search-service's projection layer may or may not — **inspect a real sample if you're unsure**. Either way, equality matching with a string (`_t: "ComponentBlockModel"`) works against both shapes (Mongo equality matches string == array element). |
| `sourceId` | UUID | yes | |
| `state` | `State` | required | |
| `title` | string | yes | |
| `description` | string | yes | |
| `notes` | string | yes | |
| `tags` | UUID[] | default `[]` | tag IDs |
| `alignmentTags` | `AlignmentTagModel[]` | default `[]` | |
| `versions` | `VersionsModel` | yes | |
| `isLatest` | bool | required | |
| `createdBy` | UUID | required | |
| `createdAt` | ISODate | required | |
| `updatedBy` | UUID | required (NOT nullable here, unlike authoring) | |
| `updatedAt` | ISODate | required (NOT nullable here, unlike authoring) | |
| `organizationId` | UUID | yes | |
| `isEpContent` | bool | required | |
| `extraElements` | BsonDocument | default `{}` | |

> **Note vs authoring-service:** `updatedBy` / `updatedAt` are non-nullable here. Also no `creationSource`, no `copiedFromId`, no `copyStatus` — content-search drops authoring-only fields.

### When `_t = "ComponentBlockModel"`

Adds:

| Field | Type | Nullable |
|---|---|---|
| `components` | `ComponentModel[]` | default `[]` |

### When `_t = "SectionBlockModel"`

Adds:

| Field | Type | Notes |
|---|---|---|
| `blocks` | UUID[] | **stored as STRINGS** (`[BsonRepresentation(BsonType.String)]`) — same as `activities.blocks` |

---

## `ComponentModel` (denormalized projection)

Content-search keeps a flatter shape than authoring-service does. Unknown fields per question type are absorbed into `extraElements`.

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `id` | UUID | required | (no `[BsonId]`, no leading `_`) |
| `type` | `ComponentType` | required | |
| `controlType` | `ControlType` | required | |
| `title` | string | yes | |
| `description` | string | yes | |
| `extraElements` | BsonDocument | default `{}` | everything question-type-specific |

### `ComponentType` enum values

`Association`, `Cloze`, `Matrix`, `Select`, `List`, `Text`, `Image`, `Video`, `Info`, `StimulusText`, `LongAnswer`.

> **Note:** This is *narrower* than authoring-service's `ComponentType` enum. Multiple authoring-side types collapse into one search-side type (e.g. all `GapsDrag` / `GapsDropdown` / `GapsImageDrag` etc. → `Cloze`). The richer distinction is in `controlType`.

### `ControlType` enum values

`Association`, `ClozeDrag`, `ClozeDropdown`, `ClozeImageDrag`, `ClozeImageDropdown`, `ClozeImagetext`, `ClozeText`, `Matrix`, `SingleChoice`, `MultiChoice`, `TrueFalseChoice`, `SortList`, `OrderedList`, `Text`, `Image`, `Video`, `Info`, `StimulusText`, `LongAnswer`.

---

## Shared types

### `AlignmentTagModel`

| Field | Type | Notes |
|---|---|---|
| `curriculum` | UUID | stored as STRING (`[BsonRepresentation(BsonType.String)]`) |
| `subject` | UUID | stored as STRING |
| `levels` | UUID[] | stored as STRINGS |
| `outcomeCodes` | UUID[] | stored as STRINGS |

> **Note:** unlike authoring-service's `AlignmentTagModel` which uses standard binary GUIDs, content-search-service stores all alignment-tag GUIDs as strings. If you query alignment tags directly in `mongosh`, use string equality, not `UUID(...)`.

### `VersionsModel`

| Field | Type | Notes |
|---|---|---|
| `current` | UUID | required |
| `all` | `VersionModel[]` | default `[]` |

### `VersionModel`

| Field | Type |
|---|---|
| `id` | UUID |
| `author` | UUID |
| `createdAt` | ISODate |
| `note` | string? |

---

## Bson conventions to remember

- **camelCase field names** (same as authoring-service).
- **Enums as strings** (same as authoring-service). `state: "Published"`, `lifeCycleStatus: "InBin"`.
- **Default GUID representation: Standard binary UUID** — but with explicit per-field overrides:
  - `activities.blocks` and `SectionBlockModel.blocks` → **stored as STRINGS**.
  - All `AlignmentTagModel.*` GUIDs → **stored as STRINGS**.
- `extraElements` exists on every model — unknown fields are preserved instead of dropped.
- **Discriminators use `_t`** on the `blocks` collection (string or array — see the BlockModel table). Components inside `ComponentBlockModel.components` use `type` (a `ComponentType` enum) instead — different convention from authoring-service which uses `_t` for both.

---

## OpenSearch projection

The same documents are indexed into OpenSearch via `IDraftBlockRepository` / `ISearchBlockRepository`. Index names typically mirror the collection (`activities`, `blocks`) but the indexer may flatten or transform fields. Queries that hit OpenSearch (`/index/_search`) operate on this projection and may not match the Mongo document shape exactly — when in doubt, prefer reasoning from the Mongo document for ground truth.

---

## Querying — worked examples

These cover the patterns you're most likely to need. Content-search has
quirks the authoring-service queries do NOT have — most importantly the
mixed GUID representation (some fields stored as binary UUIDs, others as
strings) and the `lifeCycleStatus` lifecycle filter. Read these before
hand-writing a query against this service.

### Find an activity by id

```javascript
db.activities.findOne({ _id: UUID("a39ff7b7-5268-4078-81cf-de7c21409ffe") })
```

`activities._id` uses standard binary UUID. `UUID("...")` literal in `mongosh`.

### Find an activity that references a specific block — STRING comparison

```javascript
db.activities.find({ blocks: "a39ff7b7-5268-4078-81cf-de7c21409ffe" })
```

**No `UUID(...)` wrapper.** `activities.blocks` is stored as **strings**
(`[BsonRepresentation(BsonType.String)]`). Wrapping with `UUID(...)` will
match nothing — most-painful subtle bug in this collection. Same applies
to `SectionBlockModel.blocks`.

### Find only "active" content (skip binned/deleted)

```javascript
db.activities.find({ lifeCycleStatus: "Active", isLatest: true, state: "Published" })
```

Three filters typically pair together:
- `lifeCycleStatus: "Active"` — not binned, not deleted
- `isLatest: true` — current revision, not history
- `state: "Published"` — not draft

Drop any one only when you specifically want that scope (e.g.
`lifeCycleStatus: "InBin"` to find recently-binned items).

### Find a block by component-level field (`$elemMatch`, same idiom as authoring)

```javascript
db.blocks.find({
  _t: "ComponentBlockModel",
  components: {
    $elemMatch: { type: "Image" }
  }
})
```

Note the projection differences from authoring:
- Components here have `id`, NOT `_id`.
- The discriminator on individual components is **`type`** (a `ComponentType`
  enum value), not `_t`. Content-search collapses authoring's many `_t`
  variants into a smaller `type` enum.
- Question-type-specific fields (e.g. an asset id, validation rules) live
  inside `extraElements`, NOT as top-level fields on the component.

### Match a question-type-specific field that's inside `extraElements`

```javascript
db.blocks.find({
  components: {
    $elemMatch: {
      type: "Image",
      "extraElements.assetId": null
    }
  }
})
```

Dotted path through `extraElements`. The exact key inside `extraElements`
mirrors what the projection wrote — typically the same camelCase name as
on the source `ComponentBlockModel`. When a query that "should match"
returns nothing, check whether the field lives at the top level or under
`extraElements`.

### Find recently binned activities

```javascript
db.activities.find({
  lifeCycleStatus: "InBin",
  binnedAt: { $gte: ISODate("2025-12-15T00:00:00Z") }
}).sort({ binnedAt: -1 }).limit(20)
```

### Cross-reference: this service's view vs. authoring's view of the same activity

When investigating "the document is in authoring but not search" or vice
versa, the pair of queries to run side by side is:

```javascript
// On the authoring-service Mongo
db.activities.findOne({ _id: UUID("...") })

// On the content-search-service Mongo (same _id, but possibly different state)
db.activities.findOne({ _id: UUID("...") })
```

If both return docs, compare `state`, `isLatest`, `updatedAt`. If only
authoring has it, the projection event likely failed or hasn't fired —
check Kafka. If only content-search has it, the doc may have been
deleted authoring-side but lifecycle status here hasn't been updated.

### Querying alignment tags directly — STRING UUIDs

```javascript
db.activities.find({
  "alignmentTags.curriculum": "11111111-2222-3333-4444-555555555555"
})
```

All `AlignmentTagModel` GUIDs are stored as strings here. Don't wrap
with `UUID(...)`.

### OpenSearch — same shape, different syntax

The same documents project into OpenSearch. A typical query:

```http
POST activities/_search
{
  "query": {
    "bool": {
      "must": [
        { "term": { "lifeCycleStatus": "Active" } },
        { "term": { "isLatest": true } }
      ]
    }
  }
}
```

If a Mongo query returns a doc but the equivalent OpenSearch query
doesn't (or vice versa), the index is out of sync — the projection
likely failed at write time. Compare the Mongo doc's `updatedAt` with
the OpenSearch doc's last-seen timestamp.

---


## Common gotchas (vs authoring-service)

- **`tags` type difference:** authoring uses `string[]` (tag NAMES), content-search uses `UUID[]` (tag IDs). When correlating, use the tag-id mapping, not direct equality.
- **`blocks` storage difference:** authoring stores as standard binary UUIDs, content-search stores as strings. A literal `mongosh` lookup written for one will not work on the other — see the Querying section above for the right shape.
- **`alignmentTags` GUID representation:** all four fields (`curriculum`, `subject`, `levels`, `outcomeCodes`) are stored as strings here, binary UUIDs in authoring.
- **Lifecycle fields are content-search-only:** `binnedAt`, `deletedAt`, `deletedBy`, `lifeCycleStatus`. The "active vs binned vs deleted" distinction lives here, not in authoring.
- **`extraElements` masks shape changes:** if the projection logic stops setting a field, the field will silently end up in `extraElements` rather than missing from the doc. So a missing field in `extraElements` is a stronger signal than a missing top-level field. When investigating "field X disappeared," check `extraElements` before concluding the field was dropped.
- **`updatedBy` / `updatedAt` are required here, optional in authoring.** A document with these missing is wrong and may indicate the projection event hasn't fired yet.
- **Component-level discriminator differs from authoring.** Authoring uses `_t` (e.g. `"ImageComponentModel"`); content-search uses `type` (e.g. `"Image"`) plus `controlType` for finer detail. Content-search's `type` enum is also narrower — multiple authoring `_t` values collapse into one content-search `type`.
