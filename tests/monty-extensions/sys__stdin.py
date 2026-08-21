# stdin
# Extension: `input()` and `sys.stdin`.
# Upstream Monty has neither, so a program could not be a stage in a pipeline. The
# harness runs this fixture with 'one\ntwo\nthree\n' on standard input.
import sys

# === input() reads a line without its terminator ===
assert input() == 'one'

# === sys.stdin shares the position, so what input consumed is not handed out twice ===
assert sys.stdin.readline() == 'two\n'
assert sys.stdin.read() == 'three\n'
assert sys.stdin.read() == ''

# === Past the end, input raises EOFError rather than blocking ===
try:
    input()
    assert False, 'expected EOFError'
except EOFError:
    pass
