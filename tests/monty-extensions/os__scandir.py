# Extension: `os.scandir` and its DirEntry.
# Upstream Monty has only `os.listdir`, which returns names. Every "is this a directory?"
# then costs a separate query; scandir answers from the listing it already made.
import os

root = '/virtual/scantree'
os.makedirs(root + '/sub', exist_ok=True)
open(root + '/a.txt', 'w').write('hello')
open(root + '/b.bin', 'w').write('xy')
open(root + '/sub/c.txt', 'w').write('nested')

# === Names, in the same sorted order listdir gives ===
entries = list(os.scandir(root))
assert [e.name for e in entries] == ['a.txt', 'b.bin', 'sub']
assert [e.name for e in entries] == sorted(os.listdir(root))

# === Each entry knows its own path ===
assert [e.path for e in entries] == [root + '/a.txt', root + '/b.bin', root + '/sub']

# === and its type, without a second call ===
assert [e.is_dir() for e in entries] == [False, False, True]
assert [e.is_file() for e in entries] == [True, True, False]

# No filesystem here has symbolic links, so this is a constant.
assert [e.is_symlink() for e in entries] == [False, False, False]

# === stat() is the same stat_result os.stat gives ===
first = entries[0]
assert first.stat().st_size == 5
assert first.stat().st_size == os.stat(root + '/a.txt').st_size

# === PathLike, so an entry can be opened directly ===
assert open(entries[0], 'r').read() == 'hello'
assert os.fspath(entries[0]) == root + '/a.txt'

# === Context manager ===
# There is no directory handle to release here; `close` and `__exit__` exist so that the
# `with os.scandir(...)` a program already writes keeps working.
with os.scandir(root) as scan:
    names = [e.name for e in scan]

assert names == ['a.txt', 'b.bin', 'sub']

# A closed scan yields nothing more.
scan = os.scandir(root)
scan.close()
assert list(scan) == []

# === An iterator, consumed once ===
scan = os.scandir(root)
assert len(list(scan)) == 3
assert list(scan) == []

# === Errors match listdir's ===
try:
    os.scandir(root + '/missing')
    assert False, 'expected FileNotFoundError'
except FileNotFoundError:
    pass

# === scandir as a walk's inner loop ===
def sizes(directory):
    total = 0
    for entry in os.scandir(directory):
        if entry.is_dir():
            total += sizes(entry.path)
        else:
            total += entry.stat().st_size
    return total

assert sizes(root) == 5 + 2 + 6
