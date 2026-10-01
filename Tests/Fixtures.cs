namespace XmlSourceSpans.Tests;

/// <summary>Documents that between them spell XML in most of the ways it can be spelled — empty elements three ways,
/// either quote, whitespace inside tags, character references, CDATA, comments and processing instructions holding
/// markup, multi-byte and astral text, every line end, namespaces, a DTD — for the suites to run every check over.</summary>
static class Fixtures
{
	/// <summary>Namespace-free documents with no DTD: every element's own source re-parses to that element alone.</summary>
	public static readonly string[] Plain = [
		"<r/>",
		"<r></r>",
		"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<r a=\"1\" b='2'>\n  <x/>\n  <x />\n  <x></x>\n</r>",
		"<p>In the <b>beginning</b> was the <i>word</i>.</p>",
		"<r>\n  <l n=\"1\">μῆνιν ἄειδε θεὰ Πηληϊάδεω Ἀχιλῆος</l>\n  <l n=\"2\">οὐλομένην</l>\n</r>",
		"<r><s>𒀭𒀭</s><s>b</s></r>",
		"<r a=\"&amp;&lt;&#945;\"><t>&amp;&#x1202D;&quot;</t></r>",
		"<r><![CDATA[</r> <x>]]><!-- <x></x> --><?pi a > b ?><x/></r>",
		"<r\n\ta = \"1\"\n  b\n=\n'2'\n><x\n/></r\n>",
		"<r>\r\n  <x>a\r\nb</x>\r\n</r>",
		"<r>\r<x/>\r<y>c</y>\r</r>",
		"<λόγος><ἀρχή/></λόγος>",
		"<r><x a=\"1 > 0\" b='say \"hi\"' c=\"it's\"/></r>",
		"\t<r>\n\n\t<x/>\t</r>\n",
		"<!-- head --><?pi x?><r><x>a</x   ></r><!-- tail -->",
		string.Concat(Enumerable.Repeat("<a>", 60)) + "deep" + string.Concat(Enumerable.Repeat("</a>", 60)),
	];

	/// <summary>Documents whose elements need their surroundings to re-parse: prefixes declared above them, and an entity
	/// from a DTD.</summary>
	public static readonly string[] Contextual = [
		"<r xmlns=\"urn:a\" xmlns:p=\"urn:p\"><p:x p:a=\"1\"><y/></p:x></r>",
		"<!DOCTYPE r [<!ENTITY e \"hello\">]><r>&e; <x/></r>",
	];

	public static TheoryData<string> All => new(Plain.Concat(Contextual));

	public static TheoryData<string> PlainOnly => new(Plain);
}
