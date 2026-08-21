# Extension: bounding how deep a walk goes.
# Two separate things, and the difference matters.
#
# `max_depth=` is this port's own keyword — CPython's `os.walk` has no such parameter, so
# a script that passes it will not run there. It is the *caller* asking for less, and it
# ends the walk cleanly.
#
# The sandbox's own cap (ExecutionLimits.MaxDirectoryDepth, 64 by default) is not a
# parameter at all. Reaching it *raises*, because a walk that quietly stopped part-way
# would report a subset of the tree as though it were all of it. It cannot be reached over
# the shell's filesystem, which refuses to create a path that deep in the first place; it
# is there for a host that supplies storage this sandbox did not build.
import os

root = '/virtual/depthtree'
os.makedirs(root + '/a/b/c/d', exist_ok=True)
for path in (root + '/top.txt', root + '/a/one.txt', root + '/a/b/two.txt'):
    open(path, 'w').write('x')

# === Unbounded, for comparison ===
assert [d for d, _, _ in os.walk(root)] == [
    root, root + '/a', root + '/a/b', root + '/a/b/c', root + '/a/b/c/d',
]

# === max_depth=0 visits only the top ===
walked = list(os.walk(root, max_depth=0))
assert [d for d, _, _ in walked] == [root]

# The directory list still names what is below, as it always does; the walk simply does
# not go there.
assert walked[0][1] == ['a']
assert walked[0][2] == ['top.txt']

# === max_depth=N is N levels below the top ===
assert [d for d, _, _ in os.walk(root, max_depth=1)] == [root, root + '/a']
assert [d for d, _, _ in os.walk(root, max_depth=2)] == [root, root + '/a', root + '/a/b']

# === Counted from the top, not from the filesystem root ===
assert [d for d, _, _ in os.walk(root + '/a', max_depth=1)] == [root + '/a', root + '/a/b']

# === A bound past the bottom of the tree changes nothing ===
assert len(list(os.walk(root, max_depth=99))) == 5

# === Bottom-up is bounded the same way ===
# It matters more here than top-down, because a bottom-up walk cannot be pruned by editing
# the directory list — its children are already visited when the parent is reported.
assert [d for d, _, _ in os.walk(root, topdown=False, max_depth=2)] == [
    root + '/a/b', root + '/a', root,
]

# === Collecting files under a bound ===
found = []
for directory, _, files in os.walk(root, max_depth=1):
    found += [os.path.join(directory, f) for f in files]
assert sorted(found) == [root + '/a/one.txt', root + '/top.txt']

# === The bound is checked, not assumed ===
try:
    list(os.walk(root, max_depth=-1))
    assert False, 'expected ValueError'
except ValueError as e:
    assert str(e) == 'max_depth must not be negative'

try:
    list(os.walk(root, max_depth='2'))
    assert False, 'expected TypeError'
except TypeError:
    pass

# === Path.walk takes the same keyword ===
from pathlib import Path
assert [str(d) for d, _, _ in Path(root).walk(max_depth=1)] == [root, root + '/a']
assert len(list(Path(root).walk())) == 5

try:
    list(Path(root).walk(max_depth=-1))
    assert False, 'expected ValueError'
except ValueError:
    pass
