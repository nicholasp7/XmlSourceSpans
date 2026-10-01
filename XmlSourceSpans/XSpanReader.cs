using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XmlSourceSpans;

/// <summary>
/// An <see cref="XmlReader"/> over an <see cref="XSource"/> that records, as it reads, where every element stands in the
/// source: its <see cref="XSpan"/>, read back with <c>element.Span</c> once <see cref="Annotate"/> has laid the spans on
/// the document loaded from it.
/// <para>The tree is .NET's own. The reader forwards every call to .NET's own <see cref="XmlReader"/> over the source's
/// text and only watches where each element opens and closes, and
/// <see cref="XDocument.Load(XmlReader, LoadOptions)"/> builds the tree over it. With the default reader settings, a
/// document loaded here is the document <see cref="XDocument.Parse(string, LoadOptions)"/> gives for the same text and
/// options, parsed once, with one <see cref="XSpan"/> annotation on each element. The one difference: a string that opens
/// with U+FEFF loads here, the mark counted in every offset, where <see cref="XDocument.Parse(string, LoadOptions)"/>
/// refuses it.</para>
/// <para>The positions come from the source itself, never from writing the tree back out. A source may spell its XML any
/// way XML allows — either quote, <c>&lt;x/&gt;</c> or <c>&lt;x /&gt;</c> or <c>&lt;x&gt;&lt;/x&gt;</c>, character
/// references, whitespace inside its tags — and an element written back out need not be its source's bytes.</para>
/// <para>Load in one call with <see cref="Parse"/>, <see cref="Load(byte[], LoadOptions, XmlReaderSettings)"/>,
/// <see cref="LoadFile"/> or <see cref="Load(XSource, LoadOptions, XmlReaderSettings)"/>. Or drive the reader yourself:
/// construct one over a source, load with <see cref="XDocument.Load(XmlReader, LoadOptions)"/> — wrapping it in another
/// reader first, if you like — and <see cref="Annotate"/> the document. (<see cref="XmlReader.Create(string)"/>,
/// inherited, makes a plain reader, without spans.)</para>
/// </summary>
public sealed class XSpanReader : XmlReader, IXmlLineInfo
{
	const string Issues = "https://github.com/nicholasp7/XmlSourceSpans/issues";

	readonly XmlReader _inner;
	readonly IXmlLineInfo _at;
	readonly StringReader _textReader;
	readonly XSource _source;
	readonly string _text;
	readonly Encoding _encoding;

	// Where each line starts in the text — line 1 at the origin, past a leading U+FEFF the parser does not see — and what
	// the caller's settings add to the lines and columns the reader reports.
	readonly List<int> _lineStarts;
	readonly int _lineNumberOffset;
	readonly int _linePositionOffset;

	// The document's own base URI, taken at its root; an element the reader reports under another came from an external
	// entity.
	string _baseUri;

	// The byte count so far: the character it has reached, and the byte that character stands at. Every offset the reading
	// records lies at or past the one before it, so each is counted on from the last, in one forward pass.
	int _cursorChar;
	int _cursorByte;

	// Each element's span in the order elements open — the order LINQ to XML creates them in — an open one filled in when
	// it closes; the open ones on a stack, with what their close needs.
	readonly List<XSpan> _spans;
	readonly Stack<Open> _open = new();

	// Whether the reading has reached its end, and whether a document has taken the spans.
	bool _ended;
	bool _annotated;

