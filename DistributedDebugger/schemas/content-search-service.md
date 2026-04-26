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
| `_t` | string | required | `"ComponentBlockModel"` or `"SectionBlockModel"` |
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

---

## OpenSearch projection

The same documents are indexed into OpenSearch via `IDraftBlockRepository` / `ISearchBlockRepository`. Index names typically mirror the collection (`activities`, `blocks`) but the indexer may flatten or transform fields. Queries that hit OpenSearch (`/index/_search`) operate on this projection and may not match the Mongo document shape exactly — when in doubt, prefer reasoning from the Mongo document for ground truth.

---

## Common gotchas (vs authoring-service)

- **`tags` type difference:** authoring uses `string[]` (tag NAMES), content-search uses `UUID[]` (tag IDs). When correlating, use the tag-id mapping, not direct equality.
- **`blocks` storage difference:** authoring stores as standard binary UUIDs, content-search stores as strings. A literal `mongosh` lookup written for one will not work on the other.
- **`alignmentTags` GUID representation:** all four fields (`curriculum`, `subject`, `levels`, `outcomeCodes`) are stored as strings here, binary UUIDs in authoring.
- **Lifecycle fields are content-search-only:** `binnedAt`, `deletedAt`, `deletedBy`, `lifeCycleStatus`. The "active vs binned vs deleted" distinction lives here, not in authoring.
- **`extraElements` masks shape changes:** if the projection logic stops setting a field, the field will silently end up in `extraElements` rather than missing from the doc. So a missing field in `extraElements` is a stronger signal than a missing top-level field.
- **`updatedBy` / `updatedAt` are required here, optional in authoring.** A document with these missing is wrong and may indicate the projection event hasn't fired yet.
