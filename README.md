# XmlSourceSpans

[![NuGet](https://img.shields.io/nuget/v/XmlSourceSpans.svg)](https://www.nuget.org/packages/XmlSourceSpans)

Byte offsets for LINQ to XML: where every element of an `XDocument` starts and ends in the source you loaded it from, and where its content starts and ends.

```text
<line n="1">Sing, O goddess, the anger of Achilles</line>
^           ^                                     ^      ^
Start       InnerStart                     InnerEnd    End
```

Load XML from a file, a `byte[]` or a string, and you get back an ordinary `XDocument` in which every element carries its span: these four offsets into that very source, in bytes and in characters, with the line and column of its start. So you can go straight back to any element in the original bytes, or to just its content, and read it exactly as it was written, without parsing again. All of it comes from the one parse you were already making.

LINQ to XML's own line info (`LoadOptions.SetLineInfo`) gets you only part of the way: a line and a column for each element's start tag. It gives you nothing for where an element ends, or for where its content starts and ends, and a line and column is not an offset: it still has to be counted back into bytes before you can seek to it.

```cs
using System.Xml.Linq;
using XmlSourceSpans;

XDocument doc = XSpanReader.LoadFile("book.xml");

XElement line = doc.Root.Element("line");
XSpan span = line.Span;

int start = span.Bytes.Start;           // the byte of its '<' in the file
int innerStart = span.Bytes.InnerStart; // just past its start tag, where its content starts
int innerEnd = span.Bytes.InnerEnd;     // where its content ends, at its end tag's '<'
int end = span.Bytes.End;               // one past the '>' that closes it

XRange chars = span.Chars;                 // the same four offsets in the decoded text
(int l, int c) = (span.Line, span.Column); // the line and column of its '<'

string asWritten = line.OuterSource; // the element exactly as the file wrote it
string content = line.InnerSource;   // its content, as written
```

The byte offsets are the file's own, so a program that stores them can read an element back later, straight from the file, with no parse at all:

```cs
using FileStream file = File.OpenRead("book.xml");

byte[] buffer = new byte[end - start];
file.Position = start;
file.ReadExactly(buffer); // the element, exactly as the file holds it
```

### Slices, without copies

`OuterSource` and `InnerSource` are the convenient forms, and each read copies the element's text into a new string. To avoid the copy, a span slices its own source in either unit: the text the source was decoded to, and the bytes it was loaded from, both held by reference.

```cs
ReadOnlySpan<char> text = span.OuterChars;         // the element in the source text, no copy
ReadOnlyMemory<byte> raw = span.OuterBytes;        // the element in the bytes, to keep, no copy
ReadOnlySpan<byte> contentBytes = span.InnerBytes.Span;
```

An `XRange` also slices any buffer of its own unit — `span.Chars.Outer(text)`, `span.Bytes.Inner(bytes)` — a span for a hot loop, a memory to keep. A range does not know its unit, so cut `Chars` against text and `Bytes` against bytes; the span's own slices choose the buffer for you.

A document loaded from bytes keeps that very array, not a copy, so it lives as long as the document does; don't change it afterwards. A document parsed from a string has no bytes (`OuterBytes` is empty), and its byte offsets are those of the text in UTF-8.

## Driving the reader yourself

`XSpanReader` is itself an `XmlReader`, so you can construct one over a source, load from it, and then lay the spans on the document it built:

```cs
XSource source = XSource.FromFile("book.xml");   // or FromBytes, FromText
using XSpanReader reader = new(source);
XDocument doc = XDocument.Load(reader);          // or wrap it in another reader first
reader.Annotate(doc);
```

**Why drive it yourself.** Because it is an `XmlReader`, it goes wherever a reader goes. You can stack another reader over it before the load: one of .NET's, through `XmlReader.Create(reader, settings)`, to validate the document against a schema or drop its comments on the way in; or one of your own. Or you can hand it to code that builds its tree from a reader, such as a loading helper of your own. Whatever reads through it, every element is still spanned, as long as the tree keeps the source's elements one for one: a reader in between may drop comments, processing instructions or whitespace, but not elements, since each span pairs with an element of the tree.

**When one call is enough.** If all you need is the document and its spans, the one-call loads are those same four lines, with no step to forget: `XSpanReader.LoadFile("book.xml")` is exactly the snippet above, and `XSpanReader.Load(source)` loads a source you already have.

When you drive it yourself:

- **Whitespace is the reader's call.** A tree loaded from a reader keeps whatever whitespace the reader reports, so give `LoadOptions.PreserveWhitespace` to the constructor, `new(source, LoadOptions.PreserveWhitespace)`; given only to `XDocument.Load`, it comes too late. `SetLineInfo` and `SetBaseUri` go to `XDocument.Load`, as usual.
- **`Annotate` is a step of its own,** once the load has read the reader to its end. It pairs the spans with the document's elements in document order, so annotate the very document loaded from this reader, before changing it. It throws if the reader isn't at its end, if it has annotated a document already, or if the counts don't match; but a different document of the same shape would pair without complaint.
- **`XSpanReader.Create(…)` is not ours.** It is `XmlReader.Create`, inherited, and makes a plain reader without spans. Construct with `new`.
- **Tested under `XDocument.Load`.** Async reading is not supported yet: `XDocument.LoadAsync` over it throws `NotImplementedException`, from `XmlReader`'s own `ReadAsync`.

## Installation

```bash
dotnet add package XmlSourceSpans
```

Requires .NET 10 (C# 14 extension members).

## Why

LINQ to XML's line info stops at the start: the line and column of an element's name, one past its `<`. It has nothing for the element's end or its content, and no offsets at all. Writing an element back out doesn't give you its source either, because a source may spell the same XML many ways: `<x/>`, `<x />` or `<x></x>`, either quote character, character references, whitespace inside its tags. The serializer writes one spelling.

Some programs parse XML to understand its structure but also need to point back into the exact original bytes. They might store byte offsets in an index, slice the raw source of an element, report precise locations, or compare a file's own spelling against what a serializer would write. Such a program needs each element's start and end offsets in the source, and its content's, whoever wrote the file. XmlSourceSpans records them during the parse.

## How it works

`XSpanReader` is an `XmlReader` that wraps .NET's own. It forwards every call to it and notes where each element opens and closes, and `XDocument.Load` builds the tree over it exactly as it always does. So, with the default settings, the tree is LINQ to XML's own, identical to what `XDocument.Parse` gives for the same text and options. Afterwards each element receives an `XSpan` as an annotation. Every offset comes from the source text itself, never from re-serializing the tree.

- **One parse.** No second parse and no re-serializing. Around the parse there is one linear scan of the text for its line starts, the byte count carried forward as the parse goes, and one walk of the tree to attach the spans.
- **The types you already use.** `XDocument` and `XElement`; the spans ride as annotations.
- **Two units.**
  - Byte offsets count the source's own bytes, a byte-order mark included.
  - Character offsets index the decoded text.
  - Lines and columns follow XML's line ends (`\n`, `\r\n`, a lone `\r`), with columns in UTF-16 code units.
- **Half-open ranges.** An element is `[Start, End)`, its content `[InnerStart, InnerEnd)`. An empty-element tag (`<x/>`) has empty content at its end; `<x></x>` has empty content between its two tags.

## Loading

| Call | Byte offsets |
|---|---|
| `XSpanReader.Parse(string text, …)` | the text's UTF-8 encoding; a declaration's encoding is not consulted, since the text is already decoded |
| `XSpanReader.Load(byte[] bytes, …)` | the bytes themselves: UTF-8 or UTF-16 by byte-order mark (none means UTF-8, and UTF-16 needs one), decoded strictly |
| `XSpanReader.LoadFile(string path, …)` | the file's bytes, as `Load`; the file's URI is the document's base URI |
| `XSpanReader.Load(XSource source, …)` | the source's own: `XSource.FromText`, `FromBytes` and `FromFile` make one as the three calls above read theirs |

Each also takes `LoadOptions`, as `XDocument.Parse` does, and optional `XmlReaderSettings`. By default the reader settings are `XDocument.Parse`'s own: DTDs parsed, whitespace per the options, entity expansion capped. Settings of your own replace those entirely, so whitespace then follows their `IgnoreWhitespace` rather than the options — for example `new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true }`.

One difference from `XDocument.Parse` is deliberate: a string that opens with U+FEFF loads, the mark counted in every offset, where `XDocument.Parse` refuses it.

## What it refuses

It refuses rather than guess:

| Exception | When |
|---|---|
| `XmlException` | malformed XML, as `XDocument.Parse` throws |
| `InvalidDataException` | bytes that do not decode: an invalid sequence (named by its byte), a UTF-32 mark, UTF-16 without a mark, a second mark, or a declaration naming an encoding the bytes are not read in |
| `NotSupportedException` | an element brought in by an entity's replacement text, internal or external: it has no place of its own in the document's text |
| `InvalidOperationException` | XSpanReader lost its place — a defect; please [report it](https://github.com/nicholasp7/XmlSourceSpans/issues), with the input |

## Limits in 0.1

- **Elements only.** Text, comments and attributes have no spans yet.
- **UTF-8 and UTF-16.** Other encodings are refused.
- **No entity markup.** Entities holding only text are fine.
- **Spans are annotations.** A copy (`new XElement(other)`) has none. A span keeps its own source, so `OuterSource` holds wherever the element goes — removed, or moved into another document — and an edit made to the element after the load does not change it.
- **Up to 2 GiB.** Offsets are `int`s; a source whose byte count would pass that throws `OverflowException`.

## Performance

Measured on a 1.7 MB UTF-8 document of 17,893 elements, whitespace preserved (Release, .NET 10, five runs; a MiB is 2^20 bytes):

| Load | Allocated, every run | Time, over five runs |
|---|---|---|
| `XDocument.Parse` | 5.83 MiB | 26 – 41 ms |
| `XDocument.Parse` with `SetLineInfo` | 9.10 MiB | 32 – 46 ms |
| `XSpanReader.Parse` | 7.31 MiB | 24 – 41 ms |
| `XSpanReader.Load` (from the bytes, decoding included) | 9.38 MiB | 32 – 50 ms |

Allocations repeat exactly from run to run: the spans cost 87 bytes more per element than a plain parse, 64 of them the `XSpan` itself. Times vary by a fifth or more between identical runs; the spans run at about the time of a plain parse (0.83 to 1.25 times it, over these five). Measure any file with [`tools/timing.cs`](https://github.com/nicholasp7/XmlSourceSpans/blob/main/tools/timing.cs): `dotnet run -c Release tools/timing.cs -- file.xml`.

## Status

0.1: the API may still change before 1.0. Requires .NET 10 (C# 14 extension members). MIT licensed.
