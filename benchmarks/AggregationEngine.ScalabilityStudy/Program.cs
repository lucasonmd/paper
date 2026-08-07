using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AggregationEngine.ScalabilityStudy
{
    // Measures how the two approaches scale in MAINTENANCE cost as a topic
    // module grows - the axis the paper's contribution actually lives on.
    // Every figure printed here comes from generated source that was
    // compiled and executed; nothing is estimated or extrapolated.
    internal static class Program
    {
        private static readonly int[] Sizes = { 5, 10, 20, 40 };

        private static void Main()
        {
            Directory.CreateDirectory("results");

            Console.WriteLine("==================================================================");
            Console.WriteLine(" Maintenance cost vs. number of topic kinds");
            Console.WriteLine("==================================================================");
            Console.WriteLine("N = kinds in the aggregate (1 root + N-1 parts).");
            Console.WriteLine("LOC counts exclude topic class definitions (identical for both)");
            Console.WriteLine("and exclude blank lines, braces and comments.");
            Console.WriteLine();
            Console.WriteLine($"{"N",3} | {"Legacy LOC",10} | {"Engine LOC",10} | {"Legacy pts",10} | {"Engine pts",10} | {"verified",8}");
            Console.WriteLine(new string('-', 72));

            var rows = new List<string> { "Kinds,Legacy_LOC,Engine_LOC,Legacy_ChangePoints,Engine_ChangePoints,Legacy_Files,Engine_Files,Verified,Completions" };
            var perKind = new List<string> { "Kinds,Legacy_LOC_per_added_kind,Engine_LOC_per_added_kind,Legacy_points_per_added_kind,Engine_points_per_added_kind" };

            foreach (var n in Sizes)
            {
                var m = new Model(n - 1);          // 1 root + (n-1) parts
                var legacyLoc = ModelGenerator.SignificantLines(ModelGenerator.Legacy(m));
                var engineLoc = ModelGenerator.SignificantLines(ModelGenerator.Engine(m));

                var pts = m.Shapes.Select(ModelGenerator.ChangePointsPerKind).ToList();
                int legacyPts = pts.Sum(x => x.legacy);
                int enginePts = pts.Sum(x => x.engine);

                var v = Verifier.CompileAndRun(m);
                if (!v.Compiled)
                {
                    Console.WriteLine($"{n,3} | COMPILE FAILED: {v.CompileError}");
                    continue;
                }

                Console.WriteLine($"{n,3} | {legacyLoc,10} | {engineLoc,10} | {legacyPts,10} | {enginePts,10} | " +
                                  $"{(v.Agrees ? "yes" : "NO"),8}");

                // Legacy spreads across the aggregator file plus the result
                // type; the engine keeps everything in one registration site.
                rows.Add($"{n},{legacyLoc},{engineLoc},{legacyPts},{enginePts},2,1,{v.Agrees},{v.EngineCompletions}");

                // Marginal cost of one more kind, measured by generating N+1
                // and differencing rather than dividing totals.
                var m2 = new Model(n);
                int dLegacyLoc = ModelGenerator.SignificantLines(ModelGenerator.Legacy(m2)) - legacyLoc;
                int dEngineLoc = ModelGenerator.SignificantLines(ModelGenerator.Engine(m2)) - engineLoc;
                var addedShape = m2.Shapes[m2.PartCount - 1];
                var (dLegacyPts, dEnginePts) = ModelGenerator.ChangePointsPerKind(addedShape);
                perKind.Add($"{n},{dLegacyLoc},{dEngineLoc},{dLegacyPts},{dEnginePts}");
            }

            File.WriteAllLines(Path.Combine("results", "exp8_maintenance_vs_kinds.csv"), rows, Encoding.UTF8);
            File.WriteAllLines(Path.Combine("results", "exp9_marginal_cost_per_kind.csv"), perKind, Encoding.UTF8);

            Console.WriteLine();
            Console.WriteLine("Marginal cost of adding ONE more topic kind (measured by diffing N and N+1):");
            Console.WriteLine($"{"at N",5} | {"Legacy LOC",10} | {"Engine LOC",10} | {"Legacy pts",10} | {"Engine pts",10}");
            Console.WriteLine(new string('-', 58));
            foreach (var line in perKind.Skip(1))
            {
                var c = line.Split(',');
                Console.WriteLine($"{c[0],5} | {c[1],10} | {c[2],10} | {c[3],10} | {c[4],10}");
            }

            Console.WriteLine();
            Console.WriteLine("Results written to results/exp8_*.csv and results/exp9_*.csv");
        }
    }
}
