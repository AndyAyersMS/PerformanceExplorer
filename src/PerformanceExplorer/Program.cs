﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Xml.Serialization;
using System.Text;
using System.Xml.Linq;

namespace PerformanceExplorer
{
    // A Configuration describes the environment
    // used to perform a particular run.
    public class Configuration
    {
        public static bool DisableZap = false;

        public Configuration(string name)
        {
            Name = name;
            Environment = new Dictionary<string, string>();
            ResultsDirectory = Program.RESULTS_DIR;

            if (DisableZap)
            {
                Environment["COMPlus_ZapDisable"] = "1";
            }
        }

        public string Name;
        public Dictionary<string, string> Environment;
        public string ResultsDirectory;
    }

    // PerformanceData describes performance
    // measurements for benchmark runs
    public class PerformanceData
    {
        public PerformanceData()
        {
            ExecutionTime = new SortedDictionary<string, List<double>>();
            InstructionCount = new SortedDictionary<string, List<double>>();
            id = ++idGen;
        }

        static int idGen = 0;
        static int id;

        // subPart -> list of data
        public SortedDictionary<string, List<double>> ExecutionTime;
        public SortedDictionary<string, List<double>> InstructionCount;

        public void Print(string configName)
        {
            foreach (string subBench in ExecutionTime.Keys)
            {
                Summarize(subBench, configName);
            }
        }

        public void Summarize(string subBench, string configName)
        {
            Console.Write("### [{0}] {1} perf for {2}", id, configName, subBench);

            if (ExecutionTime.ContainsKey(subBench))
            {
                Console.Write(" time {0:0.00} milliseconds (~{1:0.00}%)",
                    Average(ExecutionTime[subBench]), 
                    PercentDeviation(ExecutionTime[subBench]));
            }

            if (InstructionCount.ContainsKey(subBench))
            {
                Console.Write(" instructions {0:0.00} million (~{1:0.00}%)",
                    Average(InstructionCount[subBench]) / (1000 * 1000), 
                    PercentDeviation(InstructionCount[subBench]));
            }

            Console.WriteLine();
        }

        public static double Average(List<double> data)
        {
            if (data.Count() < 1)
            {
                return -1;
            }

            return data.Average();
        }
        public static double StdDeviation(List<double> data)
        {
            if (data.Count() < 2)
            {
                return 0;
            }

            double avg = Average(data);
            double sqError = 0;
            foreach (double d in data)
            {
                sqError += (avg - d) * (avg - d);
            }
            double estSD = Math.Sqrt(sqError / (data.Count() - 1));
            return estSD;
        }
        public static double PercentDeviation(List<double> data)
        {
            return 100.0 * StdDeviation(data) / Average(data);
        }

        // Use bootstrap to test hypothesis that difference in
        // means of the two data sets is significant at indicated level.
        // Return value is p value between 0 and 1.
        public static double Confidence(List<double> data1, List<double> data2)
        {
            int kb = data1.Count();
            int kd = data2.Count();

            double d1ave = Average(data1);
            double d2ave = Average(data2);
            double basestat = Math.Abs(d1ave - d2ave);

            // perform a boostrap test to estimate the one-sided 
            // confidence that this diff could be significant.

            List<double> mergedData = new List<double>(kb + kd);
            mergedData.AddRange(data1);
            mergedData.AddRange(data2);

            double confidence = Bootstrap(basestat, kb, kd, mergedData);

            return confidence;
        }

        // Use bootstrap to produce a p value for the hypothesis that the 
        // difference shown in basestat is significant. 
        // k1 and k2 are the sizes of the two sample populations
        // data is the combined set of observations.
        static double Bootstrap(double basestat, int k1, int k2, List<double> data)
        {
            double obs = 0;
            Random r = new Random(RandomSeed);

            for (int i = 0; i < NumberOfBootstrapTrials; i++)
            {
                List<double> z1 = Sample(data, k1, r);
                List<double> z2 = Sample(data, k2, r);

                double z1average = Average(z1);
                double z2average = Average(z2);

                double zmedian = Math.Abs(z1average - z2average);

                if (zmedian < basestat)
                {
                    obs++;
                }
            }

            return obs / NumberOfBootstrapTrials;
        }

        // Return a random sample (with replacement) of size n from the array data
        static List<double> Sample(List<double> data, int n, Random r)
        {
            int l = data.Count;
            List<double> x = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                int j = r.Next(0, l);
                x.Add(data[j]);
            }

            return x;
        }

        // Use fixed random seed so that we don't see the bootstrap p-values
        // wander from invocation to invocation.
        const int RandomSeed = 77;

