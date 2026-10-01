using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XmlSourceSpans;

/// <summary>
/// The source a document is read from: its decoded <see cref="Text"/>, which every <see cref="XSpan.Chars"/> index into,
/// and — when it was read from bytes — those <see cref="Bytes"/>, which every <see cref="XSpan.Bytes"/> index into. Both
/// are held by reference, never copied, so an element's slice of either costs nothing: <c>span.OuterChars</c>,
/// <c>span.OuterBytes</c>.
/// <para>Make one with <see cref="FromText"/>, <see cref="FromBytes"/> or <see cref="FromFile"/>, and read it with an
/// <see cref="XSpanReader"/>. A document loaded from it keeps it (<c>document.Source</c>), and so does every element's
/// span.</para>
/// </summary>
public sealed partial class XSource
{
	static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
	static readonly UnicodeEncoding Utf16LE = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
	static readonly UnicodeEncoding Utf16BE = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

	// U+FEFF, the byte-order mark as a character: text a caller decoded with its mark kept opens with it.
	internal const char ByteOrderMark = (char)0xFEFF;

	// How far into the bytes a declaration is looked for before they are decoded, "<?xml" through its "?>".
	const int DeclarationReach = 1024;

	XSource(string text, ReadOnlyMemory<byte> bytes, Encoding encoding, int bomLength, string baseUri)
	{
		Text = text;
		Bytes = bytes;
		Encoding = encoding;
		BomLength = bomLength;
		BaseUri = baseUri;
	}

	/// <summary>The decoded text: the string the source was made from, or the bytes it was read from, decoded — less their
	/// byte-order mark.</summary>
	public string Text { get; }

	/// <summary>The bytes the source was read from, the very array given, mark and all; empty for a source made from text.
	/// The array is held, not copied, so it lives as long as the source does; change it afterwards and every byte slice
	/// reads the change.</summary>
	public ReadOnlyMemory<byte> Bytes { get; }

	/// <summary>The encoding the byte offsets count in: the bytes' own, or UTF-8 for a source made from text.</summary>
	public Encoding Encoding { get; }

	/// <summary>The length of the byte-order mark at the start of <see cref="Bytes"/>: 3 for UTF-8, 2 for UTF-16, 0 for
	/// none.</summary>
	public int BomLength { get; }

	/// <summary>The URI the source was read from: a file's own, for a source from <see cref="FromFile"/>; null otherwise. A
	/// reader passes it on as the document's base URI, which <see cref="LoadOptions.SetBaseUri"/> keeps and a resolver
	/// resolves against.</summary>
	public string BaseUri { get; }

	// ── making one ───────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Text as a source. Its byte offsets are those of the text encoded as UTF-8, and it has no
	/// <see cref="Bytes"/>; an XML declaration's encoding is not consulted, since the text is already decoded. A leading
	/// U+FEFF is kept, and counts in every offset.</summary>
	/// <param name="text">The XML.</param>
	/// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
	public static XSource FromText(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		return new XSource(text, bytes: default, Utf8, bomLength: 0, baseUri: null);
	}

	/// <summary>Bytes as a source, decoded. They are UTF-8 or UTF-16, told apart by a byte-order mark — none means UTF-8,
	/// and UTF-16 needs one — and are decoded strictly: an invalid sequence refuses, as does an XML declaration naming
	/// another encoding. Byte offsets count from the first byte, a byte-order mark included. The array is held, not
	/// copied: do not change it afterwards.</summary>
	/// <param name="bytes">The XML, as its file holds it.</param>
	/// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
	/// <exception cref="InvalidDataException">The bytes do not decode: an invalid sequence, a UTF-32 mark, UTF-16 without a
	/// mark, a second mark, or a declaration naming an encoding they are not read in.</exception>
	public static XSource FromBytes(byte[] bytes)
	{
		ArgumentNullException.ThrowIfNull(bytes);

		return Decode(bytes, baseUri: null);
	}

	/// <summary>A file as a source: its bytes, decoded as <see cref="FromBytes"/> decodes them, and its own URI as the
	/// <see cref="BaseUri"/>, so a reader resolves what lies beside it.</summary>
	/// <param name="path">The file.</param>
	/// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
	/// <exception cref="IOException">The file cannot be read, as <see cref="File.ReadAllBytes(string)"/> throws.</exception>
	/// <exception cref="InvalidDataException">The bytes do not decode, as for <see cref="FromBytes"/>.</exception>
	public static XSource FromFile(string path)
	{
		ArgumentNullException.ThrowIfNull(path);

		byte[] bytes = File.ReadAllBytes(path);
		string baseUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;

		return Decode(bytes, baseUri);
	}

	// ── decoding ─────────────────────────────────────────────────────────────────────────────────────────────────

	// Bytes → their source: the encoding by the byte-order mark, the declaration checked against it before anything is
	// decoded, then the whole decoded strictly. A mark decoded at the start of the text is a second one, and refuses.
	static XSource Decode(byte[] bytes, string baseUri)
	{
		(Encoding encoding, int bomLength) = Detect(bytes);

		CheckDeclaration(bytes, encoding, bomLength);

		string text = DecodeStrictly(bytes, encoding, bomLength);

		if(text.Length > 0 && text[0] == ByteOrderMark)
			throw new InvalidDataException("The source holds a second byte-order mark after its first; a document opens with at most one.");

		return new XSource(text, bytes, encoding, bomLength, baseUri);
	}

