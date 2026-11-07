# BrokerForge — Operation Descriptor Specification

Version 0.2 · Source of truth for `BrokerForge.Descriptors` (parser, validator, renderer, response mapper).

An operation descriptor describes one call to a source: how to build the request from Dataverse inputs, and how to turn the response back into Dataverse rows, records or outputs. One descriptor per file, under `descriptors/<ServiceInstance>/<operation>.yaml`.

## 1. Top-level structure

```yaml
operation: listOrders            # required, unique within the service instance, camelCase
version: 1                       # required, integer, bumped on any change
binds: RetrieveMultiple          # required, see 2
table: ext_order                 # required for virtual table messages; omitted for Custom APIs
request:   { ... }               # required, see 4
response:  { ... }               # required except where the message returns nothing, see 6
paging:    { ... }               # required for RetrieveMultiple, see 7
errors:    { ... }               # optional, see 8
steps:     [ ... ]               # optional, see 9
hook:      { ... }               # optional, see 10
cache:     { ... }               # optional, see 11
retry:     { ... }               # optional, see 11
optionsets: { ... }              # optional, see 6 (option set maps)
filters:   { ... }               # optional, see 5: allowOr (bool, default false), orStyle (only `in`)
sortStable: true                 # required for RetrieveMultiple, see 5
```

Unknown keys anywhere are a validation error, not ignored.

**Scope.** The parser rejects `steps`, `hook` and `jsonata` map entries with "not supported until a later phase", and `cache` with "not supported until phase 5". `customapi:` binds are supported from phase 2 for unbound Custom APIs only: an `id` binding in a Custom API descriptor is rejected.

## 2. `binds`

| Value | Inputs available | Must return |
| --- | --- | --- |
| `RetrieveMultiple` | query columns, filter values, sort, page | `response.rows`, `response.key` |
| `Retrieve` | `id` (decoded external key) | `response.rows` (single), `response.key` |
| `Create` | record columns | `response.key` (new external key) |
| `Update` | `id`, changed record columns only | nothing |
| `Delete` | `id` | nothing |
| `customapi:<UniqueName>` | Custom API input parameters | `response.outputs` |

A Custom API descriptor has no `table` and no `paging`. Its unique name must be unique across every service instance the host loads; two descriptors binding the same name stop the host at startup. The parameters and their types come from the table schema file's `customApis` block (section 14); every `param` binding must name a declared input, every declared input must be sent by a `param` binding (an input nothing sends would be accepted and dropped), and every declared output must appear in `response.outputs`.

## 3. Value bindings

Every value in path, query, headers or body is either a literal (YAML scalar) or a binding object with exactly one key from this table. No other sources exist.

| Binding | Example | Resolves to | Allowed in |
| --- | --- | --- | --- |
| `static` | `{ static: "2024-06" }` | The literal value | all |
| `instance` | `{ instance: tenantId }` | Key from `bf_serviceinstance` instance config | all |
| `secret` | `{ secret: partnerKey }` | Named Key Vault reference on the service instance, resolved in the host | headers, query only |
| `context` | `{ context: userObjectId }` | `userObjectId`, `correlationId`, `environmentId`, `async` | all |
| `col` | `{ col: ext_status }` | Record column value | Create, Update |
| `id` | `{ id: true }` or `{ id: orderId }` | Decoded external key, or a named part of a composite key | Retrieve, Update, Delete (entity-bound Custom APIs later, D-19) |
| `filter` | `{ filter: ext_status, ops: [eq, in] }` | Value from the query filter on that column | RetrieveMultiple query and path |
| `sort` | `{ sort: { ext_name: name, createdon: created }, descFormat: "-{field}" }` | Source sort field for the requested column. `descFormat` renders a descending sort; without it a descending sort is `UnsupportedQueryException` | RetrieveMultiple query |
| `param` | `{ param: Reason }` | Custom API input parameter | Custom API |
| `computed` | `{ computed: uuid }` | `uuid`, `utcnow` (ISO 8601) | all |
| `paging` | `{ paging: pageSize, max: 100 }` | `pageSize`, `token`, `offset`, `pageNumber` | RetrieveMultiple query |

