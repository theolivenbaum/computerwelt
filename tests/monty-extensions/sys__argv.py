# argv
# Extension: sys.argv reflects the invocation.
# Upstream Monty's `sys` is attribute-only — platform, version, version_info, stdout and
# stderr — with no argv at all, so a script could not read its own options. The harness
# runs this fixture with ['sys__argv.py', 'alpha', 'beta'].
import sys

assert isinstance(sys.argv, list)
assert len(sys.argv) == 3
assert sys.argv[0] == 'sys__argv.py'
assert sys.argv[1:] == ['alpha', 'beta']
assert all(isinstance(a, str) for a in sys.argv)

# argv is an ordinary list, so the usual option handling works.
flags = [a for a in sys.argv[1:] if a.startswith('-')]
positional = [a for a in sys.argv[1:] if not a.startswith('-')]
assert flags == []
assert positional == ['alpha', 'beta']
