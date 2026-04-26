# authoring-service — MongoDB schema

Source of truth: `source/authoring-service/source/AuthoringService.Infrastructure/Models/`.
Bson conventions: **camelCase field names**, **enums as strings**, **GUIDs as standard binary** (uuid). Refer to those when interpreting any raw document below.

## Collections

| Name | Backed by |
|---|---|
| `activities` | `ActivityModel` (extends `BaseActivityModel`) |
| `blocks` | `BlockModel` (abstract; concrete: `ComponentBlockModel`, `SectionBlockModel`) |

The `blocks` collection uses a **discriminator field `_t`** with values `"ComponentBlockModel"` or `"SectionBlockModel"` (root class `BlockModel` is decorated with `[BsonDiscriminator(RootClass = true)]`).

---

## Collection: `activities`

Each document is one activity (lesson, quiz, exam revision). Holds metadata + an ordered list of block ids.

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `_id` | UUID | required | `[BsonId]` — maps to `Id` |
| `name` | string | yes | |
| `description` | string | yes | |
| `tags` | string[] | default `[]` | free-form tags |
| `theme` | string | yes | |
| `alignmentTags` | `AlignmentTagModel[]` | default `[]` | curriculum alignment |
| `createdAt` | ISODate | required | |
| `createdBy` | UUID | required | |
| `organizationId` | UUID | yes | null = EP org |
| `aiGenerationMetrics` | `Dictionary<string, AiCount>` | default `{}` | key = component-type-ish string |
| `useCases` | `ContentActivityType[]` | default `[]` | enum: `Quiz`, `Lesson`, `ExamRevision` |
| `isEpContent` | bool | required | true = published by EP, not customer |
| `state` | `State` | required | enum: `Draft`, `Published` |
| `blocks` | UUID[] | default `[]` | **ordered** ids referencing `blocks` collection |
| `versions` | `VersionsModel` | yes | see Shared types |
| `sourceId` | UUID | yes | original this was branched from |
| `copiedFromId` | UUID | yes | direct parent if copied |
| `copyStatus` | `CopyStatus` | yes | enum: `Processing`, `Failed`, `Ready` |
| `updatedAt` | ISODate | yes | |
| `updatedBy` | UUID | yes | |
| `visitedAt` | ISODate | yes | |
| `visitedBy` | UUID | yes | |
| `isLatest` | bool | required | true = canonical revision |

### `AiCount` (used in `aiGenerationMetrics` dictionary values)

| Field | Type |
|---|---|
| `numberGenerated` | int |
| `numberRejected` | int |

---

## Collection: `blocks`

Each document is one block. Discriminator field `_t` decides the shape.

### Common to all blocks (`BlockModel`)

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `_id` | UUID | required | |
| `_t` | string | required | `"ComponentBlockModel"` or `"SectionBlockModel"` |
| `title` | string | yes | |
| `description` | string | yes | |
| `notes` | string | yes | author notes |
| `tags` | string[] | default `[]` | |
| `alignmentTags` | `AlignmentTagModel[]` | default `[]` | |
| `creationSource` | `BlockCreationSource` | yes | enum: `Unknown`, `Human`, `AiGenerated` |
| `createdAt` | ISODate | required | |
| `createdBy` | UUID | required | |
| `organizationId` | UUID | yes | |
| `isEpContent` | bool | required | |
| `state` | `State` | required | enum: `Draft`, `Published` |
| `versions` | `VersionsModel` | yes | |
| `sourceId` | UUID | yes | |
| `copiedFromId` | UUID | yes | |
| `updatedAt` | ISODate | yes | |
| `updatedBy` | UUID | yes | |
| `isLatest` | bool | required | |

### When `_t = "ComponentBlockModel"`

Adds:

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `template` | string | yes | layout template name |
| `components` | `ComponentModel[]` | default `[]` | discriminated union — see Components |

### When `_t = "SectionBlockModel"`

Adds:

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `blocks` | UUID[] | default `[]` | **ordered** ids referencing other docs in this same `blocks` collection (recursively) |

---

## Components (inside `ComponentBlockModel.components`)

Each component is also a discriminated union. The discriminator is `_t` (subclass name), and the model also stores `type` (`ComponentType` enum) and `controlType` (string) for fast lookups.

### Common to all components (`ComponentModel`)

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `_id` | UUID | required | |
| `_t` | string | required | subclass name (e.g. `"ImageComponentModel"`) |
| `type` | `ComponentType` | required | enum (see below) |
| `controlType` | string | required | finer-grained control name |
| `order` | int | yes | display order |

### `ComponentType` enum values

`Association`, `GapsDrag`, `GapsDropdown`, `GapsImageDrag`, `GapsImageDropdown`, `GapsImageText`, `GapsText`, `Image`, `Matrix`, `OrderedList`, `Select`, `SortList`, `Text`, `Video`, `Info`, `StimulusText`, `LongAnswer`.

### `QuestionComponentModel` (intermediate base for question types)

Adds:

