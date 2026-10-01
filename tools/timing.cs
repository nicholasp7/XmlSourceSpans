#:project ../XmlSourceSpans/XmlSourceSpans.csproj
#:property Nullable=disable
// timing — XSpanReader against XDocument.Parse on one XML file: bytes allocated and milliseconds per load, after a
// warm-up, and what the spans cost per element over the plain parse. Allocations repeat exactly from run to run;
// times vary by a fifth or more, so read them over several runs.
//
// Run:  dotnet run -c Release tools/timing.cs -- <file.xml> [iterations]
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

using XmlSourceSpans;

using static System.Console;

if(args.Length == 0) {
	WriteLine("usage: dotnet run -c Release tools/timing.cs -- <file.xml> [iterations]");
	return 1;
}

string path = args[0];
int iterations = args.Length > 1 ? int.Parse(args[1]) : 20;

byte[] bytes = File.ReadAllBytes(path);
int bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
string text = Encoding.UTF8.GetString(bytes, bom, bytes.Length - bom);

WriteLine($"{Path.GetFileName(path)} — {bytes.Length:N0} bytes, {iterations} loads each, whitespace preserved");
WriteLine();

(double plainMs, long plainBytes, int elements) = Measure("XDocument.Parse", () => XDocument.Parse(text, LoadOptions.PreserveWhitespace));

Measure("XDocument.Parse + SetLineInfo", () => XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo));

(double spanMs, long spanBytes, _) = Measure("XSpanReader.Parse", () => XSpanReader.Parse(text, LoadOptions.PreserveWhitespace));

Measure("XSpanReader.Load (the bytes)", () => XSpanReader.Load(bytes, LoadOptions.PreserveWhitespace));

WriteLine();
WriteLine($"spans over the plain parse: {(spanBytes - plainBytes) / (double)elements:0} bytes more per element ({elements:N0} elements), {spanMs / plainMs:0.00}× the time");

return 0;

(double Ms, long Bytes, int Elements) Measure(string name, Func<XDocument> load)
{
	for(int i = 0; i < 3; i++)
		load();

	long before = GC.GetAllocatedBytesForCurrentThread();
	Stopwatch clock = Stopwatch.StartNew();

	XDocument doc = null;

	for(int i = 0; i < iterations; i++)
		doc = load();

	clock.Stop();

	double ms = clock.Elapsed.TotalMilliseconds / iterations;
	long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
	int elements = doc.Descendants().Count();

	WriteLine($"  {name,-30} {allocated / 1048576.0,7:0.00} MiB allocated   {ms,8:0.0} ms");

	return (ms, allocated, elements);
}
