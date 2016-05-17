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
            ResultsDirectory = @"c:\home";

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
        }

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
            Console.Write("### {0} perf for {1}", configName, subBench);

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
            Callers = new HashSet<Method>();
            Callees = new HashSet<Method>();
        }

        public MethodId getId()
        {
            MethodId id = new MethodId();
            id.Token = Token;
            id.Hash = Hash;
            return id;
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

        public Inline[] GetBfsSubtree(int k)
        {
            List<Inline> l = new List<Inline>(k);
            Queue<Inline> q = new Queue<Inline>();

            foreach (Inline i in Inlines)
            {
                q.Enqueue(i);
            }

            // BFS until we've enumerated the first k.
            while (l.Count() < k)
            {
                Inline i = q.Dequeue();
                l.Add(i);
                foreach (Inline ii in i.Inlines)
                {
                    q.Enqueue(ii);
                }
            }

            // DFS to copy with the list telling us
            // what to include.
            return GetDfsSubtree(Inlines, l);
        }

        Inline[] GetDfsSubtree(Inline[] inlines, List<Inline> filter)
        {
            List<Inline> newInlines = new List<Inline>();
            foreach (Inline x in inlines)
            {
                if (filter.Contains(x))
                {
                    Inline xn = x.ShallowCopy();
                    newInlines.Add(xn);
                    xn.Inlines = GetDfsSubtree(x.Inlines, filter);
                }
            }

            return newInlines.ToArray();
        }

        int GetBfsSubtree(Inline[] oldInlines, Inline[] newInlines, int remaining)
        {
            // Did we get enough yet?
            if (remaining <= 0)
            {
                return 0;
            }

            // Nope, start in on this level.
            for (int i = 0; i < oldInlines.Length; i++)
            {
                Inline oldInline = oldInlines[i];
                Inline newInline = newInlines[i];
                int count = oldInline.Inlines.Length;
                int take = Math.Min(count, remaining);
                newInline.Inlines = new Inline[take];

                for (int j = 0; j < take; j++)
                {
                    newInline.Inlines[j] = oldInline.Inlines[j].ShallowCopy();
                }

                remaining -= take;

                if (remaining == 0)
                {
                    break;
                }
            }

            return remaining;
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
        public void MarkAsDuplicate() { IsDuplicate = true; }
        public bool CheckIsDuplicate() { return IsDuplicate; }
        private bool IsDuplicate;

        public HashSet<Method> Callers;
        public HashSet<Method> Callees;
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
                    outFile.WriteLine("\"{0:X8}-{1:X8}\";", m.Token, m.Hash);

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
        public string Reason;
        public Inline[] Inlines;

        public Inline ShallowCopy()
        {
            Inline x = new Inline();
            x.Token = Token;
            x.Offset = Offset;
            x.Hash = Hash;
            x.Reason = Reason;
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

    public class Exploration
    {
        public Results baseResults;
        public Results endResults;
        public Benchmark benchmark;

        public void Explore()
        {
            Console.WriteLine("$$$ Exploring significant perf diff in {0} between {1} and {2}",
                benchmark.ShortName, baseResults.Name, endResults.Name);

            // Look at methods in end results with inlines.
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

            // Look at methods in end results with inlines.
            foreach (Method m in endResults.Methods.Values)
            {
                int endCount = (int)m.InlineCount;

                if (endCount == 0)
                {
                    continue;
                }

                // Noinline perf is already "known" from the baseline, so exclude that here.
                //
                // The maximal subtree perf may not be the end perf because the latter allows inlines
                // in all methods, and we're just expanding one method at a time here.
                Console.WriteLine("$$$ examining method {0} {1:X8} with {2} inlines and {3} permutations via BFS.",
                    m.Name, m.Token, endCount, m.NumSubtrees() - 1);
                m.Dump();

                // Now for the actual experiment. We're going to grow the method's inline tree from the
                // baseline tree (which is noinline) to the end result tree. For sufficiently large trees
                // there are lots of intermediate subtrees. For now we just do a simple breadth-first linear
                // exploration, as follows.
                Results[] explorationResults = new Results[endCount + 1];
                explorationResults[0] = baseResults;

                // Make a copy of the baseline inline forest.
                int methodCount = baseResults.InlineForest.Methods.Length;
                InlineForest kForest = new InlineForest();
                kForest.Methods = new Method[methodCount];
                for (int kk = 0; kk < methodCount; kk++)
                {
                    kForest.Methods[kk] = baseResults.InlineForest.Methods[kk].ShallowCopy();
                }

                // Find this method's index in the base forest.
                int index = 0;
                bool found = false;
                foreach (Method baseMethod in baseResults.InlineForest.Methods)
                {
                    if (m.getId().Equals(baseMethod.getId()))
                    {
                        found = true;
                        break;
                    }

                    index++;
                }

                if (!found)
                {
                    Console.WriteLine("$$$ Can't find method in base method list, sorry");
                    continue;
                }

                for (int k = 1; k <= endCount; k++)
                {
                    // Build inline subtree for method with first K nodes and swap it into the tree.
                    Inline[] mkInlines = m.GetBfsSubtree(k);

                    if (mkInlines == null)
                    {
                        // Only top level working for now
                        Console.WriteLine("$$$ Can't get this subtree yet, sorry");
                        continue;
                    }

                    kForest.Methods[index].Inlines = mkInlines;
                    kForest.Methods[index].InlineCount = (uint)k;

                    // Externalize the inline xml
                    XmlSerializer xo = new XmlSerializer(typeof(InlineForest));
                    string testName = String.Format("{0}-{1}-{2:X8}-{3}", benchmark.ShortName, endResults.Name, m.Token, k);
                    string xmlName = testName + ".xml";
                    string resultsDir = @"c:\repos\PerformanceExplorer\results";
                    string replayFileName = Path.Combine(resultsDir, xmlName);
                    using (Stream xmlOutFile = new FileStream(replayFileName, FileMode.Create))
                    {
                        xo.Serialize(xmlOutFile, kForest);
                    }
                    // Console.WriteLine("$$$ wrote inline xml to {0}", xmlName);

                    // Run the test and record the results.
                    XunitPerfRunner x = new XunitPerfRunner();
                    Configuration c = new Configuration(testName);
                    c.Environment["COMPlus_JitInlinePolicyReplay"] = "1";
                    c.Environment["COMPlus_JitInlineReplayFile"] = replayFileName;
                    // This dumps a lot of xml, since we're now running as part of
                    // xperf instead of as a standalone exe.
                    //
                    // c.Environment["COMPlus_JitInlineDumpXml"] = "1";
                    Results resultsK = x.RunBenchmark(benchmark, c);
                    resultsK.Performance.Print(c.Name);
                    explorationResults[k] = resultsK;

                    // Determine confidence level that something has changed.
                    // Note currently, if we can't tell the difference between the two, it may
                    // mean either (a) the method or call site was never executed, or (b)
                    // the inline had no perf impact.
                    // 
                    // We could still add this info to our model, since the jit won't generally
                    // be able to tell if a callee will be executed, but for now we just look
                    // for impactful changes.
                    Results resultsKm1 = explorationResults[k - 1];

                    foreach (string subBench in resultsKm1.Performance.InstructionCount.Keys)
                    {
                        List<double> dataKm1 = resultsKm1.Performance.InstructionCount[subBench];
                        List<double> dataK = resultsK.Performance.InstructionCount[subBench];
                        double confidence = PerformanceData.Confidence(dataKm1, dataK);

                        if (confidence > 0)
                        {
                            double avgKm1 = PerformanceData.Average(dataKm1);
                            double avgK = PerformanceData.Average(dataK);
                            double diff = avgK - avgKm1;
                            double pdiff = 100.0 * diff / avgKm1;

                            Console.WriteLine("$$$ Inline diff in {0}: {1} M instr ({2:0.00}%) measured with confidence {3:0.00}",
                                subBench, diff / (1000 * 1000), pdiff, confidence);
                        }
                    }
                }
            }
        }
    }

    // A mechanism to run the benchmark
    public abstract class Runner
    {
        public abstract Results RunBenchmark(Benchmark b, Configuration c);
        public abstract int Iterations();
    }

    public class CoreClrRunner : Runner
    {
        public CoreClrRunner()
        {
            cmdExe = @"c:\windows\system32\cmd.exe";
            runnerExe = @"c:\repos\coreclr\bin\tests\windows_nt.x64.Release\tests\core_root\corerun.exe";
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

        public override int Iterations()
        {
            return 1;
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
                    Console.WriteLine("Cleaning old xunit-perf sandbox '{0}'", sandboxDir);
                }
                Directory.Delete(sandboxDir, true);
            }

            if (verbose)
            {
                Console.WriteLine("Creating new xunit-perf sandbox '{0}'", sandboxDir);
            }
            Directory.CreateDirectory(sandboxDir);
            DirectoryInfo sandboxDirectoryInfo = new DirectoryInfo(sandboxDir);

            // Copy over xunit packages
            string xUnitPerfRunner = Path.Combine(coreclrRoot, @"packages\Microsoft.DotNet.xunit.performance.runner.Windows\1.0.0-alpha-build0029\tools");
            string xUnitPerfConsole = Path.Combine(coreclrRoot, @"packages\xunit.console.netcore\1.0.2-prerelease-00101\runtimes\any\native");
            string xUnitPerfAnalysis = Path.Combine(coreclrRoot, @"packages\Microsoft.DotNet.xunit.performance.analysis\1.0.0-alpha-build0029\tools");

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
            runnerProcess.StartInfo.Arguments = benchmarkFile + 
                " -nologo -runner xunit.console.netcore.exe -runnerhost corerun.exe -runid " + perfName;
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

                if (verbose)
                {
                    perfData.Summarize(subName, c.Name);
                }
            }

            results.Success = (b.ExitCode == runnerProcess.ExitCode);
            results.ExitCode = b.ExitCode;
            results.LogFile = "";
            results.Name = c.Name;

            return results;
        }

        public override int Iterations()
        {
            return 1;
        }

        static string sandboxDir = @"c:\repos\PerformanceExplorer\sandbox";
        static string coreclrRoot = @"c:\repos\coreclr";
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

            Configuration noInlineConfig = new Configuration("noinline");
            noInlineConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            noInlineConfig.Environment["COMPlus_JitInlinePolicyDiscretionary"] = "1";
            noInlineConfig.Environment["COMPlus_JitInlineLimit"] = "0";
            noInlineConfig.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results results = r.RunBenchmark(b, noInlineConfig);

            if (results == null || !results.Success)
            {
                Console.WriteLine("Noinline run failed\n");
                return null;
            }

            // Parse noinline xml
            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            InlineForest f;
            Stream xmlFile = new FileStream(results.LogFile, FileMode.Open);
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
            Console.WriteLine("*** Nonline config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
            results.InlineForest = f;

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

            results.Methods = methods;

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

            // Get noinline perf numbers

            for (int i = 0; i < x.Iterations(); i++)
            {
                Configuration noinlinePerfConfig = new Configuration("noinline-perf-" + i);
                noinlinePerfConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
                noinlinePerfConfig.Environment["COMPlus_JitInlinePolicyDiscretionary"] = "1";
                noinlinePerfConfig.Environment["COMPlus_JitInlineLimit"] = "0";
                Results perfResults = x.RunBenchmark(b, noinlinePerfConfig);

                // Should really "merge" the times here.
                results.Performance= perfResults.Performance;
            }

            results.Performance.Print(noInlineConfig.Name);

            return results;
        }

        // The legacy model reflects the current jit behavior.
        // Scoring of runs will be relative to this data.
        // The inherent noise level is also estimated here.
        Results BuildLegacyModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Legacy Model for {0}", b.ShortName);

            Configuration legacyConfig = new Configuration("legacy");
            legacyConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
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
            for (int i = 0; i < x.Iterations(); i++)
            {
                Configuration legacyPerfConfig = new Configuration("legacy-perf-" + i);
                legacyPerfConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
                Results perfResults = x.RunBenchmark(b, legacyPerfConfig);
                legacyResults.Performance = perfResults.Performance;
            }

            legacyResults.Performance.Print(legacyConfig.Name);

            return legacyResults;
        }

        // The full model creates an inline forest at some prescribed
        // depth. The inline configurations that will be explored
        // are sub-forests of this full forest.
        Results BuildFullModel(Runner r, Runner x, Benchmark b, Results noinlineResults)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Full Model for {0}", b.ShortName);

            string resultsDir = @"c:\repos\PerformanceExplorer\results";
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
            for (int i = 0; i < x.Iterations(); i++)
            {
                Configuration fullPerfConfig = new Configuration("full-perf-" + i);
                fullPerfConfig.Environment["COMPlus_JitInlinePolicyFull"] = "1";
                fullPerfConfig.Environment["COMPlus_JitInlineDepth"] = "10";
                fullPerfConfig.Environment["COMPlus_JitInlineSize"] = "200";
                fullPerfConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
                Results perfResults = x.RunBenchmark(b, fullPerfConfig);
                fullResults.Performance = perfResults.Performance;
            }

            fullResults.Performance.Print("full");

            return fullResults;
        }

        // The "model" model uses heuristics based on modelling actual
        // observations
        Results BuildModelModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Model Model for {0}", b.ShortName);

            Configuration modelConfig = new Configuration("model");
            modelConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            modelConfig.Environment["COMPlus_JitInlinePolicyModel"] = "1";
            modelConfig.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results results = r.RunBenchmark(b, modelConfig);

            if (results == null || !results.Success)
            {
                Console.WriteLine("{0} run failed\n", modelConfig.Name);
                return null;
            }

            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            InlineForest f;
            Stream xmlFile = new FileStream(results.LogFile, FileMode.Open);
            f = (InlineForest)xml.Deserialize(xmlFile);
            long inlineCount = f.Methods.Sum(m => m.InlineCount);
            Console.WriteLine("*** {0} config has {1} methods, {2} inlines",
                modelConfig.Name, f.Methods.Length, inlineCount);
            results.InlineForest = f;

            // Now get perf numbers
            for (int i = 0; i < x.Iterations(); i++)
            {
                Configuration modelPerfConfig = new Configuration("model-perf-" + i);
                modelPerfConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
                modelPerfConfig.Environment["COMPlus_JitInlinePolicyModel"] = "1";
                Results perfResults = x.RunBenchmark(b, modelPerfConfig);
                results.Performance = perfResults.Performance;
            }

            results.Performance.Print(modelConfig.Name);

            return results;
        }

        // The size model tries not to increase method size
        Results BuildSizeModel(Runner r, Runner x, Benchmark b)
        {
            Console.WriteLine("----");
            Console.WriteLine("---- Size Model for {0}", b.ShortName);

            Configuration sizeConfig = new Configuration("size");
            sizeConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
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
            for (int i = 0; i < x.Iterations(); i++)
            {
                Configuration sizePerfConfig = new Configuration("size-perf-" + i);
                sizePerfConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
                sizePerfConfig.Environment["COMPlus_JitInlinePolicySize"] = "1";
                Results perfResults = x.RunBenchmark(b, sizePerfConfig);
                results.Performance = perfResults.Performance;
            }

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

        public static int Main(string[] args)
        {
            Program p = new Program();
            Runner r = new CoreClrRunner();
            Runner x = new XunitPerfRunner();
            bool buildFullModel = false;
            bool buildModelModel = false;
            bool buildSizeModel = false;

            // Enumerate benchmarks that can be run
            Dictionary<string, string> benchmarks = new Dictionary<string, string>();

            string benchmarkRoot = @"c:\repos\coreclr\bin\tests\windows_nt.x64.release\jit\performance\codequality";
            DirectoryInfo benchmarkRootInfo = new DirectoryInfo(benchmarkRoot);
            foreach (FileInfo f in benchmarkRootInfo.GetFiles("*.exe", SearchOption.AllDirectories))
            {
                benchmarks.Add(f.Name, f.FullName);
            }

            // If an arg is passed, run benchmarks that contain that arg as a substring.
            // Otherwise run them all.
            List<string> benchmarksToRun = new List<string>();

            if (args.Length == 0)
            {
                benchmarksToRun.AddRange(benchmarks.Values);
            }
            else
            {
                Console.WriteLine("Scanning for benchmarks....");
                foreach (string item in args)
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
                        Console.WriteLine("No benchmark matches {0}", item);
                    }
                    else
                    {
                        Console.WriteLine("{0} benchmarks matched '{1}'",
                                benchmarksToRun.Count - beforeCount, item);
                    }
                }
            }

            foreach (string s in benchmarksToRun)
            {
                List<Results> allResults = new List<Results>();
                Benchmark b = new Benchmark();
                b.ShortName = Path.GetFileName(s);
                b.FullPath = s;
                b.ExitCode = 100;

                Results noInlineResults = p.BuildNoInlineModel(r, x, b);
                if (noInlineResults == null)
                {
                    Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                    continue;
                }
                allResults.Add(noInlineResults);

                Results legacyResults = p.BuildLegacyModel(r, x, b);
                if (legacyResults == null)
                {
                    Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                    continue;
                }
                allResults.Add(legacyResults);

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

                if (buildFullModel)
                {
                    Results fullResults = p.BuildFullModel(r, x, b, noInlineResults);
                    if (fullResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }

                    allResults.Add(fullResults);

                    CallGraph g = new CallGraph(fullResults);
                    g.DumpDot(@"c:\repos\PerformanceExplorer\results\" + b.ShortName + "-callgraph.dot");
                }

                if (buildModelModel)
                {
                    Results modelResults = p.BuildModelModel(r, x, b);
                    if (modelResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }
                    allResults.Add(modelResults);
                }

                if (buildSizeModel)
                {
                    Results sizeResults = p.BuildSizeModel(r, x, b);
                    if (sizeResults == null)
                    {
                        Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                        continue;
                    }
                    allResults.Add(sizeResults);
                }

                p.ComparePerf(allResults);
                var thingsToExplore = p.ExaminePerf(b, allResults);

                foreach(Exploration e in thingsToExplore)
                {
                    e.Explore();
                    break;
                }
            }

            return 100;
        }

        void ComparePerf(List<Results> results)
        {
            Results baseline = results.First();
            Console.WriteLine("---- Perf Results----");
            Console.Write("{0,-12}", "Test");
            foreach (Results r in results)
            {
                Console.Write(" {0,8}.T {0,8}.I", r.Name);
            }
            Console.WriteLine();

            foreach (string subBench in baseline.Performance.ExecutionTime.Keys)
            {
                Console.Write("{0,-12}", subBench);

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

            // See if any of the results are both significantly different than noinline
            // and measured with high confidence.
            foreach (string subBench in baseline.Performance.InstructionCount.Keys)
            {
                List<double> baseData = baseline.Performance.InstructionCount[subBench];
                double baseAvg = PerformanceData.Average(baseData);
                bool shown = false;

                foreach (Results diff in results)
                {
                    if (diff == baseline)
                    {
                        continue;
                    }

                    List<double> diffData = diff.Performance.InstructionCount[subBench];
                    double diffAvg = PerformanceData.Average(diffData);
                    double confidence = PerformanceData.Confidence(baseData, diffData);
                    double avgDiff = baseAvg - diffAvg;
                    double pctDiff = 100 * avgDiff / baseAvg;
                    double interestingDiff = 1;
                    double confidentDiff = 0.9;
                    bool interesting = Math.Abs(pctDiff) > interestingDiff;
                    bool confident = confidence > confidentDiff;
                    string interestVerb = interesting ? "is" : "is not";
                    string confidentVerb = confident ? "and is" : "and is not";
                    bool show = interesting && confident;

                    if (interesting && confident)
                    {
                        // Set up exploration of this performance diff
                        Exploration e = new Exploration();
                        e.baseResults = baseline;
                        e.endResults = diff;
                        e.benchmark = b;
                        interestingResults.Add(e);
                    }

                    if (!show)
                    {
                        continue;
                    }

                    shown = true;

                    Console.WriteLine(
                        "$$$ {0} diff {1} in instructions between {2} ({3}) and {4} ({5}) "
                        + "{6} interesting {7:0.00}% {8} significant p={9:0.00}",
                        subBench, avgDiff / (1000 * 1000),
                        baseline.Name, baseAvg / (1000 * 1000),
                        diff.Name, diffAvg / (1000 * 1000),
                        interestVerb, pctDiff,
                        confidentVerb, confidence);
                }

                if (!shown)
                {
                    Console.WriteLine("$$$ {0} no result diffs were both significant and confident", subBench);
                }
            }

            return interestingResults;
        }
    }
}