Binding modifiers (optional, on the same object):

- `required: true` — missing value is `UnsupportedQueryException` (for filters) or a validation error at call time (for others). Default: required for `id`, `col` on Create where the column is required, `param` where the Custom API parameter is required.
- `omitIfNull: true` — drop the query parameter or header when the value is null. Default true for query and headers.
- `optionset: <mapName>` (on `col` only) — reverse-maps the Dataverse option value through the descriptor's option set map and sends the source value. A value with no mapping is a validation error naming the column. An option set column bound without this modifier is still refused.
- `format` — `iso8601`, `date`, `epochSeconds`, `epochMillis`, `lower`, `upper`, `csv` (for `in` arrays), `json` (body template leaves only: parses a JSON string column into a nested value).

`secret` is never allowed in body or path, so secrets cannot be echoed back by a source that reflects input.

### Path templates

Path parameters use `{name}` placeholders and a `pathParams` map:

```yaml
request:
  method: GET
  path: /v2/accounts/{accountId}/orders/{orderId}
  pathParams:
    accountId: { instance: accountId }
    orderId:   { id: true }
```

Every placeholder must have a `pathParams` entry and every entry must be used. Values are URL-encoded.

## 4. `request`

```yaml
request:
  method: POST                   # GET, POST, PUT, PATCH, DELETE
  path: /v2/accounts/{accountId}/orders
  pathParams: { accountId: { instance: accountId } }
  query:
    dryRun: { static: false }
  headers:
    X-Api-Version:   { static: "2024-06" }
    X-Correlation:   { context: correlationId }
    Idempotency-Key: { computed: uuid }
  body:
    contentType: application/json     # application/json (default), application/x-www-form-urlencoded, text/xml
    template:                         # structured template; leaves are literals or bindings
      customer: { col: ext_customerref }
      lines:    { col: ext_lines_json, format: json }
      placedBy: { context: userObjectId }
```

Rules:

- `Authorization` and any header the service instance's auth mode sets are reserved, and so is the query parameter `apiKeyQuery` sends, whether declared under `query` or as a paging parameter. Names compare case-insensitively. Declaring them is a validation error.
- GET and DELETE must not have a body.
- `body.template` is a YAML structure mirrored into JSON. A leaf with `format: json` parses a JSON string column into a nested value; invalid JSON at call time is `MappingException`.
- `body.raw` (alternative to `template`) is a string with `{{ binding }}` placeholders for XML and other formats. Placeholders use the same binding names: `{{ col.ext_status }}`, `{{ context.correlationId }}`. Values are XML-escaped when `contentType` is `text/xml`.
- On Update, only changed columns are bound. A `col` binding for an unchanged column is omitted unless `alwaysSend: true`.

## 5. Filters

Filters in a RetrieveMultiple query are honoured only if a `filter` binding declares the column and operator.

| Operator | Dataverse condition | Typical source form |
| --- | --- | --- |
| `eq` | Equal | `?status=open` |
| `ne` | NotEqual | `?status_ne=open` |
| `gt`, `ge`, `lt`, `le` | GreaterThan … LessEqual | `?created_from=` |
| `in` | In | `?status=open,closed` (`format: csv`) |
| `like` | Like (begins with only) | `?q=` |
| `null` | Null / NotNull | `?hasCustomer=false` (not supported in phase 1: `UnsupportedQueryException`) |

Rules:

- A query condition on an undeclared column or operator raises `UnsupportedQueryException` naming both. The host never filters client-side.
- `or` groups are rejected unless the descriptor declares `filters.allowOr: true` and the source supports it via `filters.orStyle` (only `in`-collapsible ORs on one column are supported in v1).
- Link-entity (joined) conditions are rejected in v1.
- A binding receives at most one condition. Two conditions for the same parameter are `UnsupportedQueryException`.
- When the filtered column's `response.map` entry has `type: optionset:<map>`, the Dataverse value is reverse-mapped through that map before binding. A value with no mapping is `UnsupportedQueryException`.
- `like` is begins-with only; a value containing `%` or `_` is `UnsupportedQueryException`.
- One sort column per query. More is `UnsupportedQueryException`.
- Sorting on an undeclared column raises `UnsupportedQueryException`. A query with no sort uses the source's default order; the descriptor must declare `sortStable: true|false` so paging behaviour is known.