	/// <summary>A reader over <paramref name="source"/>'s text, recording every element's span as it reads. Load a
	/// document from it with <see cref="XDocument.Load(XmlReader, LoadOptions)"/>, then <see cref="Annotate"/> that
	/// document.</summary>
	/// <param name="source">The source to read. Its <see cref="XSource.BaseUri"/>, if it has one, is the document's base
	/// URI.</param>
	/// <param name="options">Whether the default settings keep insignificant whitespace, as for
	/// <see cref="XDocument.Parse(string, LoadOptions)"/>: <see cref="LoadOptions.PreserveWhitespace"/> keeps it. A tree
	/// loaded from a reader keeps the whitespace the reader reports, so this is where it is decided, not in the options
	/// given to <see cref="XDocument.Load(XmlReader, LoadOptions)"/>; <see cref="LoadOptions.SetLineInfo"/> and
	/// <see cref="LoadOptions.SetBaseUri"/> go there.</param>
	/// <param name="settings">The reader's settings. Null gives <see cref="XDocument.Parse(string, LoadOptions)"/>'s own.
	/// Settings of your own replace those entirely: whitespace then follows their
	/// <see cref="XmlReaderSettings.IgnoreWhitespace"/>, not <paramref name="options"/>, and the cap on entity expansion is
	/// theirs.</param>
	/// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
	public XSpanReader(XSource source, LoadOptions options = LoadOptions.None, XmlReaderSettings settings = null)
	{
		ArgumentNullException.ThrowIfNull(source);

		XmlReaderSettings effective = settings?.Clone() ?? ReaderSettings(options);

		// A leading U+FEFF — a mark the caller's own decoding kept — is text before the markup: the parser starts past it,
		// and every offset counts it.
		int origin = source.Text.Length > 0 && source.Text[0] == XSource.ByteOrderMark ? 1 : 0;

		_textReader = new StringReader(source.Text);

		if(origin == 1)
			_textReader.Read();

		_inner = XmlReader.Create(_textReader, effective, source.BaseUri);
		_at = (IXmlLineInfo)_inner;

		_source = source;
		_text = source.Text;
		_encoding = source.Encoding;
		_lineNumberOffset = effective.LineNumberOffset;
		_linePositionOffset = effective.LinePositionOffset;

		(_lineStarts, int startTags) = Scan(source.Text, origin);

		_spans = new List<XSpan>(startTags);
		_cursorByte = source.BomLength;
	}

	// ── loading in one call ──────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Parse XML text as <see cref="XDocument.Parse(string, LoadOptions)"/> does, recording every element's
	/// span: a source from <see cref="XSource.FromText"/>, loaded. The byte offsets are those of the text encoded as
	/// UTF-8; an XML declaration's encoding is not consulted, since the text is already decoded. A leading U+FEFF is
	/// accepted and counted in every offset.</summary>
	/// <param name="text">The XML.</param>
	/// <param name="options">As for <see cref="XDocument.Parse(string, LoadOptions)"/>.</param>
	/// <param name="settings">The reader's settings. Null gives <see cref="XDocument.Parse(string, LoadOptions)"/>'s own.
	/// Settings of your own replace those entirely: whitespace then follows their
	/// <see cref="XmlReaderSettings.IgnoreWhitespace"/>, not <paramref name="options"/>, and the cap on entity expansion is
	/// theirs.</param>
	/// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
	/// <exception cref="XmlException">The XML is malformed, as <see cref="XDocument.Parse(string, LoadOptions)"/> would
	/// throw.</exception>
	/// <exception cref="NotSupportedException">An element comes from an entity's replacement text, which has no place of
	/// its own in the text to span.</exception>
	/// <exception cref="InvalidOperationException">XSpanReader lost its place: a defect, to be reported with the
	/// input.</exception>
	public static XDocument Parse(string text, LoadOptions options = LoadOptions.None, XmlReaderSettings settings = null)
		=> Load(XSource.FromText(text), options, settings);

	/// <summary>Load XML from its bytes, recording every element's span: a source from <see cref="XSource.FromBytes"/>,
	/// loaded. Its byte offsets count from the first byte, a byte-order mark included. The bytes are UTF-8 or UTF-16,
	/// told apart by a byte-order mark — none means UTF-8, and UTF-16 needs one — and are decoded strictly: an invalid
	/// sequence refuses, as does an XML declaration naming another encoding. The array is held, not copied: do not change
	/// it afterwards.</summary>
	/// <param name="bytes">The XML, as its file holds it.</param>
	/// <param name="options">As for <see cref="XDocument.Parse(string, LoadOptions)"/>.</param>
	/// <param name="settings">As for <see cref="Parse(string, LoadOptions, XmlReaderSettings)"/>: null gives
	/// <see cref="XDocument.Parse(string, LoadOptions)"/>'s own, and settings of your own replace them entirely.</param>
	/// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
	/// <exception cref="InvalidDataException">The bytes do not decode: an invalid sequence, a UTF-32 mark, UTF-16 without a
	/// mark, a second mark, or a declaration naming an encoding they are not read in.</exception>
	/// <exception cref="XmlException">The XML is malformed.</exception>
	/// <exception cref="NotSupportedException">An element comes from an entity's replacement text.</exception>
	/// <exception cref="InvalidOperationException">XSpanReader lost its place: a defect, to be reported with the
	/// input.</exception>
	public static XDocument Load(byte[] bytes, LoadOptions options = LoadOptions.None, XmlReaderSettings settings = null)
		=> Load(XSource.FromBytes(bytes), options, settings);

