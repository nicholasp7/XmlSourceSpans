namespace XmlSourceSpans.Tests;

/// <summary>The tree is LINQ to XML's own: for the same text and options, <see cref="XSpanReader"/> gives the document
/// <see cref="XDocument.Parse(string, LoadOptions)"/> gives — declaration, line info and reader settings included.</summary>
public class ParityTests
{
	static readonly LoadOptions[] Options = [LoadOptions.None, LoadOptions.PreserveWhitespace];

	[Theory]
	[MemberData(nameof(Fixtures.All), MemberType = typeof(Fixtures))]
	public void The_tree_is_the_one_XDocument_Parse_builds(string xml)
	{
		foreach(LoadOptions options in Options) {
			XDocument expected = XDocument.Parse(xml, options);
			XDocument actual = XSpanReader.Parse(xml, options);

			True(XNode.DeepEquals(expected, actual), $"under {options}");
			Equal(expected.Declaration?.ToString(), actual.Declaration?.ToString());
		}
	}

	[Theory]
	[MemberData(nameof(Fixtures.All), MemberType = typeof(Fixtures))]
	public void Line_info_asked_for_is_the_line_info_XDocument_Parse_keeps(string xml)
	{
		XDocument expected = XDocument.Parse(xml, LoadOptions.SetLineInfo);
		XDocument actual = XSpanReader.Parse(xml, LoadOptions.SetLineInfo);

		Equal(expected.Descendants().Select(LineInfo), actual.Descendants().Select(LineInfo));
	}

	static (int Line, int Position) LineInfo(XElement el) => (((IXmlLineInfo)el).LineNumber, ((IXmlLineInfo)el).LinePosition);

	[Fact]
	public void The_callers_reader_settings_are_the_ones_used()
	{
		XmlReaderSettings prohibit = new() { DtdProcessing = DtdProcessing.Prohibit };

		Throws<XmlException>(() => XSpanReader.Parse(Fixtures.Contextual[1], settings: prohibit));
	}

	// Settings of your own replace the defaults whole: a fresh XmlReaderSettings keeps whitespace, whatever the options say.
	[Fact]
	public void Settings_of_your_own_replace_the_defaults_whitespace_included()
	{
		const string xml = "<r>\n  <x/>\n</r>";

		XmlReaderSettings own = new() { DtdProcessing = DtdProcessing.Prohibit };

		Single(XDocument.Parse(xml, LoadOptions.None).Root.Nodes());
		Single(XSpanReader.Parse(xml, LoadOptions.None).Root.Nodes());
		Equal(3, XSpanReader.Parse(xml, LoadOptions.None, own).Root.Nodes().Count());   // the whitespace on both sides kept
	}

	// The default settings cap entity expansion as XDocument.Parse's do: ten levels of tenfold entities would expand to
	// ten billion characters.
	[Fact]
	public void Entity_expansion_is_capped_as_XDocument_Parse_caps_it()
	{
		StringBuilder dtd = new("<!ENTITY e0 \"aaaaaaaaaa\">");

		for(int level = 1; level <= 9; level++)
			dtd.Append($"<!ENTITY e{level} \"{string.Concat(Enumerable.Repeat($"&e{level - 1};", 10))}\">");

		string xml = $"<!DOCTYPE r [{dtd}]><r>&e9;</r>";

		Throws<XmlException>(() => XDocument.Parse(xml));
		Throws<XmlException>(() => XSpanReader.Parse(xml));
	}

	[Fact]
	public void Malformed_XML_throws_as_XDocument_Parse_does()
		=> Throws<XmlException>(() => XSpanReader.Parse("<r><x></r>"));
}
