# Extension: the `glob` module.
# Upstream Monty has no `glob`, and no `fnmatch` for it to be built on. Present here only
# when the sandbox has a filesystem, for the same reason `os` is: a `glob` that always
# returned nothing would look like an empty tree rather than like absent storage.
import glob
import os

root = '/virtual/globtree'
os.makedirs(root + '/src/deep', exist_ok=True)
os.makedirs(root + '/tests', exist_ok=True)
os.makedirs(root + '/.hidden', exist_ok=True)

for path in (
    root + '/top.py',
    root + '/notes.txt',
    root + '/src/a.py',
    root + '/src/b.txt',
    root + '/src/deep/c.py',
    root + '/tests/t.py',
    root + '/.hidden/h.py',
    root + '/.dotfile',
):
    open(path, 'w').write('x')

# === A flat pattern does not descend ===
assert glob.glob(root + '/src/*.py') == [root + '/src/a.py']
assert glob.glob(root + '/*.py') == [root + '/top.py']

# === Results are sorted, which upstream's arbitrary listing order could not promise ===
assert glob.glob(root + '/src/*') == [root + '/src/a.py', root + '/src/b.txt', root + '/src/deep']

# === `**` spans directories only when asked ===
assert glob.glob(root + '/**/*.py', recursive=True) == [
    root + '/src/a.py',
    root + '/src/deep/c.py',
    root + '/tests/t.py',
    root + '/top.py',
]

# Without `recursive`, `**` is just another `*` — one segment.
assert glob.glob(root + '/**/*.py') == [root + '/src/a.py', root + '/tests/t.py']

# `**` stands for no segments at all as well as for several, which is why a file in the
# search root itself is found.
assert root + '/top.py' in glob.glob(root + '/**/*.py', recursive=True)

# === Hidden entries ===
# A wildcard skips a leading dot unless the pattern asked for one.
assert glob.glob(root + '/.*') == [root + '/.dotfile', root + '/.hidden']
assert root + '/.hidden' not in glob.glob(root + '/*')
assert root + '/.hidden/h.py' in glob.glob(root + '/**/*.py', recursive=True, include_hidden=True)

# === A trailing slash asks for directories, and stays on what comes back ===
assert glob.glob(root + '/*/') == [root + '/src/', root + '/tests/']
assert glob.glob(root + '/.*/') == [root + '/.hidden/']

# === A pattern with nothing to match is one existence check ===
assert glob.glob(root + '/src/a.py') == [root + '/src/a.py']
assert glob.glob(root + '/src/missing.py') == []

# === root_dir makes the results relative to it ===
assert glob.glob('*.py', root_dir=root + '/src') == ['a.py']
assert sorted(glob.glob('**/*.py', root_dir=root, recursive=True)) == [
    'src/a.py', 'src/deep/c.py', 'tests/t.py', 'top.py',
]

# === iglob returns the same thing ===
# A list rather than a generator: laziness buys a caller nothing over a virtual
# filesystem inside a bounded instruction budget.
assert list(glob.iglob(root + '/src/*.py')) == [root + '/src/a.py']

# === escape ===
assert glob.escape('a[1].py') == 'a[[]1].py'
assert glob.escape('a*b?c') == 'a[*]b[?]c'
assert glob.escape('plain.py') == 'plain.py'

# An escaped pattern matches the literal name.
open(root + '/odd[1].py', 'w').write('x')
assert glob.glob(root + '/' + glob.escape('odd[1].py')) == [root + '/odd[1].py']

# === has_magic ===
assert glob.has_magic('*.py')
assert glob.has_magic('a?b')
assert glob.has_magic('a[0-9]')
assert not glob.has_magic('plain/path.py')

# === Character classes work in a path segment ===
assert glob.glob(root + '/src/[ab].py') == [root + '/src/a.py']

# === A missing directory is empty, not an error ===
assert glob.glob(root + '/nowhere/*.py') == []