	// A byte-order mark names the encoding, UTF-8 or UTF-16 in either order; no mark means UTF-8. A UTF-32 mark refuses,
	// and so does UTF-16 without a mark, which XML does not allow and which would decode as nonsense.
	static (Encoding Encoding, int BomLength) Detect(byte[] bytes)
	{
		if(Opens(bytes, 0xFF, 0xFE, 0, 0) || Opens(bytes, 0, 0, 0xFE, 0xFF))
			throw new InvalidDataException("The source opens with a UTF-32 byte-order mark; XSpanReader reads UTF-8 and UTF-16.");

		if(Opens(bytes, 0xEF, 0xBB, 0xBF))
			return (Utf8, 3);

		if(Opens(bytes, 0xFF, 0xFE))
			return (Utf16LE, 2);

		if(Opens(bytes, 0xFE, 0xFF))
			return (Utf16BE, 2);

		// '<' as a UTF-16 unit, in either byte order, with no mark before it
		if(Opens(bytes, 0x3C, 0) || Opens(bytes, 0, 0x3C))
			throw new InvalidDataException(
				"The source looks like UTF-16 without a byte-order mark; XML requires the mark for UTF-16, and XSpanReader reads UTF-16 only with one.");

		return (Utf8, 0);
	}

	static bool Opens(byte[] bytes, params ReadOnlySpan<byte> mark) => bytes.AsSpan().StartsWith(mark);

	// A declared encoding must be the one the bytes are read in. It is read from the bytes' start before they are decoded,
	// so a mis-declared file hears about its declaration, not about its first undecodable byte. The text is decoded before
	// the parser sees it, so the parser cannot honor a declaration itself, and offsets counted in the wrong encoding would
	// be wrong everywhere.
	static void CheckDeclaration(byte[] bytes, Encoding encoding, int bomLength)
	{
		string declared = DeclaredEncoding(Prefix(bytes, encoding, bomLength));

		if(declared == null)
			return;

		string name = declared.ToLowerInvariant();
		bool utf8 = encoding is UTF8Encoding;

		if(utf8 && name is "us-ascii" or "ascii") {
			int high = bytes.AsSpan(bomLength).IndexOfAnyInRange((byte)0x80, (byte)0xFF);

			if(high >= 0)
				throw new InvalidDataException($"The source declares encoding=\"{declared}\" but holds a byte above 0x7F at byte {bomLength + high}.");

			return;
		}

		bool fits = name switch {
			"utf-8" or "utf8" => utf8,
			"utf-16" or "unicode" or "ucs-2" => !utf8,
			"utf-16le" => encoding == Utf16LE,
			"utf-16be" => encoding == Utf16BE,
			_ => false,
		};

		if(!fits)
			throw new InvalidDataException(
				$"The source declares encoding=\"{declared}\" but reads as {EncodingName(encoding)}; XSpanReader reads UTF-8 and UTF-16, told apart by the byte-order mark.");
	}

	// The bytes' start, decoded leniently — far enough to hold a declaration, never failing on what follows it. A UTF-8
	// declaration is ASCII, so Latin-1 reads it byte for byte whatever comes after.
	static string Prefix(byte[] bytes, Encoding encoding, int bomLength)
	{
		int count = Math.Min(DeclarationReach, bytes.Length - bomLength);

		if(encoding is UTF8Encoding)
			return Encoding.Latin1.GetString(bytes, bomLength, count);

		Encoding lenient = encoding == Utf16BE ? Encoding.BigEndianUnicode : Encoding.Unicode;

		return lenient.GetString(bytes, bomLength, count & ~1);
	}

	// The encoding= of the text's XML declaration, or null. A declaration is "<?xml" and whitespace, at the very start.
	static string DeclaredEncoding(string text)
	{
		if(text.Length < 6 || !text.StartsWith("<?xml", StringComparison.Ordinal) || text[5] is not (' ' or '\t' or '\r' or '\n'))
			return null;

		int end = text.IndexOf("?>", StringComparison.Ordinal);

		if(end < 0)
			return null;

		Match m = DeclaredEncodingPattern().Match(text, 0, end);

		return m.Success ? m.Groups[2].Value : null;
	}

	[GeneratedRegex("""\sencoding\s*=\s*(["'])(.*?)\1""")]
	private static partial Regex DeclaredEncodingPattern();

	// The whole decoded, strictly. A UTF-8 decoder names the failing byte exactly; for UTF-16 the first unit that is no
	// character is found by a scan of its own, since the decoder reports a lone high surrogate a unit late.
	static string DecodeStrictly(byte[] bytes, Encoding encoding, int bomLength)
	{
		try {
			return encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
		}
		catch(DecoderFallbackException ex) {
			int at = encoding is UTF8Encoding ? bomLength + ex.Index : FirstInvalidUnit(bytes, bomLength, bigEndian: encoding == Utf16BE);

			throw new InvalidDataException(
				$"The source is not {EncodingName(encoding)}: an invalid byte sequence at byte {at}. Every byte offset counts the source's own bytes, so they must decode.", ex);
		}
	}

	// The byte of the first UTF-16 unit that is no character: a low surrogate with no high one before it, a high one with
	// no low one after it, or a last odd byte.
	static int FirstInvalidUnit(byte[] bytes, int start, bool bigEndian)
	{
		for(int i = start; i < bytes.Length; i += 2) {
			if(i + 1 == bytes.Length)
				return i;

			char unit = Unit(bytes, i, bigEndian);

			if(char.IsLowSurrogate(unit))
				return i;

			if(!char.IsHighSurrogate(unit))
				continue;

			if(i + 3 >= bytes.Length || !char.IsLowSurrogate(Unit(bytes, i + 2, bigEndian)))
				return i;

			i += 2;
		}

		return bytes.Length;
	}

	static char Unit(byte[] bytes, int i, bool bigEndian)
		=> (char)(bigEndian ? (bytes[i] << 8) | bytes[i + 1] : bytes[i] | (bytes[i + 1] << 8));

	static string EncodingName(Encoding encoding) => encoding is UTF8Encoding ? "UTF-8" : "UTF-16";
}
