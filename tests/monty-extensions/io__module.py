# Extension: the `io` module.
# Upstream Monty has `open` as a builtin and nothing else — no `io`, so `io.open`, the
# spelling a patch script uses when it wants the encoding stated rather than guessed,
# raised ModuleNotFoundError before the file was ever touched.
import io

# === io.open is the builtin under its other name ===
path = '/virtual/iomodule.txt'
io.open(path, 'w', encoding='utf-8').write('first\nsecond\n')
assert io.open(path, encoding='utf-8').read() == 'first\nsecond\n'
assert open(path).read() == io.open(path).read()

# === io.UnsupportedOperation is re-exported where CPython defines it ===
assert issubclass(io.UnsupportedOperation, OSError)
assert issubclass(io.UnsupportedOperation, ValueError)

# === StringIO accumulates writes ===
buffer = io.StringIO()
assert buffer.write('one') == 3
buffer.write(' two')
assert buffer.getvalue() == 'one two'
assert buffer.tell() == 7

# === and starts from a value ===
buffer = io.StringIO('hello')
assert buffer.getvalue() == 'hello'
assert buffer.read(3) == 'hel'
assert buffer.read() == 'lo'
assert buffer.read() == ''

# === Reading lines ===
assert io.StringIO('a\nb\n').readlines() == ['a\n', 'b\n']
assert io.StringIO('a\nb\n').readline() == 'a\n'
assert [line.strip() for line in io.StringIO('a\nb\n')] == ['a', 'b']

# === Seeking rewinds, and a write overwrites in place ===
buffer = io.StringIO('abcd')
buffer.seek(0)
buffer.write('X')
assert buffer.getvalue() == 'Xbcd'
assert buffer.tell() == 1
assert buffer.seek(0, 2) == 4

# === truncate ===
buffer = io.StringIO('abcdef')
buffer.seek(3)
buffer.truncate()
assert buffer.getvalue() == 'abc'

# === BytesIO is the same class in bytes ===
binary = io.BytesIO()
binary.write(b'ab')
assert binary.getvalue() == b'ab'
assert io.BytesIO(b'xyz').read(2) == b'xy'

# === Each refuses the other's data ===
try:
    io.StringIO().write(b'x')
    assert False, 'expected TypeError'
except TypeError:
    pass

try:
    io.BytesIO().write('x')
    assert False, 'expected TypeError'
except TypeError:
    pass

# === A closed stream refuses everything ===
buffer = io.StringIO('x')
buffer.close()
assert buffer.closed
try:
    buffer.read()
    assert False, 'expected ValueError'
except ValueError:
    pass

# === Context manager ===
with io.StringIO() as buffer:
    buffer.write('done')
    assert buffer.getvalue() == 'done'
assert buffer.closed

# === readable / writable / seekable ===
buffer = io.StringIO()
assert buffer.readable() and buffer.writable() and buffer.seekable()
