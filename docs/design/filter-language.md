# Franthropy Filter Language

Status: implemented architecture

Audience: Franthropy and consuming-plugin maintainers

Primary consumers: MarketMafioso, Squire, Craft Architect, inventory and retainer tooling, hosted companion surfaces

Canonical field reference: [Canonical FFXIV Filter Vocabulary](filter-vocabulary.md)

Living implementation plan: [Filter Language and Inventory Viewer Implementation Roadmap](filter-language-implementation.md)

## Summary

Franthropy should provide one coherent filter language for FFXIV data. The language must behave the same wherever it appears: a user who learns `ilvl>=660`, `job:WHM`, or `world:Siren` in one tool should not encounter a different spelling or meaning in another.

The design separates three concerns:

1. **`Franthropy.Filtering` owns the language.** It provides tokenization, parsing, typed values, semantic binding, diagnostics, compilation, completion, documentation metadata, and evaluation. It has no Dalamud, Lumina, ImGui, or product dependency.
2. **`Franthropy.FFXIV.Filtering` owns the canonical FFXIV vocabulary.** It defines stable field identities, aliases, types, value domains, and descriptions for concepts such as item name, job, slot, quality, ownership, location, price, world, and acquisition source. Field paths describe semantic entities, never the product or module that happens to expose them.
3. **Contexts bind the vocabulary to available data.** A consumer supplies accessors for the canonical fields its records can answer. It does not create a local dialect or reinterpret common terms.

This is a filter language, not an automation language. It selects records from an already-observed snapshot; it does not travel, refresh market data, mutate inventory, or call product services while evaluating a predicate.

## Why this belongs in Franthropy

The same concepts cross product and module boundaries. `price` is useful to an outfitter, a market browser, an acquisition planner, and a restock policy. `job`, `slot`, and `quality` are useful to inventory, retainers, gear planning, crafting, and disposal. Assigning those words to whichever module first implements them would make implementation history part of the user-facing language. Canonical paths therefore describe the thing being queried—such as an item, physical instance, owner, or purchase offer—rather than `squire.*`, `marketAcquisition.*`, or another feature boundary.

Franthropy already owns neutral FFXIV models for worlds, characters, equipment, inventory observation, and item-name input. A shared filter language is the query surface over that shared domain. It satisfies Franthropy's reuse maxim because several unrelated workflows can consume it without inheriting MarketMafioso's orchestration or policy.

### Ecosystem precedent

