# Design

## Summary (short)

LINQ to XML knows where an element starts but never where it ends, and writing an element back out does not reproduce its source's bytes. XmlSourceSpans wraps .NET's own `XmlReader` in a reader that forwards every call and notes where each element opens and closes. .NET's own loader builds the tree over it, and each element then receives its span as an annotation. The tree stays LINQ to XML's, the text is parsed once, and every position comes from the source. On a 1.7 MB document it allocates 87 bytes per element more than a plain `XDocument.Parse`, at about its time.

## The problem

A program that parses XML to understand its structure sometimes also needs each element's exact place in the original file. It might store byte offsets in an index, slice an element's raw source, report a precise location, or compare a file's own spelling with a serializer's. LINQ to XML gives two partial answers, and neither is enough:

- **`LoadOptions.SetLineInfo`** records an element's start, as a line and column, and only its start. .NET does record the end-tag position too while it loads (`XContainer`'s content reader calls `SetEndElementLineInfo` at each `EndElement`), but it keeps that in an internal annotation, with no public API.
- **Serialization** (`ToString()`, `XmlWriter`) writes the tree in one house spelling. The source may have written `<x/>`, `<x />` or `<x></x>`; either quote character; `&#945;` or `α`; whitespace inside its tags. Every spelling becomes the same tree, and the tree becomes the serializer's one spelling. So an element written back out is not its source's bytes, and its length is not the source's length.

## What was considered

1. **Compare a re-serialization with the file.** Write each element out, lay the result over the file at its start, and walk past the known spelling differences. This works while the spellings agree and fails at the first difference nobody anticipated. It depends on the one thing a source cannot promise.
2. **Scan the file's own tags from each start:** depth up at a start tag, down at an end tag or `/>`, skipping comments, CDATA, instructions and quoted attribute values. This is independent of spelling, but it is a second walk over every element's bytes, and it duplicates, by hand, knowledge the parser already has.
3. **Read .NET's internal end annotation by reflection.** It is recorded already, so this would need no second pass. But it would mean reflection on a private type that any .NET update may change. Not a foundation.
4. **A full-fidelity parser** such as GuiLabs.Language.Xml, a Roslyn-derived parser that round-trips every character and exposes each node's position. Its tree is its own, not `XElement`. Reaching LINQ to XML from it would mean a second parse, or a conversion that has to reproduce `XDocument`'s semantics exactly. By its author's account it is not tuned for performance.
5. **A tree builder of our own over `XmlReader`,** replacing `XDocument.Load`. This is one parse, and it knows every position. But whitespace handling, CDATA, entity expansion, namespaces, declarations and line info would all have to match `XDocument.Load` exactly, forever, and any drift changes the tree a caller gets.
6. **Chosen: a forwarding reader under .NET's own loader.** `XSpanReader` is that reader: an `XmlReader` wrapping .NET's own over the source's text, so `XDocument.Load(reader, options)` builds exactly the tree it always builds. It forwards every member to .NET's reader and, in `Read()`, notes each `Element` and `EndElement` from the reader's own line and column. Afterwards `Annotate` pairs the recorded spans with the tree's elements in document order, since LINQ to XML creates elements in the order the reader opens them. The static `Parse`, `Load` and `LoadFile` take that path in one call; a caller can also drive the reader, or stack another reader over it. One parse, the platform's own tree, positions from the source.

## The arithmetic

These were measured, not assumed: a probe over a private corpus of 3,011 files (350 MB, 2,028,764 elements, all with LF line ends) found no exception to the start-tag and end-tag rules, and this library's tests hold every rule, line ends included.

