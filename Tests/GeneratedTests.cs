using Xunit.Sdk;

namespace XmlSourceSpans.Tests;

/// <summary>
/// Documents generated from a fixed seed, each spelling its XML at random in every way the fixtures do — empty elements
/// three ways, either quote, whitespace in its tags, every line end, references, CDATA, comments and instructions
/// holding markup, multi-byte and astral text, nesting to six levels — and each checked element by element: the tree
/// against <see cref="XDocument.Parse(string, LoadOptions)"/>'s, every invariant, and every element's own source
/// re-parsed; in UTF-8 without and with a mark, and in UTF-16 in both byte orders.
/// </summary>
public class GeneratedTests
{
	const int Documents = 400;

	public static TheoryData<int> Seeds => new(Enumerable.Range(0, Documents));

	[Theory]
	[MemberData(nameof(Seeds))]
	public void A_generated_document_keeps_every_invariant(int seed)
	{
		string xml = new DocumentGenerator(seed).Next();

		XDocument doc = XSpanReader.Parse(xml, LoadOptions.PreserveWhitespace);

		True(XNode.DeepEquals(XDocument.Parse(xml, LoadOptions.PreserveWhitespace), doc), "the tree is XDocument.Parse's");

		Invariants.Check(doc, xml, Encoding.UTF8, bomLength: 0, reparse: true);
	}

	[Theory]
	[MemberData(nameof(Seeds))]
	public void A_generated_document_keeps_every_invariant_as_bytes(int seed)
	{
		string xml = new DocumentGenerator(seed).Next();

		byte[] utf8 = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(xml)];

		Invariants.Check(XSpanReader.Load(utf8, LoadOptions.PreserveWhitespace), xml, Encoding.UTF8, bomLength: 3, reparse: true);

		// UTF-16 declares itself so: a declaration naming UTF-8 would be refused over UTF-16 bytes
		string asUtf16 = xml.Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"", StringComparison.Ordinal);

		byte[] utf16LE = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(asUtf16)];
		byte[] utf16BE = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(asUtf16)];

		Invariants.Check(XSpanReader.Load(utf16LE, LoadOptions.PreserveWhitespace), asUtf16, Encoding.Unicode, bomLength: 2, reparse: true);
		Invariants.Check(XSpanReader.Load(utf16BE, LoadOptions.PreserveWhitespace), asUtf16, Encoding.BigEndianUnicode, bomLength: 2, reparse: true);
	}

	// The generated documents between them hold every spelling the checks are meant to meet — so a generator gone
	// quietly narrow fails here, not by passing everything else.
	[Fact]
	public void The_generated_documents_spell_XML_every_way_they_should()
	{
		List<string> all = [.. Enumerable.Range(0, Documents).Select(s => new DocumentGenerator(s).Next())];

		string[] spellings = ["/>", " />", "></", "='", "=\"", "a > b", "\r\n", "&amp;", "&#x1202D;", "𒀭", "μῆνιν", "λόγος",
			"<![CDATA[", "<!--", "<?pi", "<?xml", "\t"];

		foreach(string spelling in spellings)
			True(all.Any(d => d.Contains(spelling, StringComparison.Ordinal)), $"no generated document holds {spelling}");

		True(all.Any(d => d.Contains('\r') && !d.Contains("\r\n", StringComparison.Ordinal)), "no generated document ends a line with a lone \\r");

		int elements = all.Sum(d => XDocument.Parse(d).Descendants().Count());
		int deepest = all.Max(d => XDocument.Parse(d).Descendants().Max(e => e.Ancestors().Count()));

		True(elements > 5_000, $"only {elements} elements generated");
		True(deepest >= 5, $"nesting reaches only {deepest}");
	}

	// The invariants have teeth: the same document checked against a count that is off fails an assertion — not merely
	// throws something.
	[Fact]
	public void The_invariants_fail_when_an_offset_is_off()
	{
		string xml = new DocumentGenerator(seed: 1).Next();

		XDocument doc = XSpanReader.Load([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(xml)], LoadOptions.PreserveWhitespace);

		ThrowsAny<XunitException>(() => Invariants.Check(doc, xml, Encoding.UTF8, bomLength: 0, reparse: false));
	}
}

/// <summary>Random, well-formed, namespace-free XML from a seed — the same document for the same seed on a given .NET,
/// which does not promise <see cref="Random"/>'s sequence across versions; the coverage test above catches a sequence
/// that drifts narrow.</summary>
sealed class DocumentGenerator(int seed)
{
	readonly Random _random = new(seed);
	readonly StringBuilder _xml = new();

	static readonly string[] Names = ["a", "b", "lg", "v", "x-y", "n.1", "_z", "λόγος"];
	static readonly string[] Values = ["1", "a > b", "it's", "say \"hi\"", "&amp;", "&#945;", "𒀭", "tab\there", ""];
	static readonly string[] Texts = ["plain", "μῆνιν ἄειδε", "𒀭𒀭", "a &amp; b", "&#x1202D;", " ", "&lt;tag&gt;", "-->"];
	static readonly string[] LineEnds = ["\n", "\r\n", "\r"];

	public string Next()
	{
		_xml.Clear();

		if(_random.Next(3) == 0)
			_xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(LineEnd());

		Element(depth: 0);

		return _xml.ToString();
	}

	void Element(int depth)
	{
		string name = Pick(Names);

		_xml.Append('<').Append(name);

		int attributes = _random.Next(3);

		for(int i = 0; i < attributes; i++)
			Attribute(i);

		_xml.Append(Gap());

		// The root always holds children; below it an element is empty one time in five, and always at the depth limit.
		if(depth >= 5 || (depth > 0 && _random.Next(5) == 0)) {
			Empty(name);
			return;
		}

		_xml.Append('>');

		int children = depth < 2 ? _random.Next(2, 8) : _random.Next(0, 5);

		for(int i = 0; i < children; i++)
			Child(depth);

		_xml.Append("</").Append(name).Append(Gap()).Append('>');
	}

	void Attribute(int index)
	{
		string value = Pick(Values);
		char quote = value.Contains('"') ? '\'' : value.Contains('\'') ? '"' : _random.Next(2) == 0 ? '"' : '\'';

		_xml.Append(Space()).Append('a').Append(index).Append(Pick(["=", " = ", "=\t"])).Append(quote).Append(value).Append(quote);
	}

	// An empty element written short (<x/>, or <x /> after the gap) or open and closed (<x></x>).
	void Empty(string name)
	{
		if(_random.Next(3) == 0)
			_xml.Append("></").Append(name).Append('>');
		else
			_xml.Append("/>");
	}

	// Half of all children are elements; the rest text, line ends, CDATA, comments and instructions.
	void Child(int depth)
	{
		switch(_random.Next(10)) {
			case 0:
				_xml.Append("<![CDATA[</fake> <x> & ]]>");
				break;

			case 1:
				_xml.Append("<!-- <x> & </x> -->");
				break;

			case 2:
				_xml.Append("<?pi data > stuff ?>");
				break;

			case 3:
				_xml.Append(LineEnd());
				break;

			case 4:
				_xml.Append(Pick(Texts));
				break;

			default:
				Element(depth + 1);
				break;
		}
	}

	string Space() => Pick([" ", "  ", "\t", LineEnd()]);

	string Gap() => _random.Next(2) == 0 ? "" : Space();

	string LineEnd() => Pick(LineEnds);

	string Pick(string[] from) => from[_random.Next(from.Length)];
}
