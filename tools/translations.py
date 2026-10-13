"""Prints the interface texts in all three languages as a Markdown table,
read from the code: L.T("中文", "English", "日本語") in the Windows app,
tr("中文", "English", "日本語") in the macOS app, tr!(…) in core and
core-ffi. Texts with the same Chinese but other English or Japanese on the
two platforms show next to each other.

    python tools/translations.py > translations.md
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

SOURCES = [
    ('Windows', 'apps/windows/WormholeSend', '*.cs', 'L.T('),
    ('macOS', 'apps/macos', '**/*.swift', 'tr('),
    ('core', 'core/src', '*.rs', 'tr!('),
    ('core', 'core-ffi/src', '*.rs', 'tr!('),
]

# A C#, Swift or Rust string literal (C# may put $ in front), then a comma
# or the end of the call.
LITERAL = re.compile(r'\s*\$?"((?:[^"\\]|\\.)*)"\s*,?')


def triples(text, call):
    for match in re.finditer(re.escape(call), text):
        # `tr(` must not be the end of another name (e.g. `attr(`).
        if call == 'tr(' and match.start() > 0 and (text[match.start() - 1].isalnum() or text[match.start() - 1] == '_'):
            continue
        rest = text[match.end():match.end() + 2000]
        parts, at = [], 0
        while len(parts) < 3:
            literal = LITERAL.match(rest, at)
            if not literal:
                break
            parts.append(literal.group(1))
            at = literal.end()
        if len(parts) == 3:
            yield tuple(parts)


def main():
    rows = {}
    for platform, folder, pattern, call in SOURCES:
        for path in sorted((ROOT / folder).glob(pattern)):
            for triple in triples(path.read_text(encoding='utf-8'), call):
                rows.setdefault(triple, set()).add(platform)

    def cell(text):
        return text.replace('|', '\\|').replace('\\"', '"')

    print('| 中文 | English | 日本語 | used in |')
    print('| --- | --- | --- | --- |')
    for (zh, en, ja), platforms in sorted(rows.items()):
        print(f'| {cell(zh)} | {cell(en)} | {cell(ja)} | {", ".join(sorted(platforms))} |')
    print(f'{len(rows)} texts', file=sys.stderr)


if __name__ == '__main__':
    main()
