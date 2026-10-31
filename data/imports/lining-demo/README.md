# Li-Ning demo catalog

## Current local demo state

The owner subsequently authorized pricing, demo stock and publication. Executed `python scripts/publish_lining_demo.py`: 20 PUBLISHED products, 104 variant prices matching the cached source VND prices, 10–50 demo units per SKU (3,099 total), 104 STOCK_IN ledger entries and 124 SYSTEM audit entries. Re-running skips the completed batch without adding stock. Original import instructions below describe initial staging, not current publication status. This explicit local seed uses SYSTEM attribution rather than impersonating an authenticated Admin; it does not change the production RBAC workflow.

Source: https://lining.com.vn/collections/cau-long-2

The user authorized collecting this sample for their local demo on 2026-10-31.
This is a snapshot, not a live supplier integration or a representation of stock owned by the shop.

## Files

- `products.json`: normalized manifest, original product/variant IDs, source URLs, retrieval timestamps, reference prices, local image paths.
- Other JSON files: cached source responses, retained for traceability. Source descriptions are not copied verbatim into the storefront.
- Images: `frontend/public/images/lining-demo/`, maximum three per product. Local image filenames are hashes of their source URLs.

## Repeatable local import

```powershell
python scripts/import_lining_demo.py --apply
```

Requires Python and psycopg. Reads the API project's existing `StoreDb` User Secret without printing credentials. Restricted to localhost database `hcc_commerce_dev`.

Without `--apply`, only collects files. Uses cached responses and manifest on subsequent runs. Requests are spaced 1.5 seconds apart; HTTP errors stop the run, with no CAPTCHA, rate limit or TLS bypass.

The importer creates a locked, non-login STAFF actor for attribution; it does not elevate existing accounts. Products are DRAFT, SKU prices are zero, and all inventory partitions are zero. Source prices stay in the manifest only. Products will not appear in the public catalog until an Admin sets prices and publishes them. Actual demo stock must be entered through the inventory workflow.

Deterministic IDs, an advisory transaction lock and whole-product skipping make repeated imports non-duplicating and preserve subsequent manual edits. An exception rolls back the database transaction. Images/cache files may remain on disk after a failed import and can be reused.

Docker uses a separate database volume. This command does not modify it. Vite serves the local images immediately; a Docker frontend needs a rebuild to include new assets.
