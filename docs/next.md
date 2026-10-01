# Next: API changes made before the first commit, to revisit

## Summary (short)

A review before the first commit changed the public API's shape in four places: `XSpanReader` became a static class over an internal reader, `XSpan` gained the source it counts into, `XSource.Bom` became `BomLength`, and spans gained slices that choose their own buffer. Each is listed below with what it was, why it changed, what it costs, what else could be done, and what a rollback would touch. All four are decided. The first was decided on 2026-09-30: `XSpanReader` is now the public reader, built over an `XSource` callers can make. The other three were decided on 2026-10-01, kept as they stand. The behaviour fixes made at the same time are listed after them, for completeness.

## 1. `XSpanReader`: the public reader, built over an `XSource` — decided 2026-09-30

**Decided.** `XSpanReader` is the public reader, `public sealed class XSpanReader : XmlReader, IXmlLineInfo`, constructed over an `XSource` that callers can make. Nothing useful stays hidden, and a reader you construct and drive is what readers elsewhere in .NET look like. The one-call statics stay beside it.

```cs
XSource source = XSource.FromFile("book.xml");
using XSpanReader reader = new(source);
XDocument doc = XDocument.Load(reader);   // or wrap it in another reader first
reader.Annotate(doc);
```

**Before.** `public sealed partial class XSpanReader : XmlReader, IXmlLineInfo` held both the three entry points (`Parse`, `Load`, `LoadFile`, all static) and the recording itself. Its only constructor was private, so its 38 public instance members — `Read` and the forwarders to the inner reader — could never be reached by a caller, carried no XML docs, and `<NoWarn>1591</NoWarn>` hid the warnings. `design.md` sketched `XDocument.Load(new XSpanReader(inner), options)`, which did not compile.

A review then made `XSpanReader` a static class holding the entry points and the decoding, over an `internal sealed class SpanRecordingReader` holding the recording. That gave the one-call loads an honest home, but it hid a reader callers could use; so the reader is public again, this time constructible.

**Now.**
- `XSpanReader` is the reader, the recording merged back into `XSpanReader.cs`, constructed as `new XSpanReader(source, options, settings)`: the static loads' own parameters and defaults. Its 38 forwarders carry `/// <inheritdoc/>`; `Read`, the constructor and `Annotate` have docs of their own, and the build still has no doc warnings.
- `XSource` is made by `FromText`, `FromBytes` and `FromFile`. The decoding moved into it, and its constructor went private. A new `BaseUri` carries a file's URI to the reader, so `new XSpanReader(XSource.FromFile(path))` loads as `LoadFile(path)` does.
- A fourth one-call load, `XSpanReader.Load(XSource)`, is the path the other three take.
- No `Create` factories. `XmlReader.Create` is inherited, so `XSpanReader.Create(stream)` already compiles, to a plain reader without spans, and a `Create` of ours would share that overload set. The class doc says so.

**The old objection, met.** A public reader needs more than an `XmlReader` to wrap: the text the inner reader reads, the source's bytes, encoding and mark, where line 1 starts, the caller's line offsets. Built from an `XSource`, it has them all, and it makes .NET's reader over the text itself, so the two cannot disagree. `Annotate` still runs after `XDocument.Load`: that two-step contract is public now, and guarded.

**What it costs, documented rather than fixed.**
- Whitespace is the reader's call. A tree loaded from a reader keeps the whitespace the reader reports, so `PreserveWhitespace` goes to the constructor; given only to `XDocument.Load`, it comes too late (`ReaderTests`).
- `Annotate` is a step a caller can forget. It refuses before the reader's end, a second time, on a document with spans already, and on a count mismatch, which leaves that document as it was. A different document of the same shape still pairs without complaint; checking names would not catch it either, since the same shape has the same names.
- It is tested under `XDocument.Load`, alone and under a wrapping reader. Async reading is not supported: `XDocument.LoadAsync` over it throws `NotImplementedException`, from `XmlReader`'s own `ReadAsync` (probed 2026-10-01).

**Weighed, not taken.** Keeping the reader internal: the smallest surface, but it hid a useful reader. A public reader under another name beside a static `XSpanReader`: the least churn, but the type named Reader would not be the reader. An instance loader configured once, such as `new XSpanLoader(options)`: worth weighing again when configuration grows, with the node kinds to span or the encodings to read.

**Still open.** Annotating a subtree read with `XNode.ReadFrom`, to stream a large file record by record; async reading.

## 2. `XSpan` carries its `Source` — decided 2026-10-01

