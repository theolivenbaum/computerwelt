# === __call__ makes an instance callable ===
# Upstream Monty does not dispatch this dunder: limitations/classes.md lists `__call__`
# among the protocols a user-defined instance does not get. It is added here because a
# callable object is how several real Python APIs present something that is both a function
# and a namespace, and because a host library written in C# needs the same shape.


class Adder:
    def __init__(self, base):
        self.base = base

    def __call__(self, n):
        return self.base + n


add_ten = Adder(10)

assert add_ten(5) == 15
assert add_ten(-10) == 0


# === keyword and default arguments reach it ===
class Greeter:
    def __call__(self, name, greeting='hello'):
        return f'{greeting}, {name}'


greet = Greeter()

assert greet('world') == 'hello, world'
assert greet('world', greeting='hi') == 'hi, world'
assert greet(name='you') == 'hello, you'


# === a callable instance is an ordinary value ===
# The point of the protocol: it goes wherever a function goes.
assert [add_ten(n) for n in [1, 2, 3]] == [11, 12, 13]
assert list(map(add_ten, [1, 2])) == [11, 12]
assert sorted([3, 1, 2], key=Adder(0)) == [1, 2, 3]


# === state survives between calls, which is the reason to write one ===
class Counter:
    def __init__(self):
        self.calls = 0

    def __call__(self):
        self.calls += 1
        return self.calls


count = Counter()

assert count() == 1
assert count() == 2
assert count.calls == 2


# === an instance without __call__ is still not callable ===
# The upstream behaviour this must not disturb: `tests/monty-spec/class__type_errors.py`
# pins this message.
class Plain:
    pass


try:
    Plain()()
    assert False, 'expected a TypeError'
except TypeError as e:
    assert str(e) == "'Plain' object is not callable", str(e)


# === __call__ that is not callable is reported, not followed ===
class Broken:
    __call__ = 3


try:
    Broken()()
    assert False, 'expected a TypeError'
except TypeError as e:
    assert str(e) == "'int' object is not callable", str(e)


# === a cycle is an error, not a crash ===
# Two objects whose `__call__` is the other would recurse forever if the chain were
# followed by recursion. It is followed by a bounded loop instead, so the program gets an
# error it can catch.
class Loop:
    pass


first = Loop()
second = Loop()
first.__call__ = second
second.__call__ = first

try:
    first()
    assert False, 'expected a TypeError'
except TypeError as e:
    assert str(e) == "'Loop' object is not callable", str(e)
