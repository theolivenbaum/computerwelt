# Extension: the rest of `os.path`, and importing it by name.
# Upstream Monty's `os.path` is join / basename / dirname / split / splitext / abspath /
# normpath / isabs / exists / isfile / isdir / islink / getsize. Everything below is new,
# and `import os.path` did not resolve at all — `os.path` was reachable only as an
# attribute of an already-imported `os`.
import os
import os.path
import posixpath

# === os.path, posixpath and os.path-as-an-attribute are one object ===
assert os.path is posixpath
assert os.path.join('a', 'b') == posixpath.join('a', 'b')

# === normpath collapses `..`, which it did not before ===
assert os.path.normpath('/a/./b/../c') == '/a/c'
assert os.path.normpath('a/b/../c') == 'a/c'
assert os.path.normpath('/a/b/../..') == '/'

# `..` above the root is the root; above a relative start it has to stay, because there
# is no way to know what it would climb to.
assert os.path.normpath('/..') == '/'
assert os.path.normpath('../a') == '../a'
assert os.path.normpath('../../a') == '../../a'
assert os.path.normpath('') == '.'
assert os.path.normpath('.') == '.'

# pathlib deliberately does NOT do this: PurePath is lexical about a component that could
# name a symbolic link, and upstream's fixtures pin that.
from pathlib import Path
assert str(Path('a/b/../c')) == 'a/b/../c'

# === abspath and realpath ===
assert os.path.abspath('/a/./b/../c') == '/a/c'
assert os.path.isabs(os.path.abspath('relative'))

# No symbolic links here, so resolving is normalising.
assert os.path.realpath('/a/./b/../c') == '/a/c'

# === relpath ===
assert os.path.relpath('/a/b/c', '/a/b') == 'c'
assert os.path.relpath('/a/b/c', '/a') == 'b/c'
assert os.path.relpath('/a/b', '/a/c') == '../b'
assert os.path.relpath('/a', '/a') == '.'
assert os.path.relpath('/a', '/a/b/c') == '../..'
assert os.path.relpath('/x', '/a/b') == '../../x'

# Lexical throughout: it never asks whether any of this exists.
assert os.path.relpath('/nowhere/at/all', '/nowhere') == 'at/all'

# === commonpath, component-wise ===
assert os.path.commonpath(['/a/b/c', '/a/b/d']) == '/a/b'
assert os.path.commonpath(['/a/b', '/a/b']) == '/a/b'
assert os.path.commonpath(['/a/b', '/c/d']) == '/'
assert os.path.commonpath(['a/b/c', 'a/b/d']) == 'a/b'

try:
    os.path.commonpath(['/a', 'b'])
    assert False, 'expected ValueError for mixed absolute and relative'
except ValueError:
    pass

try:
    os.path.commonpath([])
    assert False, 'expected ValueError for an empty sequence'
except ValueError:
    pass

# === commonprefix, character-wise ===
# The documented wart: it does not respect component boundaries, which is exactly why
# commonpath exists.
assert os.path.commonprefix(['/a/bc', '/a/bd']) == '/a/b'
assert os.path.commonprefix([]) == ''
assert os.path.commonprefix(['abc']) == 'abc'

# === Paths are POSIX whatever the host is, so normcase is the identity ===
assert os.path.normcase('/A/b') == '/A/b'

# === There are no home directories in this sandbox ===
# `~` stays as written rather than expanding to somewhere that does not exist.
assert os.path.expanduser('~/x') == '~/x'

# === lexists, getmtime ===
open('/virtual/pathext.txt', 'w').write('x')
assert os.path.lexists('/virtual/pathext.txt')
assert not os.path.lexists('/virtual/definitely-absent')
assert isinstance(os.path.getmtime('/virtual/pathext.txt'), float)