**Decided.** Kept: a span carries its source, so `OuterSource` holds wherever the element goes, for 8 bytes more per span. Weighed, not taken: computing `Line` and `Column` from the source's line starts instead of storing them. That would pay the 8 bytes back, but the source would keep its line starts alive, 4 bytes a line, and the two would become lookups. It can still come later, since `span.Line` reads the same either way.

**Before.** `XSpan(XRange Bytes, XRange Chars, int Line, int Column)`; `OuterSource` and `InnerSource` sliced `element.Document`'s source with the element's span.

**The defect it fixed.** An element removed from one loaded document and added to another read the *other* document's text with its own offsets — silently wrong text, or `ArgumentOutOfRangeException` from a property getter when that document was shorter. The docs promised null instead.

**Now.** `XSpan(XRange Bytes, XRange Chars, int Line, int Column, XSource Source)`. The slices read the span's own source, so they hold wherever the element goes: removed, or moved into another document. An edit to the element after the load does not change them.

**Cost.** 8 bytes more per element: an `XSpan` is 64 bytes, where it was 56.

**Alternatives.** Keep the four-parameter record and document the hazard; or keep it and make `OuterSource` return null when the element's document is not the one its span was measured in (which needs the source on the span anyway, to compare).

**A rollback touches** `XSpan.cs`, `XSpanExtensions.cs` (the slices, `OuterSource`, `InnerSource`), `XSpanReader.cs` (where spans are built), `SourceTests` (the removed and moved element), `Invariants.cs` (`Same(source, span.Source)`), the README's "Spans are annotations" and `design.md`'s memory figures.

## 3. `XSource.Bom` → `XSource.BomLength` — decided 2026-10-01

**Decided.** `BomLength` stays: the name says what the property holds.

The property held a length, not a mark; the old name needed its doc to read. A rollback is the rename back, in `XSource.cs`, `XSpanReader.cs` and `SliceTests`.

## 4. Slices that choose their own buffer — decided 2026-10-01

**Decided.** Kept as they are: one `XRange` for both units, and slices on the span that pick the buffer. Unit-typed ranges, a generic `XRange<byte>` and `XRange<char>`, were probed on 2026-10-01: the wrong cut stops compiling (`span.Chars.Outer(source.Bytes)` is CS1503), and a `string` or `byte[]` needs no overload of its own. Not taken: every declaration would repeat the unit the property's name already carries (`XRange<char> chars = span.Chars`), and written inline the mismatch already reads wrong. What's left is a range passed through a variable or a parameter, which `XRange`'s doc warns about.

**Added.** Extension properties on `XSpan` — `OuterChars` and `InnerChars` (`ReadOnlySpan<char>` of the source text), `OuterBytes` and `InnerBytes` (`ReadOnlyMemory<byte>` of the source bytes, empty for a document parsed from a string) — and `XRange.Outer(string)` / `Inner(string)` overloads.

**Why.** An `XRange` does not know its unit, so `span.Chars.Outer(source.Bytes)` compiles and returns the wrong characters; and a plain `string` did not infer for the generic overloads (CS0411), so `.AsSpan()` was required.

**Alternatives.** Unit-typed ranges (`XByteRange` and `XCharRange`, so the wrong cut cannot compile); slice methods on `XSource` instead (`source.Outer(span)`); or neither, with the hazard documented. The slices are extension properties rather than record members so they stay out of the record's equality and its `ToString`.

**A rollback touches** `XSpanExtensions.cs`, `XSpan.cs`, `SliceTests`, `Invariants.cs`, the README's "Slices, without copies".

## Behaviour fixed at the same time (not shape)

- An element brought in by an external entity is refused (`NotSupportedException`), found by the reader's base URI; before, it could take another node's span silently.
- Before a span is recorded, the text must hold the element's `<` and its own name at the computed position.
- Entity markup written with a character reference is refused as entity markup, not reported as a defect; a caller's `LineNumberOffset` and `LinePositionOffset` are taken back off.
- Refused plainly now: UTF-16 without a byte-order mark, a second mark, an ASCII declaration over a byte above 0x7F, a UTF-16 declaration naming the other byte order. The declaration is read before the bytes are decoded; a UTF-16 decoding error names its exact byte; the byte count is checked against `int` overflow.
- `LoadFile` passes the file's URI as the base URI.
- Every public member documented, the three entry points with their exceptions; messages in sentence case, a defect's with the issues URL.

## Kept as they were, by choice

- `Nullable` disabled, so the public API is nullable-oblivious to consumers who enable it.
- `InvalidDataException` for encoding refusals, where .NET's own reader uses `XmlException`: the bytes fail before they are XML.
- Version `0.1.0`, no pre-release suffix: a 0.x version already says the API may change.
- `net10.0` only: the extension properties are C# 14.
- The test project's VSTest packages, for editors that discover tests that way.