## 6. `response`

```yaml
response:
  rows: $.data[*]                # JSONPath to the row array (RetrieveMultiple) or single object (Retrieve)
  key: $.id                      # JSONPath to the external key, or a map for composite keys
  map:                           # column logical name -> JSONPath (or transform)
    ext_name:   $.reference
    ext_status: $.state.code
    ext_total:  { path: $.totals.gross, type: decimal }
    ext_placed: { path: $.placedAt, type: datetime }
    ext_note:   { path: $.note, optional: true }
    ext_label:  { jsonata: "$uppercase(state.code) & ' / ' & reference" }
  outputs:                       # Custom API only: output parameter -> JSONPath
    SubmittedAt: $.submittedAt
```

`outputs` entries take the same forms as `map` entries (a path, or `{ path, type, optional }`), with `type` limited to the Custom API parameter types: `string`, `int`, `decimal`, `bool`, `datetime`, `guid`. A missing output path is `MappingException` naming the output unless `optional: true`.

Rules:

- `rows` matching nothing returns an empty set only when the path exists and is an empty array. A missing path is `MappingException`. Retrieve and RetrieveMultiple both require `rows`.
- Within a row, JSON `null` maps to null. A map path that matches nothing is `MappingException` unless the entry has `optional: true`, which maps it to null.
- Every column in the table's generated schema must appear in `map` or be explicitly listed under `response.unmapped: [ ... ]`. Silent gaps are a validation error.
- `type` coerces: `string`, `int`, `decimal`, `bool`, `datetime`, `date`, `guid`, `optionset:<mapName>`, `json`. `json` serialises the matched JSON value (object, array or scalar) to a compact JSON string, for memo columns that hold a nested structure. Coercion failure is `MappingException` naming column and path. `money` is refused: virtual tables cannot have Money columns, so amounts use `decimal`.
- Composite keys: `key: { accountId: $.accountId, orderId: $.id }`. The object map's key strategy must be `keymap` or reversible if it fits.
- Create must return a key. If the source returns 201 with a `Location` header only, use `key: { header: Location, pattern: "/orders/(?<id>[^/]+)$" }`.

### Option set maps

```yaml
optionsets:
  orderStatus:
    open: 100000000
    shipped: 100000001
    cancelled: 100000002
    _unknown: error            # error (default) | null
```

An unmapped source value is `MappingException` unless `_unknown: null`.

## 7. `paging` (RetrieveMultiple)

```yaml
paging:
  strategy: cursor               # cursor | offset | pageNumber | link | none
  pageSize: { param: limit, max: 100 }
  token:    { param: cursor, next: $.meta.nextCursor }
```

| Strategy | Request | Next page detection |
| --- | --- | --- |
| `cursor` | `token.param` = previous `next` | `token.next` JSONPath non-null |
| `offset` | `offset.param` = page × size | returned row count = page size, or `total` path |
| `pageNumber` | `page.param` = page index (base declared by `page.base: 0|1`) | as offset |
| `link` | URL from previous response | `Link` header `rel="next"` |
| `none` | one call | never; requesting page 2 is `UnsupportedQueryException` |

The host translates Dataverse paging cookies to and from the source token. If the source page size is smaller than Dataverse requested, the host makes further calls to fill the page, up to `paging.maxCallsPerPage` (default 5). When the budget runs out first, the host returns the rows collected with a next token: a complete page with more to follow, never a short page presented as the end.

The page token the host hands to Dataverse names the service instance, table, operation and descriptor version and is signed with the host's page token key (HMAC-SHA256, `Host:PageTokenKeyRef`). A token for another instance, table or operation, an older version or a different filter, or a token that is corrupt or unsigned, is `UnsupportedQueryException`. A `link` URL is followed only when it has the service instance base URL's scheme, host and port and exactly the operation's request path; only its query may differ.

A source page the host cannot track is `SourceException`: more rows than the page size asked for, an empty page while `total` says rows remain, the same cursor twice, or the same key twice within one Dataverse page.

## 8. `errors`

