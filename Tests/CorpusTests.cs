using System.Security.Cryptography;

namespace XmlSourceSpans.Tests;

/// <summary>
/// Real documents from a text-conversion pipeline (<c>Tests/Corpus/README.md</c>): each held to the hash its manifest
/// names, loaded from its bytes, its tree checked against <see cref="XDocument.Parse(string, LoadOptions)"/>'s, and every
/// invariant kept by every element — and the figures measured on these exact bytes by <c>grep -b</c>, before this
/// library existed, pinned.
/// </summary>
public class CorpusTests
{
	static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Corpus");

	// SHA256SUMS: a hash, a space, a mode mark (' ' or '*'), and the path within the corpus.
	static readonly (string Hash, string Path)[] Manifest = [.. File.ReadAllLines(Path.Combine(Root, "SHA256SUMS"))
		.Where(line => line.Length > 66)
		.Select(line => (line[..64], line[66..]))];

	public static TheoryData<string> Files => new(Manifest.Select(m => m.Path));

	static byte[] Bytes(string path) => File.ReadAllBytes(Path.Combine(Root, path));

	static XDocument Load(string path) => XSpanReader.Load(Bytes(path), LoadOptions.PreserveWhitespace);

	static XElement First(XDocument doc, string name, string n) => doc.Descendants(name).First(e => (string)e.Attribute("n") == n);

	[Theory]
	[MemberData(nameof(Files))]
	public void A_file_is_the_bytes_its_manifest_names(string path)
	{
		string hash = Convert.ToHexString(SHA256.HashData(Bytes(path))).ToLowerInvariant();

		Equal(Manifest.Single(m => m.Path == path).Hash, hash);
	}

	// The theories run what the manifest lists, so a file added without its line would be skipped silently.
	[Fact]
	public void Every_file_in_the_corpus_is_in_its_manifest()
	{
		List<string> files = [.. Directory.EnumerateFiles(Root, "*.xml", SearchOption.AllDirectories)
			.Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
			.Order(StringComparer.Ordinal)];

		Equal(files, Manifest.Select(m => m.Path).Order(StringComparer.Ordinal));
	}

	[Theory]
	[MemberData(nameof(Files))]
	public void Every_element_of_a_file_keeps_every_invariant(string path)
	{
		byte[] bytes = Bytes(path);

		Equal((byte)'<', bytes[0]);   // UTF-8 without a mark, as the corpus README says

		string text = Encoding.UTF8.GetString(bytes);
		XDocument doc = XSpanReader.Load(bytes, LoadOptions.PreserveWhitespace);

		True(XNode.DeepEquals(XDocument.Parse(text, LoadOptions.PreserveWhitespace), doc), "the tree is XDocument.Parse's");

		Invariants.Check(doc, text, Encoding.UTF8, bomLength: 0, reparse: true);
	}

	[Fact]
	public void The_Iliads_first_elements_stand_where_grep_found_them()
	{
		XDocument doc = Load("homer.grk.pers/iliad.mq.xml");

		XElement card = doc.Descendants("milestone").First(m => (string)m.Attribute("unit") == "card" && (string)m.Attribute("n") == "1");

		Equal((346, 31), (card.Span.Bytes.Start, card.Span.Bytes.Length));
		Equal((384, 93), Where(First(doc, "l", "1")));
		Equal((484, 90), Where(First(doc, "l", "2")));
		Equal((69_018, 103), Where(First(doc, "l", "611")));
	}

	[Fact]
	public void A_chapter_written_empty_stands_where_grep_found_it()
	{
		XDocument doc = Load("herodotus.grk.pers/8.mq.xml");

		// 140 is also the number of the two lettered chapters beside it, 140A and 140B: the base is the one written empty
		XElement chapter = doc.Descendants("chapter").Single(c => (string)c.Attribute("n") == "140" && (string)c.Attribute("fill") == "base");

		Equal((202_083, 31), Where(chapter));
		Equal("""<chapter n="140" fill="base" />""", chapter.OuterSource);
	}

	[Fact]
	public void A_section_holding_a_tight_empty_tag_is_its_files_length_not_the_serializers()
	{
		XDocument doc = Load("aeschines.grk.pers/3.mq.xml");

		XElement section = First(doc, "v", "29");

		Equal((33_299, 942), Where(section));
		Contains("<note/>", section.OuterSource);
		Equal(943, Encoding.UTF8.GetByteCount(section.ToString(SaveOptions.DisableFormatting)));   // the serializer writes <note />
	}

	[Fact]
	public void A_stanza_spans_its_lines_and_what_stands_between_them()
	{
		XDocument doc = Load("sumerian-lit.eng.etcsl/c222.mq.xml");

		XElement stanza = doc.Descendants("lg").First();

		Equal((274, 5_465), Where(stanza));
		True(stanza.Elements("l").Any());
		All(stanza.Elements(), e => True(e.Span.Bytes.Start >= stanza.Span.Bytes.InnerStart && e.Span.Bytes.End <= stanza.Span.Bytes.InnerEnd));
	}

	[Theory]
	[InlineData("bible.eng.bsb.berean/ruth.mq.xml", "p", 71)]
	[InlineData("philo.grk.f1gk/gig.mq.xml", "lb", 278)]
	public void A_bare_empty_tag_reads_back_tight_as_the_file_wrote_it(string path, string name, int count)
	{
		List<XElement> bare = [.. Load(path).Descendants(name).Where(e => e.IsEmpty && !e.HasAttributes)];

		Equal(count, bare.Count);
		All(bare, e => Equal($"<{name}/>", e.OuterSource));
		Equal($"<{name} />", bare[0].ToString(SaveOptions.DisableFormatting));
	}

	[Fact]
	public void Each_comment_of_a_verse_stands_inside_that_verse()
	{
		List<XElement> comments = [.. Load("sforno.tanach.eng.sef/lev.mq.xml").Descendants("comment")];

		True(comments.Count > 1);

		foreach(XElement comment in comments) {
			XRange verse = comment.Parent.Span.Bytes;

			True(comment.Span.Bytes.Start >= verse.InnerStart && comment.Span.Bytes.End <= verse.InnerEnd);
			StartsWith("<comment", comment.OuterSource);
		}
	}

	static (int Start, int Length) Where(XElement el) => (el.Span.Bytes.Start, el.Span.Bytes.Length);
}
