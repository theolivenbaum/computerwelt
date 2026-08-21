# Extensions beyond Monty

Every fixture here exercises behaviour that **upstream Monty does not have**. Run one
against `monty` itself and it fails — usually at the import, sometimes at the first call.
That is the point: this folder is the record of where this port has deliberately gone past
its specification, so the two can be told apart at a glance.

`tests/monty-spec/` is the opposite: it is upstream's corpus, carried over unchanged, and
it is what "correct" means. Nothing here may contradict anything there. Where the two
would disagree, upstream wins and the behaviour is recorded as a limitation instead —
`open(..., newline='')` is the standing example, refused here because
`monty-spec/open__fs.py` pins the refusal even though this port translates no line endings
and could honour it.

## Format

Identical to `tests/monty-spec/`: ordinary Python whose body is `assert` statements. A
fixture passes when it runs to completion without raising. Two header markers are
available, because some extensions are about what the *host* supplies:

| Marker | Effect |
|---|---|
| `# argv` | `sys.argv` is set to `['<fixture>.py', 'alpha', 'beta']` |
| `# stdin` | standard input is `one\ntwo\nthree\n` |

Each fixture opens with a comment naming the extension and what upstream does instead.

## Skipping

The suite is absolute — no ratchet, every fixture must pass. To run the port against
upstream's corpus alone, as though none of these extensions existed:

```bash
COMPUTERWELT_SKIP_EXTENSIONS=1 dotnet test
```

That skips this folder and nothing else, which is the check that the port has not quietly
come to *depend* on its own extensions.
