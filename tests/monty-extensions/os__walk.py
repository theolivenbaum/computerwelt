# Extension: `os.walk`.
# Upstream Monty's `os` exposes eleven operations and tree-walking is not one of them, so
# a program had to hand-roll a recursion over `os.listdir` — and pay the interpreter's
# recursion limit for the depth of a tree it did not choose.
import os

root = '/virtual/walktree'
os.makedirs(root + '/src/deep/deeper', exist_ok=True)
os.makedirs(root + '/tests', exist_ok=True)
os.makedirs(root + '/obj', exist_ok=True)

for path in (
    root + '/top.txt',
    root + '/src/a.py',
    root + '/src/deep/b.py',
    root + '/src/deep/deeper/c.py',
    root + '/tests/t.py',
    root + '/obj/generated.py',
):
    open(path, 'w').write('x')

# === Top-down: a directory is reported before its children ===
walked = [(d, sorted(ds), sorted(fs)) for d, ds, fs in os.walk(root + '/src')]
assert walked == [
    (root + '/src', ['deep'], ['a.py']),
    (root + '/src/deep', ['deeper'], ['b.py']),
    (root + '/src/deep/deeper', [], ['c.py']),
]

# === Bottom-up: the deepest directory first ===
bottom = [d for d, _, _ in os.walk(root + '/src', topdown=False)]
assert bottom == [
    root + '/src/deep/deeper',
    root + '/src/deep',
    root + '/src',
]

# === Pruning ===
# Editing the directory list in place skips that subtree. This is why the walk has to be
# lazy: the descent happens after the caller's turn, reading back from the very list it
# was handed.
visited = []
for directory, directories, files in os.walk(root):
    directories[:] = [d for d in directories if d != 'obj']
    visited.append(directory)

assert root + '/obj' not in visited
assert root + '/src' in visited
assert root + '/src/deep' in visited

# Clearing the list entirely stops the walk at the top.
shallow = []
for directory, directories, files in os.walk(root):
    directories[:] = []
    shallow.append(directory)

assert shallow == [root]

# === Collecting paths, which is what a walk is usually for ===
found = []
for directory, directories, files in os.walk(root):
    for name in files:
        if name.endswith('.py'):
            found.append(os.path.join(directory, name))

assert sorted(found) == [
    root + '/obj/generated.py',
    root + '/src/a.py',
    root + '/src/deep/b.py',
    root + '/src/deep/deeper/c.py',
    root + '/tests/t.py',
]

# === An empty directory is reported, with two empty lists ===
os.makedirs(root + '/empty', exist_ok=True)
empty = [t for t in os.walk(root + '/empty')]
assert empty == [(root + '/empty', [], [])]

# === A path that cannot be listed yields nothing ===
# Silently, unless the caller asked to hear about it — CPython's contract.
assert list(os.walk(root + '/does-not-exist')) == []
assert list(os.walk(root + '/top.txt')) == []

errors = []
list(os.walk(root + '/top.txt', onerror=errors.append))
assert len(errors) == 1
assert isinstance(errors[0], OSError)

# === followlinks is accepted and has nothing to do ===
# There are no symbolic links in this filesystem, so a walk cannot loop through one.
assert len(list(os.walk(root + '/src', followlinks=True))) == 3

# === The result is a lazy iterator, not a list ===
walker = os.walk(root)
assert not isinstance(walker, list)
first = next(iter(walker))
assert first[0] == root
