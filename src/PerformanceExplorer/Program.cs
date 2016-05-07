using System;
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
            ExecutionTimes = new SortedDictionary<string, double>();
        }

        public SortedDictionary<string, double> ExecutionTimes;

        public void Print(string configName)
        {
            foreach (string subBench in ExecutionTimes.Keys)
            {
                Console.WriteLine("{0} perf for {1} is {2:0.00} milliseconds",
                    configName, subBench, ExecutionTimes[subBench]);
            }
        }
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

        public uint Token;
        public uint Hash;
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
        public Inline[] Inlines;
        public uint Token;
        public uint Offset;
        public string Reason;
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
        public int SubBenchmarkCount;
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
            veryVerbose = false;
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
            results.Performance.ExecutionTimes[b.ShortName] = runnerProcess.ExitTime.Subtract(runnerProcess.StartTime).TotalMilliseconds;
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
                " -runner xunit.console.netcore.exe -runnerhost corerun.exe -runid " + perfName;
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
                Console.WriteLine("xUnitPerf: Finished running {0} -- configuration: {1}, exit code: {2} (expected {3})",
                    b.ShortName, c.Name, runnerProcess.ExitCode, b.ExitCode);
            }

            // Parse iterations out of perf-*.xml
            // Note: will need something smarter for multiple benchmarks in a binary.
            string xmlPerfResultsFile = Path.Combine(sandboxDir, perfName) + ".xml";
            XElement root = XElement.Load(xmlPerfResultsFile);
            IEnumerable<XElement> subBenchmarks =
                from el in root.Descendants("test") select el;
            SortedDictionary<string, double> perfNumbers = new SortedDictionary<string, double>();

            foreach (XElement sub in subBenchmarks)
            {
                IEnumerable<double> executionTimes =
                    from el in sub.Descendants("iteration")
                    where (string)el.Attribute("index") != "0"
                    select Double.Parse((string)el.Attribute("Duration"));

                if (executionTimes.Count() > 0)
                {
                    double avg = executionTimes.Average();
                    if (veryVerbose)
                    {
                        Console.WriteLine("Perf for {0} was {1}", sub.Attribute("name"), avg);
                    }
                    perfNumbers[(string)sub.Attribute("name")] = avg;
                }
                else
                {
                    Console.WriteLine("No perf data for {0} in {1} ?", sub.Attribute("name"), xmlPerfResultsFile);
                }
            }

            Results results = new Results();
            results.Success = (b.ExitCode == runnerProcess.ExitCode);
            results.ExitCode = b.ExitCode;
            results.LogFile = "";
            results.Performance.ExecutionTimes = perfNumbers;

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
        public static int Main(string[] args)
        {
            Program p = new Program();
            Runner r = new CoreClrRunner();
            Runner x = new XunitPerfRunner();

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

                Results legacyResults = p.BuildLegacyModel(r, x, b);
                if (legacyResults == null)
                {
                    Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                    continue;
                }

                // See impact of LegacyPolicy inlines

                int legacyCount = legacyResults.Performance.ExecutionTimes.Count;
                int noInlineCount = noInlineResults.Performance.ExecutionTimes.Count;

                if (legacyCount!= noInlineCount)
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

                foreach (string subBench in legacyResults.Performance.ExecutionTimes.Keys)
                {
                    double legacyTime = legacyResults.Performance.ExecutionTimes[subBench];
                    double noinlineTime = noInlineResults.Performance.ExecutionTimes[subBench];
                    double improvement = noinlineTime - legacyTime;
                    string change = improvement > 0 ? "improvement" : "regression";
                    Console.WriteLine("{0}: Legacy Policy perf {1}: {2:0.00} ({3:0.00}%)",
                        subBench, change, improvement, improvement / noinlineTime * 100);
                }

                Results fullResults = p.BuildFullModel(r, x, b, noInlineResults);
                if (fullResults == null)
                {
                    Console.WriteLine("Skipping remainder of runs for {0}", b.ShortName);
                    continue;
                }
            }

            return 100;
        }

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

            Results results = r.RunBenchmark(b, legacyConfig);

            if (results == null || !results.Success)
            {
                Console.WriteLine("Legacy run failed\n");
                return null;
            }

            XmlSerializer xml = new XmlSerializer(typeof(InlineForest));
            InlineForest f;
            Stream xmlFile = new FileStream(results.LogFile, FileMode.Open);
            f = (InlineForest) xml.Deserialize(xmlFile);
            long inlineCount = f.Methods.Sum(m => m.InlineCount);
            Console.WriteLine("*** Legacy config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
            results.InlineForest = f;

            // Now get legacy perf numbers
            for (int i = 0; i < x.Iterations(); i++)
            {
                Configuration legacyPerfConfig = new Configuration("legacy-perf-" + i);
                legacyPerfConfig.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
                Results perfResults = x.RunBenchmark(b, legacyPerfConfig);
                results.Performance = perfResults.Performance;
            }

            results.Performance.Print(legacyConfig.Name);

            return results;
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
    }
}
