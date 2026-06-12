import os, re, urllib.parse

ROOT = os.path.abspath("docs/site")
REPO = os.path.abspath(".")

md_files, toc_files = [], []
for dirpath, dirnames, filenames in os.walk(ROOT):
    # exclude built output and obj
    dirnames[:] = [d for d in dirnames if d not in ("_site", "obj")]
    for f in filenames:
        p = os.path.join(dirpath, f)
        if f.lower().endswith(".md"):
            md_files.append(p)
        elif f == "toc.yml":
            toc_files.append(p)

SKIP = re.compile(r'^(https?:|mailto:|xref:|data:|javascript:|ftp:|#)', re.I)

def strip_code(text):
    # remove fenced code blocks
    text = re.sub(r'^(```|~~~).*?^(```|~~~)\s*$', '', text, flags=re.S | re.M)
    # remove inline code spans per line
    text = re.sub(r'`[^`\n]*`', '', text)
    return text

link_patterns = [
    re.compile(r'!?\[[^\]]*\]\(\s*<([^>]+)>'),                      # [t](<path with spaces>)
    re.compile(r'!?\[[^\]]*\]\(\s*([^()\s]+?)(?:\s+["\'][^"\']*["\'])?\s*\)'),  # [t](path "title")
    re.compile(r'^\s*\[[^\]]+\]:\s*(\S+)', re.M),                   # [ref]: path
    re.compile(r'(?:href|src)\s*=\s*"([^"]+)"'),                    # html attrs
    re.compile(r"(?:href|src)\s*=\s*'([^']+)'"),
]
toc_pattern = re.compile(r'^\s*-?\s*(?:href|homepage|topicHref|tocHref):\s*(.+?)\s*$', re.M)

total = checked = ok = skipped = 0
generated, broken = [], []
results_seen = set()

def check(src, raw):
    global total, checked, ok, skipped
    total += 1
    t = raw.strip().strip('"').strip("'")
    if not t or SKIP.match(t):
        skipped += 1
        return
    # strip anchor / query
    t2 = t.split('#', 1)[0].split('?', 1)[0]
    if not t2:   # pure anchor
        skipped += 1
        return
    t2 = urllib.parse.unquote(t2)
    if t2.startswith('~/'):
        resolved = os.path.normpath(os.path.join(ROOT, t2[2:]))
    elif t2.startswith('/'):
        resolved = os.path.normpath(os.path.join(ROOT, t2.lstrip('/')))
    else:
        resolved = os.path.normpath(os.path.join(os.path.dirname(src), t2))
    checked += 1
    if os.path.exists(resolved):
        ok += 1
        return
    rel_src = os.path.relpath(src, REPO).replace(os.sep, '/')
    key = (rel_src, t)
    if key in results_seen:
        checked -= 1
        total -= 1
        return
    results_seen.add(key)
    api_dir = os.path.join(ROOT, 'api')
    if os.path.normcase(resolved).startswith(os.path.normcase(api_dir + os.sep)):
        generated.append(rel_src + " -> " + t + " (generated api page, expected)")
    else:
        broken.append(rel_src + " -> " + t)

for f in md_files:
    text = strip_code(open(f, encoding='utf-8-sig').read())
    seen_spans = set()
    for pat in link_patterns:
        for m in pat.finditer(text):
            span = (m.start(1), m.end(1))
            if span in seen_spans:
                continue
            seen_spans.add(span)
            check(f, m.group(1))

for f in toc_files:
    text = open(f, encoding='utf-8-sig').read()
    for m in toc_pattern.finditer(text):
        check(f, m.group(1))

print("FILES: %d md, %d toc.yml (excluded _site/, obj/)" % (len(md_files), len(toc_files)))
print("LINKS: total=%d skipped_external_or_anchor=%d checked_local=%d ok=%d" % (total, skipped, checked, ok))
print("GENERATED-MISSING: %d" % len(generated))
for g in generated:
    print("  GEN: " + g)
print("BROKEN: %d" % len(broken))
for b in broken:
    print("  BAD: " + b)