```yaml
errors:
  404: NotFound
  409: Conflict
  422: { exception: Validation, message: $.error.detail }
  default: Fail
```

| Value | Raised as |
| --- | --- |
| `NotFound` | Dataverse record-not-found |
| `Conflict` | Dataverse duplicate/concurrency error |
| `Forbidden` | Dataverse privilege error |
| `Validation` | Dataverse invalid-argument error, message from the JSONPath if given (redacted, max 500 chars) |
| `Fail` | `SourceException` with status and correlation ID |

`default` must be `Fail`. Any status not listed is `SourceException`. 2xx responses are success unless `errors.successBodyCheck` declares a JSONPath and expected value for APIs that return errors inside 200.

## 9. `steps` (multi-call operations)

For a lookup before the main call, or async 202 polling. Maximum 3 steps in v1.

```yaml
steps:
  - name: resolveAccount
    request:
      method: GET
      path: /v2/accounts
      query: { ref: { col: ext_accountref } }
    capture:
      accountId: $.data[0].id          # missing capture is MappingException
  - name: main                         # the request block of the descriptor, with step captures available as { step: resolveAccount.accountId }
  - name: poll
    when: { status: 202 }
    request:
      method: GET
      path: "{location}"
      pathFrom: { header: Location }
    until: { path: $.status, equals: complete }
    every: 2s
    timeout: 60s                       # must leave headroom inside the 90s host limit
```

## 10. `hook` (escape hatch)

```yaml
hook:
  pre: BrokerForge.Hooks.Orders.SignRequest       # IPreRequestHook implementation
  post: BrokerForge.Hooks.Orders.FlattenLines     # IPostResponseHook implementation
  reason: "Partner API needs a body hash header computed over the canonical JSON"
```

`reason` is required. Hooks receive the rendered request or raw response and may not read secrets directly; they use the same `IAuthenticatedChannel`.

## 11. `cache` and `retry`

```yaml
cache:
  ttl: 60s                 # off when absent
  perUser: true            # forced true when the service instance uses a per-user mode
retry:
  idempotent: true         # required true for automatic retry; Create and any POST need an Idempotency-Key binding
  attempts: 3
  on: [429, 503, network]
```

## 12. Validation rules summary

The validator rejects a descriptor when any of these hold:

1. Unknown key, or a binding object with zero or more than one source key.
2. A binding source not allowed for the field (table in 3) or for the message (`binds`).
3. A path placeholder without a `pathParams` entry, or an unused entry.
4. A reserved header or query parameter declared.
5. GET or DELETE with a body.
6. RetrieveMultiple without `paging`, Retrieve/RetrieveMultiple without `response.rows`, or Retrieve/RetrieveMultiple/Create without `response.key`.
7. With the table schema (section 14): a table column neither mapped nor listed in `response.unmapped` on Retrieve and RetrieveMultiple; a mapped, bound, filtered or sorted column that is not in the schema; an option set map that does not cover exactly the schema column's options; a Custom API `param` or output not declared in the schema.
8. `errors.default` other than `Fail`.
9. `retry` without `idempotent: true`, or retry on Create or any POST without an `Idempotency-Key: { computed: uuid }` header.
10. A `hook` without `reason`.
11. `steps` over 3, or a poll timeout leaving under 20 seconds of the 90-second host limit.
12. `secret` used in path or body.

## 13. Worked examples (Orders mock API)

### List

```yaml
operation: listOrders
version: 1
binds: RetrieveMultiple
table: ext_order
request:
  method: GET
  path: /v2/orders
  query:
    status:   { filter: ext_status, ops: [eq, in], format: csv }
    customer: { filter: ext_customerref, ops: [eq] }
    sort:     { sort: { ext_name: reference, ext_placed: placedAt }, descFormat: "-{field}" }
paging:
  strategy: cursor
  pageSize: { param: limit, max: 100 }
  token:    { param: cursor, next: $.meta.nextCursor }
response:
  rows: $.data[*]
  key: $.id
  map:
    ext_name:        $.reference
    ext_status:      { path: $.state.code, type: optionset:orderStatus }
    ext_customerref: $.customer.ref
    ext_placed:      { path: $.placedAt, type: datetime }
optionsets:
  orderStatus: { open: 100000000, shipped: 100000001, cancelled: 100000002 }
errors:
  default: Fail
sortStable: true
```