	/// <summary>Load an XML file, as <see cref="Load(byte[], LoadOptions, XmlReaderSettings)"/> loads its bytes: a source
	/// from <see cref="XSource.FromFile"/>, loaded. The file's own URI is the document's base URI, so
	/// <see cref="LoadOptions.SetBaseUri"/> and a resolver find what lies beside it.</summary>
	/// <param name="path">The file.</param>
	/// <param name="options">As for <see cref="XDocument.Parse(string, LoadOptions)"/>.</param>
	/// <param name="settings">As for <see cref="Parse(string, LoadOptions, XmlReaderSettings)"/>.</param>
	/// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
	/// <exception cref="IOException">The file cannot be read, as <see cref="File.ReadAllBytes(string)"/> throws.</exception>
	/// <exception cref="InvalidDataException">The bytes do not decode, as for
	/// <see cref="Load(byte[], LoadOptions, XmlReaderSettings)"/>.</exception>
	/// <exception cref="XmlException">The XML is malformed.</exception>
	/// <exception cref="NotSupportedException">An element comes from an entity's replacement text.</exception>
	/// <exception cref="InvalidOperationException">XSpanReader lost its place: a defect, to be reported with the
	/// input.</exception>
	public static XDocument LoadFile(string path, LoadOptions options = LoadOptions.None, XmlReaderSettings settings = null)
		=> Load(XSource.FromFile(path), options, settings);

	/// <summary>Load a source, recording every element's span: a reader over it, the document
	/// <see cref="XDocument.Load(XmlReader, LoadOptions)"/> builds over that reader, and the spans laid on the document —
	/// the path every one-call load takes.</summary>
	/// <param name="source">The source, from <see cref="XSource.FromText"/>, <see cref="XSource.FromBytes"/> or
	/// <see cref="XSource.FromFile"/>.</param>
	/// <param name="options">As for <see cref="XDocument.Parse(string, LoadOptions)"/>.</param>
	/// <param name="settings">As for <see cref="Parse(string, LoadOptions, XmlReaderSettings)"/>.</param>
	/// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
	/// <exception cref="XmlException">The XML is malformed.</exception>
	/// <exception cref="NotSupportedException">An element comes from an entity's replacement text.</exception>
	/// <exception cref="InvalidOperationException">XSpanReader lost its place: a defect, to be reported with the
	/// input.</exception>
	public static XDocument Load(XSource source, LoadOptions options = LoadOptions.None, XmlReaderSettings settings = null)
	{
		using XSpanReader reader = new(source, options, settings);

		XDocument doc_res = XDocument.Load(reader, options);

		reader.Annotate(doc_res);

		return doc_res;
	}

	// XDocument.Parse's own reader settings, so the tree comes out the same: insignificant whitespace dropped unless the
	// options preserve it, DTDs parsed, entity expansion capped at ten million characters.
	static XmlReaderSettings ReaderSettings(LoadOptions options) => new() {
		IgnoreWhitespace = (options & LoadOptions.PreserveWhitespace) == 0,
		DtdProcessing = DtdProcessing.Parse,
		MaxCharactersFromEntities = 10_000_000,
	};

	// ── recording ────────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Read the next node, as .NET's reader does, noting where each element opens and closes.</summary>
	/// <returns>True if a node was read; false at the end.</returns>
	/// <exception cref="XmlException">The XML is malformed.</exception>
	/// <exception cref="NotSupportedException">An element comes from an entity's replacement text, which has no place of
	/// its own in the text to span.</exception>
	/// <exception cref="InvalidOperationException">XSpanReader lost its place: a defect, to be reported with the
	/// input.</exception>
	public override bool Read()
	{
		if(!_inner.Read()) {
			_ended = true;
			return false;
		}

		switch(_inner.NodeType) {
			case XmlNodeType.Element:
				OnElement();
				break;

			case XmlNodeType.EndElement:
				OnEndElement();
				break;
		}

		return true;
	}

