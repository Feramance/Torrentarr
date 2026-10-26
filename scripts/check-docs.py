#!/usr/bin/env python3
"""Validate current documentation TOML fences and built internal links/anchors."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
import re
import sys
import tomllib

ROOT = Path(__file__).resolve().parents[1]
HISTORICAL = ('parity/', 'reviews/', 'development/pr-', 'development/webui-parity',
              'development/webui-source-reconciliation')


class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = set()
        self.hrefs = []

    def handle_starttag(self, tag, attrs):
        data = dict(attrs)
        if 'id' in data: self.ids.add(data['id'])
        if tag == 'a' and 'href' in data: self.hrefs.append(data['href'])


def main():
    errors = []
    examples = 0
    for path in (ROOT / 'docs').rglob('*.md'):
        relative = path.relative_to(ROOT / 'docs').as_posix()
        if relative.startswith(HISTORICAL): continue
        for number, block in enumerate(re.findall(r'```toml\n(.*?)```', path.read_text(encoding='utf-8-sig'), re.S), 1):
            examples += 1
            try: tomllib.loads(block)
            except tomllib.TOMLDecodeError as error: errors.append(f'{relative}: TOML example {number}: {error}')
    site = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'site'
    if not site.exists(): errors.append(f'Build documentation first; missing {site}')
    pages = {}
    for path in site.rglob('*.html'):
        parser = Links()
        parser.feed(path.read_text())
        pages[path.resolve()] = parser
    checked = 0
    for path, parser in pages.items():
        relative = path.relative_to(site.resolve()).as_posix()
        if relative.startswith(HISTORICAL): continue
        for href in parser.hrefs:
            url = urlsplit(href)
            if url.scheme or url.netloc: continue
            value = unquote(url.path)
            if value.startswith('/Torrentarr/'): value = value[len('/Torrentarr'):]
            target = (site / value.lstrip('/')) if value.startswith('/') else path.parent / value
            if not value: target = path
            if target.is_dir(): target = target / 'index.html'
            target = target.resolve()
            checked += 1
            if not target.exists(): errors.append(f'{relative}: missing {href}')
            elif url.fragment and target in pages and unquote(url.fragment) not in pages[target].ids:
                errors.append(f'{relative}: missing anchor {href}')
    for error in sorted(set(errors)): print(error)
    print(f'{examples} current TOML examples; {checked} internal links/anchors; {len(set(errors))} issues')
    return bool(errors)


if __name__ == '__main__':
    raise SystemExit(main())
