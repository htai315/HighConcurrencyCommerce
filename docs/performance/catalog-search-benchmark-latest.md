# T50.8 Catalog Search Benchmark

Generated: 2026-11-11 02:50:03 UTC
Dataset: 10,000 products, 30,000 SKUs, 20 brands, 20 categories.
Samples: 25 after one warm-up; command/statement timeout: 2 seconds.

| Case | p95 client ms | EXPLAIN execution ms | Reference target | Indexes | Seq scans |
|---|---:|---:|---|---|---|
| Exact SKU | 2.26 | 1.83 | < 300 ms | ix_product_skus_search_sku_trgm, products_pkey |  |
| Accent-insensitive title prefix | 1.94 | 1.16 | < 300 ms | ix_products_search_title_trgm |  |
| Fuzzy title | 73.91 | 45.59 | < 300 ms | ix_products_search_title_trgm |  |
| Combined search facets | 0.86 | 0.08 | < 300 ms | inventory_balances_product_sku_id_key, ix_product_reviews_public_product, ix_product_skus_catalog_price, ix_products_search_title_trgm |  |
| Suggestion | 95.32 | 48.78 | < 150 ms | ix_products_search_title_trgm |  |
| Best-selling 90 days | 40.66 | 45.08 | < 300 ms |  | product_skus, products, sales_order_items |
| Deep page | 3.48 | 2.88 | < 300 ms |  | products |

Timing targets are informative and are not hard assertions in the normal integration suite. The hard gate requires the expected trigram indexes and rejects sequential scans of `products`/`product_skus` for the five search/suggestion cases.
The dataset is created in a disposable loopback-only `hcc_perf_<guid>` database and is never part of demo or production migrations.