	// The reader stands at the element's name, one past its '<'. The start tag runs on to its first '>' outside a quoted
	// attribute value, and an empty element ends there, its span whole at once; an open one waits for its close. Before
	// anything is recorded, the text must hold this element there: its '<' and its own name.
	void OnElement()
	{
		RefuseExternalEntity();

		(int line, int name) = CharAt(_at.LineNumber, _at.LinePosition);
		int lt = name - 1;

		if(lt < _cursorChar)
			throw EntityMarkup();

		if(_text[lt] != '<' || !NameStandsAt(name))
			throw Lost($"the start tag of <{_inner.Name}>", lt);

		int afterStartTag = StartTagEnd(lt);

		// A column counts from its line's start; line 1 starts at the text's first character, a leading U+FEFF included.
		int lineStart = line == 1 ? 0 : _lineStarts[line - 1];
		int column = lt - lineStart + 1;

		int startByte = ByteAt(lt);
		int innerStartByte = ByteAt(afterStartTag);

		if(_inner.IsEmptyElement) {
			XRange chars = new(
				Start: lt,
				InnerStart: afterStartTag,
				InnerEnd: afterStartTag,
				End: afterStartTag);

			XRange bytes = new(
				Start: startByte,
				InnerStart: innerStartByte,
				InnerEnd: innerStartByte,
				End: innerStartByte);

			XSpan span = new(
				Bytes: bytes,
				Chars: chars,
				Line: line,
				Column: column,
				Source: _source);

			_spans.Add(span);
			return;
		}

		Open open = new(
			Index: _spans.Count,
			Start: lt,
			InnerStart: afterStartTag,
			StartByte: startByte,
			InnerStartByte: innerStartByte,
			Line: line,
			Column: column);

		_open.Push(open);
		_spans.Add(null);
	}

	// The reader stands at the end tag's name, two past its '<'; the tag runs on to its '>', and the element it closes has
	// its span whole.
	void OnEndElement()
	{
		RefuseExternalEntity();

		(_, int name) = CharAt(_at.LineNumber, _at.LinePosition);
		int lt = name - 2;

		if(lt < _cursorChar)
			throw EntityMarkup();

		if(_text[lt] != '<' || _text[lt + 1] != '/' || !NameStandsAt(name))
			throw Lost($"the end tag of <{_inner.Name}>", lt);

		int gt = _text.IndexOf('>', name + _inner.Name.Length);

		if(gt < 0)
			throw Lost($"the '>' closing the end tag of <{_inner.Name}>", name);

		int innerEndByte = ByteAt(lt);
		int endByte = ByteAt(gt + 1);

		Open open = _open.Pop();

		XRange chars = new(
			Start: open.Start,
			InnerStart: open.InnerStart,
			InnerEnd: lt,
			End: gt + 1);

		XRange bytes = new(
			Start: open.StartByte,
			InnerStart: open.InnerStartByte,
			InnerEnd: innerEndByte,
			End: endByte);

		XSpan span = new(
			Bytes: bytes,
			Chars: chars,
			Line: open.Line,
			Column: open.Column,
			Source: _source);

		_spans[open.Index] = span;
	}

	// An element under another base URI than the document's came from an external entity: its line and column count in
	// the entity's own text, which is not the document's. The root is always the document's own, so its base URI is taken
	// as the document's.
	void RefuseExternalEntity()
	{
		string baseUri = _inner.BaseURI;

		if(_baseUri == null)
			_baseUri = baseUri;
		else if(!string.Equals(baseUri, _baseUri, StringComparison.Ordinal))
			throw EntityMarkup();
	}

	// The reader's own name for the element, qualified as written, standing at the text's offset and ended there: by
	// whitespace, '/', or '>'.
	bool NameStandsAt(int at)
	{
		string name = _inner.Name;
		int after = at + name.Length;

		return _text.AsSpan(at).StartsWith(name, StringComparison.Ordinal)
			&& after < _text.Length
			&& _text[after] is ' ' or '\t' or '\r' or '\n' or '/' or '>';
	}

	// A character offset → its byte offset, counted on from the last. Every offset recorded lies at or past the one
	// before, once entity markup is refused, so the count only moves forward.
	int ByteAt(int c)
	{
		if(c < _cursorChar)
			throw Lost($"an offset at or past character {_cursorChar}", c);

		int stretch = _encoding.GetByteCount(_text.AsSpan(_cursorChar, c - _cursorChar));

		_cursorByte = checked(_cursorByte + stretch);
		_cursorChar = c;

		return _cursorByte;
	}

