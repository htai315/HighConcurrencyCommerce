"""Import a small, cached Li-Ning sample into LOCAL development PostgreSQL only.
Requires psycopg. Never publishes products, sets selling prices or adds stock.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import time
import urllib.request
import urllib.robotparser
import uuid
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
BASE = 'https://lining.com.vn'
OUT = ROOT / 'data' / 'imports' / 'lining-demo'
MEDIA = ROOT / 'frontend' / 'public' / 'images' / 'lining-demo'
NAMESPACE = uuid.UUID('58dc690a-1ecf-4ff4-aed8-f7a729c51a82')
AGENT = 'HighCommerceDemoCatalog/1.0'

def identity(value):
    return uuid.uuid5(NAMESPACE, str(value))

def fetch(url):
    time.sleep(1.5)
    request = urllib.request.Request(url, headers={'User-Agent': AGENT})
    # No retries on 429/403 and no bypass of authentication or TLS verification.
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read(), response.headers.get_content_type()

def category(title):
    value = title.lower()
    for word, name, slug in [('giày', 'Giày cầu lông', 'giay-cau-long'), ('vợt', 'Vợt cầu lông', 'vot-cau-long'), ('áo', 'Trang phục cầu lông', 'trang-phuc-cau-long'), ('quần', 'Trang phục cầu lông', 'trang-phuc-cau-long'), ('váy', 'Trang phục cầu lông', 'trang-phuc-cau-long'), ('túi', 'Túi & balo', 'tui-balo'), ('balo', 'Túi & balo', 'tui-balo')]:
        if word in value:
            return name, slug
    return 'Phụ kiện cầu lông', 'phu-kien-cau-long'

def collect():
    OUT.mkdir(parents=True, exist_ok=True)
    MEDIA.mkdir(parents=True, exist_ok=True)
    manifest = OUT / 'products.json'
    if manifest.exists():
        return json.loads(manifest.read_text(encoding='utf-8'))
    robots = urllib.robotparser.RobotFileParser()
    robots.parse(fetch(BASE + '/robots.txt')[0].decode().splitlines())
    listing = BASE + '/collections/cau-long-2'
    if not robots.can_fetch(AGENT, listing):
        raise RuntimeError('Listing blocked by robots.txt')
    html = fetch(listing)[0].decode('utf-8')
    links = list(dict.fromkeys(re.findall(r'href=["\x27](/products/[^"\x27?#]+)', html)))
    products = []
    for path in links:
        if len(products) == 20:
            break
        url = BASE + path
        if not robots.can_fetch(AGENT, url + '.json'):
            continue
        handle = path.rsplit('/', 1)[-1]
        cache = OUT / (handle + '.json')
        if not cache.exists():
            raw, _ = fetch(url + '.json')
            json.loads(raw)
            cache.write_bytes(raw)
        raw = json.loads(cache.read_text(encoding='utf-8'))
        product = raw.get('product', raw)
        if not product.get('variants'):
            continue
        images = []
        for image in product.get('images', [])[:3]:
            source = image['src']
            if source.startswith('//'):
                source = 'https:' + source
            from urllib.parse import urlparse
            host = urlparse(source).hostname or ''
            if not (host == 'lining.com.vn' or host.endswith('.hstatic.net')):
                continue
            filename = hashlib.sha256(source.encode()).hexdigest()[:24]
            existing = list(MEDIA.glob(filename + '.*'))
            if existing:
                target = existing[0]
            else:
                content, mime = fetch(source)
                extension = {'image/jpeg': '.jpg', 'image/png': '.png', 'image/webp': '.webp'}.get(mime)
                if not extension or len(content) > 12_000_000:
                    raise ValueError('Unexpected image format/size')
                target = MEDIA / (filename + extension)
                target.write_bytes(content)
            images.append({'sourceUrl': source, 'fileKey': '/images/lining-demo/' + target.name})
        name, slug = category(product['title'])
        products.append({'sourceId': str(product['id']), 'sourceUrl': url, 'retrievedAt': datetime.now(timezone.utc).isoformat(),
                         'title': product['title'], 'brand': product.get('vendor', 'Li-Ning'), 'category': name, 'categorySlug': slug,
                         'images': images, 'variants': [{'sourceId': str(v['id']), 'sourceSku': v.get('sku'), 'optionLabel': v['title'],
                            'referencePrice': v.get('price'), 'referenceCurrency': 'VND'} for v in product['variants']]})
        print('Collected', len(products), '/', 20, flush=True)
    if len(products) != 20:
        raise ValueError(f'Expected 20 products; found {len(products)}. Nothing imported.')
    manifest.write_text(json.dumps(products, ensure_ascii=False, indent=2), encoding='utf-8')
    return products

def connect():
    import psycopg
    secret = Path(os.environ['APPDATA']) / 'Microsoft/UserSecrets/3c567ea2-055d-4e2d-bba9-5d824fbf554c/secrets.json'
    config = json.loads(secret.read_text(encoding='utf-8-sig'))
    connection = config.get('ConnectionStrings:StoreDb') or config['ConnectionStrings']['StoreDb']
    parts = dict(part.split('=', 1) for part in connection.split(';') if '=' in part)
    keys = {'host': 'host', 'port': 'port', 'database': 'dbname', 'username': 'user', 'password': 'password'}
    options = {keys[k.strip().lower()]: v.strip() for k, v in parts.items() if k.strip().lower() in keys}
    if options.get('host') not in ('localhost', '127.0.0.1', '::1') or options.get('dbname') != 'hcc_commerce_dev':
        raise ValueError('Importer is restricted to local hcc_commerce_dev')
    return psycopg.connect(**options)

def run_import(products):
    actor = identity('import-actor')
    inserted = 0
    with connect() as db:
        db.execute("SELECT pg_advisory_xact_lock(38160431)")
        role = identity('import-staff-role')
        db.execute("INSERT INTO store.roles(id,role_key,display_name) VALUES (%s,'STAFF','Nhân viên') ON CONFLICT(role_key) DO NOTHING", (role,))
        role = db.execute("SELECT id FROM store.roles WHERE role_key='STAFF'").fetchone()[0]
        db.execute("""INSERT INTO store.user_accounts(id,role_id,email,password_hash,full_name,account_status,locked_at)
            VALUES (%s,%s,'lining-import@demo.invalid','!DISABLED-DEMO-IMPORT!','Li-Ning Demo Import','LOCKED',now()) ON CONFLICT(id) DO NOTHING""", (actor, role))
        for p in products:
            pid = identity('product:' + p['sourceId'])
            # Entire existing products are skipped, preserving subsequent manual edits.
            if db.execute('SELECT 1 FROM store.products WHERE id=%s', (pid,)).fetchone():
                continue
            cid = identity('category:' + p['categorySlug'])
            db.execute('INSERT INTO store.categories(id,name,slug) VALUES (%s,%s,%s) ON CONFLICT(slug) DO NOTHING', (cid,p['category'],p['categorySlug']))
            cid = db.execute('SELECT id FROM store.categories WHERE slug=%s', (p['categorySlug'],)).fetchone()[0]
            description = f"{p['category']} Li-Ning — {p['title']}.\nDữ liệu demo tham khảo từ {p['sourceUrl']}. Thông số và giá bán cần được cửa hàng kiểm tra trước khi công bố."
            db.execute("INSERT INTO store.products(id,category_id,title,description,publication_status,created_by) VALUES (%s,%s,%s,%s,'DRAFT',%s)", (pid,cid,p['title'],description,actor))
            for v in p['variants']:
                sid = identity('variant:' + v['sourceId'])
                db.execute("INSERT INTO store.product_skus(id,product_id,sku_code,option_label,regular_price,sku_status) VALUES (%s,%s,%s,%s,0,'ACTIVE')", (sid,pid,'LN-DEMO-' + v['sourceId'],v['optionLabel']))
                db.execute('INSERT INTO store.inventory_balances(id,product_sku_id) VALUES (%s,%s)', (identity('balance:' + v['sourceId']),sid))
            for index, image in enumerate(p['images']):
                db.execute('INSERT INTO store.product_images(id,product_id,file_key,alt_text,display_order) VALUES (%s,%s,%s,%s,%s)', (identity(f'image:{pid}:{index}'),pid,image['fileKey'],p['title'],index))
            inserted += 1
    print(json.dumps({'inserted': inserted, 'skipped': len(products)-inserted, 'manifestProducts': len(products), 'manifestSkus': sum(len(p['variants']) for p in products), 'manifestImages': sum(len(p['images']) for p in products)}))

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--apply', action='store_true', help='Import into local development DB; default only collects files')
    args = parser.parse_args()
    products = collect()
    if args.apply:
        run_import(products)
