using FamilyMEP.Plugin.Compatibility;

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
string sample = "ABCDEF";
Check(sample[1..^1] == "BCDE" && sample[^1] == 'F', "Legacy ranges must preserve string slicing.");
Check(new[] {1,2,3,4}.TakeLast(2).SequenceEqual(new[] {3,4}), "TakeLast order");
Check(!new[] {1}.TakeLast(0).Any(), "TakeLast zero");
Check(new[] {"A","a","B"}.DistinctBy(x => x.ToUpperInvariant()).SequenceEqual(new[] {"A","B"}), "DistinctBy first occurrence");
var dictionary = new Dictionary<string,int> { ["x"]=3 };
Check(dictionary.GetValueOrDefault("absent",7)==7 && dictionary.GetValueOrDefault("x")==3, "Dictionary defaults");
Check(!dictionary.TryAdd("x",9) && dictionary["x"]==3, "TryAdd preserves existing value");
Check(dictionary.Remove("x",out int removed) && removed==3, "Remove returns previous value");
Check("aAa".Replace("a","$",StringComparison.OrdinalIgnoreCase)=="$$$", "Ordinal replacement treats replacement literally");
Check(PortableMath.Clamp(-2,0,4)==0 && PortableMath.Clamp(8.0,0.0,4.0)==4.0, "Clamp boundaries");
Check(!PortableMath.IsFinite(double.NaN) && !PortableMath.IsFinite(double.PositiveInfinity) && PortableMath.IsFinite(0), "Finite guard");
using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("pdf-output"));
using var reader = new StreamReader(stream);
Check(await reader.ReadToEndAsync(CancellationToken.None)=="pdf-output", "Legacy async stream capture");
Console.WriteLine("PASS: .NET Framework 4.8 compatibility smoke tests.");
