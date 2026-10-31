"""One-time local demo activation, explicitly authorized by project owner.
Uses the cached source's VND variant prices, deterministic demo stock and audit ledger.
"""
import json
from decimal import Decimal
from import_lining_demo import OUT, connect, identity

BATCH = 'lining-demo-activation-v1'

def activate():
    products = json.loads((OUT / 'products.json').read_text(encoding='utf-8'))
    changed = 0
    with connect() as db:
        db.execute('SELECT pg_advisory_xact_lock(38160431)')
        for product in sorted(products, key=lambda p: p['sourceId']):
            pid = identity('product:' + product['sourceId'])
            status = db.execute('SELECT publication_status FROM store.products WHERE id=%s FOR UPDATE', (pid,)).fetchone()
            if not status:
                raise ValueError('Imported product missing')
            if db.execute('SELECT 1 FROM store.audit_logs WHERE id=%s', (identity(BATCH + ':publish:' + str(pid)),)).fetchone():
                continue
            if status[0] != 'DRAFT':
                raise ValueError('Expected DRAFT; refusing to change existing published data')
            for variant in sorted(product['variants'], key=lambda v: v['sourceId']):
                sid = identity('variant:' + variant['sourceId'])
                price = Decimal(str(variant['referencePrice']))
                if not price.is_finite() or price <= 0 or price != price.to_integral_value() or variant['referenceCurrency'] != 'VND':
                    raise ValueError('Invalid VND reference price')
                old = db.execute('SELECT regular_price FROM store.product_skus WHERE id=%s AND product_id=%s FOR UPDATE', (sid,pid)).fetchone()
                if old is None:
                    raise ValueError('Variant missing')
                db.execute('UPDATE store.product_skus SET regular_price=%s,updated_at=now() WHERE id=%s', (price,sid))
                db.execute("""INSERT INTO store.audit_logs(id,actor_type,action,entity_type,entity_id,before_data,after_data,correlation_id)
                  VALUES (%s,'SYSTEM','SKU_PRICE_UPDATED','ProductSku',%s,%s::jsonb,%s::jsonb,%s)""",
                  (identity(BATCH + ':price:' + str(sid)),sid,json.dumps({'regularPrice':float(old[0])}),json.dumps({'regularPrice':int(price),'sourceUrl':product['sourceUrl'],'sourceRetrievedAt':product['retrievedAt'],'demo':True}),BATCH))
                quantity = 10 + identity(BATCH + ':qty:' + str(sid)).int % 41
                balance = db.execute('SELECT id FROM store.inventory_balances WHERE product_sku_id=%s FOR UPDATE', (sid,)).fetchone()
                if not balance:
                    raise ValueError('Initial inventory balance missing')
                db.execute('UPDATE store.inventory_balances SET physical_qty=physical_qty+%s,available_qty=available_qty+%s,revision=revision+1,updated_at=now() WHERE product_sku_id=%s', (quantity,quantity,sid))
                db.execute("""INSERT INTO store.inventory_movements(id,product_sku_id,movement_type,qty_delta,source_type,source_id,note)
                  VALUES (%s,%s,'STOCK_IN',%s,'DEMO_SEED',%s,'Demo stock authorized by project owner; not supplier inventory')""",
                  (identity(BATCH + ':movement:' + str(sid)),sid,quantity,identity(BATCH)))
            active, invalid = db.execute("SELECT count(*),count(*) FILTER(WHERE regular_price<=0) FROM store.product_skus WHERE product_id=%s AND sku_status='ACTIVE'", (pid,)).fetchone()
            if not active or invalid:
                raise ValueError('Publish invariant violated')
            # System attribution avoids pretending a real Admin session approved this seed.
            db.execute("UPDATE store.products SET publication_status='PUBLISHED',published_at=now(),updated_at=now() WHERE id=%s", (pid,))
            db.execute("""INSERT INTO store.audit_logs(id,actor_type,action,entity_type,entity_id,before_data,after_data,correlation_id)
              VALUES (%s,'SYSTEM','PRODUCT_PUBLISHED','Product',%s,'{"publicationStatus":"DRAFT"}'::jsonb,
              jsonb_build_object('publicationStatus','PUBLISHED','publishedAt',now(),'demo',true,'authorization','project owner'),%s)""",
              (identity(BATCH + ':publish:' + str(pid)),pid,BATCH))
            changed += 1
    print(json.dumps({'published':changed,'skipped':len(products)-changed,'batch':BATCH}))

if __name__ == '__main__':
    activate()