[Item Search](https://github.com/Caraxi/ItemSearchPlugin/blob/master/tag-filtering.md) demonstrates that FFXIV users benefit from mixing ordinary name search with structured job, slot, level, rarity, crafting, and property terms. [Allagan Tools](https://github.com/Critical-Impact/InventoryTools/blob/main/InventoryTools/Extensions/FilterComparisonExtensions.cs) demonstrates demand for comparison and boolean operators. Both are valuable product-local precedents, but their parsing is coupled to individual filters or string splitting rather than a typed grammar with a shared semantic catalog.

Franthropy should retain the successful UX—the search box remains useful before a user learns any syntax, while concise terms add precision—but replace the ad hoc architecture. The language must have a real parser, exact source spans, typed binding, and a vocabulary that survives reuse across records and products.

## Design principles

### One language, variable evidence

A field has one canonical identity and meaning across Franthropy. Contexts differ only in whether they can supply that field for the records being filtered.

- `offer.price` always means the unit price of a purchasable offer, whether the offer came from a market listing or an NPC vendor.
- `item.itemLevel` always means the item's FFXIV item level.
- `instance.quantity` always means the quantity represented by an observed physical item stack.

If a known field is unavailable in a context, compilation produces an availability diagnostic such as `price is not available while filtering local inventory`. It must not be reported as an unknown word, silently ignored, or reinterpreted.

### Names are the interface; IDs are implementation details

FFXIV users identify items, worlds, jobs, categories, and locations by name. Canonical resolvers accept those names and produce stable internal identifiers when necessary. Numeric item IDs are not part of the standard user-facing vocabulary.

Diagnostic and developer tooling may display resolved IDs under explicit technical detail, but ordinary filters, completions, examples, saved expressions, and validation messages remain name-first.

### Plain searches remain plain

Unqualified text performs the context's documented default text search, usually against `item.name` or another primary label:

```text
augmented ironworks
```

Structured terms add precision without forcing every search to become a query:

```text
augmented ironworks job:WHM ilvl>=130
```

The context chooses which canonical fields participate in unqualified text, but it cannot change operator behavior, aliases, field types, or structured-term meaning.

### Healthy ambiguity is resolved; harmful ambiguity is diagnosed

Explicit aliases such as `ilvl` and `lvl` are globally registered and have one meaning. A context cannot redefine them. Unqualified leaf names such as `price` and `quantity` are accepted only when resolution is unambiguous. An inventory-stack context can resolve `quantity` to `instance.quantity`, while an offer context resolves it to `offer.quantity`; a composite context containing both reports an ambiguity and suggests the two qualified forms. This is contextual abbreviation, not contextual semantics.

Field-name resolution follows a fixed order: exact canonical path, global explicit alias, then canonical leaf. A globally unique leaf resolves even when unavailable so Franthropy can produce an availability diagnostic. If several canonical fields share a leaf, exactly one active binding may resolve the shorthand; otherwise the compiler reports ambiguity. The editor can always display or insert the fully qualified form.

If two candidate values have the same visible name, the value resolver likewise returns a disambiguation diagnostic and completion choices instead of selecting an arbitrary row.

### Documentation is generated from the implementation

Field help, operator help, value examples, availability, aliases, and completion descriptions come from the same descriptors used by semantic binding. Franthropy should not maintain a hand-written reference that can drift away from runtime behavior.

## Terminology

| Term | Meaning |
| --- | --- |
| expression | the user-authored filter text |
| syntax tree | the parsed structure of an expression, including incomplete nodes and source spans |
| field | one globally defined queryable FFXIV concept, such as `item.itemLevel` |
| catalog | the immutable collection of canonical fields, types, aliases, values, and documentation metadata |
| context | a named data shape that declares which catalog fields its records can answer |
| binding | a typed accessor connecting one canonical field to one context's record type |
| resolver | a name-first service that turns user-visible values such as `Siren` or `White Mage` into stable domain values |
| evidence | a known field value or an explicit reason that the value is unknown for one record |
| compilation | parsing and semantically binding an expression to a context, producing diagnostics and an executable predicate |
| extension field | a namespaced product-workflow concept that is not part of the canonical FFXIV catalog |

“Query” may describe the complete expression in API prose, but user-facing UI should normally say **filter**. This keeps the feature grounded in its purpose and avoids suggesting a database or automation console.

## Language surface

### V1 examples

```text
darksteel ilvl>=50 acquisition.source:craft
"augmented ironworks" job:(WHM | SCH) quality:HQ
slot:ring (job:WHM | job:SCH) -is:equipped
offer.source:vendor price<5000
world:Siren quantity>=20 is:hq
-darksteel location:(inventory | retainer) condition<100
acquisition.source:vendor is:nq
```

The first expression means that the default text fields contain `darksteel`, item level is at least 50, and crafting is a known acquisition source. Whitespace is implicit `AND`.

### Operators

| Form | Meaning |
| --- | --- |
| `a b` | `a AND b` |
| `a AND b`, `a && b` | both expressions must match |
| `a OR b`, `a \| b` | either expression may match |
| `NOT a`, `!a`, `-a` | negate an expression; `-darksteel` excludes the default-name match |
| `( ... )` | explicit grouping |
| `field:value` | concise, type-appropriate direct match; text and explicitly searchable name fields use record-level fuzzy containment, while other typed and named values resolve exactly |
| `field=value`, `field!=value` | fuzzy match or its negation; finite vocabularies accept a partial only when it resolves uniquely |
| `field==value`, `field!==value` | normalized whole-value exact equality or its negation |
| `<`, `<=`, `>`, `>=` | ordered comparison for numeric, temporal, and other ordered types |
| `field:low..high` | inclusive range; either endpoint may be omitted |
| `field:(a \| b \| c)` | match any listed value |
| `known(field)`, `unknown(field)` | explicitly test whether a record has evidence for a field |

The negation modifier never changes the equality mode: `!=` is the negative partner of fuzzy `=`, while `!==` is the negative partner of exact `==`. Exact text and named-value comparison normalizes Unicode compatibility forms, case, and whitespace before comparing the whole text, name, or alias. Fuzzy text comparison performs normalized substring matching; it does not use regexes, edit distance, or stemming.

Item names deliberately distinguish direct search from identity resolution. `name:darksteel` tests every record's normalized display name and may return many different Darksteel items. `name=darksteel` asks the finite item catalog to resolve that partial to one identity and diagnoses ambiguity rather than choosing, while `name=="Darksteel Ingot"` requires a complete normalized name or alias. All three retain stable item keys internally; none exposes IDs to users.

Human-readable predicate namespaces express common states: `is:equipped`, `is:hq`, and `is:nq`. Canonical quality forms remain `quality:hq` and `quality:nq`; the `is:` spellings share those predicates' semantic identities. `has:` is reserved for future evidence predicates such as `has:price`.

Keywords are case-insensitive. Symbolic and word operators are aliases for the same AST nodes and therefore have identical precedence.

### Precedence

From strongest to weakest:

1. field comparison, range, list, function call, and parenthesized expression;
2. unary negation;
3. `AND`, including implicit whitespace;
4. `OR`.

`a b OR c` therefore means `(a AND b) OR c`. The formatter and help UI should make this visible rather than relying on users to memorize it.

### Literals

- Bare words are case-insensitive text or named-domain values.
- Double quotes preserve spaces and operator characters: `item:"Grade 8 Dark Matter"`.
- Backslash escapes `"`, `\\`, and reserved punctuation inside quoted text.
- Integers allow separators in input (`1,000` or `1_000`) and normalize them internally.
- Decimal values use invariant `.` syntax in persisted expressions; the editor may accept locale-aware input and normalize it.
- Durations use compact units such as `30m`, `6h`, and `2d` where a field declares duration semantics.
- Boolean fields accept `true`, `false`, `yes`, and `no`. Bare words always remain default-text search, so states use intentional forms such as `is:equipped` rather than a bare `equipped` token.
- A field's value resolver owns named values and aliases. For example, `job:WHM` and `job:"White Mage"` resolve to the same stable job identity.

V1 does not expose regular expressions, arbitrary functions, arithmetic, property reflection, or executable callbacks in query text.

### Grammar sketch

The normative grammar will live beside parser tests; this sketch defines the intended shape:

```ebnf
query          = or-expression , EOF ;
or-expression  = and-expression , { OR , and-expression } ;
and-expression = unary-expression , { ( AND | implicit-AND ) , unary-expression } ;
unary-expression = [ NOT | "!" | "-" ] , primary ;
primary        = "(" , or-expression , ")"
               | function-call
               | field-expression
               | free-text ;
field-expression = field-name , ( ":" , field-value
                                  | [ ":" ] , comparison-operator , field-value ) ;
comparison-operator = "=" | "!=" | "==" | "!==" | "<" | "<=" | ">" | ">=" ;
field-value    = scalar | range | value-list ;
range          = [ scalar ] , ".." , [ scalar ] ;
value-list     = "(" , scalar , { "|" , scalar } , ")" ;
function-call  = ( "known" | "unknown" ) , "(" , field-name , ")" ;
```

The shape `qualifier:domain:specifier` is reserved for future parameterized namespaces, following forms such as `stat:range:>=50`. V1 parses that shape and emits a focused reserved-syntax diagnostic; it does not assign ceremonial meanings such as `is:quality:hq`. This reservation prevents a consumer from claiming an incompatible ad hoc interpretation before a real nested domain exists.

Legacy operator-only forms such as `quantity>20` and dotted canonical paths remain valid advanced syntax. Primary completion and reference UI teach concise direct forms such as `quantity:20`, `location:armoury`, and `is:hq` instead.

The syntax tree retains the exact source text for saved human-authored filters. Formatting can produce a stable readable spelling, while compilation also emits a separate canonical semantic expression for cache identity, comparison, diagnostics, and telemetry; semantic normalization never rewrites the user's saved text.

Implicit `AND` must be inserted by the parser from token boundaries, not by splitting the input string on spaces. This preserves quoted values, parentheses, source spans, and useful incomplete-input diagnostics.

## Type system and evaluation

### Standard field types

The core engine supplies reusable descriptors for:

- text;
- integer and decimal numbers;
- boolean;
- enum;
- named entity backed by a stable internal key;
- set of enum or named-entity values;
- date/time, age, and duration;
- quantity and currency specializations with appropriate formatting.

Types define permitted operators, literal parsing, equality, ordering, display formatting, examples, and completion behavior. Consumers bind typed fields; they do not receive raw strings to interpret.

### Three-valued evidence

Field access returns `Known(value)` or `Unknown(reason)`. Unknown is distinct from a field being unavailable:

- **Unavailable** is a compile-time context error: this record type cannot answer the field.
- **Unknown** is a per-record value: the context supports the field, but this observation lacks evidence.

Evaluation uses three-valued logic (`true`, `false`, `unknown`). Only `true` records pass the filter. Negating an unknown result remains unknown, so `-tradable` cannot accidentally include items whose tradability was never observed. Users can deliberately select incomplete records with `unknown(tradable)`.

This matters especially for market snapshots, retainer observations, and partially loaded game data: absence of evidence must not become evidence of the opposite state.

### Evaluation purity

Compiled predicates are synchronous and side-effect free. Accessors read a supplied snapshot; they cannot refresh data, issue network requests, call Dalamud UI, or mutate records. A product must gather required evidence before filtering.

This makes evaluation deterministic, cheap enough for per-frame UI use, and testable outside the game process.

## Canonical FFXIV vocabulary

This section defines the initial catalog shape. The companion [Canonical FFXIV Filter Vocabulary](filter-vocabulary.md) provides field-by-field semantics, value behavior, context availability, and worked expressions intended for adopters.

Canonical keys are fully qualified. Concise aliases are global conveniences, not separate fields. The initial catalog should be deliberately useful rather than encyclopedic and should grow from demonstrated consumers.

| Canonical field | Short form | Type | Meaning |
| --- | --- | --- | --- |
| `item.name` | `item`; `name` when unambiguous | named item | localized item name backed by a stable internal item key |
| `item.itemLevel` | `ilvl` | integer | FFXIV item level |
| `item.equipLevel` | `level`, `lvl` | integer | required equip level |
| `item.job` | `job`, `class` | named set | jobs eligible to use or equip the item |
| `item.slot` | `slot` | enum set | applicable equipment slot |
| `item.rarity` | `rarity` | enum | normalized item rarity |
| `item.uiCategory` | `category` | named entity | user-facing FFXIV item UI category |
| `item.unique` | `unique` | boolean | unique-item flag |
| `item.tradable` | `tradable` | boolean | may be traded or listed where applicable |
| `item.desynthesizable` | `desynth` | boolean | item is eligible for desynthesis |
| `instance.quality` | `quality` | enum | NQ or HQ quality state of an observed instance or represented offer |
| `instance.quantity` | `quantity` when unambiguous | quantity | amount represented by a physical item stack |
| `instance.location` | `location` when unambiguous | named entity | inventory, retainer, armoury, saddlebag, and similar location |
| `instance.equipped` | `is:equipped` | boolean predicate | instance is currently equipped |
| `instance.condition` | `condition` | percentage | item condition |
| `instance.spiritbond` | `spiritbond` | percentage | spiritbond progress |
| `ownership.owned` | `owned` | boolean | at least one matching instance exists in the observed ownership scope |
| `ownership.quantity` | unqualified when unambiguous | quantity | total amount across the observed ownership scope |
| `ownership.character` | `character` when unambiguous | named set | characters contributing ownership evidence |
| `ownership.retainer` | `retainer` | named set | retainers contributing ownership evidence |
| `offer.source` | `source` when unambiguous | enum | source of the represented purchase offer |
| `offer.price` | `price` when unambiguous | currency | gil per unit for a vendor or market offer |
| `offer.totalPrice` | `totalPrice` | currency | total gil for the represented offer quantity |
| `offer.quantity` | unqualified when unambiguous | quantity | amount available in the represented offer |
| `offer.world` | `world` when unambiguous | named world | world on which the offer is available |
| `offer.dataCenter` | `dataCenter` when unambiguous | named entity | data center derived from the offer world |
| `offer.region` | `region` when unambiguous | enum | FFXIV region derived from the offer world |
| `offer.age` | `age` when unambiguous | duration | time since the offer evidence was observed |
| `acquisition.source` | `source` when unambiguous | enum set | known ways the item can be obtained, such as vendor, market, craft, gather, retainer, or quest |

The standard catalog does not expose `item.id` as an ordinary filter field. Stable IDs remain available to resolvers and bound records internally.

Aliases require careful review because they occupy global language space. Prefer a canonical leaf such as `price` over registering a global alias when another credible domain may later use the same word. A consumer needing a different price concept defines an accurately qualified canonical or extension field; it does not redefine `offer.price`. Unqualified leaf resolution does not create an alias and must fail when more than one active binding has that leaf.

### Context availability

A context is a named, versioned set of bindings over the canonical catalog. Example contexts might be `ffxiv.item-instances`, `ffxiv.purchase-offers`, `ffxiv.equipment-candidates`, and `ffxiv.recipes`. These are data shapes, not product names.

An inventory-item context could bind `item.name`, `instance.quality`, `instance.quantity`, and `instance.location` while leaving `offer.price` unavailable. An outfitter can compose equipment and purchase evidence into a richer record and bind both `item.job` and `offer.price` without creating an Outfitter-specific meaning for either field.

Contexts declare:

- which canonical fields are bound;
- typed accessors returning known or unknown evidence;
- the default text fields for unqualified search;
- the stable context ID and schema version used by caches and saved-filter validation;
- optional value providers for completion and name resolution;
- a short user-facing description of the records being filtered.

Representative context shapes make the reuse boundary concrete:

| Context shape | Typical canonical bindings | Credible consumers |
| --- | --- | --- |
| item instances | item identity, quality, stack quantity, location, condition, equipped state, owner | inventory browsers, retainer tools, Squire cleanup rules |
| ownership summaries | item identity, total owned quantity, characters, retainers, locations | Outfitter, restock planning, workshop material availability |
| purchase offers | item identity, offer quantity, unit price, source, world, age | market acquisition, Outfitter sourcing, restock costing |
| equipment candidates | item identity, item/equip level, jobs, slots, stats, ownership, best offer | Squire Outfitter, gear comparison, retainer equipment planning |
| recipes | result item, crafting job, recipe level, ingredients, unlock state | Craft Architect, workshop planning, acquisition-source analysis |

Composite records bind the union of fields they actually contain. Composition does not merge or nest language contexts at evaluation time, and it does not grant one field several meanings. If composition introduces two fields with the same leaf—such as `instance.quantity` and `offer.quantity`—the shorthand becomes ambiguous and the compiler requires a qualified field name.

### Extensions and promotion

A genuinely product-specific concept may be registered under an explicit namespace, such as `mmf.routeStatus`. Extensions use the same type system, diagnostics, completion, and documentation metadata as canonical fields.

Extensions are the exception. A proposed field should first be tested against the question: does this describe FFXIV data or only one product's workflow? Shared domain concepts enter `Franthropy.FFXIV.Filtering` immediately. Product workflow state remains namespaced until another credible consumer demonstrates a canonical meaning, at which point it can be promoted with a migration alias.

Contexts cannot shadow canonical keys or aliases.

## API shape

The following illustrates responsibilities rather than fixing final C# names:

```csharp
var catalog = FfxivFilterCatalog.Default;

var context = FilterContext<InventoryRow>
    .Create("ffxiv.inventory-items", catalog, schemaVersion: 1)
    .Bind(FfxivFields.ItemName, row => Known(row.ItemName))
    .Bind(FfxivFields.InstanceQuality, row => Known(row.IsHighQuality ? Quality.Hq : Quality.Nq))
    .Bind(FfxivFields.InstanceQuantity, row => Known(row.Quantity))
    .Bind(FfxivFields.InstanceLocation, row => row.Location is null
        ? Unknown<InventoryLocation>("Location was not observed.")
        : Known(row.Location))
    .UseDefaultText(FfxivFields.ItemName)
    .Build();

FilterCompilation<InventoryRow> result =
    FilterCompiler.Compile("darksteel quality:HQ quantity>=20", context);

IReadOnlyList<InventoryRow> visible = result.Success
    ? rows.Where(result.Predicate).ToList()
    : rows;
```

Key public contracts should include:

- `FilterSyntaxTree` with immutable nodes and exact source spans;
- `FilterDiagnostic` with code, severity, message template, span, suggested fixes, and documentation key;
- `FilterCatalog` and immutable `FilterFieldDescriptor` metadata;
- `FilterContext<TRecord>` and typed `FilterFieldBinding<TRecord, TValue>`;
- `FieldEvidence<TValue>` for known and unknown values;
- `FilterCompilation<TRecord>` containing syntax, semantic model, predicate, diagnostics, and normalized expression;
- `FilterCompletionService` returning replacement spans and typed suggestions;
- `FilterReferenceModel` generated from a catalog plus context availability;
- `FilterFormatter` for normalization and optional expanded-precedence display.

Registration builders perform all validation up front: duplicate keys, alias collisions, incompatible types, missing documentation, and invalid context bindings fail during startup or tests rather than during user input.

## Diagnostics and intrinsic verbiage

Franthropy owns diagnostic codes and default English wording. Consumers may provide localization resources or surrounding UI labels, but they must not rewrite language errors independently.

Required diagnostic classes include:

| Situation | Example wording |
| --- | --- |
| unknown field | `Unknown field 'ilevel'. Did you mean 'ilvl'?` |
| unavailable field | `'price' is valid, but purchase offers are not available while filtering local inventory.` |
| ambiguous shorthand | `'quantity' could mean 'instance.quantity' or 'offer.quantity' here. Choose the field you intend.` |
| missing value | `Expected a value after 'job:'.` |
| wrong value type | `'many' is not a valid quantity.` |
| invalid operator | `'contains' is not valid for item level. Use :, =, !=, <, <=, >, or >=.` |
| ambiguous name | `'Ring' matches multiple item categories. Choose one of the suggested values.` |
| unresolved name | `No FFXIV world named 'Siern' was found. Did you mean 'Siren'?` |
| incomplete range | `A range needs at least one endpoint.` |
| complexity limit | `This filter is too deeply nested to evaluate safely.` |

Diagnostics use source spans so an ImGui editor, browser editor, log, or command-line surface can highlight the exact term. Error messages should describe the user's expression and recovery, not tokenizer or AST implementation details.

Partial input is normal while typing. The parser should preserve incomplete nodes and produce recoverable diagnostics rather than throwing or discarding the entire tree. Completion consumes that partial semantic model.

## Completion, editor, and help surfaces

`Franthropy.Filtering` supplies editor-neutral completion and reference models. UI packages render them:

- `Franthropy.Dalamud.UI.Filtering` provides an ImGui input, completion popup, error indicator, and compact contextual help.
- Hosted .NET or web consumers can serialize completion and diagnostic models or render them with their own frontend.

Completion is syntax-aware:

- at the beginning of a term, suggest available fields and default-text values;
- after `job:`, suggest jobs rather than every value in the catalog;
- after a numeric field, suggest valid operators and representative ranges;
- inside a list, avoid already-selected values where duplication is meaningless;
- for an unavailable field, explain availability rather than hiding that the language supports it elsewhere.

The generated reference should show:

- the stable field name and aliases;
- description and value type;
- supported operators;
- availability in the active context;
- representative examples;
- named values and aliases when the set is reasonably small.

Healthy filters stay visually quiet. Syntax help and full diagnostics appear progressively; the common path remains a compact search input with useful completion.

## Persistence, compatibility, and versioning

Saved filters store the original expression plus optional normalized text, catalog version, context ID, and context schema version. The expression remains the source of truth so users can read and edit it.

Compatibility rules:

- canonical keys and aliases are stable public API;
- adding a field or non-conflicting value alias is backward compatible;
- removing or changing a field meaning requires a major catalog version and migration diagnostic;
- renamed fields retain deprecated aliases for at least one major version;
- a context schema change invalidates compiled predicates but should not invalidate expressions unless availability changed;
- unknown future fields fail explicitly rather than being ignored by older clients.

The formatter may normalize whitespace and operator spelling, but it must not rewrite user input automatically unless the user accepts a fix or saves a normalized expression. Original source spans and intent matter during editing.

## Performance and safety

The target workload is interactive filtering over hundreds to tens of thousands of snapshot records without visible frame-time churn.

- Tokenize, parse, bind, and compile only when the expression, context schema, catalog, or locale changes.
- Cache compiled predicates by normalized expression, catalog version, context ID, context schema version, and locale.
- Pre-resolve named values during binding; do not perform string-to-ID searches for every record.
- Accessors should avoid allocations in the per-record path.
- Completion providers may be asynchronous and cancellable, but predicate evaluation is synchronous.
- Enforce configurable limits for query length, token count, nesting depth, list length, and diagnostic count.
- Do not execute regex, reflection, dynamic code, network calls, or game UI operations from query text.
- Measure parse, bind, and evaluation costs independently in benchmarks before adopting the first in-game consumer.

## Project structure

The preferred end state is:

```text
src/
  Franthropy.Filtering/
    Syntax/
    Semantics/
    Evaluation/
    Completion/
    Documentation/
  Franthropy.FFXIV/
    Filtering/
      FfxivFilterCatalog.cs
      Fields/
      Values/
      Resolution/
  Franthropy.Dalamud/
    Filtering/
      DalamudFfxivValueProviders.cs
    UI/Filtering/
      DalamudFilterEditor.cs
tests/
  Franthropy.Filtering.Tests/
  Franthropy.FFXIV.Tests/
  Franthropy.Dalamud.Tests/
```

`Franthropy.Filtering` and `Franthropy.FFXIV` should target ordinary supported .NET runtimes and remain usable by services, test projects, and desktop tools. `Franthropy.Dalamud` adds live game-data and ImGui adapters without becoming the only way to consume the language.

This split can be introduced incrementally, but the generic parser must not begin life inside `Franthropy.Dalamud`; extracting it later would make Dalamud assumptions part of its public contracts.

## Testing strategy

### Conformance tests

A shared corpus should describe input, tokens, AST, normalized expression, diagnostics, and evaluation results. Every frontend and context implementation consumes the same corpus where applicable.

Coverage must include:

- precedence and implicit `AND`;
- quoting, escaping, punctuation, and Unicode item names;
- incomplete expressions at every token boundary;
- exact diagnostic spans and fix suggestions;
- alias collision and registration failures;
- unavailable versus unknown evidence;
- three-valued negation and boolean composition;
- numeric boundaries, ranges, currency, percentages, and durations;
- ambiguous and localized named-value resolution;
- complexity limits and hostile input;
- normalized-expression round trips;
- cache invalidation by catalog, context, schema, and locale;
- allocation and throughput budgets for compiled evaluation.

### Vocabulary contract tests

Canonical fields require snapshot tests for their keys, aliases, descriptions, types, operators, and examples. This makes accidental language drift a review-visible change.

Every context should have a contract test proving its bindings are type-compatible and documenting unavailable fields relevant to that surface. Consumers test only record mapping and workflow behavior; they should not duplicate parser semantics tests.

### Cross-surface tests

The same expression and evidence fixture should produce the same result in a pure .NET test, a Dalamud context, and any hosted MMF context. This is the proof that Franthropy provides one language rather than similarly named parsers.

## Delivery plan

### Phase 1: language kernel

Create `Franthropy.Filtering` with immutable syntax trees, source spans, tokenizer, parser, formatter, diagnostics, primitive types, three-valued evaluation, context binding, and conformance tests. Use a small synthetic catalog to stabilize semantics before adding FFXIV breadth.

Exit criteria: the grammar, incomplete-input behavior, diagnostic model, and evaluation truth tables are documented and exhaustively tested without a Dalamud dependency.

### Phase 2: canonical FFXIV catalog

Create `Franthropy.FFXIV` with the initial field catalog, global aliases, named value types, and name-first resolvers for jobs, slots, quality, worlds, regions, locations, and acquisition sources. Add catalog snapshot tests and generated reference output.

Exit criteria: aliases cannot collide, standard documentation is generated, and the same vocabulary can be bound by two different synthetic contexts.

### Phase 3: first real contexts

Adopt two deliberately different consumers to prove the abstraction: one inventory/equipment context and one market or acquisition context. Their overlap should demonstrate that fields retain meaning while availability changes.

MarketMafioso is a useful proving ground because its existing search boxes cover workshop projects, materials, retainers, Outfitter candidates, and market evidence. Migration should begin with a read-only list filter, not a rule that triggers automation.

Exit criteria: both contexts use the shared compiler, share common expressions, explain unavailable fields correctly, and contain no consumer-local token parsing.

### Phase 4: shared editors

Add `Franthropy.Dalamud.UI.Filtering` and a transport-neutral editor model for hosted surfaces. Implement completion, exact-span diagnostics, compact help, filter history, and generated active-context reference.

Exit criteria: the in-game and hosted editors consume identical diagnostics and completions from the core, and visual testing confirms the input remains compact during ordinary use.

### Phase 5: saved filters and broader migration

Add persisted filter envelopes, compatibility diagnostics, context validation, and migration helpers. Migrate suitable ad hoc searches and rule conditions incrementally; do not force the language into simple selectors where a dropdown or direct manipulation is clearer.

Exit criteria: saved expressions survive catalog additions and field renames according to the compatibility rules, and at least three distinct workflows reuse the language without local dialects.

## Explicit non-goals

- Replacing every search box, selector, or checkbox with query text.
- Encoding actions, automation steps, routing, or mutation in expressions.
- Fetching missing evidence during predicate evaluation.
- Exposing raw FFXIV row IDs as the normal user interface.
- Supporting regex in V1.
- Letting consumers redefine canonical aliases or operator semantics.
- Creating a general-purpose programming language, SQL implementation, or LINQ replacement.
- Treating a generated query as inherently safe authorization for an action.

## Decisions recorded by this design

1. The filter language is shared Franthropy infrastructure, not a MarketMafioso or Squire subsystem.
2. Canonical FFXIV terms belong to a shared vocabulary rather than arbitrary modules.
3. Contexts bind available evidence; they do not own or redefine language terms.
4. Generic parsing remains independent of Dalamud so hosted and offline tools can use it.
5. Names are user-facing and IDs remain internal.
6. Unknown evidence uses three-valued logic and never silently becomes false evidence under negation.
7. Diagnostics, completion metadata, and base English verbiage are intrinsic to Franthropy.
8. Documentation is generated from registered language metadata.
9. Evaluation is pure over snapshots and cannot perform automation or I/O.
10. Product extensions are namespaced, cannot shadow canonical vocabulary, and may graduate when a shared meaning is demonstrated.
11. Item obtainability uses `acquisition.source`, while `offer.source` identifies one represented purchase opportunity; Franthropy does not duplicate craft or vendor availability as separately bound item booleans.

## Questions to settle during Phase 1

These questions affect surface polish but do not change the architecture:

- Whether the normalized display form prefers word operators (`AND`, `OR`, `NOT`) or symbols while accepting both.
- Whether comma is accepted as an additional list separator without conflicting with localized numeric input.
- Whether percentage literals accept both `condition<80` and `condition<80%` or normalize to one documented form.
- Which diagnostic severities should allow evaluation of a valid partial subtree during live typing.
- Whether saved-filter envelopes belong in `Franthropy.Filtering` or a small optional persistence companion package.

They should be resolved through parser prototypes and editor testing, then incorporated into the conformance corpus before a consumer persists production expressions.
