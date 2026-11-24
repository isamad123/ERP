# Packs (slice S1.23b)

One folder per pack, named by the pack id (`packs/country-pk/`, S1.23c). A folder holds:

- `pack.json`: `format` (1), `id`, `kind` (`country`, `industry`, `template`, `language`), `version` (`1.0.0`), `country` (a country
  pack only, two capital letters), `entitlement` (the licence key, for example `pack.country.pk`), `title` and `releaseNotes`
  (`{ "en": ..., "ur": ... }`), `compatible` (`{ "from": "1.0", "to": null }`), `dependencies` (`[{ "id", "from" }]`).
- `objects/**/*.json`: one file per object: `{ "type", "key", "source", "verify": "verify before go-live", "value" }`. Every file
  names where its content comes from. Object types and their values are listed in `docs/registers/contracts.md` (pack object
  handler contract): `setting`, `label`, `option_list`, `option_list_item`, `holiday_calendar`, `holiday`, `bank`, `policy`.
- `manifest.json`: written by `pack build` (the hash of every object file and a merge rule per type); never edited by hand.

No rate, slab, threshold, statute or statutory form goes into a pack before its slice and a practising professional's validation
(AS-4, R-002): `pack build` refuses those object types. Building, signing and publishing: `docs/runbooks/tenant-operations.md`.
