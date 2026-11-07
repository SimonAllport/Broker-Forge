# Connection design format

A connection design is one JSON document per service instance. It holds everything the host needs to build an instance except secrets and per-environment values. DataverseToCode reads the same documents from unpacked solutions, so the format is stable: **format version 1; any breaking change bumps it.**

Code: `src/BrokerForge.Connections` (`ConnectionDesign`, `DesignSerializer`, `DesignCarrier`).

## Carrier

The design is stored in a Dataverse web resource of type 4 (XML), named `<prefix>_/brokerforge/<Instance>/design.xml` (for example `bf_/brokerforge/OrdersAPI/design.xml`). The body is:

```xml
<brokerforgeDesign formatVersion="1"><![CDATA[ ...design JSON... ]]></brokerforgeDesign>
```

`DesignCarrier.Wrap` and `Unwrap` are strict: the root must be `brokerforgeDesign` with exactly the attribute `formatVersion="1"` and exactly one CDATA section. Any other child node, attribute or value throws `DesignFormatException` naming the source.

### The `]]>` rule

CDATA ends at the first `]]>`, and OpenAPI or YAML text can contain it. `DesignSerializer.Serialize` therefore writes the `>` of every `]]>` as the JSON escape for U+003E: the two characters `]]` followed by the six characters backslash, `u`, `0`, `0`, `3`, `E` (in C# source `"]]\\u003E"`). That sequence can only occur inside a JSON string. `Parse` accepts it because any JSON parser decodes the escape back to `>`, so the text survives Serialize, Wrap, Unwrap and Parse unchanged. `Wrap` refuses JSON that still contains a raw `]]>`. The serializer otherwise uses relaxed escaping, so `<`, `>`, `&` and quotes in YAML stay readable.

## Serialization rules

- camelCase keys, indented with two spaces, `\n` line endings, null values omitted.
- Object keys in the declaration order below; dictionary keys sorted ordinally. Re-saving an unchanged design produces no diff.
- `Parse(json, source)` throws `DesignFormatException` naming `source` and the JSON path for an unknown key, a missing required key, a wrong type, a `formatVersion` other than 1, a duplicate key in any object or dictionary, and any property under `auth` named `secret`, `password`, `clientSecret`, `apiKeyValue` or `value` (the message never includes the value).

## Keys

Root:

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `formatVersion` | integer | yes | Always 1. |
| `instance` | string | yes | Service instance name, for example `OrdersAPI`. |
| `version` | integer | yes | Design revision, for the portal's own use. |
| `brokerType` | string | yes | Adapter type, for example `rest`. |
| `openApi` | string | yes | OpenAPI document text (YAML or JSON). |
| `answers` | object, string to string | yes | The wizard answers used to draft descriptors. |
| `auth` | object | yes | See below. |
| `objectMap` | object | no | Table and operation mapping. |
| `schema` | string | yes | Table schema YAML text (descriptor spec 14). |
| `descriptors` | object, operation name to string | yes | One YAML text per operation. |

`auth` (shape only: no values, no secret references):

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `mode` | string | yes | Authentication mode: exactly `ServiceAccount`, `Static`, `Impersonate`, `Sso` or `OAuth` (case-sensitive, no spaces, no numbers). |
| `staticKind` | string | no | Static mode: how the secret is sent. |
| `headerName` | string | no | Static header name. |
| `queryParam` | string | no | Static query parameter name. |
| `username` | string | no | Basic-style user name (not a secret). |
| `scope` | string | no | Service-account scope. |
| `ssoKind` | string | no | SSO mode kind. |
| `asyncContext` | string | no | Async context rule. |
| `oauth` | object | no | See below. |
| `secretNames` | array of string | yes | Names of the instance secrets the design needs, for example `["apiKey"]`. |

`auth.oauth`:

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `authorizeUrl` | string | no | The source's own authorize endpoint, https. Must be absent when `usesConnectApp` is true: the host then uses `https://login.microsoftonline.com/<Trust:TenantId>/oauth2/v2.0/authorize`. |
| `tokenUrl` | string | no | The source's own token endpoint, https. Must be absent when `usesConnectApp` is true: the host then uses `https://login.microsoftonline.com/<Trust:TenantId>/oauth2/v2.0/token`. Only the design's own `clientSecret` instance secret is ever sent to it. |
| `scopes` | array of string | yes | |
| `clientId` | string | no | |
| `usesConnectApp` | boolean | yes | True when the host's connect app is the OAuth client. |

`objectMap`:

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `table` | string | yes | Logical table name. |
| `keyStrategy` | string | yes | Key strategy name. |
| `keyKind` | string | yes | Key kind. |
| `operations` | object, string to string | yes | Table message to operation name. |

## Answer keys

`answers` holds the admin's answers to the draft service's questions, as string to string. `<path>` is a column's source JSONPath, such as `$.reference` or `$.state.code`, and `<op>` is an operation name: the operationId with a lower-case first letter, or its words in camelCase when it has other characters (`list-widgets` becomes `listWidgets`). Two operations with the same name are both left out. An answer whose key no question asks is not applied: the review lists it as unused. An answer that isn't valid for its question is refused, and the question stays open. An empty string counts as no answer.

| Key | Answer | Asked when |
| --- | --- | --- |
| `table.collection` | a collection path | more than one collection has an item path |
| `table.name` | logical name, `<prefix>_` then a lower-case letter, then letters, digits or `_`; at most 50 characters | optional; proposed from the collection |
| `table.displayName`, `table.collectionName` | 1–100 characters | optional; proposed |
| `table.externalName` | a lower-case letter, then lower-case letters, digits or `_`; at most 50 characters | optional; proposed from the table name without its prefix. An existing table keeps the external name it was created with, so give that one |
| `table.key` | a top-level scalar path | the item has no scalar `id` |
| `table.list`, `table.open`, `table.new`, `table.save`, `table.delete` | the operation name to use | more than one endpoint could be that message (PUT and PATCH both Save); the others are left out |
| `table.primaryName` | a string column's path | no string property is called `name` |
| `column.<path>.name` | logical name as for `table.name`; unique, and not `<table>id` | optional; proposed |
| `column.<path>.displayName` | 1–100 characters | optional; proposed |
| `column.<path>.maxLength` | 1–4000 | a string with no `maxLength`, or one outside 1–4000 |
| `column.<path>.precision` | 0–10 | a number whose `multipleOf` isn't a power of ten from 1 to 0.0000000001 |
| `op.<op>.use` | `yes` or `no` | optional; `yes` when absent |
| `op.<op>.paging` | `cursor`, `offset`, `pageNumber` or `link`, whichever the shape supports, or `none` | every list |
| `op.<op>.pageBase` | `0` or `1` | `pageNumber`, when `page` has no `minimum` of 0 or 1 |
| `op.<op>.pageSizeMax` | 1–5000 | the page size parameter has no `maximum`, or one outside 1–5000 |
| `op.<op>.filter.<param>` | `none`, `eq`, `in`, `eq,in` or `like` (string); `none`, `eq`, `in` or `eq,in` (option set); `none` or `eq` (other types) | a query parameter that matches a column |
| `op.<op>.sort` | `none`, `asc`, `-{field}` or `{field} desc` | a `sort`, `orderBy` or `order_by` parameter |
| `op.<op>.sort.<path>` | the source's sort field, 1–100 characters, no spaces | optional, once the sort isn't `none`; at least one is needed |
| `op.<op>.sortStable` | `yes` or `no` | every list |
| `op.<op>.value.<param>` | 1–200 characters, sent as a static value | a path parameter that isn't the record key; on a list, a required query parameter that matches no column; on a retrieve, write or delete, every required query parameter |
| `op.<op>.error.<status>` | `NotFound`, `Conflict`, `Forbidden`, `Validation` or `Fail` | a documented status other than 2xx, 403, 404, 409 and 422 |
| `op.<op>.error.<status>.message` | a string path of the error body, or `none`; with no documented body, an optional JSONPath such as `$.error.detail` | a status that maps to `Validation` |
| `op.<op>.header.<name>` | `correlationId`, `uuid` (a new ID on every call), `userObjectId`, `value` (then `op.<op>.value.<name>` is asked), or `none` when the header isn't required | a header parameter, except the authentication's own header and `Authorization`; optional when the header is, and an unanswered optional header is not sent |
| `op.<op>.body.<path>` | `userObjectId`, `correlationId`, or `none` when the property isn't required (`<path>` without `$.`, such as `placedBy`) | a create or update body property that matches no column; optional when the property is |
| `op.<op>.retry` | `no` or `yes` (3 attempts on 429, 503 or a network failure) | optional, `no` when absent; on a create or an action, `yes` needs an `Idempotency-Key` header answered `uuid` |

## Per-environment values

No key in the design is per-environment. Those values are `EnvironmentSettings(BaseUrl, AuthOverrides)` and are supplied as Dataverse environment variables:

- `bf_<Instance>_BaseUrl`: the source base URL. It must be https and may not point at a loopback, link-local, private or carrier-grade NAT address, by literal or by what its name resolves to, unless the host's administrator lists the host in `Config:Designs:AllowedPrivateHosts` (a listed host may also use http). The same rule applies to `auth.oauth` URLs.
- `bf_<Instance>_Auth`: JSON object of `AuthOverrides` (string to string). Each key replaces the `auth` string field of the same name: `staticKind`, `headerName`, `queryParam`, `username`, `scope` or `ssoKind`. `mode` and `asyncContext` cannot be overridden: who the source sees the call as belongs to the design that was approved, not to an environment variable. Any other key, `mode` and `asyncContext` included, is a configuration problem naming the key and the instance, and the design is left out of that environment.

Secret values are never part of either; they stay in Key Vault or the host's encrypted store.

## Tokens minted through the host

ServiceAccount (the host's managed identity) and Impersonate (a user's Entra token through the host's connect app) send a token to the design's base URL. A design may use either only with a scope that the host lists for its environment in `Trust:AllowedCallers[i]:DesignScopes`; the list is empty by default, so such a design is refused until the host's administrator adds the scope. For ServiceAccount the scope is `auth.scope` (after any `bf_<Instance>_Auth` override); for Impersonate every scope in `auth.oauth.scopes` except the sign-in scopes `openid`, `profile`, `email` and `offline_access`, and at least one other scope is required. Scopes for Azure management, Key Vault, Storage, Dataverse and Microsoft Graph are always refused, and a bare scope such as `User.Read` counts as Microsoft Graph. Each scope must be an absolute `https://` or `api://` URI with no user info, query or fragment; any other spelling (a bare host, a bare app ID, another scheme) is refused. A refused design is a configuration problem naming the instance, the scope and the environment.
