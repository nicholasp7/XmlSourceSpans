namespace XmlSourceSpans.Tests;

/// <summary>
/// What must hold for every element of a document <see cref="XSpanReader"/> loaded, checked against the source text
/// alone — never against the reader's own arithmetic:
/// <list type="bullet">
/// <item>its outer range is its source from a <c>&lt;</c> to a <c>&gt;</c>, and <c>element.OuterSource</c> and the span's
/// own slices are that text; its inner range sits between its tags, and is empty at its end for an empty-element
/// tag;</item>
/// <item>its byte offsets are its character offsets counted in the source's encoding, after any byte-order mark, and its
/// byte slice decodes to the same text;</item>
/// <item>its span counts into the document's own source;</item>
/// <item>its line and column are its <c>&lt;</c>'s, by XML's line ends;</item>
/// <item>its children stand inside its inner range, in order, each clear of the last;</item>
/// <item>optionally, its own source re-parses to an element deep-equal to it.</item>
/// </list>
/// The text is measured once per document — a byte count and a line for every character — so a check over a large file
/// stays linear.
/// </summary>
static class Invariants
{
	public static void Check(XDocument doc, string text, Encoding encoding, int bomLength, bool reparse, LoadOptions reparseAs = LoadOptions.PreserveWhitespace)
	{
		Measure measure = new(text, encoding, bomLength);

		foreach(XElement el in doc.Descendants())
			CheckElement(el, doc.Source, text, encoding, measure, reparse, reparseAs);
	}

	static void CheckElement(XElement el, XSource source, string text, Encoding encoding, Measure measure, bool reparse, LoadOptions reparseAs)
	{
		XSpan span = el.Span;

		NotNull(span);
		Same(source, span.Source);

		XRange c = span.Chars;

		True(c.Start < c.InnerStart && c.InnerStart <= c.InnerEnd && c.InnerEnd <= c.End, $"ordered offsets: {c}");

		string outer = text[c.Start..c.End];

		True(outer.StartsWith('<') && outer.EndsWith('>'), $"a whole element: {outer}");
		Equal(outer, el.OuterSource);
		Equal(outer, span.OuterChars.ToString());
		Equal(text[c.InnerStart..c.InnerEnd], el.InnerSource);

		if(c.InnerEnd == c.End)
			True(outer.EndsWith("/>", StringComparison.Ordinal), $"an empty element ends at its '/>': {outer}");
		else
			True(text.AsSpan(c.InnerEnd).StartsWith("</", StringComparison.Ordinal), $"an end tag after the inside: {outer}");

		XRange b = span.Bytes;

		Equal(measure.ByteAt[c.Start], b.Start);
		Equal(measure.ByteAt[c.InnerStart], b.InnerStart);
		Equal(measure.ByteAt[c.InnerEnd], b.InnerEnd);
		Equal(measure.ByteAt[c.End], b.End);

		if(!source.Bytes.IsEmpty)
			Equal(outer, encoding.GetString(span.OuterBytes.Span));

		(int line, int column) = measure.LineAndColumn(c.Start);

		Equal(line, span.Line);
		Equal(column, span.Column);

		int cursor = c.InnerStart;

		foreach(XElement child in el.Elements()) {
			XRange k = child.Span.Chars;

			True(k.Start >= cursor && k.End <= c.InnerEnd, $"a child inside its parent, after its elder sibling: {k} in {c}");
			cursor = k.End;
		}

		if(reparse)
			True(XNode.DeepEquals(XElement.Parse(outer, reparseAs), el), $"its own source is the element: {outer}");
	}

	/// <summary>A text measured once: the byte offset of every character boundary (a surrogate pair counted as one
	/// character, its bytes landing after it), and where each line starts by XML's line ends.</summary>
	sealed class Measure
	{
		public readonly int[] ByteAt;

		readonly List<int> _lineStarts = [0];

		public Measure(string text, Encoding encoding, int bomLength)
		{
			ByteAt = new int[text.Length + 1];
			ByteAt[0] = bomLength;

			for(int i = 0; i < text.Length;) {
				int units = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
				int bytes = encoding.GetByteCount(text.AsSpan(i, units));

				if(units == 2)
					ByteAt[i + 1] = ByteAt[i];   // inside the pair: no offset ever falls here

				ByteAt[i + units] = ByteAt[i] + bytes;

				char ch = text[i];

				if(ch == '\n' || (ch == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
					_lineStarts.Add(i + 1);

				i += units;
			}
		}

		// The 1-based line and column of a character.
		public (int Line, int Column) LineAndColumn(int index)
		{
			int at = _lineStarts.BinarySearch(index);
			int line = at >= 0 ? at : ~at - 1;

			return (line + 1, index - _lineStarts[line] + 1);
		}
	}
}