| Field | Type | Nullable |
|---|---|---|
| `title` | string | yes |
| `titleJson` | string | yes |
| `description` | string | yes |
| `descriptionJson` | string | yes |
| `feedback` | `Feedback` | yes |

### Concrete components (extra fields beyond their base)

| `_t` | Extends | Extra fields |
|---|---|---|
| `ImageComponentModel` | `ComponentModel` | `assetId: UUID` (**required**, non-nullable) |
| `VideoComponentModel` | `ComponentModel` | `assetId: UUID`, `metadataJson: string?` |
| `InfoComponentModel` | `ComponentModel` | `title?`, `description?`, `titleJson?`, `descriptionJson?` |
| `StimulusTextComponentModel` | `ComponentModel` | `content?`, `contentJson?` |
| `LongAnswerComponentModel` | `ComponentModel` | `title?`, `titleJson?`, `description?`, `descriptionJson?`, `defaultContent?`, `modelAnswerJson?`, `explanationJson?`, `settings?`, `rubric?`, `markingMethod: LongAnswerMarkingMethod` |
| `TextComponentModel` | `QuestionComponentModel` | `validation: TextValidationResponseModel` (required) |
| `SelectComponentModel` | `QuestionComponentModel` | `options: OptionModel[]`, `validation: SelectValidationModel?`, `allowMultiple: bool`, `shuffleOptions: bool` |
| `MatrixComponentModel` | `QuestionComponentModel` | (no extra fields) |
| `OrderedListComponentModel` | `QuestionComponentModel` | (no extra fields) |
| `SortListComponentModel` | `QuestionComponentModel` | (no extra fields) |
| `AssociationComponentModel` | `QuestionComponentModel` | `targets: OptionModel[]`, `options: OptionModel[]`, `validation: AssociationValidationModel?` |
| `GapsDragComponentModel` | `QuestionComponentModel` | `options: GapsDragOptionModel[]`, `validation: GapsDragValidationModel?`, `text`, `template`, `templateRendered` |
| `GapsDropdownComponentModel` | `QuestionComponentModel` | `matrixOptions: Dictionary<UUID, OptionModel[]>`, `validation: GapsDropdownValidationModel?`, `text`, `template`, `templateRendered`, `shuffleOptions: bool` |
| `GapsTextComponentModel` | `QuestionComponentModel` | (no extra fields) |
| `GapsImageDragComponentModel` | `QuestionComponentModel` | (no extra fields) |
| `GapsImageDropdownComponentModel` | `QuestionComponentModel` | (no extra fields) |
| `GapsImageTextComponentModel` | `QuestionComponentModel` | (no extra fields) |

### `OptionModel` (used in Select / Association / Gaps options)

| Field | Type | Notes |
|---|---|---|
| `value` | UUID | required (renamed from typical `id`) |
| `label` | string | required |
| `labelJson` | string | yes |

`GapsDragOptionModel` extends with `isCorrect: bool`.

---

## Shared types

### `AlignmentTagModel`

| Field | Type |
|---|---|
| `curriculum` | UUID |
| `subject` | UUID |
| `levels` | UUID[] |
| `outcomeCodes` | UUID[] |

### `VersionsModel`

| Field | Type | Notes |
|---|---|---|
| `current` | UUID | required — id of current version |
| `all` | `VersionModel[]` | history |

### `VersionModel`

| Field | Type |
|---|---|
| `id` | UUID |
| `author` | UUID |
| `createdAt` | ISODate |
| `note` | string? |

---

## Bson conventions to remember when reading raw docs

- **Field names are camelCase**, not PascalCase. `Id` → `id`. `CreatedAt` → `createdAt`. `IsLatest` → `isLatest`. The one exception is `_id` (Mongo standard) which is the same field as `Id` in C#.
- **Enums are stored as their string name**, not integer value. `state: "Draft"` not `state: 0`.
- **GUIDs use `GuidRepresentation.Standard`** — they show as `UUID("…")` in `mongosh`, not as `BinData(3, …)` or `BinData(4, …)`.
- **Discriminators use `_t`** — `_t: "ComponentBlockModel"` etc.
- **The `[BsonExtraElements]` is NOT used** in authoring-service models, so unknown fields would normally throw on deserialization — but the global `IgnoreExtraElementsConvention` is registered, so they're silently dropped instead.

## Common gotchas

- `ImageComponentModel.assetId` is `Guid` (non-nullable). A document with `assetId: null` will **fail to deserialize** with `FormatException: An error occurred while deserializing the AssetId property`. Same for `VideoComponentModel.assetId`. This is one of the recurring CoCo bugs.
- Documents with `isLatest: false` are historical — most queries should filter `isLatest: true` to get the canonical revision.
- `state: "Draft"` and `state: "Published"` are the only two values. Activities/blocks deletion is soft (handled by `lifeCycleStatus` in content-search-service, not here).
- An activity references blocks by id (`blocks: [UUID, …]`). A `SectionBlock` further references blocks (also by id). Recursion depth is at most 2 in practice (activity → section block → component block) but the schema doesn't enforce this.
