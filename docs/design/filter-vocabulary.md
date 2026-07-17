# Canonical FFXIV Filter Vocabulary

Status: implemented vocabulary contract

Audience: filter users, UI authors, context implementers, and vocabulary contributors

Companion architecture: [Franthropy Filter Language](filter-language.md)

Living implementation plan: [Filter Language and Inventory Viewer Implementation Roadmap](filter-language-implementation.md)

## Purpose

This document explains the shared FFXIV vocabulary used by Franthropy filter expressions. The architecture document defines how expressions are parsed and evaluated; this reference defines what canonical fields mean, which values they accept, and how the same expression behaves across different data contexts.

The goal is transferable literacy. A user who learns that `ilvl` means item level, `price` means the unit price of a purchase offer, and `owned` means observed ownership should be able to carry that knowledge between inventory, Outfitter, market, retainer, crafting, and hosted tools.

Until the catalog is implemented, this document is the proposed vocabulary contract. After implementation, field descriptors and their contract tests become mechanically authoritative. This document remains the human explanation of domain boundaries and worked usage; generated Markdown and JSON references should supply exhaustive field and named-value listings.

## Reading the vocabulary

### A filter starts as an ordinary search

Bare words search the active context's documented primary text fields:

```text
darksteel
```

Structured terms narrow the same result set:

```text
darksteel quality:HQ quantity>=20
```

Whitespace means `AND`. Parentheses and `OR` express alternatives:

```text
job:WHM (slot:ring OR slot:neck)
```