### Retrieve

```yaml
operation: getOrder
version: 1
binds: Retrieve
table: ext_order
request:
  method: GET
  path: /v2/orders/{orderId}
  pathParams: { orderId: { id: true } }
response:
  rows: $
  key: $.id
  map:
    ext_name:        $.reference
    ext_status:      { path: $.state.code, type: optionset:orderStatus }
    ext_customerref: $.customer.ref
    ext_placed:      { path: $.placedAt, type: datetime }
optionsets:
  orderStatus: { open: 100000000, shipped: 100000001, cancelled: 100000002 }
errors:
  404: NotFound
  default: Fail
```

### Create

```yaml
operation: createOrder
version: 1
binds: Create
table: ext_order
request:
  method: POST
  path: /v2/accounts/{accountId}/orders
  pathParams: { accountId: { instance: accountId } }
  headers:
    X-Correlation:   { context: correlationId }
    Idempotency-Key: { computed: uuid }
  body:
    template:
      customer: { ref: { col: ext_customerref } }
      lines:    { col: ext_lines_json, format: json }
      placedBy: { context: userObjectId }
response:
  key: $.id
  unmapped: [ext_name, ext_status, ext_placed]
errors:
  409: Conflict
  422: { exception: Validation, message: $.error.detail }
  default: Fail
retry:
  idempotent: true
  attempts: 3
  on: [429, 503, network]
```

### Custom API

```yaml
operation: submitOrder
version: 1
binds: customapi:ext_SubmitOrder
request:
  method: POST
  path: /v2/orders/{orderId}/submit
  pathParams: { orderId: { param: OrderId } }
  headers:
    X-Reason: { param: Reason, omitIfNull: true }
  body:
    template:
      notify: { param: NotifyCustomer }
response:
  outputs:
    SubmittedAt: { path: $.submittedAt, type: datetime }
    Status:      $.state.code
errors:
  404: NotFound
  409: Conflict
  default: Fail
```

The Custom API `ext_SubmitOrder` is generated with inputs `OrderId` (string, required), `Reason` (string, optional), `NotifyCustomer` (bool, required) and outputs `SubmittedAt` (datetime), `Status` (string).

## 14. Table schema files

Each service instance has one schema file, `schemas/<ServiceInstance>.yaml`, describing the virtual table and the Custom APIs its descriptors bind. The validator (rule 7), the host (Custom API parameter checks) and the generator read it. SwaggerForge drafts it.

```yaml
table: ext_item                 # logical name; the prefix comes from here
displayName: Item
displayCollectionName: Items
externalName: item
primaryName: ext_name           # string column used as the primary name
columns:
  ext_name:   { type: string, maxLength: 100, displayName: Name }
  ext_onhand: { type: int, displayName: On hand }
  ext_price:  { type: decimal, precision: 2, displayName: Price }
  ext_state:  { type: optionset, options: { active: 100000000, discontinued: 100000001 }, displayName: State }
customApis:
  ext_AdjustStock:
    displayName: Adjust stock
    inputs:  { ItemId: { type: int, required: true }, Delta: { type: int, required: true }, Reason: { type: string } }
    outputs: { OnHand: int, AdjustedAt: datetime }
```

Rules:

- Unknown keys are an error, as in descriptors.
- Column types: `string` (needs `maxLength`), `memo`, `int`, `decimal` (needs `precision`), `bool`, `datetime`, `date`, `optionset` (needs `options`). `money` is refused. The primary key column `<table>id` is implicit and is not listed.
- `primaryName` must name a `string` column.
- Every column and Custom API name uses the same prefix as the table (with no table, as the first Custom API). The generator creates a source under one publisher, so one prefix covers it.
- Option values are integers; the option labels match the source values used in the descriptors' option set maps.
- Custom API parameter and output types: `string`, `int`, `decimal`, `bool`, `datetime`, `guid`. Inputs default to `required: false`.
- `customApis` and `table` are both optional, but a file must declare at least one of them.
