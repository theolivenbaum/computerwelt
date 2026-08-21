# Extension: Path.glob, Path.rglob, Path.match, Path.full_match and Path.walk.
# Upstream Monty's Path can list a directory with `iterdir` and nothing more — there was
# no way to match a pattern against a path at all.
import os
from pathlib import Path

root = Path('/virtual/pathglob')
os.makedirs('/virtual/pathglob/src/deep', exist_ok=True)
os.makedirs('/virtual/pathglob/tests', exist_ok=True)

for name in ('src/a.py', 'src/b.txt', 'src/deep/c.py', 'tests/t.py', 'top.py'):
    open('/virtual/pathglob/' + name, 'w').write('x')

# === glob is one level of pattern, and returns Paths ===
found = root.glob('src/*.py')
assert [str(p) for p in found] == ['/virtual/pathglob/src/a.py']
assert all(isinstance(p, Path) for p in root.glob('src/*'))

# === rglob is glob('**/' + pattern) ===
assert [str(p) for p in root.rglob('*.py')] == [
    '/virtual/pathglob/src/a.py',
    '/virtual/pathglob/src/deep/c.py',
    '/virtual/pathglob/tests/t.py',
    '/virtual/pathglob/top.py',
]

assert [str(p) for p in root.glob('**/*.py')] == [str(p) for p in root.rglob('*.py')]

# === Results are sorted ===
assert [str(p) for p in root.glob('src/*')] == [
    '/virtual/pathglob/src/a.py',
    '/virtual/pathglob/src/b.txt',
    '/virtual/pathglob/src/deep',
]

# === A glob that matches nothing is empty, not an error ===
assert list(root.glob('src/*.rs')) == []
assert list(Path('/virtual/nowhere').glob('*')) == []

# === match anchors at the right ===
assert Path('a/b/c.py').match('*.py')
assert Path('a/b/c.py').match('b/*.py')
assert Path('a/b/c.py').match('a/b/c.py')
assert not Path('a/b/c.py').match('a/*.py')
assert not Path('a/b/c.py').match('*.txt')

# An absolute pattern has to match the whole path.
assert Path('/a/b/c.py').match('/a/b/*.py')
assert not Path('/a/b/c.py').match('/b/*.py')

# === full_match accounts for the whole path, and understands `**` ===
assert Path('a/b/c.py').full_match('a/**/*.py')
assert Path('a/b/c.py').full_match('**/c.py')
assert Path('a/b/c.py').full_match('a/b/c.py')
assert not Path('a/b/c.py').full_match('*.py')
assert not Path('a/b/c.py').full_match('b/*.py')

# `**` stands for no segments as well as for several.
assert Path('a/c.py').full_match('a/**/c.py')

# === walk gives the same triples os.walk does, with Paths ===
walked = [(str(d), sorted(ds), sorted(fs)) for d, ds, fs in root.walk()]
assert walked[0] == ('/virtual/pathglob', ['src', 'tests'], ['top.py'])
assert [str(d) for d, _, _ in root.walk()] == [
    '/virtual/pathglob',
    '/virtual/pathglob/src',
    '/virtual/pathglob/src/deep',
    '/virtual/pathglob/tests',
]

assert [str(d) for d, _, _ in root.walk(top_down=False)][0] == '/virtual/pathglob/src/deep'
