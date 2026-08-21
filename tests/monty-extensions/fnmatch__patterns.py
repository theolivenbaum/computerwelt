# Extension: the `fnmatch` module.
# Upstream Monty has no `fnmatch`, so `import fnmatch` raises ModuleNotFoundError and
# nothing below runs. There is no filesystem involved — this is string matching — which
# is why the module is importable even in a sandbox with no storage.
import fnmatch

# === Literal and wildcard ===
assert fnmatch.fnmatch('report.py', '*.py')
assert not fnmatch.fnmatch('report.txt', '*.py')
assert fnmatch.fnmatch('report.py', 'report.py')
assert fnmatch.fnmatch('anything', '*')
assert fnmatch.fnmatch('', '*')

# === Single character ===
assert fnmatch.fnmatch('a1.py', 'a?.py')
assert not fnmatch.fnmatch('a12.py', 'a?.py')
assert not fnmatch.fnmatch('a.py', 'a?.py')

# === Character classes ===
assert fnmatch.fnmatch('a1', 'a[0-9]')
assert not fnmatch.fnmatch('ax', 'a[0-9]')
assert fnmatch.fnmatch('ax', 'a[!0-9]')
assert not fnmatch.fnmatch('a1', 'a[!0-9]')
assert fnmatch.fnmatch('ab', 'a[bc]')
assert fnmatch.fnmatch('ac', 'a[bc]')
assert not fnmatch.fnmatch('ad', 'a[bc]')

# An unclosed bracket is a literal '[', not an error.
assert fnmatch.fnmatch('a[', 'a[')

# === `*` crosses a separator, because fnmatch knows nothing about paths ===
assert fnmatch.fnmatch('src/deep/a.py', '*.py')
assert fnmatch.fnmatch('src/deep/a.py', 'src/*')

# === fnmatchcase ===
# Both are the same function here: every path in this sandbox is POSIX and case
# sensitive, so there is nothing for the non-`case` form to fold.
assert fnmatch.fnmatchcase('report.py', '*.py')
assert not fnmatch.fnmatchcase('REPORT.PY', '*.py')

# === filter ===
assert fnmatch.filter(['a.py', 'b.txt', 'c.py'], '*.py') == ['a.py', 'c.py']
assert fnmatch.filter([], '*.py') == []
assert fnmatch.filter(['a.py'], '*.txt') == []

# filter preserves the input's order rather than sorting.
assert fnmatch.filter(['z.py', 'a.py'], '*.py') == ['z.py', 'a.py']

# === translate ===
# The regex is the one CPython reports, wrapper and anchor included, because a script
# that hands the result to `re.compile` depends on both.
assert fnmatch.translate('*.py') == r'(?s:.*\.py)\z'
assert fnmatch.translate('?') == r'(?s:.)\z'

import re
assert re.match(fnmatch.translate('*.py'), 'a/b/c.py')
assert re.match(fnmatch.translate('*.py'), 'a.txt') is None

# === Errors ===
try:
    fnmatch.fnmatch(1, '*')
    assert False, 'expected TypeError'
except TypeError:
    pass
