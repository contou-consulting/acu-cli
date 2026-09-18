# acu-cli

A command-line interface for [Acumatica ERP](https://www.acumatica.com/), designed to be
easy for both humans and AI agents to use. It works with any contract-based REST endpoint —
the **Default endpoint** that ships with Acumatica as well as **custom endpoints**, which
follow the same architecture. Authentication is **OAuth 2.0 only** (OpenID Connect).

Verified against a real Acumatica site's public API surface: the endpoint index
(`GET /entity`), the OpenAPI documents (`/entity/{endpoint}/{version}/swagger.json`),
and the identity provider's discovery document
(`/identity/.well-known/openid-configuration`).

## Layout

| Project            | Purpose                                                        |
| ------------------ | -------------------------------------------------------------- |
| `acu-cli`          | CLI entry point: commands, config, token store, schema cache   |
| `acu-cli.core`     | Acumatica REST/OAuth client library (bearer auth, CRUD, actions, OIDC) |

## Building

```bash
dotnet build
```

Run it directly during development:

```bash
dotnet run --project acu-cli -- <command> [options]
```

For day-to-day use, publish it and put it on your `PATH`:

```bash
dotnet publish acu-cli -c Release -o ./publish
```

## Getting started

1. In Acumatica, register an OpenID Connect application
   (**Integration > OpenID Connect Applications**) with a redirect URI of
   `http://localhost:8389/callback`.
2. Sign in:

```bash
# Non-interactive (recommended for agents and servers): client-credentials grant
acu login --url https://acumatica.contou.com --client-id <id> --client-secret <secret>

# Interactive (browser-based): authorization-code grant with PKCE
acu login --url https://acumatica.contou.com --client-id <id>
# add --no-browser to print the sign-in URL instead of opening one
```

Notes:

- `acu whoami` runs a full connectivity check (OAuth session + endpoint schema).
- `acu login` resolves the endpoint version automatically: it picks the newest version of
  the endpoint installed on the site (from the public `GET /entity` index) and validates
  it. Override with `--endpoint` / `--endpoint-version` (e.g. `--endpoint-version 25.200.001`).
- `acu logout` revokes the tokens and removes the saved session.
- Use `--insecure` for self-signed development certificates.

Files created under `~/.acu-cli/`:

| File | Contents |
| ---- | -------- |
| `config.json` | One connection profile per site (URL, endpoint, version, TLS) plus the active site |
| `auth.json`  | OAuth sessions (one per site: client app, access/refresh tokens, token endpoints; chmod 600) |
| `schema/`    | Cached endpoint swagger documents (`{endpoint}/{version}/swagger.json`) |

The client secret is stored in plaintext in `auth.json` (with a warning) so tokens can be
renewed non-interactively. Sessions and connection profiles are stored **per site**, so
you can be signed in to several sites at once — e.g. a production site and a sandbox —
and switch between them with `acu use`:

```bash
acu use                     # list saved sites (active is marked with *)
acu use contou             # switch by URL or a unique part of it
ACU_URL=<url> acu get ...  # override the active site for one command
```

Every command also accepts the settings directly (`--url`, `--client-id`,
`--client-secret`, `--endpoint`, `--endpoint-version`, `--insecure`) and environment
variables (`ACU_URL`, `ACU_CLIENT_ID`, `ACU_CLIENT_SECRET`, `ACU_ENDPOINT`,
`ACU_VERSION`, `ACU_INSECURE`). Precedence: options > environment > saved files.

### How tokens are kept fresh

- Expired **client-credentials** tokens are silently re-requested using the stored secret.
- Expired **authorization-code** tokens are refreshed via the stored refresh token
  (the default scope `api offline_access` requests one; override with `--scope`).
- When renewal is impossible, the command fails with a clear `run 'acu login' again` hint.

## Exploring an endpoint: `acu schema`

Endpoint schemas (swagger.json) are cached under `~/.acu-cli/schema/{endpoint}/{version}/` —
the same mechanism covers default and custom endpoints. All schema commands need **no
credentials**.

```bash
acu schema endpoints                            # endpoints + versions installed on the site
acu schema sync                                 # download + cache the endpoint's schema
acu schema list                                 # entities with screen + field/action counts
acu schema show StockItem                       # fields (with types) and actions of one entity
acu schema actions [SalesOrder]                 # actions of one entity or the whole endpoint
acu schema list --refresh                       # force a re-download
acu schema sync --endpoint MyCustom --endpoint-version 1.0.0   # custom endpoints work the same
```

Entity fields print their Acumatica value type (`StringValue`, `DecimalValue`,
`AttributeValue[]`, ...); every entity additionally has the base fields `id`, `rowNumber`,
`note`, `custom`, `error` and `files`.

## Working with data

All data commands authenticate with the stored OAuth bearer token and speak **YAML by
 default** — much cheaper for humans and AI agents to read and write than Acumatica's
 raw `{"value": ...}` JSON. Plain JSON is still accepted as input, and `--format json`
 (or `--format table`) switches output back.

### Output: flattened YAML

The API wraps every field: `"InventoryID": {"value": "X"}`. acu-cli flattens that to
 `InventoryID: X`, so responses stay small and readable:

```yaml
- id: 6413ca6c-8414-f011-92de-6045bdc7d881
  rowNumber: 1
  CustomerID: AMTRASH
  CustomerName: American Trash
  _links:
    self: /entity/Default/25.200.001/Customer/6413ca6c-8414-f011-92de-6045bdc7d881
```

A field that the server rejected gets a sibling error property:

```yaml
CustomerName: American Trash
CustomerNameError: 'Discount code is required.'
```

### `get` — list entities or fetch a record

```bash
acu get StockItem                                    # first 20 records (see --top)
acu get StockItem --top 0                            # all records
acu get StockItem --top 100 --skip 100               # paging
acu get StockItem AALEGO500                          # by key (composite keys: list values in order)
acu get Customer ACME "MAIN"                         # composite key
acu get StockItem --filter "InventoryID eq 'AALEGO500'"
acu get StockItem --select InventoryID,Description
acu get StockItem --expand SalesOrderDetails
acu get StockItem --custom UsrMyField                # fields not in the contract ($custom)
acu get StockItem --format table                     # compact table
acu get StockItem --format json                      # raw Acumatica JSON
```

Supported query options (per the API): `$filter`, `$select`, `$expand`, `$custom`,
`$top`, `$skip`. There is no `$orderby` in this API.

### `put` — insert or update (upsert)

`PUT` goes to the entity collection URL, so **key fields belong in the body**. Write
bodies in the same flattened YAML you see in output — acu-cli re-wraps them into the
API's `{"value": ...}` format for you:

```bash
acu put StockItem --file item.yaml
cat item.yaml | acu put StockItem
cat item.yaml | acu put StockItem --select InventoryID,Description   # trim the response
```

```yaml
# item.yaml — natural style (values are re-wrapped)
InventoryID: AALEGO500
Description: New description
Attributes:
  - AttributeID: COLOR
    Value: Red
```

The body may also be written in explicit style (`InventoryID: {value: AALEGO500}` —
objects with only `value`/`error` keys pass through untouched) or as plain JSON —
anything starting with `{` is parsed as JSON.

### `patch` — partial update

Updates only the fields present in the body of an existing record (identified by the key
fields in the body):

```bash
acu patch Customer --file note.yaml
cat note.yaml | acu patch Customer --select CustomerID,CustomerName   # trim the response
```

```yaml
# note.yaml
CustomerID: AMTRASH
note: updated via acu-cli
```

### `delete` — remove a record

```bash
acu delete StockItem AALEGO500
```

### `invoke` — call an action

Actions are `POST /{entity}/{action}` and expect a body with an `entity` property holding
the record's key fields (see `acu schema actions`). The same YAML input applies — nested
maps are recursed into, so a natural action body just works:

```yaml
# body.yaml
entity:
  OrderNbr: "000001"
```

```bash
acu invoke SalesOrder CancelSalesOrder --file body.yaml
acu invoke SalesOrder CancelSalesOrder          # no body needed? fine, {} is sent
```

### `raw` — escape hatch

Send any request with the stored OAuth token against the site (output is also YAML
unless you ask for `--format json`):

```bash
acu raw GET "entity/Default/25.200.001/StockItem?\$top=1"
acu raw PUT "entity/Default/25.200.001/StockItem/AALEGO500/files/report.pdf" --file report.pdf
```

### Agent mode

AI agents integrating acu-cli can add `--agent` (or set `ACU_AGENT=1`) to guard against
oversized responses: listings without `--select` are refused with the entity's field
count and example fields, e.g.

```
error: Agent mode requires --select for listings (Customer has 65 fields).
E.g. --select AccountRef,ApplyOverdueCharges,Attributes. See `acu schema show Customer`.
```

Key-based `get`s are exempt (single records). The flag is never persisted.

### Other commands

- `acu login` / `acu logout` — manage the OAuth session of one site (never touches other sites)
- `acu use [site]` — switch the active site, or list saved ones
- `acu whoami` — verify the session and endpoint
- `acu config` — show the effective connection settings (secrets are masked)

## Behavior notes

- Exit code `0` on success, `1` on error (details on stderr, data on stdout).
- Data output is flattened YAML by default; `--format json` returns raw Acumatica JSON
  and `--format table` a compact table. Input bodies accept YAML (recommended) or JSON.
- `get` listings default to 20 records; use `--top 0` for everything.
- Requests time out after 300 seconds (long Acumatica operations are common).
- Server error bodies use `{"message": ..., "exceptionMessage": ...}`; acu-cli surfaces
  the `exceptionMessage`. OAuth errors (`{"error": ..., "error_description": ...}`) are
  surfaced too.
- `--select` accepts contract fields **and base fields** (`id`, `rowNumber`, `note`,
  `custom`, `_links`): base fields are always returned by the API but rejected in
  `$select`, so acu-cli strips them from the request — `acu get Customer AMTRASH
  --select CustomerID,note` returns both. Selecting *only* base fields returns the
  full record (nothing can be trimmed server-side). Selecting a non-existent field
  still yields the server's HTTP 500.
- OAuth grant support depends on the identity provider: this CLI implements
  authorization-code (+ PKCE), client-credentials and refresh. The device-code flow is
  available on some sites but not implemented yet.
