# CLAUDE.md

This file guides Claude Code (claude.ai/code) in this repository.

## What this is

**XmlSourceSpans** is a small .NET library. It loads XML into ordinary LINQ to XML documents and records, for every element, its start and end offsets in its source: the outer range (its `<` through the `>` that closes it) and the inner range (its content, between its tags), in bytes and in characters, with the line and column of its start. All of it comes from the one parse. Its public surface:
- `XSpanReader`, an `XmlReader` over an `XSource`: construct one, load from it with `XDocument.Load` (alone or under another reader), then `Annotate` the document; or load in one call with its static `Parse` / `Load` / `LoadFile` / `Load(XSource)`
- the types `XSpan` and `XRange`; a span carries the `XSource` it counts into, and `XRange` slices any buffer of its own unit without a copy (`Outer` and `Inner`, a span or a memory, a string too)
- `XSource`, the document's source, made by `FromText` / `FromBytes` / `FromFile`: its text, and its bytes when read from bytes, held by reference; a file's URI as its `BaseUri`
- the extension properties `element.Span`, `element.OuterSource`, `element.InnerSource`, `document.Source`, and on a span `OuterChars`, `InnerChars`, `OuterBytes`, `InnerBytes`

The why behind the design, and the paths it rejected, are in [`docs/design.md`](docs/design.md). Read it before changing how spans are found.

## The scenario it serves

A pipeline turns XML documents into database rows. It parses each document to decide which elements become rows, stores each row's content, and beside each row the byte range its element occupies in the source file. A consumer can then read the raw source for any row by offset, without parsing again: the exact element, or everything up to the next row.

Three constraints follow:
- **The sources cannot be trusted to share one spelling.** They may be hand-written, or produced by different tools. So their spelling cannot be assumed to match any serializer's: quote style, `<x/>` or `<x />` or `<x></x>`, character references, whitespace inside tags. Offsets must come from the source itself, never from writing the tree back out.
- **The tree must stay LINQ to XML's own.** Callers' code is `XElement`-based.
- **Cost matters.** Corpora are large, so work and allocations per element count.

## Commands

```bash
dotnet build
dotnet build -c Release                        # also writes the package, .nupkg and .snupkg, into XmlSourceSpans/bin/Release/
dotnet test                                    # the whole suite: xunit v3 on Microsoft.Testing.Platform
dotnet test --filter-class "*CorpusTests"      # one class
dotnet test --filter-method "*Iliad*"          # matching methods
dotnet run -c Release tools/timing.cs -- <file.xml> [iterations]   # allocations and time against XDocument.Parse
```

## Architecture

| File | Holds |
|---|---|
| `XmlSourceSpans/XSpanReader.cs` | The recording reader, public: an `XmlReader` wrapping .NET's own over an `XSource`'s text. It forwards every member and notes where each element opens (`OnElement`) and closes (`OnEndElement`), counting bytes as it goes (`ByteAt`). `XDocument.Load` builds the tree over it, then `Annotate` pairs the recorded spans with the tree's elements in document order. The static one-call loads take that same path. |
| `XmlSourceSpans/XSpan.cs` | `XSpan`, the annotation, and `XRange`, the four offsets of an element in one unit, with its copy-free slices |
| `XmlSourceSpans/XSpanExtensions.cs` | The C# 14 extension properties on `XElement`, `XSpan` and `XDocument` |
| `XmlSourceSpans/XSource.cs` | The source: the decoded text, the bytes it was read from, the encoding, the byte-order mark's length, a file's base URI. And how one is made: `FromText`; `FromBytes`, the decoding (the byte-order mark, the declaration checked before the bytes are decoded, the strict decode with the failing byte named); `FromFile` |
| `tools/timing.cs` | A file-based benchmark against `XDocument.Parse` |

## Invariants — do not break

1. **The tree is .NET's own.** `XDocument.Load` builds it over the recording reader. Never build a tree of our own. With the default settings, `XNode.DeepEquals` against `XDocument.Parse` holds (`ParityTests`); the one deliberate difference is a string opening with U+FEFF, which loads here.
2. **One parse.** Positions come from the reader's own line and column, never from re-serializing and never from a second parse.
3. **The arithmetic.**
   - The reader reports an element at its name, one past `<`, and an end tag at its name, two past `<`.
   - Columns count UTF-16 code units from the line's start; a caller's `LineNumberOffset` and `LinePositionOffset` come back off first.
   - Lines break at `\n`, `\r\n` and a lone `\r`.
   - A start tag ends at the first `>` outside a quoted attribute value; an end tag at the first `>` after its name.
4. **The text must hold the element where the reader says.** Before a span is recorded, the text at the computed position must hold the element's `<` and its own name.
5. **Offsets only move forward.** So bytes are counted incrementally, in one pass. An element behind the count, or under another base URI than the document's, came from an entity's replacement text, which has no place in the document's own text: it is refused (`NotSupportedException`), never spanned somewhere misleading.
6. **Units.** Ranges are half-open. Bytes count from the source's first byte, a byte-order mark included. Characters index the decoded text, and a leading U+FEFF in a caller's string counts.
7. **Refuse, never guess.** Any other mismatch throws `InvalidOperationException` ("lost its place") rather than returning a wrong span.

## Tests

| Class | Pins |
|---|---|
| `ParityTests` | the tree against `XDocument.Parse`'s, line info asked for, the caller's reader settings replacing the defaults, the entity-expansion cap |
| `SpanTests` | hand-counted offsets, lines and columns; entity markup refused, internal and external; the caller's line offsets; every fixture's invariants, whitespace kept and dropped |
| `EncodingTests` | UTF-8 and UTF-16, byte-order marks, strict decoding and the failing byte, declarations read before decoding, a file's base URI |
| `SourceTests` | `OuterSource` and `InnerSource` as written, wherever the element goes; copies and elements built in code |
| `SliceTests` | slices are the source's own memory, the bytes the very array given; each unit's slice from its own buffer; what `XSource` holds |
| `ReaderTests` | the reader driven by hand, alone or under another reader (one dropping comments, one validating against a schema), loads what the one-call load does; whitespace decided by the reader; a file's URI carried by its source; the misuses `Annotate` refuses; async reading not supported yet |
| `GeneratedTests` | 400 seeded random documents, every invariant in four encodings; a coverage guard, so the generator can't go narrow; a control, so the checks can fail |
| `CorpusTests` | real documents (`Tests/Corpus/`, byte-exact against `SHA256SUMS`, every file listed): every invariant, and figures measured independently by `grep -b` |

`Invariants.cs` is the oracle: every check derives from the text alone, measured once per document so it stays linear. Theories expand per data row, so the suite runs far more cases than it has methods.

## Conventions

- `net10.0`, `Nullable` disabled, `ImplicitUsings`, tabs (`.editorconfig`).
- Every public member carries its XML doc; the build has no doc warnings to suppress.
- xunit v3 with `global using static Xunit.Assert`; test names read as sentences; theory data as `TheoryData<T>`.
- LF line endings everywhere (`.gitattributes`). The corpus is never converted.

## Status and next steps

0.1: element spans, UTF-8 and UTF-16, copy-free slices, parity with `XDocument.Parse`, the corpus. Open, roughly in order:
1. **Spans for other nodes:** text, comments, CDATA, processing instructions, attributes. The reader meets them all, and the annotation model extends to them.
2. **Markup from DTD entities.** It is refused today; what such an element's span should mean is still to be decided (`docs/design.md`, "Open questions").
3. **More encodings:** any stateless encoding a declaration names.
4. **A BenchmarkDotNet project,** if timing ever becomes a gate.
