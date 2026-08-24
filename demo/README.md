# Screenshot files

Sample files for taking screenshots of nib's highlighting and themes. Each one
is sized to fill a normal terminal without scrolling, and front-loads its most
colourful constructs so the first screen is the interesting one.

```powershell
nib demo/demo.yaml          # then Alt+T to cycle themes
nib -l demo/demo.py         # with the line-number gutter
nib --theme monokai demo/demo.js
```

| File | Shows off |
|---|---|
| `demo.py` | Docstrings, decorators, type hints, f-strings, dunder names |
| `demo.js` | Template literals, classes, async/await, numeric separators, spread |
| `demo.sql` | CTEs, window functions, `CASE`, dialect keywords, aligned aliases |
| `demo.json` | Nested objects, arrays, strings, numbers, booleans |
| `demo.toml` | Tables, array-of-tables, dates, arrays, comments |
| `demo.yaml` | Anchors and merge keys, block scalars, flow mappings, quoted scalars |
| `demo.cs` | **Nothing — see below** |

`demo.cs` opens **uncoloured**. There is no C# grammar in the vendored set (21
files, none of them `source.cs`), so `.cs` falls through to `NullHighlighter`.
That is correct behaviour rather than a bug, but it makes for a dull screenshot.
Use `tests/fixtures/highlight/sample.ts` for a C-family language that does
colour, or keep `demo.cs` around as the "unknown file type opens clean" shot.

These are not test fixtures — `tests/fixtures/highlight/` holds those, and they
are deliberately tiny. Delete this directory whenever it stops being useful.