- **An element** is reported at its name, one past its `<`. So `<` sits at `lineStart + column - 2`, with columns 1-based, and the start tag runs on to its first `>` outside a quoted attribute value. For an empty element, that `>` ends it.
- **An end tag** is reported at its name, two past its `<`. The tag runs on to the first `>` after its name, since whitespace may stand before the `>`.
- **Columns** count UTF-16 code units from the line's start. A character outside the Basic Multilingual Plane is two, and a tab is one. A caller's `LineNumberOffset` (added by .NET to every line) and `LinePositionOffset` (added to line 1's columns only) come back off before anything is counted.
- **Lines** break where XML says: `\n`, `\r\n`, a lone `\r`.
- **Bytes.** Every offset the reading records lies at or past the one before it. So byte offsets are counted in one forward pass, `GetByteCount` over each stretch between consecutive offsets, from the byte-order mark on. No sorting and no arrays of offsets.

Before any span is recorded, the text at the computed position must hold the element: its `<` (or `</`) and its own name, as the reader names it, ended by whitespace, `/` or `>`. That check, with the forward-only count, makes a wrong position impossible to record silently: a position on a real element's start makes that element step backwards when its own turn comes, and a position anywhere else fails the name.

Three refusals follow:
- **Entity markup, internal.** An element behind the count belongs to markup an entity's replacement text brought in, since the DTD stands before the document element. It has no place in the document's own text, and the reader refuses (`NotSupportedException`) rather than span it somewhere misleading.
- **Entity markup, external.** With a resolver of the caller's, an external entity's elements report their lines and columns in the entity's own file. The reader's base URI tells them apart: an element under another base URI than the document's root is refused the same way.
- **Any other mismatch** between the reader's report and the text is a defect in the arithmetic. It throws `InvalidOperationException`, naming where; it never guesses.

## Memory

A first version counted bytes afterwards: every offset sorted, then counted once. That cost 256 bytes per element over a plain parse, most of it transient arrays. Counting as the reading goes removed the arrays and the sort, and sizing the list of spans once — from a count of the text's start tags, taken in the same scan that finds its line starts — removed the list's doubling. On a 1.7 MB document of 17,893 elements the overhead is now 87 bytes per element (7.31 MiB against 5.83 MiB, whitespace preserved), less than `SetLineInfo` adds on its own (9.10 MiB). Allocations repeat exactly from run to run; times vary by a fifth or more between identical runs, and the spans run at about the plain parse's time.

Of the 87 bytes, 64 are the `XSpan` itself: two ranges, a line and a column, and a reference to its source, which lets a span read true wherever its element goes. That is the minimum: LINQ to XML annotations are objects (`Annotation<T>() where T : class`), so one small object per element is unavoidable. A `ref struct` cannot be an annotation at all. `XRange` is a struct and allocates nothing of its own.

## Slices

`OuterSource` and `InnerSource` return strings, so each read copies the element's text: two bytes per character, on every access. They stay as the convenient forms. For everything else, an `XRange` slices any buffer of its own unit, the way `string` offers both `AsSpan` and `AsMemory`:
- `Outer` and `Inner` over a `ReadOnlySpan<T>`, for a hot loop. A span can't be stored, captured or carried across an `await`, which is exactly right inside a loop.
- The same over a `ReadOnlyMemory<T>`, to keep.

`XSource` holds the document's text and, when it was loaded from bytes, those bytes, so the slices need no buffer of the caller's own; the document carries it (`document.Source`), and so does every span (`span.Source`). Both buffers are held by reference. The byte array is the very one the caller passed in, so holding it costs nothing but its lifetime: it lives as long as the document or any of its spans. A range does not know its unit, so a span's own slices — `span.OuterChars` from the text, `span.OuterBytes` from the bytes, and their inner forms — pick the buffer, and a range cut against the wrong one cannot happen through them.

Copying slices out instead would cost more, not less. Spans nest, so a stanza's bytes contain its lines' bytes, and per-element copies would duplicate every byte once per level of nesting, with an object per slice besides. Re-encoding a slice of text on demand would be a copy and an encoding pass on every read.

The alternatives weighed:
- **Returning `ReadOnlyMemory<char>` from `OuterSource`.** This loses the null that says "no span", since an empty memory reads the same as an empty inside, and it makes every caller who wants a string write `.ToString()`.
- **Strings only.** They copy on every read.

## Open questions

### Markup from DTD entities

With `<!ENTITY e "<b>bold</b>">`, the `<b>` element is written once, inside the DTD, and `&e;` brings in a copy of it wherever it is used; an external entity's elements are written in a file of their own. Such a span could mean three different things: the reference `&e;` in the body, the declaration (or the entity's file), or nothing at all. Each is a choice about meaning, not a matter of work. The reader reports no sign that an element came from an entity, and the backwards offset or the changed base URI is how one is noticed, so refusing is cheap. It is refused until a source needs it and the choice is made.

### Beyond elements

The reader meets every node, so text, comments, CDATA, processing instructions and attributes could carry spans too, and the annotation model extends to them. Attributes are the hard case: `XmlReader` reports an attribute's position, but nothing about its value's extent, so that would take a scan of the start tag.

### Encodings

UTF-8 and UTF-16 are told apart by the byte-order mark. Any stateless single-byte or multi-byte encoding a declaration names could be supported by decoding with it and counting bytes in it. A declaration naming another encoding is refused today, because decoding the bytes as UTF-8 regardless would put every byte offset out.