        // The bootstrap test works by taking a number of random samples
        // and computing how frequently the samples exhibit the same 
        // statistic as observed statstic. N determines the 
        // number of bootstrap trials to run. A higher value is better
        // but takes longer.
        const int NumberOfBootstrapTrials = 1000;
    }

    // Information that identifies a method
    public struct MethodId : IEquatable<MethodId>
    {
        public override bool Equals(object obj)
        {
            return (obj is MethodId) && Equals((MethodId)obj);
        }
        public bool Equals(MethodId other)
        {
            return (this.Token == other.Token && this.Hash == other.Hash);
        }
        public override string ToString()
        {
            return String.Format("{0:X8}-{1:X8}", Token, Hash);
        }

        public override int GetHashCode()
        {
            int hash = 23;
            hash = hash * 31 + (int)Token;
            hash = hash * 31 + (int)Hash;
            return hash;
        }

        public uint Token;
        public uint Hash;
    }

    // A method seen either in jitting or inlining
    public class Method
    {
        public Method()
        {
            Callers = new List<Method>();
            Callees = new List<Method>();
        }

        public MethodId getId()
        {
            MethodId id = new MethodId();
            id.Token = Token;
            id.Hash = Hash;
            return id;
        }

        public static int HasMoreInlines(Method x, Method y)
        {
            return (int) y.InlineCount - (int) x.InlineCount;
        }

        public static int HasMoreCalls(Method x, Method y)
        {
            if (x.CallCount > y.CallCount)
            {
                return -1;
            }
            else if (x.CallCount < y.CallCount)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }

        public double NumSubtrees()
        {
            double result = 1;
            foreach (Inline i in Inlines)
            {
                result *= (1 + i.NumSubtrees());
            }
            return result;
        }

        public void Dump()
        {
            Console.WriteLine("Inlines into {0} {1:X8}", Name, Token);
            foreach (Inline x in Inlines)
            {
                x.Dump(2);
            }
        }

        public Inline[] GetBfsSubtree(int k, out Inline lastInline)
        {
            List<Inline> l = new List<Inline>(k);
            Queue<Inline> q = new Queue<Inline>();
            lastInline = null;

            foreach (Inline i in Inlines)
            {
                q.Enqueue(i);
            }

            // BFS until we've enumerated the first k.
            while (q.Count() > 0)
            {
                Inline i = q.Dequeue();
                l.Add(i);

                if (l.Count() == k)
                {
                    lastInline = i;
                    break;
                }

                foreach (Inline ii in i.Inlines)
                {
                    q.Enqueue(ii);
                }
            }

            // DFS to copy with the list telling us
            // what to include.
            return GetDfsSubtree(Inlines, l, lastInline);
        }

        Inline[] GetDfsSubtree(Inline[] inlines, List<Inline> filter, Inline lastInline)
        {
            List<Inline> newInlines = new List<Inline>();
            foreach (Inline x in inlines)
            {
                if (filter.Contains(x))
                {
                    Inline xn = x.ShallowCopy();
                    // Flag the last inline so the jit can collect
                    // data for it during replay.
                    if (x == lastInline)
                    {
                        xn.CollectData = 1;
                    }
                    newInlines.Add(xn);
                    xn.Inlines = GetDfsSubtree(x.Inlines, filter, lastInline);
                }
            }

            return newInlines.ToArray();
        }

        public Method ShallowCopy()
        {
            Method r = new Method();
            r.Token = Token;
            r.Hash = Hash;
            r.Name = Name;
            r.Inlines = new Inline[0];
            return r;
        }

        public uint Token;
        public uint Hash;
        public string Name;
        public uint InlineCount;
        public uint HotSize;
        public uint ColdSize;
        public uint JitTime;
        public uint SizeEstimate;
        public uint TimeEstimate;
        public Inline[] Inlines;
        public ulong CallCount;
        public void MarkAsDuplicate() { IsDuplicate = true; }
        public bool CheckIsDuplicate() { return IsDuplicate; }
        private bool IsDuplicate;

        public List<Method> Callers;
        public List<Method> Callees;
    }

    // THe jit-visible call graph
    public class CallGraph
    {
        public CallGraph(Results fullResults)
        {
            Map = fullResults.Methods;
            Nodes = new HashSet<Method>();
            Roots = new HashSet<Method>();
            Leaves = new HashSet<Method>();
            Build();
        }
        public void Build()
        {
            // Populate per-method caller and callee lists
            // Drive via IDs to consolidate dups
            foreach(MethodId callerId in Map.Keys)
            {
                Method caller = Map[callerId];

                foreach (Inline i in caller.Inlines)
                {
                    MethodId calleeId = i.GetMethodId();

                    // Not sure why it wouldn't....
                    if (Map.ContainsKey(calleeId))
                    {
                        Method callee = Map[calleeId];

                        caller.Callees.Add(callee);
                        callee.Callers.Add(caller);
                    }         
                }
            }

            foreach (MethodId methodId in Map.Keys)
            {
                Method method = Map[methodId];

                Nodes.Add(method);

                // Methods with no callers are roots.
                if (method.Callers.Count == 0)
                {
                    Roots.Add(method);
                }

                // Methods with no callees are leaves.
                if (method.Callees.Count == 0)
                {
                    Leaves.Add(method);
                }
            }
        }

        public Dictionary<MethodId, Method> Map;
        public HashSet<Method> Nodes;
        public HashSet<Method> Roots;
        public HashSet<Method> Leaves;

        public void DumpDot(string file)
        {
            using (StreamWriter outFile = File.CreateText(file))
            {
                outFile.WriteLine("digraph CallGraph {");
                foreach (Method m in Nodes)
                {
                    outFile.WriteLine("\"{0:X8}-{1:X8}\" [ label=\"{2}\"];", m.Token, m.Hash, m.Name);

                    foreach (Method p in m.Callees)
                    {
                        outFile.WriteLine("\"{0:X8}-{1:X8}\" -> \"{2:X8}-{3:X8}\";",
                            m.Token, m.Hash, p.Token, p.Hash);
                    }
                }
                outFile.WriteLine("}");
            }
        }
    }

    // A node in an inline tree.
    public class Inline
    {
        public double NumSubtrees()
        {
            double result = 1;
            foreach (Inline i in Inlines)
            {
                result *= (1 + i.NumSubtrees());
            }
            return result;
        }

        public uint Token;
        public uint Hash;
        public uint Offset;
        public uint CollectData;
        public string Reason;
        public Inline[] Inlines;

        public Inline ShallowCopy()
        {
            Inline x = new Inline();
            x.Token = Token;
            x.Hash = Hash;
            x.Offset = Offset;
            x.Reason = Reason;
            x.CollectData = 0;
            x.Inlines = new Inline[0];
            return x;
        }

        public MethodId GetMethodId()
        {
            MethodId id = new MethodId();
            id.Token = Token;
            id.Hash = Hash;
            return id;
        }
        public void Dump(int indent)
        {
            for (int i = 0; i < indent; i++) Console.Write(" ");
            Console.WriteLine("{0:X8} {1}", Token, Reason);
            foreach (Inline x in Inlines)
            {
                x.Dump(indent + 2);
            }
        }
    }

    // InlineForest describes the inline forest used for the run.
    public class InlineForest
    {
        public string Policy;
        public string DataSchema;
        public Method[] Methods;
    }

    // The benchmark of interest
    public class Benchmark
    {
        public string ShortName;
        public string FullPath;
        public int ExitCode;
    }

    // The results of running a benchmark
    public class Results
    {
        public Results()
        {
            Performance = new PerformanceData();
        }

        public int ExitCode;
        public string LogFile;
        public bool Success;
        public InlineForest InlineForest;
        public Dictionary<MethodId, Method> Methods;
        public PerformanceData Performance;
        public string Name;
    }

    public class InlineDelta : IComparable<InlineDelta>
    {
        public Method rootMethod;
        public MethodId inlineMethodId;
        public double pctDelta;
        public int index;
        public string subBench;
        public double confidence;
        public double instructionsDelta;
        public double callsDelta;
        public bool hasPerCallDelta;
        public double perCallDelta;
        public int CompareTo(InlineDelta other)
        {
            return -Math.Abs(pctDelta).CompareTo(Math.Abs(other.pctDelta));
        }
    }

    public class Exploration : IComparable<Exploration>
    {
        public Results baseResults;
        public Results endResults;
        public Benchmark benchmark;

        // Consider benchmarks with fewer roots as better
        // candidates for exploration.
        public int CompareTo(Exploration other)
        {
            return endResults.Methods.Count() - other.endResults.Methods.Count();
        }

        public void Explore(StreamWriter combinedDataFile, ref bool combinedHasHeader)
        {
            Console.WriteLine("$$$ Exploring significant perf diff in {0} between {1} and {2}",
                benchmark.ShortName, baseResults.Name, endResults.Name);

            // Summary of performance results
            List<InlineDelta> deltas = new List<InlineDelta>();

            // Fully detailed result trees with performance data
            Dictionary<MethodId, Results[]> recapturedData = new Dictionary<MethodId, Results[]>();

            // Similar but for call count reductions....
            Dictionary<MethodId, double[]> recapturedCC = new Dictionary<MethodId, double[]>();

            // Count methods in end results with inlines, and total subtree size.
            int candidateCount = 0;
            int exploreCount = 0;
            foreach (Method m in endResults.Methods.Values)
            {
                int endCount = (int)m.InlineCount;
                if (endCount > 0)
                {
                    candidateCount++;
                    exploreCount += endCount;
                }
            }

            Console.WriteLine("$$$ Examining {0} methods, {1} inline combinations", candidateCount, exploreCount);

            // Todo: order methods by call count. Find top N% of these. Determine callers (and up the tree)
            // Explore from there.

            // Explore each method with inlines. Arbitrarily bail after some number of explorations.
            int methodsExplored = 0;
            int inlinesExplored = 0;
            int perMethodExplorationLimit = 25;
            int perBenchmarkExplorationLimit = 100;
            List<Method> methodsToExplore = new List<Method>(endResults.Methods.Values);
            methodsToExplore.Sort(Method.HasMoreCalls);

            foreach (Method rootMethod in methodsToExplore)
            {
                Console.WriteLine("$$$ InlinesExplored {0} MethodsExplored {1}", inlinesExplored, methodsExplored);
                Console.WriteLine("$$$ Exploring inlines for {0}", rootMethod.Name);

                // Only explore methods that had inlines
                int endCount = (int) rootMethod.InlineCount;

                if (endCount == 0)
                {
                    Console.WriteLine("$$$ Skipping -- no inlines");
                    continue;
                }

                // Don't bother exploring main since it won't be invoked via xperf
                // and so any apparent call count reductions from main will be misleading.
                if (rootMethod.Name.Equals("Main"))
                {
                    Console.WriteLine("$$$ Skipping -- not driven by xunit-perf");
                    continue;
                }

                //if (!rootMethod.Name.Contains("b__1"))
                //{
                //    // Console.WriteLine("$$$ Skipping, not special method of interest");
                //    continue;
                //}

                // Only expore methods that were called in the noinline run
                if (rootMethod.CallCount == 0)
                {
                    //Console.WriteLine("$$$ Skipping -- not called");
                    continue;
                }

                // Limit volume of exploration
                if (inlinesExplored >= perBenchmarkExplorationLimit)
                {
                    Console.WriteLine("$$$ Reached benchmark limit of {0} explored inlines, moving on to next benchmark",
                        perBenchmarkExplorationLimit);
                    break;
                }

                if (endCount > perMethodExplorationLimit)
                {
                    int newEndCount = perMethodExplorationLimit;
                    Console.WriteLine("$$$ Limiting exploration for this root to {0} inlines out of {1}", newEndCount, endCount);
                    endCount = newEndCount;
                }

                // Trim exploration here if full explore would put us over the limit
                if (inlinesExplored + endCount >= perBenchmarkExplorationLimit)
                {
                    int newEndCount = perBenchmarkExplorationLimit - inlinesExplored;
                    Console.WriteLine("$$$ Might hit limit of {0} inlines explored, trimming end count from {1} to {2}", 
                        perBenchmarkExplorationLimit, endCount, newEndCount);
                    endCount = newEndCount;
                }

                // Noinline perf is already "known" from the baseline, so exclude that here.
                //
                // The maximal subtree perf may not equal the end perf because the latter allows inlines
                // in all methods, and we're just inlining into one method at a time here.
                Console.WriteLine("$$$ [{0}] examining method {1} {2:X8} with {3} inlines and {4} permutations via BFS.",
                methodsExplored++, rootMethod.Name, rootMethod.Token, endCount, rootMethod.NumSubtrees() - 1);
                rootMethod.Dump();

                // Now for the actual experiment. We're going to grow the method's inline tree from the
                // baseline tree (which is noinline) towards the end result tree. For sufficiently large trees
                // there are lots of intermediate subtrees. For now we just do a simple breadth-first linear
                // exploration.
                //
                // However, we'll measure the full tree first. If there's no significant diff between it
                // and the noinline tree, then we won't bother enumerating and measuring the remaining subtrees.
                Results[] explorationResults = new Results[endCount + 1];
                explorationResults[0] = baseResults;

                // After we measure perf via xunit-perf, do a normal run to recapture inline observations.
                // We could enable observations in the perf run, but we'd get inline xml for all the xunit
                // scaffolding too. This way we get something minimal.
                Results[] recaptureResults = new Results[endCount + 1];
                recaptureResults[0] = baseResults;

                // Call count reduction at each step of the tree expansion
                double[] ccDeltas = new double[endCount + 1];

                // We take advantage of the fact that for replay Xml, the default is to not inline.
                // So we only need to emit Xml for the methods we want to inline. Since we're only
                // inlining into one method, our forest just has one Method entry.
                InlineForest kForest = new InlineForest();
                kForest.Policy = "ReplayPolicy";
                kForest.Methods = new Method[1];
                kForest.Methods[0] = rootMethod.ShallowCopy();

                // Always explore methods with one or two possible inlines, since checking to see if the
                // exploration is worthwhile costs just as much as doing the exploration.
                //
                // If there are multiple inlines, then jump to the end to see if any of them matter.
                // If not then don't bother exploring the intermediate states.
                //
                // This might bias the exploration into visiting more good cases than "normal".
                if (rootMethod.InlineCount > 2)
                {
                    // See if any inline in the tree has a perf impact. If not, don't bother exploring.
                    ulong dontcare = 0;
                    ExploreSubtree(kForest, endCount, rootMethod, benchmark, explorationResults, null, null, out dontcare);
                    bool shouldExplore = CheckResults(explorationResults, endCount, 0);

                    if (!shouldExplore)
                    {
                        Console.WriteLine("$$$ Full subtree perf NOT significant, skipping...");
                        continue;
                    }
                    else
                    {
                        Console.WriteLine("$$$ Full subtree perf significant, exploring...");
                    }
                }
                else
                {
                    Console.WriteLine("$$$ Single/Double inline, exploring...");
                }

                // Keep track of the current call count for each method.
                // Initial value is the base model's count.
                Dictionary<MethodId, ulong> callCounts = new Dictionary<MethodId, ulong>(baseResults.Methods.Count());
                foreach (MethodId id in baseResults.Methods.Keys)
                {
                    callCounts[id] = baseResults.Methods[id].CallCount;
                }

                // TODO: Every so often, rerun the noinline baseline, and see if we have baseline shift.

                ccDeltas[0] = 0;

                for (int k = 1; k <= endCount; k++)
                {
                    inlinesExplored++;
                    ulong ccDelta = 0;
                    Inline lastInlineK =
                        ExploreSubtree(kForest, k, rootMethod, benchmark, explorationResults, recaptureResults, callCounts, out ccDelta);
                    ShowResults(explorationResults, k, k - 1, rootMethod, lastInlineK, deltas, ccDelta);
                    ccDeltas[k] = ccDelta;
                }

                // Save off results for later processing.
                recapturedData[rootMethod.getId()] = recaptureResults;
                recapturedCC[rootMethod.getId()] = ccDeltas;
            }

            // Sort deltas and display
            deltas.Sort();
            Console.WriteLine("$$$ --- {0}: inlines in order of impact ---", endResults.Name);
            foreach (InlineDelta dd in deltas)
            {
                string currentMethodName = null;
                if (baseResults.Methods != null && baseResults.Methods.ContainsKey(dd.inlineMethodId))
                {
                    currentMethodName = baseResults.Methods[dd.inlineMethodId].Name;
                }
                else
                {
                    currentMethodName = dd.inlineMethodId.ToString();
                }

                Console.Write("$$$ --- [{0,2:D2}] {1,12} -> {2,-12} {3,6:0.00}%",
                    dd.index, dd.rootMethod.Name, currentMethodName, dd.pctDelta);
                if (dd.hasPerCallDelta)
                {
                    Console.Write(" {0,10:0.00} pc", dd.perCallDelta);
                }
                Console.WriteLine();
            }

            // Build integrated data model...
            string dataModelName = String.Format("{0}-{1}-data-model.csv", benchmark.ShortName, endResults.Name);
            string dataModelFileName = Path.Combine(Program.RESULTS_DIR, dataModelName);
            bool hasHeader = false;
            char[] comma = new char[] { ',' };
            using (StreamWriter dataModelFile = File.CreateText(dataModelFileName))
            {
                foreach (MethodId methodId in recapturedData.Keys)
                {
                    Results[] resultsSet = recapturedData[methodId];
                    double[] ccDeltas = recapturedCC[methodId];

                    // resultsSet[0] is the noinline run. We don't have a <Data> entry
                    // for it, but key column values are spilled into the inline Xml and
                    // so deserialized into method entries.
                    if (!baseResults.Methods.ContainsKey(methodId))
                    {
                        Console.WriteLine("!!! Odd -- no base data for root {0}", methodId);
                        continue;
                    }

                    int baseMethodHotSize = (int) baseResults.Methods[methodId].HotSize;
                    int baseMethodColdSize = (int) baseResults.Methods[methodId].ColdSize;
                    int baseMethodJitTime = (int) baseResults.Methods[methodId].JitTime;
                    ulong baseMethodCallCount = baseResults.Methods[methodId].CallCount;

                    for (int i = 1; i < resultsSet.Length; i++)
                    {
                        Results rK = resultsSet[i];
                        Results rKm1 = resultsSet[i - 1];

                        if (rK == null || rKm1 == null)
                        {
                            continue;
                        }

                        // Load up the recapture xml
                        XElement root = XElement.Load(rK.LogFile);

                        // Look for the embedded inliner observation schema
                        IEnumerable<XElement> schemas = from el in root.Descendants("DataSchema") select el;
                        XElement schema = schemas.First();
                        string schemaString = (string)schema;
                        // Add on the performance data column headers
                        string extendedSchemaString = 
                            "Benchmark,SubBenchmark," +
                            schemaString + 
                            ",HotSizeDelta,ColdSizeDelta,JitTimeDelta,InstRetiredDelta,InstRetiredPct" +
                            ",CallDelta,InstRetiredPerCallDelta,RootCallCount,InstRetiredPerRootCallDelta,Confidence";

                        // If we haven't yet emitted a local header, do so now.
                        if (!hasHeader)
                        {
                            dataModelFile.WriteLine(extendedSchemaString);
                            hasHeader = true;
                        }

                        // Similarly for the combined data file
                        if (!combinedHasHeader)
                        {
                            combinedDataFile.WriteLine(extendedSchemaString);
                            combinedHasHeader = true;
                        }

                        // Figure out relative position of a few key columns
                        string[] columnNames = schemaString.Split(comma);
                        int hotSizeIndex = -1;
                        int coldSizeIndex = -1;
                        int jitTimeIndex = -1;
                        int index = 0;
                        foreach (string s in columnNames)
                        {
                            switch (s)
                            {
                                case "HotSize":
                                    hotSizeIndex = index;
                                    break;
                                case "ColdSize":
                                    coldSizeIndex = index;
                                    break;
                                case "JitTime":
                                    jitTimeIndex = index;
                                    break;
                            }

                            index++;
                        }                   

                        // Find the embededed inline observation data
                        IEnumerable<XElement> data = from el in root.Descendants("Data") select el;
                        string dataString = (string)data.First();
                        string[] dataStringX = dataString.Split(comma);

                        // Split out the observations that we need for extended info.
                        int currentMethodHotSize = hotSizeIndex >= 0 ? Int32.Parse(dataStringX[hotSizeIndex]) : 0;
                        int currentMethodColdSize = coldSizeIndex >= 0 ? Int32.Parse(dataStringX[coldSizeIndex]) : 0;
                        int currentMethodJitTime = jitTimeIndex >= 0 ? Int32.Parse(dataStringX[jitTimeIndex]) : 0;
                        double currentCCDelta = ccDeltas[i];

                        // How to handle data from multi-part benchmarks?
                        // Aggregate it here, iteration-wise
                        int subParts = rK.Performance.InstructionCount.Keys.Count;
                        List<double> arKData = null;
                        List<double> arKm1Data = null;
                        foreach (string subBench in rK.Performance.InstructionCount.Keys)
                        {
                            if (!rK.Performance.InstructionCount.ContainsKey(subBench))
                            {
                                Console.WriteLine("!!! Odd -- no data for root {0} on {1} at index {2}",
                                    methodId, subBench, i);
                                continue;
                            }

                            if (!rKm1.Performance.InstructionCount.ContainsKey(subBench))
                            {
                                Console.WriteLine("!!! Odd -- no data for root {0} on {1} at index {2}",
                                    methodId, subBench, i - 1);
                                continue;
                            }

                            List<double> rKData = rK.Performance.InstructionCount[subBench];
                            List<double> rKm1Data = rKm1.Performance.InstructionCount[subBench];

                            if (arKData == null)
                            {
                                arKData = new List<double>(rKData);
                                arKm1Data = new List<double>(rKm1Data);
                                if (arKData.Count != arKm1Data.Count)
                                {
                                    Console.WriteLine("!!! Odd -- mismatched data for root {0} on {1} at index {2}",
                                        methodId, subBench, i);
                                    continue;
                                }
                            }
                            else
                            {
                                for (int ii = 0; ii < arKData.Count; ii++)
                                {
                                    arKData[ii] += rKData[ii];
                                    arKm1Data[ii] += rKm1Data[ii];
                                }
                            }
                        }

                        double confidence = PerformanceData.Confidence(arKData, arKm1Data);
                        double arKAvg = PerformanceData.Average(arKData);
                        double arKm1Avg = PerformanceData.Average(arKm1Data);
                        double change = arKAvg - arKm1Avg;
                        double pctDiff = 100.0 * change / arKm1Avg;
                        // Number of instructions saved per call to the current inlinee
                        double perCallDelta = (currentCCDelta == 0) ? 0 : change / currentCCDelta;
                        // Number of instructions saved per call to the root method
                        double perRootDelta = (baseMethodCallCount == 0) ? 0 : change / baseMethodCallCount;

                        int hotSizeDelta = currentMethodHotSize - baseMethodHotSize;
                        int coldSizeDelta = currentMethodColdSize - baseMethodColdSize;
                        int jitTimeDelta = currentMethodJitTime - baseMethodJitTime;

                        dataModelFile.WriteLine("{0},{1},{2},{3},{4},{5},{6:0.00},{7:0.00},{8:0.00},{9:0.00},{10:0.00},{11:0.00},{12:0.00}",
                            benchmark.ShortName, "agg",
                            dataString,
                            hotSizeDelta, coldSizeDelta, jitTimeDelta,
                            change / (1000 * 1000), pctDiff, currentCCDelta, perCallDelta, 
                            baseMethodCallCount, perRootDelta, confidence);

                        combinedDataFile.WriteLine("{0},{1},{2},{3},{4},{5},{6:0.00},{7:0.00},{8:0.00},{9:0.00},{10:0.00},{11:0.00},{12:0.00}",
                            benchmark.ShortName, "agg",
                            dataString,
                            hotSizeDelta, coldSizeDelta, jitTimeDelta,
                            change / (1000 * 1000), pctDiff, currentCCDelta, perCallDelta, 
                            baseMethodCallCount, perRootDelta, confidence);

                        baseMethodHotSize = currentMethodHotSize;
                        baseMethodColdSize = currentMethodColdSize;
                        baseMethodJitTime = currentMethodJitTime;
                    }
                }
            }
        }

        Inline ExploreSubtree(InlineForest kForest, int k, Method rootMethod,
            Benchmark benchmark, Results[] explorationResults, Results[] recaptureResults, 
            Dictionary<MethodId, ulong> callCounts, out ulong ccDelta)
        {
            ccDelta = 0;

            // Build inline subtree for method with first K nodes and swap it into the tree.
            int index = 0;
            Inline currentInline = null;
            Inline[] mkInlines = rootMethod.GetBfsSubtree(k, out currentInline);

            if (mkInlines == null)
            {
                Console.WriteLine("$$$ {0} [{1}] Can't get this inline subtree yet, sorry", rootMethod.Name, k);
                return null;
            }

            kForest.Methods[index].Inlines = mkInlines;
            kForest.Methods[index].InlineCount = (uint) k;

            // Externalize the inline xml
            XmlSerializer xo = new XmlSerializer(typeof(InlineForest));
            string testName = String.Format("{0}-{1}-{2:X8}-{3}", benchmark.ShortName, endResults.Name, rootMethod.Token, k);
            string xmlName = testName + ".xml";
            string resultsDir = Program.RESULTS_DIR;
            string replayFileName = Path.Combine(resultsDir, xmlName);
            using (Stream xmlOutFile = new FileStream(replayFileName, FileMode.Create))
            {
                xo.Serialize(xmlOutFile, kForest);
            }

            // Run the test and record the perf results.
            XunitPerfRunner x = new XunitPerfRunner();
            Configuration c = new Configuration(testName);
            c.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
            c.Environment["COMPlus_JitInlineReplayFile"] = replayFileName;
            Results resultsK = x.RunBenchmark(benchmark, c);
            explorationResults[k] = resultsK;

            if (recaptureResults != null)
            {
                // Run test and recapture the inline XML along with observational data about the last inline
                string retestName = String.Format("{0}-{1}-{2:X8}-{3}-data", benchmark.ShortName, endResults.Name, rootMethod.Token, k);
                Configuration cr = new Configuration(retestName);
                CoreClrRunner clr = new CoreClrRunner();
                cr.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
                cr.Environment["COMPlus_JitInlineReplayFile"] = replayFileName;
                // Ask for "minimal" replay XML here
                cr.Environment["COMPlus_JitInlineDumpXml"] = "2";
                cr.Environment["COMPlus_JitInlineDumpData"] = "1";
                Results resultsClr = clr.RunBenchmark(benchmark, cr);
                // Snag performance data from above
                resultsClr.Performance = resultsK.Performance;
                recaptureResults[k] = resultsClr;
            }

            // Run and capture method call counts
            if (Program.CaptureCallCounts && callCounts != null)
            {
                string callCountName = String.Format("{0}-{1}-{2:X8}-{3}-cc", benchmark.ShortName, endResults.Name, rootMethod.Token, k);
                Configuration cc = new Configuration(callCountName);
                CoreClrRunner clr = new CoreClrRunner();
                cc.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
                cc.Environment["COMPlus_JitInlineReplayFile"] = replayFileName;
                // Ask for method entry instrumentation
                cc.Environment["COMPlus_JitMeasureEntryCounts"] = "1";
                Results resultsCC = clr.RunBenchmark(benchmark, cc);

                MethodId currentId = currentInline.GetMethodId();
                bool foundcc = false;
                // Parse results back and find call count for the current inline.
                using (StreamReader callCountStream = File.OpenText(resultsCC.LogFile))
                {
                    string callCountLine = callCountStream.ReadLine();
                    while (callCountLine != null)
                    {
                        string[] callCountFields = callCountLine.Split(new char[] { ',' });
                        if (callCountFields.Length == 3)
                        {
                            uint token = UInt32.Parse(callCountFields[0], System.Globalization.NumberStyles.HexNumber);
                            uint hash = UInt32.Parse(callCountFields[1], System.Globalization.NumberStyles.HexNumber);
                            ulong count = UInt64.Parse(callCountFields[2]);

                            if (token == currentId.Token && hash == currentId.Hash)
                            {
                                foundcc = true;

                                if (callCounts.ContainsKey(currentId))
                                {
                                    // Note we expect it not to increase!
                                    //
                                    // Zero is possible if we inline at a call site that was not hit.
                                    // We may even see perf impact with zero call count change,
                                    // because of changes elsewhere in the method in code that is hit.
                                    ulong oldCount = callCounts[currentId];
                                    callCounts[currentId] = count;
                                    Console.WriteLine("Call count for {0:X8}-{1:X8} went from {2} to {3}",
                                        token, hash, oldCount, count);
                                    ccDelta = oldCount - count;
                                    if (ccDelta < 0)
                                    {
                                        Console.WriteLine("Call count unexpectedly increased!");
                                    }
                                }
                                else
                                {
                                    // Don't really expect to hit this.. we'll never see this method as a root.
                                    Console.WriteLine("Call count for {0:X8}-{1:X8} went from {2} to {3}",
                                        token, hash, "unknown", count);
                                }
                                break;
                            }
                        }

                        callCountLine = callCountStream.ReadLine();
                    }
                }

                if (!foundcc)
                {
                    // Note if the method is never called we may not have an entry!
                    // Will just assume after result is zero.
                    Console.WriteLine("No call count entry for {0:X8}-{1:X8}. Assuming zero.",
                        currentId.Token, currentId.Hash);
                    if (callCounts.ContainsKey(currentId))
                    {
                        ccDelta = callCounts[currentId];
                    }
                    callCounts[currentId] = 0;
                }
            }

            return currentInline;
        }

        // Determine confidence level that performance differs in the two indicated
        // result sets.
        //
        // If we can't tell the difference between the two, it may
        // mean either (a) the method or call site was never executed, or (b)
        // the inlines had no perf impact.
        //
        // We could still add this info to our model, since the jit won't generally
        // be able to tell if a callee will be executed, but for now we just look
        // for impactful changes.
        bool CheckResults(Results[] explorationResults, int diffIndex, int baseIndex)
        {
            Results baseResults = explorationResults[baseIndex];
            Results diffResults = explorationResults[diffIndex];

            // Make sure runs happened. Might not if we couldn't find the base method.
            if (baseResults == null)
            {
                Console.WriteLine("$$$ Can't get base run data, sorry");
                return false;
            }

            if (diffResults == null)
            {
                Console.WriteLine("$$$ Can't get diff run data, sorry");
                return false;
            }

            bool signficant = false;

            foreach (string subBench in baseResults.Performance.InstructionCount.Keys)
            {
                List<double> baseData = baseResults.Performance.InstructionCount[subBench];
                List<double> diffData = diffResults.Performance.InstructionCount[subBench];
                double confidence = PerformanceData.Confidence(baseData, diffData);

                signficant |= (confidence > 0.8);
            }

            return signficant;
        }
        void ShowResults(Results[] explorationResults, int diffIndex, int baseIndex, 
            Method rootMethod, Inline currentInline, List<InlineDelta> deltas, ulong ccDelta)
        {
            Results zeroResults = explorationResults[0];
            Results baseResults = explorationResults[baseIndex];
            Results diffResults = explorationResults[diffIndex];

            // Make sure runs happened. Might not if we couldn't find the base method.
            if (zeroResults == null)
            {
                Console.WriteLine("$$$ Can't get noinline run data, sorry");
                return;
            }

            if (baseResults == null)
            {
                Console.WriteLine("$$$ Can't get base run data, sorry");
                return;
            }

            if (diffResults == null)
            {
                Console.WriteLine("$$$ Can't get diff run data, sorry");
                return;
            }

            // Try and get the name of the last inline.
            // We may not know it, if the method was prejitted, since it will
            // never be a jit root.
            // If so, use the token value.
            MethodId currentMethodId = currentInline.GetMethodId();
            string currentMethodName = null;
            if (baseResults.Methods != null && baseResults.Methods.ContainsKey(currentMethodId))
            {
                currentMethodName = baseResults.Methods[currentMethodId].Name;
            }
            else
            {
                currentMethodName = String.Format("Token {0:X8} Hash {1:X8}",
                    currentMethodId.Token, currentMethodId.Hash);
            }

            Console.WriteLine("$$$ Root {0} index {1} inlining {2}", rootMethod.Name, diffIndex, currentMethodName);

            foreach (string subBench in baseResults.Performance.InstructionCount.Keys)
            {
                List<double> zeroData = zeroResults.Performance.InstructionCount[subBench];
                List<double> baseData = baseResults.Performance.InstructionCount[subBench];
                List<double> diffData = diffResults.Performance.InstructionCount[subBench];

                double confidence = PerformanceData.Confidence(baseData, diffData);
                double baseAvg = PerformanceData.Average(baseData);
                double diffAvg = PerformanceData.Average(diffData);
                double change = diffAvg - baseAvg;
                double pctDiff = 100.0 * change / baseAvg;

                double confidence0 = PerformanceData.Confidence(baseData, diffData);
                double zeroAvg = PerformanceData.Average(zeroData);
                double change0 = diffAvg - zeroAvg;
                double pctDiff0 = 100.0 * change0 / zeroAvg;

                Console.WriteLine("{0:30}: base {1:0.00}M new {2:0.00}M delta {3:0.00}M ({4:0.00}%) confidence {5:0.00}",
                    subBench,
                    baseAvg / (1000 * 1000), diffAvg / (1000 * 1000),
                    change / (1000 * 1000), pctDiff, confidence);
                Console.Write("{0:30}  noinl {1:0.00}M delta {2:0.00}M ({3:0.00}%) confidence {4:0.00}",
                    "", zeroAvg / (1000 * 1000), change0 / (1000 * 1000), pctDiff0, confidence0);

                if (ccDelta != 0)
                {
                    Console.Write(" cc-delta {0} ipc {1:0.00}", ccDelta, change / ccDelta );
                }

                Console.WriteLine();

                if (deltas != null)
                {
                    InlineDelta d = new InlineDelta();

                    d.rootMethod = rootMethod;
                    d.inlineMethodId = currentMethodId;
                    d.pctDelta = pctDiff;
                    d.index = diffIndex;
                    d.subBench = subBench;
                    d.confidence = confidence;
                    d.instructionsDelta = change;
                    if (ccDelta != 0)
                    {
                        d.hasPerCallDelta = true;
                        d.perCallDelta = change / ccDelta;
                        d.callsDelta = ccDelta;
                    }

                    deltas.Add(d);
                }
            }
        }
    }

    // A mechanism to run the benchmark
    public abstract class Runner
    {
        public abstract Results RunBenchmark(Benchmark b, Configuration c);
    }

    public class CoreClrRunner : Runner
    {
        public CoreClrRunner()
        {
            cmdExe = Program.SHELL;
            runnerExe = Program.CORERUN;
            verbose = true;
            veryVerbose = true;
        }

        public override Results RunBenchmark(Benchmark b, Configuration c)
        {
            // Make sure there's an exe to run.
            if (!File.Exists(runnerExe))
            {
                Console.WriteLine("Can't find runner exe: '{0}'", runnerExe);
                return null;
            }

            // Setup process information
            System.Diagnostics.Process runnerProcess = new Process();
            runnerProcess.StartInfo.FileName = cmdExe;
            string stderrName = c.ResultsDirectory + @"\" + b.ShortName + "-" + c.Name + ".xml";

            foreach (string envVar in c.Environment.Keys)
            {
                runnerProcess.StartInfo.Environment[envVar] = c.Environment[envVar];
            }
            runnerProcess.StartInfo.Environment["CORE_ROOT"] = Path.GetDirectoryName(runnerExe);
            runnerProcess.StartInfo.Arguments = "/C \"" + runnerExe + " " + b.FullPath + " 2> " + stderrName + "\"";
            runnerProcess.StartInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(b.FullPath);
            runnerProcess.StartInfo.UseShellExecute = false;

            if (veryVerbose)
            {
                Console.WriteLine("CoreCLR: launching " + runnerProcess.StartInfo.Arguments);
            }

            runnerProcess.Start();
            runnerProcess.WaitForExit();

            if (verbose)
            {
                Console.WriteLine("CoreCLR: Finished running {0} -- configuration: {1}, exit code: {2} (expected {3})",
                    b.ShortName, c.Name, runnerProcess.ExitCode, b.ExitCode);
            }

            Results results = new Results();
            results.Success = (b.ExitCode == runnerProcess.ExitCode);
            results.ExitCode = b.ExitCode;
            results.LogFile = stderrName;
            results.Name = c.Name;

            // TODO: Iterate to get perf data
            List<double> timeData = new List<double>(1);
            timeData.Add(runnerProcess.ExitTime.Subtract(runnerProcess.StartTime).TotalMilliseconds);
            results.Performance.ExecutionTime[b.ShortName] = timeData;
            return results;
        }

        private string runnerExe;
        private string cmdExe;
        bool verbose;
        bool veryVerbose;
    }

    public class XunitPerfRunner : Runner
    {
        public XunitPerfRunner()
        {
            verbose = true;
            veryVerbose = false;

            SetupSandbox();
        }

        void SetupSandbox()
        {
            // Only do this once per run
            if (sandboxIsSetup)
            {
                return;
            }

            if (Directory.Exists(sandboxDir))
            {
                if (verbose)
                {
                    Console.WriteLine("...Cleaning old xunit-perf sandbox '{0}'", sandboxDir);
                }
                Directory.Delete(sandboxDir, true);
            }

            if (verbose)
            {
                Console.WriteLine("...Creating new xunit-perf sandbox '{0}'", sandboxDir);
            }
            Directory.CreateDirectory(sandboxDir);
            DirectoryInfo sandboxDirectoryInfo = new DirectoryInfo(sandboxDir);

            // Copy over xunit packages
            string xUnitPerfRunner = Path.Combine(coreclrRoot, @"packages\Microsoft.DotNet.xunit.performance.runner.Windows\1.0.0-alpha-build0035\tools");
            string xUnitPerfConsole = Path.Combine(coreclrRoot, @"packages\xunit.console.netcore\1.0.2-prerelease-00101\runtimes\any\native");
            string xUnitPerfAnalysis = Path.Combine(coreclrRoot, @"packages\Microsoft.DotNet.xunit.performance.analysis\1.0.0-alpha-build0035\tools");

            CopyAll(new DirectoryInfo(xUnitPerfRunner), sandboxDirectoryInfo);
            CopyAll(new DirectoryInfo(xUnitPerfConsole), sandboxDirectoryInfo);
            CopyAll(new DirectoryInfo(xUnitPerfAnalysis), sandboxDirectoryInfo);
            CopyAll(new DirectoryInfo(testOverlayRoot), sandboxDirectoryInfo);

            sandboxIsSetup = true;
        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            Directory.CreateDirectory(target.FullName);

            // Copy each file into the new directory.
            foreach (FileInfo fi in source.GetFiles())
            {
                fi.CopyTo(Path.Combine(target.FullName, fi.Name), true);
            }

            // Copy each subdirectory using recursion.
            foreach (DirectoryInfo diSourceSubDir in source.GetDirectories())
            {
                DirectoryInfo nextTargetSubDir =
                    target.CreateSubdirectory(diSourceSubDir.Name);
                CopyAll(diSourceSubDir, nextTargetSubDir);
            }
        }

        // See if there's some way to just run a particular sub benchmark?
        public override Results RunBenchmark(Benchmark b, Configuration c)
        {
            // Copy benchmark to sandbox
            string benchmarkFile = Path.GetFileName(b.FullPath);
            File.Copy(b.FullPath, Path.Combine(sandboxDir, benchmarkFile), true);

            // Setup process information
            System.Diagnostics.Process runnerProcess = new Process();
            runnerProcess.StartInfo.FileName = Path.Combine(sandboxDir, "xunit.performance.run.exe");
            string perfName = c.Name + "-" + b.ShortName;

            foreach (string envVar in c.Environment.Keys)
            {
                runnerProcess.StartInfo.Environment[envVar] = c.Environment[envVar];
            }
            runnerProcess.StartInfo.Environment["CORE_ROOT"] = sandboxDir;
            runnerProcess.StartInfo.Environment["XUNIT_PERFORMANCE_MIN_ITERATION"] = Program.MinIterations.ToString();
            runnerProcess.StartInfo.Environment["XUNIT_PERFORMANCE_MAX_ITERATION"] = Program.MaxIterations.ToString();

            runnerProcess.StartInfo.Arguments = benchmarkFile +
                " -nologo -runner xunit.console.netcore.exe -runnerhost corerun.exe -runid " +
                perfName +
                (Program.ClassFilter == null ? "" : " -class " + Program.ClassFilter) +
                (Program.MethodFilter == null ? "" : " -method " + Program.MethodFilter);
            
            runnerProcess.StartInfo.WorkingDirectory = sandboxDir;
            runnerProcess.StartInfo.UseShellExecute = false;

            if (veryVerbose)
            {
                Console.WriteLine("xUnitPerf: launching " + runnerProcess.StartInfo.Arguments);
            }

            runnerProcess.Start();
            runnerProcess.WaitForExit();

            if (verbose)
            {   
                // Xunit doesn't run Main so no 100 exit here.
                Console.WriteLine("xUnitPerf: Finished running {0} -- configuration: {1}, exit code: {2}",
                    b.ShortName, c.Name, runnerProcess.ExitCode);
            }

            // Parse iterations out of perf-*.xml
            string xmlPerfResultsFile = Path.Combine(sandboxDir, perfName) + ".xml";
            XElement root = XElement.Load(xmlPerfResultsFile);
            IEnumerable<XElement> subBenchmarks = from el in root.Descendants("test") select el;

            // We keep the raw iterations results and just summarize here.
            Results results = new Results();
            PerformanceData perfData = results.Performance;

            foreach (XElement sub in subBenchmarks)
            {
                string subName = (string)sub.Attribute("name");

                IEnumerable<double> iExecutionTimes =
                    from el in sub.Descendants("iteration")
                    where el.Attribute("Duration") != null && (string)el.Attribute("index") != "0"
                    select Double.Parse((string)el.Attribute("Duration"));

                IEnumerable<double> iInstructionsRetired =
                    from el in sub.Descendants("iteration")
                    where el.Attribute("InstRetired") != null && (string)el.Attribute("index") != "0"
                    select Double.Parse((string)el.Attribute("InstRetired"));

                perfData.ExecutionTime[subName] = new List<double>(iExecutionTimes);
                perfData.InstructionCount[subName] = new List<double>(iInstructionsRetired);
            }

            if (verbose)
            {
                perfData.Print(c.Name);
            }

            results.Success = (b.ExitCode == runnerProcess.ExitCode);
            results.ExitCode = b.ExitCode;
            results.LogFile = "";
            results.Name = c.Name;

            return results;
        }

        static string sandboxDir = Program.SANDBOX_DIR;
        static string coreclrRoot = Program.CORECLR_ROOT;
        static string testOverlayRoot = Path.Combine(coreclrRoot, @"bin\tests\Windows_NT.x64.Release\tests\Core_Root");
        static bool sandboxIsSetup;
        bool verbose;
        bool veryVerbose;
    }

    public class Program
    {

        // The noinline model is one where inlining is disabled.
        // The inline forest here is minimal.
        //
        // An attributed profile of this model helps the tool
        // identify areas for investigation.
        Results BuildNoInlineModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- No Inline Model for {0}", b.ShortName);

            // Create empty inline replay XML
            InlineForest emptyForest = new InlineForest();
            emptyForest.Policy = "ReplayPolicy";
            XmlSerializer emptySerializer = new XmlSerializer(typeof(InlineForest));
            string emptyXmlFile = String.Format("{0}-empy-replay.xml", b.ShortName);
            string emptyXmlPath = Path.Combine(Program.RESULTS_DIR, emptyXmlFile);
            using (Stream emptyXmlStream = new FileStream(emptyXmlPath, FileMode.Create))
            {
                emptySerializer.Serialize(emptyXmlStream, emptyForest);
            }

            // Replay with empty xml and recapture the full noinline xml. Latter will
            // show all the methods that were jitted.
            Configuration noInlineConfig = new Configuration("noinl");
            noInlineConfig.ResultsDirectory = Program.RESULTS_DIR;
            noInlineConfig.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
            noInlineConfig.Environment["COMPlus_JitInlineReplayFile"] = emptyXmlPath;
            noInlineConfig.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results noInlineResults = r.RunBenchmark(b, noInlineConfig);

            if (noInlineResults == null || !noInlineResults.Success)
            {
                Console.WriteLine("Noinline run failed\n");
                return null;
            }

            // Parse noinline xml
            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            InlineForest f;
            Stream xmlFile = new FileStream(noInlineResults.LogFile, FileMode.Open);
            try
            {
                f = (InlineForest)xml.Deserialize(xmlFile);
            }
            catch (System.Exception ex)
            {
                Console.WriteLine("Xml deserialization failed: " + ex.Message);
                return null;
            }

            long inlineCount = f.Methods.Sum(m => m.InlineCount);
            Console.WriteLine("*** Noinline config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
            noInlineResults.InlineForest = f;

            // Determine set of unique method Ids and build map from ID to method
            Dictionary<MethodId, uint> idCounts = new Dictionary<MethodId, uint>();
            Dictionary<MethodId, Method> methods = new Dictionary<MethodId, Method>(f.Methods.Length);

            foreach (Method m in f.Methods)
            {
                MethodId id = m.getId();
                methods[id] = m;

                if (idCounts.ContainsKey(id))
                {
                    idCounts[id]++;
                }
                else
                {
                    idCounts[id] = 1;
                }
            }

            noInlineResults.Methods = methods;

            Console.WriteLine("*** Noinline config has {0} unique method IDs", idCounts.Count);

            foreach (MethodId m in idCounts.Keys)
            {
                uint count = idCounts[m];
                if (count > 1)
                {
                    Console.WriteLine("*** MethodId Token:0x{0:X8} Hash:0x{1:X8} has {2} duplicates", m.Token, m.Hash, count);
                }
            }

            // Mark methods in noinline results that do not have unique IDs
            foreach (Method m in f.Methods)
            {
                MethodId id = m.getId();
                if (idCounts[id] > 1)
                {
                    m.MarkAsDuplicate();
                }
            }

            // Get noinline perf numbers using empty replay xml
            Configuration noinlinePerfConfig = new Configuration("noinline-perf");
            noinlinePerfConfig.ResultsDirectory = Program.RESULTS_DIR;
            noinlinePerfConfig.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
            noinlinePerfConfig.Environment["COMPlus_JitInlineReplayFile"] = emptyXmlPath;
            Results perfResults = x.RunBenchmark(b, noinlinePerfConfig);
            Console.WriteLine("-- Updating noinline results");
            noInlineResults.Performance = perfResults.Performance;
            noInlineResults.Performance.Print(noInlineConfig.Name);

            // Get noinline method call counts
            // Todo: use xunit runner and capture stderr? Downside is that xunit-perf
            // entry points won't be in the baseline method set.
            if (CaptureCallCounts)
            {
                Configuration noInlineCallCountConfig = new Configuration("noinline-cc");
                noInlineCallCountConfig.ResultsDirectory = Program.RESULTS_DIR;
                noInlineCallCountConfig.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
                noInlineCallCountConfig.Environment["COMPlus_JitInlineReplayFile"] = emptyXmlPath;
                noInlineCallCountConfig.Environment["COMPlus_JitMeasureEntryCounts"] = "1";
                Results ccResults = r.RunBenchmark(b, noInlineCallCountConfig);

                AnnotateCallCounts(ccResults, noInlineResults);
            }

            return noInlineResults;
        }

        // The legacy model reflects the current jit behavior.
        // Scoring of runs will be relative to this data.
        // The inherent noise level is also estimated here.
        Results BuildLegacyModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Legacy Model for {0}", b.ShortName);

            Configuration legacyConfig = new Configuration("legacy");
            legacyConfig.ResultsDirectory = Program.RESULTS_DIR;
            legacyConfig.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results legacyResults = r.RunBenchmark(b, legacyConfig);

            if (legacyResults == null || !legacyResults.Success)
            {
                Console.WriteLine("Legacy run failed\n");
                return null;
            }

            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            InlineForest f;
            Stream xmlFile = new FileStream(legacyResults.LogFile, FileMode.Open);
            f = (InlineForest) xml.Deserialize(xmlFile);
            long inlineCount = f.Methods.Sum(m => m.InlineCount);
            Console.WriteLine("*** Legacy config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
            legacyResults.InlineForest = f;

            // Populate the methodId -> method lookup table
            Dictionary<MethodId, Method> methods = new Dictionary<MethodId, Method>(f.Methods.Length);
            foreach (Method m in f.Methods)
            {
                MethodId id = m.getId();
                methods[id] = m;
            }
            legacyResults.Methods = methods;

            // Now get legacy perf numbers
            Configuration legacyPerfConfig = new Configuration("legacy-perf");
            legacyPerfConfig.ResultsDirectory = Program.RESULTS_DIR;
            Results perfResults = x.RunBenchmark(b, legacyPerfConfig);
            legacyResults.Performance = perfResults.Performance;
            legacyResults.Performance.Print(legacyConfig.Name);

            // Get legacy method call counts
            if (CaptureCallCounts)
            {
                Configuration legacyCallCountConfig = new Configuration("legacy-cc");
                legacyCallCountConfig.ResultsDirectory = Program.RESULTS_DIR;
                legacyCallCountConfig.Environment["COMPlus_JitMeasureEntryCounts"] = "1";
                Results ccResults = r.RunBenchmark(b, legacyCallCountConfig);

                // Parse results back and annotate base method set
                AnnotateCallCounts(ccResults, legacyResults);
            }

            return legacyResults;
        }

        // The full model creates an inline forest at some prescribed
        // depth. The inline configurations that will be explored
        // are sub-forests of this full forest.
        Results BuildFullModel(Runner r, Runner x, Benchmark b, Results noinlineResults)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Full Model for {0}", b.ShortName);

            string resultsDir = Program.RESULTS_DIR;
            // Because we're jitting and inlining some methods won't be jitted on
            // their own at all. To unearth full trees for all methods we need
            // to iterate. The rough idea is as follows.
            //
            // Run with FullPolicy for all methods. This will end up jitting
            // some subset of methods seen in the noinline config. Compute this subset,
            // collect up their trees, and then disable inlining for those methods.
            // Rerun. This time around some of the methods missed in the first will
            // be jitted and will grow inline trees. Collect these new trees and
            // add those methods to the disabled set. Repeat until we've seen all methods.
            //
            // Unfortunately we don't have unique IDs for methods. To handle this we
            // need to determine which methods do have unique IDs.

            // This is the count of noinline methods with unique IDs.
            int methodCount = noinlineResults.Methods.Count;

            // We'll collect up these methods with their full trees here.
            HashSet<MethodId> fullMethodIds = new HashSet<MethodId>();
            List<Method> fullMethods = new List<Method>(methodCount);
            uint iteration = 0;
            uint maxInlineCount = 0;
            uint leafMethodCount = 0;
            uint newMethodCount = 0;
            Method maxInlineMethod = null;
            bool failed = false;

            while (fullMethodIds.Count < methodCount + newMethodCount)
            {
                iteration++;

                Console.WriteLine("*** Full config -- iteration {0}, still need trees for {1} out of {2} methods",
                    iteration, methodCount + newMethodCount - fullMethodIds.Count, methodCount + newMethodCount);

                Configuration fullConfiguration = new Configuration("full-" + iteration);
                fullConfiguration.ResultsDirectory = resultsDir;
                fullConfiguration.Environment["COMPlus_JitInlinePolicyFull"] = "1";
                fullConfiguration.Environment["COMPlus_JitInlineDepth"] = "10";
                fullConfiguration.Environment["COMPlus_JitInlineSize"] = "200";
                fullConfiguration.Environment["COMPlus_JitInlineDumpXml"] = "1";

                // Build an exclude string disabiling inlining in all the methods we've
                // collected so far. If there are no methods yet, don't bother.
                if (fullMethodIds.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (MethodId id in fullMethodIds)
                    {
                        sb.Append(" ");
                        sb.Append(id.Hash);
                    }
                    string excludeString = sb.ToString();
                    // Console.WriteLine("*** exclude string: {0}\n", excludeString);
                    fullConfiguration.Environment["COMPlus_JitNoInlineRange"] = excludeString;
                }

                // Run this iteration
                Results currentResults = r.RunBenchmark(b, fullConfiguration);

                if (currentResults == null ||  !currentResults.Success)
                {
                    failed = true;
                    Console.WriteLine("Full run failed\n");
                    break;
                }

                // Parse the resulting xml
                XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
                Stream xmlFile = new FileStream(currentResults.LogFile, FileMode.Open);
                InlineForest f = (InlineForest) xml.Deserialize(xmlFile);
                long inlineCount = f.Methods.Sum(m => m.InlineCount);
                Console.WriteLine("*** This iteration of full config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
                currentResults.InlineForest = f;

                // Find the set of new methods that we saw
                HashSet<MethodId> newMethodIds = new HashSet<MethodId>();
                foreach (Method m in f.Methods)
                {
                    MethodId id = m.getId();

                    if (!fullMethodIds.Contains(id) && !newMethodIds.Contains(id))
                    {
                        fullMethods.Add(m);
                        newMethodIds.Add(id);

                        if (!noinlineResults.Methods.ContainsKey(id))
                        {
                            // Need to figure out why this happens.
                            //
                            // Suspect we're inlining force inlines in the noinline model but not here.
                            Console.WriteLine("*** full model uncovered new method: Token:0x{0:X8} Hash:0x{1:X8}", m.Token, m.Hash);
                            newMethodCount++;
                        }

                        if (m.InlineCount > maxInlineCount)
                        {
                            maxInlineCount = m.InlineCount;
                            maxInlineMethod = m;
                        }

                        if (m.InlineCount == 0)
                        {
                            leafMethodCount++;
                        }
                    }
                }

                Console.WriteLine("*** found {0} new methods", newMethodIds.Count);

                if (newMethodIds.Count == 0)
                {
                    failed = true;
                    Console.WriteLine("*** bailing out, unable to make forward progress");
                    break;
                }

                fullMethodIds.UnionWith(newMethodIds);
            }

            if (failed)
            {
                return null;
            }

            Console.WriteLine("*** Full model complete, took {0} iterations", iteration);

            // Now build the aggregate inline forest....
            InlineForest fullForest = new InlineForest();
            fullForest.Methods = fullMethods.ToArray();

            // And consolidate into a results set
            Results fullResults = new Results();
            fullResults.InlineForest = fullForest;
            fullResults.Name = "full";

            // Populate the methodId -> method lookup table
            Dictionary<MethodId, Method> methods = new Dictionary<MethodId, Method>(fullMethods.Count);
            foreach (Method m in fullMethods)
            {
                MethodId id = m.getId();
                methods[id] = m;
            }
            fullResults.Methods = methods;

            long fullInlineCount = fullForest.Methods.Sum(m => m.InlineCount);
            uint nonLeafMethodCount = (uint) fullMethods.Count - leafMethodCount;
            Console.WriteLine("*** Full config has {0} methods, {1} inlines", fullForest.Methods.Length, fullInlineCount);
            Console.WriteLine("*** {0} leaf methods, {1} methods with inlines, {2} average inline count",
                leafMethodCount, nonLeafMethodCount, fullInlineCount/ nonLeafMethodCount);
            Console.WriteLine("*** {0} max inline count for method 0x{1:X8} -- {2} subtrees", 
                maxInlineCount, maxInlineMethod.Token, maxInlineMethod.NumSubtrees());

            // Serialize out the consolidated set of trees
            XmlSerializer xo = new XmlSerializer(typeof(InlineForest));
            Stream xmlOutFile = new FileStream(Path.Combine(resultsDir, b.ShortName + "-full-consolidated.xml"), FileMode.Create);
            xo.Serialize(xmlOutFile, fullForest);

            // Now get full perf numbers -- just for the initial set
            Configuration fullPerfConfig = new Configuration("full-perf");
            fullPerfConfig.Environment["COMPlus_JitInlinePolicyFull"] = "1";
            fullPerfConfig.Environment["COMPlus_JitInlineDepth"] = "10";
            fullPerfConfig.Environment["COMPlus_JitInlineSize"] = "200";
            fullPerfConfig.ResultsDirectory = Program.RESULTS_DIR;
            Results perfResults = x.RunBenchmark(b, fullPerfConfig);
            fullResults.Performance = perfResults.Performance;
            fullResults.Performance.Print("full");

            // Get full call counts.
            // Ideally, perhaps, drive this from the noinline set...?
            if (CaptureCallCounts)
            {
                Configuration fullPerfCallCountConfig = new Configuration("full-perf-cc");
                fullPerfCallCountConfig.ResultsDirectory = Program.RESULTS_DIR;
                fullPerfCallCountConfig.Environment["COMPlus_JitInlinePolicyFull"] = "1";
                fullPerfCallCountConfig.Environment["COMPlus_JitInlineDepth"] = "10";
                fullPerfCallCountConfig.Environment["COMPlus_JitInlineSize"] = "200";
                fullPerfCallCountConfig.Environment["COMPlus_JitMeasureEntryCounts"] = "1";
                Results ccResults = r.RunBenchmark(b, fullPerfCallCountConfig);

                AnnotateCallCounts(ccResults, fullResults);
            }

            return fullResults;
        }

        // The "model" model uses heuristics based on modelling actual
        // observations
        Results BuildModelModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Model Model for {0}", b.ShortName);

            Configuration modelConfig = new Configuration("model");
            modelConfig.ResultsDirectory = Program.RESULTS_DIR;
            modelConfig.Environment["COMPlus_JitInlinePolicyModel"] = "1";
            modelConfig.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results modelResults = r.RunBenchmark(b, modelConfig);

            if (modelResults == null || !modelResults.Success)
            {
                Console.WriteLine("{0} run failed\n", modelConfig.Name);
                return null;
            }

            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            Stream xmlFile = new FileStream(modelResults.LogFile, FileMode.Open);
            InlineForest f = (InlineForest)xml.Deserialize(xmlFile);
            long inlineCount = f.Methods.Sum(m => m.InlineCount);
            Console.WriteLine("*** {0} config has {1} methods, {2} inlines",
                modelConfig.Name, f.Methods.Length, inlineCount);
            modelResults.InlineForest = f;

            // Populate the methodId -> method lookup table
            Dictionary<MethodId, Method> methods = new Dictionary<MethodId, Method>(f.Methods.Length);
            foreach (Method m in f.Methods)
            {
                MethodId id = m.getId();
                methods[id] = m;
            }
            modelResults.Methods = methods;

            // Now get perf numbers
            Configuration modelPerfConfig = new Configuration("model-perf");
            modelPerfConfig.ResultsDirectory = Program.RESULTS_DIR;
            modelPerfConfig.Environment["COMPlus_JitInlinePolicyModel"] = "1";
            Results perfResults = x.RunBenchmark(b, modelPerfConfig);
            modelResults.Performance = perfResults.Performance;
            modelResults.Performance.Print(modelConfig.Name);

            // Get method call counts
            if (CaptureCallCounts)
            {
                Configuration modelCallCountConfig = new Configuration("model-cc");
                modelCallCountConfig.ResultsDirectory = Program.RESULTS_DIR;
                modelCallCountConfig.Environment["COMPlus_JitMeasureEntryCounts"] = "1";
                modelCallCountConfig.Environment["COMPlus_JitInlinePolicyModel"] = "1";
                Results ccResults = r.RunBenchmark(b, modelCallCountConfig);

                // Parse results back and annotate base method set
                AnnotateCallCounts(ccResults, modelResults);
            }

            return modelResults;
        }

        // The size model tries not to increase method size
        Results BuildSizeModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Size Model for {0}", b.ShortName);

            Configuration sizeConfig = new Configuration("size");
            sizeConfig.ResultsDirectory = Program.RESULTS_DIR;
            sizeConfig.Environment["COMPlus_JitInlinePolicySize"] = "1";
            sizeConfig.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results results = r.RunBenchmark(b, sizeConfig);

            if (results == null || !results.Success)
            {
                Console.WriteLine("{0} run failed\n", sizeConfig.Name);
                return null;
            }

            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            InlineForest f;
            Stream xmlFile = new FileStream(results.LogFile, FileMode.Open);
            f = (InlineForest)xml.Deserialize(xmlFile);
            long inlineCount = f.Methods.Sum(m => m.InlineCount);
            Console.WriteLine("*** {0} config has {1} methods, {2} inlines", 
                sizeConfig.Name, f.Methods.Length, inlineCount);
            results.InlineForest = f;

            // Now get perf numbers
            Configuration sizePerfConfig = new Configuration("size-perf");
            sizePerfConfig.ResultsDirectory = Program.RESULTS_DIR;
            sizePerfConfig.Environment["COMPlus_JitInlinePolicySize"] = "1";
            Results perfResults = x.RunBenchmark(b, sizePerfConfig);
            results.Performance = perfResults.Performance;
            results.Performance.Print(sizeConfig.Name);

            return results;
        }

        // The random model is random
        Results BuildRandomModel(Runner r, Runner x, Benchmark b, uint seed)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Random Model {0} for {1}", seed, b.ShortName);

            // Grr, requires DEBUG build. Punt for now.
            return null;

        }

        // Paths to repos and binaries. 
        // Todo: Make this configurable.
        public static string REPO_ROOT = @"c:\repos";
        public static string CORECLR_ROOT = REPO_ROOT + @"\coreclr";
        public static string CORECLR_BENCHMARK_ROOT = CORECLR_ROOT + @"\bin\tests\Windows_NT.x64.Release\JIT\performance\codequality";
        public static string CORERUN = CORECLR_ROOT +  @"\bin\tests\Windows_NT.x64.release\tests\Core_Root\corerun.exe";
        public static string SHELL = @"c:\windows\system32\cmd.exe";
        public static string RESULTS_DIR = REPO_ROOT + @"\PerformanceExplorer\results";
        public static string SANDBOX_DIR = REPO_ROOT + @"\PerformanceExplorer\sandbox";

        // Various aspects of the exploration that can be enabled/disabled.
        // Todo: Make this configurable.
        public static bool UseLegacyModel = true;
        public static bool UseFullModel = false;
        public static bool UseModelModel = false;
        public static bool UseSizeModel = false;
        public static bool ExploreInlines = true;
        public static bool CaptureCallCounts = true;
        public static bool SkipProblemBenchmarks = true;
        public static uint MinIterations = 5;
        public static uint MaxIterations = 5;
        public static string ClassFilter = null;
        public static string MethodFilter = null;

        public static List<string> ParseArgs(string[] args)
        {
            List<string> benchNames = new List<string>();

            for (int i = 0; i< args.Length; i++)
            {
                string arg = args[i];

                if (arg[0] == '-')
                {
                    if (arg == "-perf")
                    {
                        ExploreInlines = false;
                        CaptureCallCounts = false;
                    }
                    else if (arg == "-allTests")
                    {
                        SkipProblemBenchmarks = false;
                    }
                    else if (arg == "-noLegacy")
                    {
                        UseLegacyModel = false;
                    }
                    else if (arg == "-noExplore")
                    {
                        ExploreInlines = false;
                    }
                    else if (arg == "-useFull")
                    {
                        UseFullModel = true;
                    }
                    else if (arg == "-useSize")
                    {
                        UseSizeModel = true;
                    }
                    else if (arg == "-useModel")
                    {
                        UseModelModel = true;
                    }
                    else if (arg == "-minIterations" && (i + 1) < args.Length)
                    {
                        MinIterations = UInt32.Parse(args[++i]);
                    }
                    else if (arg == "-maxIterations" && (i + 1) < args.Length)
                    {
                        MaxIterations = UInt32.Parse(args[++i]);
                    }
                    else if (arg == "-method" && (i + 1) < args.Length)
                    {
                        MethodFilter = args[++i];
                    }
                    else if (arg == "-class" && (i + 1) < args.Length)
                    {
                        ClassFilter = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("... ignoring '{0}'", arg);
                    }
                }
                else
                {
                    benchNames.Add(arg);
                }
            }

            return benchNames;
        }

        public static bool Configure()
        {
            // Verify repo root
            if (Directory.Exists(REPO_ROOT))
            {
                if (Directory.Exists(Path.Combine(REPO_ROOT, "coreclr")))
                {
                    return true;
                }
            }

            // Else search up from current WD
            string cwd = Directory.GetCurrentDirectory();
            Console.WriteLine("... coreclr repo not at {0}, searching up from {1}", REPO_ROOT, cwd);
            DirectoryInfo cwdi = new DirectoryInfo(cwd);
            bool found = false;
            while (cwdi != null)
            {
                string prospect = Path.Combine(cwdi.FullName, "coreclr");
                Console.WriteLine("... looking for {0}", prospect);
                if (Directory.Exists(prospect))
                {
                    REPO_ROOT = cwdi.FullName;
                    Console.WriteLine("... found coreclr repo at {0}", prospect);
                    found = true;
                    break;
                }

                cwdi = cwdi.Parent;
            }

            if (!found)
            {
                return false;
            }

            // Set up other paths
            CORECLR_ROOT = Path.Combine(REPO_ROOT, "coreclr");
            CORECLR_BENCHMARK_ROOT = Path.Combine(new string[]
                {CORECLR_ROOT, "bin", "tests", "Windows_NT.x64.Release", "JIT", "performance", "codequality"});
            CORERUN = Path.Combine(new string[] 
                { CORECLR_ROOT, "bin", "tests", "Windows_NT.x64.release", "tests", "Core_Root", "corerun.exe"});
            RESULTS_DIR = Path.Combine(REPO_ROOT, "PerformanceExplorer", "results");
            SANDBOX_DIR = Path.Combine(REPO_ROOT, "PerformanceExplorer", "sandbox");

            return true;
        }

        public static int Main(string[] args)
        {
            List<string> benchNames = ParseArgs(args);
            bool ok = Configure();
            if (!ok)
            {
                Console.WriteLine("Cound not find coreclr repo");
                return -1;
            }

            Program p = new Program();

            // Enumerate benchmarks that can be run
            string benchmarkRoot = CORECLR_BENCHMARK_ROOT;
            Console.WriteLine("...Enumerating benchmarks under {0}", benchmarkRoot);
            Dictionary<string, string> benchmarks = new Dictionary<string, string>();
            DirectoryInfo benchmarkRootInfo = new DirectoryInfo(benchmarkRoot);
            foreach (FileInfo f in benchmarkRootInfo.GetFiles("*.exe", SearchOption.AllDirectories))
            {
                benchmarks.Add(f.Name, f.FullName);
            }

            Console.WriteLine("...Found {0} benchmarks", benchmarks.Count());

            // If an arg is passed, run benchmarks that contain that arg as a substring.
            // Otherwise run them all.
            List<string> benchmarksToRun = new List<string>();

            if (benchNames.Count == 0)
            {
                Console.WriteLine("...Running all benchmarks");
                benchmarksToRun.AddRange(benchmarks.Values);
            }
            else
            {
                Console.WriteLine("...Scanning for benchmarks matching your pattern(s)");
                foreach (string item in benchNames)
                {
                    int beforeCount = benchmarksToRun.Count;
                    foreach (string benchName in benchmarks.Keys)
                    {
                        if (benchmarks[benchName].IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            benchmarksToRun.Add(benchmarks[benchName]);
                        }
                    }

                    if (benchmarksToRun.Count == 0)
                    {
                        Console.WriteLine("No benchmark matches '{0}'", item);
                    }
                    else
                    {
                        Console.WriteLine("{0} benchmarks matched '{1}'",
                                benchmarksToRun.Count - beforeCount, item);
                    }
                }
            }

            int result = p.RunBenchmarks(benchmarksToRun);

            return result;
        }

        int RunBenchmarks(List<string> benchmarksToRun)
        {
            Runner r = new CoreClrRunner();
            Runner x = new XunitPerfRunner();

            // Build integrated data model...
            string dataModelName = "All-Benchmark-data-model.csv";
            string dataModelFileName = Path.Combine(Program.RESULTS_DIR, dataModelName);
            bool hasHeader = false;
            StreamWriter dataModelFile = null;
            if (ExploreInlines)
            {
                dataModelFile = File.CreateText(dataModelFileName);
            }

            // Collect up result sets
            List<List<Results>> aggregateResults = new List<List<Results>>(benchmarksToRun.Count());

            foreach (string s in benchmarksToRun)
            {
                // Ignore benchmarks that are not reliable enough for us to to measure when looking for
                // per-inline deltas.
                if (SkipProblemBenchmarks)
                {
                    if (s.IndexOf("bytemark", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.WriteLine(".... bytemark disabled (noisy), sorry");
                        continue;
                    }
                    if (s.IndexOf("raytracer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.WriteLine(".... raytracer disabled (nondeterministic), sorry");
                        continue;
                    }
                }

                List<Results> benchmarkResults = new List<Results>();
                Benchmark b = new Benchmark();
                b.ShortName = Path.GetFileName(s);
                b.FullPath = s;
                b.ExitCode = 100;

                Results noInlineResults = BuildNoInlineModel(r, x, b);
                if (noInlineResults == null)
                {
                    Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                    continue;
                }
                benchmarkResults.Add(noInlineResults);

                if (UseLegacyModel)
                {
                    Results legacyResults = BuildLegacyModel(r, x, b);
                    if (legacyResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }
                    benchmarkResults.Add(legacyResults);

                    // See impact of LegacyPolicy inlines

                    int legacyCount = legacyResults.Performance.ExecutionTime.Count;
                    int noInlineCount = noInlineResults.Performance.ExecutionTime.Count;

                    if (legacyCount != noInlineCount)
                    {
                        Console.WriteLine("Odd, noinline had {0} parts, legacy has {1} parts. " +
                            "Skipping remainder of work for this benchmark",
                            noInlineCount, legacyCount);
                        continue;
                    }

                    if (legacyCount == 0)
                    {
                        Console.WriteLine("Odd, benchmark has no perf data. Skipping");
                        continue;
                    }
                }

                if (UseFullModel)
                {
                    Results fullResults = BuildFullModel(r, x, b, noInlineResults);
                    if (fullResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }

                    benchmarkResults.Add(fullResults);

                    CallGraph g = new CallGraph(fullResults);
                    string fileName = b.ShortName + "-callgraph.dot";
                    g.DumpDot(Path.Combine(RESULTS_DIR, fileName));
                }

                if (UseModelModel)
                {
                    Results modelResults = BuildModelModel(r, x, b);
                    if (modelResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }
                    benchmarkResults.Add(modelResults);
                }

                if (UseSizeModel)
                {
                    Results sizeResults = BuildSizeModel(r, x, b);
                    if (sizeResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }
                    benchmarkResults.Add(sizeResults);
                }

                aggregateResults.Add(benchmarkResults);

                if (ExploreInlines)
                {
                    var thingsToExplore = ExaminePerf(b, benchmarkResults);

                    foreach (Exploration e in thingsToExplore)
                    {
                        e.Explore(dataModelFile, ref hasHeader);
                    }

                    dataModelFile.Flush();
                }
            }

            bool first = true;
            foreach(List<Results> rr in aggregateResults)
            {
                ComparePerf(rr, first);
                first = false;
            }

            return 100;
        }

        void ComparePerf(List<Results> results, bool showHeader = true)
        {
            Results baseline = results.First();

            if (showHeader)
            {
                Console.WriteLine("---- Perf Results----");
                Console.Write("{0,-42}", "Test");
                foreach (Results r in results)
                {
                    Console.Write(" {0,8}.T {0,8}.I", r.Name);
                }
                Console.WriteLine();
            }

            foreach (string subBench in baseline.Performance.ExecutionTime.Keys)
            {
                Console.Write("{0,-42}", subBench);

                foreach (Results diff in results)
                {
                    double diffTime = PerformanceData.Average(diff.Performance.ExecutionTime[subBench]);
                    Console.Write(" {0,10:0.00}", diffTime);
                    double diffInst = PerformanceData.Average(diff.Performance.InstructionCount[subBench]);
                    Console.Write(" {0,10:0.00}", diffInst / (1000 * 1000));
                }

                Console.WriteLine();
            }
        }

        List<Exploration> ExaminePerf(Benchmark b, List<Results> results)
        {
            Results baseline = results.First();
            Console.WriteLine("---- Perf Examination----");
            List<Exploration> interestingResults = new List<Exploration>();

            foreach (Results diff in results)
            {
                // No need to investigate the baseline
                if (diff == baseline)
                {
                    continue;
                }

                // See if any of the sub-bench results are both significantly different 
                // than the baseline and measured with high confidence.
                bool added = false;

                foreach (string subBench in baseline.Performance.InstructionCount.Keys)
                {
                    List<double> baseData = baseline.Performance.InstructionCount[subBench];
                    double baseAvg = PerformanceData.Average(baseData);
                    List<double> diffData = diff.Performance.InstructionCount[subBench];
                    double diffAvg = PerformanceData.Average(diffData);
                    double confidence = PerformanceData.Confidence(baseData, diffData);
                    double avgDiff = diffAvg - baseAvg;
                    double pctDiff = 100 * avgDiff / baseAvg;
                    double interestingDiff = 1;
                    double confidentDiff = 0.9;
                    bool interesting = Math.Abs(pctDiff) > interestingDiff;
                    bool confident = confidence > confidentDiff;
                    string interestVerb = interesting ? "is" : "is not";
                    string confidentVerb = confident ? "and is" : "and is not";
                    bool show = interesting && confident;

                    if (!added & interesting && confident)
                    {
                        Exploration e = new Exploration();
                        e.baseResults = baseline;
                        e.endResults = diff;
                        e.benchmark = b;
                        interestingResults.Add(e);
                        added = true;

                        Console.WriteLine(
                            "$$$ {0} diff {1} in instructions between {2} ({3}) and {4} ({5}) "
                            + "{6} interesting {7:0.00}% {8} significant p={9:0.00}",
                            subBench, avgDiff / (1000 * 1000),
                            baseline.Name, baseAvg / (1000 * 1000),
                            diff.Name, diffAvg / (1000 * 1000),
                            interestVerb, pctDiff,
                            confidentVerb, confidence);

                        break;
                    }
                }

                if (!added)
                {
                    Console.WriteLine("$$$ {0} performance diff from {1} was not significant and confident", b.ShortName, diff.Name);
                }
            }

            return interestingResults;
        }

        static void AnnotateCallCounts(Results ccResults, Results results)
        {
            // Parse results back and annotate base method set
            using (StreamReader callCountStream = File.OpenText(ccResults.LogFile))
            {
                string callCountLine = callCountStream.ReadLine();
                while (callCountLine != null)
                {
                    string[] callCountFields = callCountLine.Split(new char[] { ',' });
                    if (callCountFields.Length == 3)
                    {
                        uint token = UInt32.Parse(callCountFields[0], System.Globalization.NumberStyles.HexNumber);
                        uint hash = UInt32.Parse(callCountFields[1], System.Globalization.NumberStyles.HexNumber);
                        ulong count = UInt64.Parse(callCountFields[2]);

                        MethodId id = new MethodId();
                        id.Hash = hash;
                        id.Token = token;

                        if (results.Methods.ContainsKey(id))
                        {
                            Method m = results.Methods[id];
                            m.CallCount = count;
                            Console.WriteLine("{0} called {1} times", m.Name, count);
                        }
                        else
                        {
                            Console.WriteLine("{0:X8} {1:X8} called {2} times, but is not in base set?", token, hash, count);
                        }
                    }
                    callCountLine = callCountStream.ReadLine();
                }
            }
        }
    }
}