The complete grammar and precedence rules live in the [language architecture](filter-language.md#language-surface).

### Canonical paths describe subjects, not features

Every field has a qualified canonical path:

- `item.*` describes the FFXIV item definition.
- `instance.*` describes an observed physical item or stack.
- `ownership.*` describes an aggregate ownership scope.
- `offer.*` describes an opportunity to purchase an item.
- `acquisition.*` describes how an item can be obtained.

These subjects are intentionally independent of MarketMafioso, Squire, Craft Architect, or any UI tab. An Outfitter candidate can bind item, ownership, and offer fields at once without creating an Outfitter dialect.

### Short field names are conveniences, not new meanings

The compiler resolves a field name in this order:

1. an exact canonical path such as `offer.quantity`;
2. a globally registered alias such as `ilvl`;
3. an unqualified leaf such as `quantity`, but only when the active context makes it unambiguous.

For an inventory stack, `quantity` can mean `instance.quantity`. For a purchase-offer row, it can mean `offer.quantity`. If a composed Outfitter row binds both, `quantity` is ambiguous and the user must choose:

```text
ownership.quantity>=1 offer.quantity>=10
```

The context did not redefine `quantity`; it exposed two different canonical quantities and refused to guess.

### Field availability and unknown evidence are different

An unavailable field cannot be answered by the active context. A local inventory row has no purchase offer, so `price<5000` produces an availability error.

An unknown field is supported by the context but missing for one record. A purchase-offer context might support `age`, while a particular imported quote lacks an observation timestamp. Comparisons against that record evaluate as unknown and do not match. Negation stays unknown rather than turning absent evidence into a positive match.

Use explicit evidence tests when incompleteness is itself relevant:

```text
known(price)
unknown(age)
```

## Matching behavior by type

| Field type | `:` behavior | `=` behavior | Ordered comparisons |
| --- | --- | --- | --- |
| text | normalized containment | normalized containment (`!=` negates it); `==` is normalized whole-value equality | unavailable |
| named entity | exact identity resolution, except fields such as item name that explicitly declare record-level display-name containment | uniquely resolved partial name or alias; `==` requires a complete normalized name or alias | unavailable unless the type declares ordering |
| enum | named-value equality | uniquely resolved partial value; `==` requires a complete value or alias | unavailable unless explicitly ordered |
| set | contains or overlaps the supplied values | contains a uniquely resolved value; exact operators also compare member identity rather than whole-set shape | unavailable |
| boolean | typed equality | typed equality | unavailable |
| number, quantity, currency, percentage | equality | equality | `<`, `<=`, `>`, `>=`, and ranges |
| duration or age | equality | equality | `<`, `<=`, `>`, `>=`, and ranges |

For set fields, `job:(WHM | SCH)` means the record contains either eligible job. Equality operators test membership against the resolved values rather than comparing the entire stored set; `!=` and `!==` require that none of the requested members overlap.

Ranges are inclusive:

```text
ilvl:650..660
price:..5000
condition:80..
```

## Item definition fields

Item fields describe stable facts from the FFXIV item definition. They do not describe a particular stack, ownership snapshot, listing, or acquisition plan.

### `item.name`

- **Type:** named item
- **Short forms:** `item`; `name` when unambiguous
- **Typical operators:** `:`, `=`, `!=`, `==`, `!==`

The visible value is the localized item name, while resolution retains a stable internal item key. Item IDs are never required in ordinary input.

```text
item:"Aetheryte Ring"
item="Grade 8 Dark Matter"
```

`:` is the broad name-search experience: `name:darksteel` evaluates each record's normalized display name and can match several distinct item identities. `=` instead resolves a partial against the finite item catalog and succeeds only when one identity remains. `==` requests a normalized whole-name or alias match. If localized exact names or partial identity searches collide, completion and diagnostics disambiguate with user-recognizable metadata rather than asking for a numeric ID.

### `item.itemLevel`

- **Type:** non-negative integer
- **Aliases:** `ilvl`
- **Typical operators:** all numeric comparisons and ranges

This is the FFXIV item level used for gear progression and item-strength comparisons. It is not the character level required to equip the item.

```text
ilvl>=660
ilvl:130..270
```

### `item.equipLevel`

- **Type:** non-negative integer
- **Aliases:** `level`, `lvl`
- **Typical operators:** all numeric comparisons and ranges

This is the required character level to equip an item. Non-equipment records may expose it as unknown or leave the field unavailable depending on the context's record contract.

```text
level<=50
level:90..100
```

### `item.job`

- **Type:** set of named FFXIV jobs and classes
- **Aliases:** `job`, `class`
- **Typical operators:** set membership with `:`, fuzzy member resolution with `=`, and complete member resolution with `==`

Values resolve from stable class/job identities and accept canonical abbreviations and localized names. The catalog is populated from game data rather than a hand-maintained list, so newly added jobs do not require parser changes.

```text
job:WHM
job:"White Mage"
job:(WHM | SCH | AST | SGE)
```

Eligibility is contextual. Equipment contexts bind jobs that can equip the item; recipe contexts may eventually bind `recipe.job` because the crafting job that produces an item is a different fact.

### `item.slot`

- **Type:** set of equipment slots
- **Alias:** `slot`
- **Typical operators:** set membership with `:`, fuzzy member resolution with `=`, and complete member resolution with `==`

Initial canonical values follow Franthropy equipment semantics: `mainHand`, `offHand`, `head`, `body`, `hands`, `legs`, `feet`, `ears`, `neck`, `wrists`, `ring`, and `soulCrystal`. Friendly plurals and familiar names may be value aliases, but their stable keys remain unchanged.

```text
slot:ring
slot:(head | body | hands | legs | feet)
```

An item capable of occupying either ring position still has the semantic slot `ring`; left and right placement belongs to loadout positioning, not item identity.

### `item.rarity`

- **Type:** enum
- **Alias:** `rarity`
- **Typical operators:** `:`, `=`, `!=`

This is Franthropy's normalized FFXIV rarity, not merely a UI color string. Human color names may be accepted as value aliases where the mapping is exact. The generated reference must list the values supported by the installed catalog.

```text
rarity:rare
rarity:(uncommon | rare | relic)
```

### `item.uiCategory`

- **Type:** named FFXIV item UI category
- **Alias:** `category`
- **Typical operators:** `:`, `=`, `!=`

This uses the user-facing FFXIV item UI category. It must not silently substitute the market-search category or equip-slot category, which are different game-data concepts and require distinct canonical fields if exposed later.

```text
category:ring
category:"Metal"
```

### `item.unique`

- **Type:** boolean
- **Alias:** `unique`

```text
unique:true
unique:false
```

The first expression requires the FFXIV unique-item flag. The second requires it to be known and false; records with unknown uniqueness do not match either expression.

### `item.tradable`

- **Type:** boolean
- **Alias:** `tradable`

This describes whether the item definition permits trade or market listing where applicable. It does not prove that a listing currently exists.

```text
tradable:true
tradable:false
```

### `item.desynthesizable`

- **Type:** boolean
- **Alias:** `desynth`

This reflects item-definition eligibility for desynthesis. Character skill, unlock state, and the safety of automating desynthesis are separate concerns.

```text
desynth:true
desynth:false
```

### Deliberate omissions

The item family does not duplicate acquisition facts as `item.craftable` or `item.vendorAvailable`. Use `acquisition.source:craft` or `acquisition.source:vendor` when asking whether those methods are known for an item. A particular actionable vendor or market quote uses `offer.source` instead.

This gives obtainability one representation at each grain. If concise predicate aliases such as `craftable` are later justified for adoption, they must expand to canonical expressions rather than introduce separately bound boolean evidence.

## Item-instance fields

Instance fields describe an observed physical item or stack. Their values may change without the item definition changing.

### `instance.quality`

- **Type:** enum (`NQ`, `HQ`)
- **Short form:** `quality` when unambiguous

```text
quality:HQ
quality:NQ
is:hq
is:nq
```

Omitting the field means either quality is accepted. `quality:any` is therefore unnecessary and is not a canonical value. Collectability is a separate concept and must not be folded into NQ/HQ.

### `instance.quantity`

- **Type:** non-negative quantity
- **Short form:** `quantity` when unambiguous

This is the amount in one observed physical stack. It is not total ownership and not a purchase offer's available quantity.

```text
instance.quantity>=20
quantity:1..99
```

### `instance.location`

- **Type:** named FFXIV storage location
- **Short form:** `location` when unambiguous

Values are stable semantic locations such as inventory, armoury chest, equipped gear, retainer inventory, saddlebag, glamour dresser, and armoire. Internal container IDs remain hidden behind these names.

```text
location:retainer
location:(inventory | armoury | equipped)
```

If a surface distinguishes several retainers, `ownership.retainer` identifies the owner while `instance.location` remains the semantic storage kind.

### `instance.equipped`

- **Type:** boolean
- **Friendly predicate:** `is:equipped`

This indicates that the observed instance occupies a currently equipped slot. Gearset membership is not equivalent to being equipped and requires its own field if later exposed.

```text
is:equipped
-is:equipped
```

### `instance.condition`

- **Type:** percentage from 0 through 100
- **Alias:** `condition`

The context normalizes FFXIV's internal representation to a user-facing percentage.

```text
condition<100
condition:0..20
```

### `instance.spiritbond`

- **Type:** percentage from 0 through 100
- **Alias:** `spiritbond`

```text
spiritbond>=100
spiritbond:80..99
```

Whether materia extraction is unlocked or advisable is separate from the observed spiritbond percentage.

## Ownership fields

Ownership fields describe an explicitly documented scope, such as the active character, all locally observed characters, or a selected set of retainers. The surrounding surface must make that scope clear; the language should not quietly change it.

### `ownership.owned`

- **Type:** boolean
- **Alias:** `owned`

This is true when at least one matching instance exists inside the active ownership scope.

```text
owned:true
owned:false
```

`owned:false` requires a complete enough snapshot to establish absence. If the ownership snapshot is incomplete, the value should be unknown rather than false.

A context can evaluate `owned:false` only when its record universe includes candidates with no matching instances, such as an item catalog, recipe requirement list, or Outfitter candidate set. A viewer built only from observed stacks has no absent-item records and should leave `ownership.owned` unavailable rather than offering a predicate that can never find anything.

### `ownership.quantity`

- **Type:** non-negative quantity
- **Short form:** `quantity` when unambiguous

This is the total amount across the active ownership scope after the context's documented aggregation. It differs deliberately from `instance.quantity`.

```text
ownership.quantity<10
ownership.quantity:1..99
```

### `ownership.character`

- **Type:** set of named characters
- **Short form:** `character` when unambiguous

This identifies characters contributing matching ownership evidence. Resolution should include home world when necessary to disambiguate identical names.

```text
character:"Alyx Example@Siren"
```

### `ownership.retainer`

- **Type:** set of named retainers
- **Alias:** `retainer`

This identifies retainers contributing matching ownership evidence. It is unavailable when the ownership source cannot attribute records to individual retainers.

```text
retainer:Belladonna
retainer:(Belladonna | Marmalade)
```

## Purchase-offer fields

Offer fields describe one observed opportunity to purchase an item. NPC vendor stock and market listings can share this shape where their semantics genuinely agree.

### `offer.source`

- **Type:** canonical purchase-offer source enum
- **Short form:** `source` when unambiguous

This identifies the source of the represented actionable offer, such as `vendor`, `market`, or `exchange`. It is singular because one offer has one source.

```text
offer.source:vendor
offer.source:(vendor | market)
```

This differs from `acquisition.source`, which is the set of all known ways an item can be obtained. A composite candidate may expose both and therefore requires the qualified form.

### `offer.price`

- **Type:** non-negative gil currency per unit
- **Short form:** `price` when unambiguous

```text
price<5000
offer.price:1000..2500
```

This is always the unit purchase price. Vendor sell value, historical sale price, and total listing price are different concepts and must use different canonical fields.

### `offer.totalPrice`

- **Type:** non-negative gil currency
- **Short form:** `totalPrice` when unambiguous

This is the cost of the represented offer quantity. It may be derived from price and quantity, but a context should bind it as unknown when either input is unreliable rather than manufacture precision.

```text
totalPrice<=100000
```

### `offer.quantity`

- **Type:** non-negative quantity
- **Short form:** `quantity` when unambiguous

This is the amount available in the represented offer. It is neither the user's target quantity nor existing ownership.

```text
offer.quantity>=20
```

### `offer.world`

- **Type:** named FFXIV world
- **Short form:** `world` when unambiguous

Values resolve through Franthropy's world catalog by name; row IDs remain internal.

```text
world:Siren
world:(Siren | Faerie | Gilgamesh)
```

### `offer.dataCenter`

- **Type:** named FFXIV data center
- **Short form:** `dataCenter` when unambiguous

This is normally derived from `offer.world` using the same world-catalog snapshot.

```text
dataCenter:Aether
```

### `offer.region`

- **Type:** FFXIV region enum
- **Short form:** `region` when unambiguous

This is normally derived from the offer world and uses stable region keys with friendly value aliases.

```text
region:"North America"
```

### `offer.age`

- **Type:** non-negative duration
- **Short form:** `age` when unambiguous

This is elapsed time since the represented price and availability evidence was observed, not the age of the item or listing itself.

```text
age<=30m
age:1h..6h
```

Live vendor data may reasonably bind age as zero or leave it unavailable, depending on whether the context models the vendor interaction as a timestamped offer.

## Acquisition fields

### `acquisition.source`

- **Type:** set of canonical acquisition-source values
- **Short form:** `source` when unambiguous

This is the set of known ways the item can be obtained, independent of whether an actionable offer is currently present. The initial vocabulary should cover at least `vendor`, `market`, `craft`, `gather`, `retainer`, `quest`, `duty`, `drop`, and `exchange`. Each value describes a method of obtaining the item, not a product subsystem that performs it.

```text
acquisition.source:vendor
acquisition.source:(vendor | market)
acquisition.source:(craft | gather) NOT acquisition.source:vendor
```

Named source values are stable keys with user-facing labels. More specific provenance—vendor identity, recipe, gathering node, duty, or currency—belongs in additional canonical fields rather than encoded strings inside `source`.

## Context availability

The following matrix describes the expected starting shape. **Required** means the context cannot fulfill its purpose without the field family. **Optional** means the context may bind it when the record carries reliable evidence. A dash means the family is normally unavailable.

| Context | Item | Instance | Ownership | Offer | Acquisition |
| --- | --- | --- | --- | --- | --- |
| `ffxiv.item-instances` | Required | Required | Optional attribution | — | Optional |
| `ffxiv.ownership-summaries` | Required | — | Required | — | Optional |
| `ffxiv.purchase-offers` | Required | Optional quality | — | Required | — |
| `ffxiv.equipment-candidates` | Required | Optional | Optional | Optional | Optional |
| `ffxiv.recipes` | Required result item | — | Optional ingredients | — | Required craft source |

The matrix is guidance, not runtime inference. Every concrete context declares its bindings explicitly and receives a schema version. Generated help shows exact availability for the active surface.

## Worked expressions

### Find retainer stacks worth consolidating

```text
darksteel location:retainer instance.quantity>=20
```

In an item-instance context, bare `darksteel` searches `item.name`; `location` resolves to `instance.location`; and the qualified quantity makes the stack-level intent explicit.

### Find relevant healing gear below an item-level threshold

```text
job:(WHM | SCH | AST | SGE) slot:(head | body | hands | legs | feet) ilvl<660
```

Both `job` and `slot` are set-membership predicates. The expression matches equipment usable by any listed healer in any listed armour slot.

### Find cheap vendor or market offers

```text
offer.source:(vendor | market) price<=5000 age<=30m
```

This requires a purchase-offer context with offer source, price, and observation age. If vendor offers do not carry meaningful age in that context, the compiler reports availability or those records evaluate unknown according to the declared binding.

### Find an owned upgrade or an affordable offer

```text
job:WHM slot:ring ilvl>=660 (owned:true OR offer.price<=10000)
```

An equipment-candidate context can combine stable item facts with ownership and offer evidence. The qualified price remains readable and prevents future ambiguity if another kind of price is added to the candidate record.

### Separate owned quantity from offer quantity

```text
ownership.quantity<10 offer.quantity>=20 offer.price<=2500
```

This is the composite-record case that forbids bare `quantity`. The expression asks for an ownership shortage, a sufficiently large offer, and an acceptable unit price without collapsing three different business facts into one field.

### Find incomplete market evidence

```text
offer.source:market (unknown(price) OR unknown(age))
```

Unknown-evidence tests are explicit. Ordinary negation would not treat missing evidence as a confirmed cheap, stale, or non-market result.

### Preserve ordinary name search

```text
augmented ironworks job:WHM
```

The user does not need to write `item:"augmented ironworks"`. Free text and structured terms coexist, which is essential for adoption in compact search boxes.

## Filters inside rules

The same expression may select targets for a rule, but the language only answers **which records match**. The rule separately owns the action, thresholds not represented by the target record, execution policy, confirmation, and audit behavior.

For example:

```text
location:retainer -is:equipped condition<100
```

This can identify instances, but it does not authorize repair, transfer, sale, or disposal. Keeping selection separate from action lets several products share the language without inheriting one another's automation model.

## Adding vocabulary

### Canonical versus extension fields

A field belongs in the canonical FFXIV catalog when it describes a stable FFXIV concept that two credible contexts could share. A field remains a namespaced extension when it describes one product's workflow state, presentation, or orchestration.

Good canonical candidates:

- `recipe.job`, because recipes and crafting tools share its meaning;
- `gearset.member`, because equipment and inventory contexts may both use it;
- `vendor.name`, because acquisition and travel contexts can agree on the NPC identity.

Appropriate extensions:

- `mmf.routeStatus`, because it describes MarketMafioso workflow state;
- `squire.recoveryCheckpoint`, because it describes a particular runner lifecycle;
- a tab selection, UI expansion state, lease, or internal revision.

An extension that later gains a stable shared meaning can graduate into the canonical catalog. The old name becomes a deprecated migration alias; consumers do not silently reinterpret saved expressions.

### Field proposal checklist

Every proposed canonical field should answer:

1. **What subject does it describe?** Choose a semantic path such as item, instance, ownership, offer, recipe, character, gearset, vendor, or place—not a module name.
2. **What is its exact grain?** Distinguish one stack from aggregate ownership, one offer from historical price, and definition facts from observed state.
3. **What is its type?** Operators and unknown behavior follow from the type rather than custom parsing.
4. **What evidence makes it known?** State whether the value comes from game data, a live observation, a snapshot, a derivation, or external evidence.
5. **What can it be confused with?** Review leaf-name collisions, named-value ambiguity, localization, and neighboring concepts.
6. **Which contexts credibly share it?** Name at least two without bending the definition.
7. **What are the user examples?** Include a common expression, a qualified composite expression, and an unknown or unavailable case.
8. **How will it remain compatible?** Define stable keys, aliases, deprecation behavior, and catalog tests.

### Descriptor requirements

The implemented field descriptor must contain enough metadata to generate:

- canonical path and explicit aliases;
- user-facing label and description;
- field type and supported operators;
- value source, value aliases, and completion provider;
- representative examples;
- evidence and unknown-value description;
- catalog version introduced and deprecation metadata;
- context availability supplied by each binding set.

Registration fails for duplicate paths, alias collisions, undocumented fields, invalid examples, or incompatible bindings. Vocabulary quality is enforced at startup and in tests rather than left to UI convention.

## Documentation and publication

The repository is the source of truth because vocabulary changes are API changes and should travel with implementation, tests, and review history.

The intended publication pipeline is:

1. field descriptors and contract tests define the implemented mechanics;
2. a documentation generator emits exhaustive Markdown and JSON references for each catalog version;
3. this document supplies the human rationale, boundaries, and worked examples that metadata alone cannot explain;
4. GitHub Pages, a wiki, or product help may publish the generated artifacts, but those mirrors are never edited independently.

This avoids asking adopters to reconcile a wiki, source code, and in-game help when they inevitably drift. One catalog produces every mechanical reference; one reviewed narrative explains why the vocabulary is shaped this way.
