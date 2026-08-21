# Extension: SystemExit carries a status.
# Upstream Monty's `sys` has no `exit`, so `sys.exit(2)` was a NameError. Raising
# SystemExit by hand worked, but nothing read the code off it — `python check.py || handle`
# could not fire. Here the exception carries its argument, and a host reads the status
# from it. What that status becomes is the host's business, which is why this fixture
# checks the exception rather than an exit code.
import sys

assert callable(sys.exit)

# === sys.exit raises SystemExit with the status as its argument ===
try:
    sys.exit(3)
    assert False, 'expected SystemExit'
except SystemExit as e:
    assert e.args == (3,)

try:
    sys.exit()
    assert False, 'expected SystemExit'
except SystemExit as e:
    assert e.args == (None,)

# A non-integer argument is a message; a host prints it and exits 1.
try:
    sys.exit('no config found')
    assert False, 'expected SystemExit'
except SystemExit as e:
    assert e.args == ('no config found',)

# === SystemExit is not an Exception, so a bare `except Exception` does not swallow it ===
caught = False
try:
    try:
        sys.exit(1)
    except Exception:
        caught = True
except SystemExit:
    pass
assert not caught, 'Exception should not catch SystemExit'

# === raise SystemExit is the same thing ===
try:
    raise SystemExit(4)
except SystemExit as e:
    assert e.args == (4,)