	// One past a start tag's '>': the first '>' outside a quoted attribute value.
	int StartTagEnd(int lt)
	{
		char quote = '\0';

		for(int i = lt + 1; i < _text.Length; i++) {
			char c = _text[i];

			if(quote != '\0') {
				if(c == quote)
					quote = '\0';

				continue;
			}

			if(c is '"' or '\'')
				quote = c;
			else if(c == '>')
				return i + 1;
		}

		throw Lost("the '>' closing a start tag", lt);
	}

	// A line and a column the reader reports → the line in the text, and the character offset they name. The caller's
	// settings may add to both (LineNumberOffset to every line, LinePositionOffset to line 1's columns), and those come
	// back off first. A column counts UTF-16 code units from its line's start, so a character outside the Basic
	// Multilingual Plane is two.
	(int Line, int Offset) CharAt(int reportedLine, int reportedColumn)
	{
		int line = reportedLine - _lineNumberOffset;
		int column = reportedColumn - (line == 1 ? _linePositionOffset : 0);

		if(line < 1 || line > _lineStarts.Count)
			throw Lost($"line {line} of a text of {_lineStarts.Count} lines");

		int offset_res = _lineStarts[line - 1] + column - 1;

		return (line, offset_res);
	}

	// One pass before the parse: where each line starts, by XML's own line ends (\n, \r\n, a lone \r), line 1 at the
	// origin; and how many start tags the text can hold at most — a '<' before neither '/', '!' nor '?' — so the spans
	// list is sized once.
	static (List<int> LineStarts, int StartTags) Scan(string text, int origin)
	{
		List<int> lineStarts_res = [origin];
		int startTags_res = 0;

		for(int i = origin; i < text.Length; i++) {
			char c = text[i];

			if(c == '\n' || (c == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
				lineStarts_res.Add(i + 1);
			else if(c == '<' && i + 1 < text.Length && text[i + 1] is not ('/' or '!' or '?'))
				startTags_res++;
		}

		return (lineStarts_res, startTags_res);
	}

	Exception EntityMarkup()
		=> new NotSupportedException(
			$"The element <{_inner.Name}> at line {_at.LineNumber}, column {_at.LinePosition} comes from an entity's replacement text, "
			+ "which has no place of its own in the document's text; XSpanReader does not span it.");

	Exception Lost(string what, int at = -1)
	{
		string where = at < 0 ? "" : $" at character {at}";

		return new InvalidOperationException(
			$"XSpanReader lost its place: it expected {what}{where} (the reader at line {_at.LineNumber}, column {_at.LinePosition}), and the text holds "
			+ $"something else. This is a defect in XSpanReader; please report it, with the input, at {Issues}.");
	}

	// What an open element's close needs: its place among the spans, its start tag's offsets in both units, and its
	// start's line and column.
	readonly record struct Open(int Index, int Start, int InnerStart, int StartByte, int InnerStartByte, int Line, int Column);

	// ── annotating ───────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Lay each recorded span on its element in <paramref name="document"/>, and the source on the document,
	/// once <see cref="XDocument.Load(XmlReader, LoadOptions)"/> has read this reader to its end.
	/// <para>LINQ to XML creates elements in the order a reader opens them, so the spans pair with the document's elements
	/// in document order. Annotate the very document loaded from this reader, before changing it: the counts are
	/// checked, but a different document of the same shape would pair without complaint.</para></summary>
	/// <param name="document">The document loaded from this reader.</param>
	/// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
	/// <exception cref="InvalidOperationException">The reader has not been read to its end, or has annotated a document
	/// already; the document has spans already; or its elements do not pair with the spans recorded, which leaves the
	/// document as it was.</exception>
	public void Annotate(XDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		if(!_ended || _open.Count > 0)
			throw new InvalidOperationException("The reader has not been read to its end: annotate the document once XDocument.Load has read it all.");

		if(_annotated)
			throw new InvalidOperationException("The reader has annotated a document already; its spans go on the one document loaded from it.");

		if(document.Annotation<XSource>() != null)
			throw new InvalidOperationException("The document has spans already, from the reader it was loaded from.");

		if(!Pair(document))
			throw Unpaired();

		document.AddAnnotation(_source);
		_annotated = true;
	}

	// Each element, in document order, takes the span recorded when it opened; true when the two run out together.
	// Otherwise the spans laid so far come back off, and the document is as it was.
	bool Pair(XDocument document)
	{
		int k = 0;
		bool more = false;

		foreach(XElement el in document.Descendants()) {
			if(k == _spans.Count) {
				more = true;
				break;
			}

			el.AddAnnotation(_spans[k++]);
		}

		if(!more && k == _spans.Count)
			return true;

		foreach(XElement el in document.Descendants().Take(k))
			el.RemoveAnnotations<XSpan>();

		return false;
	}

	Exception Unpaired()
		=> new InvalidOperationException(
			$"The document's elements do not pair with the {_spans.Count} this reader recorded: annotate the very document loaded from this reader, "
			+ $"before changing it. If you did, this is a defect in XSpanReader; please report it, with the input, at {Issues}.");

	// ── XmlReader and IXmlLineInfo, forwarded to .NET's reader ───────────────────────────────────────────────────

	/// <inheritdoc/>
	public override int AttributeCount => _inner.AttributeCount;

	/// <inheritdoc/>
	public override string BaseURI => _inner.BaseURI;

	/// <inheritdoc/>
	public override bool CanResolveEntity => _inner.CanResolveEntity;

	/// <inheritdoc/>
	public override int Depth => _inner.Depth;

	/// <inheritdoc/>
	public override bool EOF => _inner.EOF;

	/// <inheritdoc/>
	public override bool HasAttributes => _inner.HasAttributes;

	/// <inheritdoc/>
	public override bool HasValue => _inner.HasValue;

	/// <inheritdoc/>
	public override bool IsDefault => _inner.IsDefault;

	/// <inheritdoc/>
	public override bool IsEmptyElement => _inner.IsEmptyElement;

	/// <inheritdoc/>
	public override string LocalName => _inner.LocalName;

	/// <inheritdoc/>
	public override string Name => _inner.Name;

	/// <inheritdoc/>
	public override string NamespaceURI => _inner.NamespaceURI;

	/// <inheritdoc/>
	public override XmlNameTable NameTable => _inner.NameTable;

	/// <inheritdoc/>
	public override XmlNodeType NodeType => _inner.NodeType;

	/// <inheritdoc/>
	public override string Prefix => _inner.Prefix;

	/// <inheritdoc/>
	public override char QuoteChar => _inner.QuoteChar;

	/// <inheritdoc/>
	public override ReadState ReadState => _inner.ReadState;

	/// <inheritdoc/>
	public override XmlReaderSettings Settings => _inner.Settings;

	/// <inheritdoc/>
	public override string Value => _inner.Value;

	/// <inheritdoc/>
	public override string XmlLang => _inner.XmlLang;

	/// <inheritdoc/>
	public override XmlSpace XmlSpace => _inner.XmlSpace;

	/// <inheritdoc/>
	public override string GetAttribute(int i) => _inner.GetAttribute(i);

	/// <inheritdoc/>
	public override string GetAttribute(string name) => _inner.GetAttribute(name);

	/// <inheritdoc/>
	public override string GetAttribute(string name, string namespaceURI) => _inner.GetAttribute(name, namespaceURI);

	/// <inheritdoc/>
	public override string LookupNamespace(string prefix) => _inner.LookupNamespace(prefix);

	/// <inheritdoc/>
	public override void MoveToAttribute(int i) => _inner.MoveToAttribute(i);

	/// <inheritdoc/>
	public override bool MoveToAttribute(string name) => _inner.MoveToAttribute(name);

	/// <inheritdoc/>
	public override bool MoveToAttribute(string name, string ns) => _inner.MoveToAttribute(name, ns);

	/// <inheritdoc/>
	public override bool MoveToElement() => _inner.MoveToElement();

	/// <inheritdoc/>
	public override bool MoveToFirstAttribute() => _inner.MoveToFirstAttribute();

	/// <inheritdoc/>
	public override bool MoveToNextAttribute() => _inner.MoveToNextAttribute();

	/// <inheritdoc/>
	public override bool ReadAttributeValue() => _inner.ReadAttributeValue();

	/// <inheritdoc/>
	public override void ResolveEntity() => _inner.ResolveEntity();

	/// <inheritdoc/>
	public override void Close() => _inner.Close();

	/// <inheritdoc/>
	public bool HasLineInfo() => _at.HasLineInfo();

	/// <inheritdoc/>
	public int LineNumber => _at.LineNumber;

	/// <inheritdoc/>
	public int LinePosition => _at.LinePosition;

	/// <inheritdoc/>
	protected override void Dispose(bool disposing)
	{
		// This reader owns .NET's reader and the text reader under it: disposing this disposes both.
		if(disposing) {
			_inner.Dispose();
			_textReader.Dispose();
		}

		base.Dispose(disposing);
	}
}
