using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Xml.Serialization;
using System.Text;

namespace PerformanceExplorer
{
    // A Configuration describes the environment
    // used to perform a particular run.
    public class Configuration
    {
        public Configuration(string name)
        {
            Name = name;
            Environment = new Dictionary<string, string>();
            ResultsDirectory = @"c:\home";
        }

        public string Name;
        public Dictionary<string, string> Environment;
        public string ResultsDirectory;
    }

    // PerformanceData describes performance
    // measurements for run
    public class PerformanceData
    {

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
    }

    // The results of running a benchmark
    public class Results
    {
        public int ExitCode;
        public string LogFile;
        public bool Success;
        public InlineForest InlineForest;
        public Dictionary<MethodId, Method> Methods;
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
            cmdExe = @"c:\windows\system32\cmd.exe";
            runnerExe = @"c:\repos\coreclr\bin\tests\windows_nt.x64.Release\tests\core_root\corerun.exe";
            verbose = true;
            veryVerbose = false;
        }

        public override Results RunBenchmark(Benchmark b, Configuration c)
        {
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

            return results;
        }

        private string runnerExe;
        private string cmdExe;
        bool verbose;
        bool veryVerbose;
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            Program p = new Program();
            Runner r = new CoreClrRunner();
            Benchmark b = new Benchmark();

            b.ShortName = "8Queens";
            b.FullPath = @"c:\repos\coreclr\bin\tests\windows_nt.x64.release\jit\performance\codequality\benchi\8queens\8queens\8queens.exe";
            b.ExitCode = 100;

            // Can't handle Roslyn yet. The inline Xml gets messed up by multithreading :-(
            //b.ShortName = "CscBench";
            //b.FullPath = @"C:\repos\coreclr\bin\tests\Windows_NT.x64.Release\JIT\Performance\CodeQuality\Roslyn\CscBench\CscBench.exe";
            //b.ExitCode = 100;

            //b.ShortName = "Linq";
            //b.FullPath = @"c:\repos\coreclr\bin\tests\windows_nt.x64.release\jit\performance\codequality\Linq\Linq\Linq.exe";
            //b.ExitCode = 100;

            Results baseResults = p.BuildBaseModel(r, b);
            Results defaultResults = p.BuildDefaultModel(r, b);
            Results fullResults = p.BuildFullModel(r, b, baseResults);
        }

        // The base model is one where inlining is disabled.
        // The inline forest here is minimal.
        //
        // An attributed profile of this model helps the tool
        // identify areas for investigation.
        Results BuildBaseModel(Runner r, Benchmark b)
        {
            Configuration baseConfiguration = new Configuration("base");
            baseConfiguration.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            baseConfiguration.Environment["COMPlus_ZapDisable"] = "1";
            baseConfiguration.Environment["COMPlus_JitInlinePolicyDiscretionary"] = "1";
            baseConfiguration.Environment["COMPlus_JitInlineLimit"] = "0";
            baseConfiguration.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results results = r.RunBenchmark(b, baseConfiguration);

            if (results.Success)
            {
                // Parse base inline xml
                XmlSerializer x = new XmlSerializer(typeof(InlineForest));
                InlineForest f;
                Stream xmlFile = new FileStream(results.LogFile, FileMode.Open);
                f = (InlineForest)x.Deserialize(xmlFile);
                long inlineCount = f.Methods.Sum(m => m.InlineCount);
                Console.WriteLine("*** Base config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
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

                Console.WriteLine("*** Base config has {0} unique method IDs", idCounts.Count);

                foreach (MethodId m in idCounts.Keys)
                {
                    uint count = idCounts[m];
                    if (count > 1)
                    {
                        Console.WriteLine("*** MethodId Token:0x{0:X8} Hash:0x{1:X8} has {2} duplicates", m.Token, m.Hash, count);
                    }
                }

                // Mark methods in base results that do not have unique IDs
                foreach (Method m in f.Methods)
                {
                    MethodId id = m.getId();
                    if (idCounts[id] > 1)
                    {
                        m.MarkAsDuplicate();
                    }
                }

                return results;
            }

            return null;
        }

        // The default model reflects the current jit behavior.
        // Scoring of runs will be relative to this data.
        // The inherent noise level is also estimated here.
        Results BuildDefaultModel(Runner r, Benchmark b)
        {
            Configuration defaultConfiguration = new Configuration("default");
            defaultConfiguration.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            defaultConfiguration.Environment["COMPlus_ZapDisable"] = "1";
            defaultConfiguration.Environment["COMPlus_JitInlineDumpXml"] = "1";

            Results results = r.RunBenchmark(b, defaultConfiguration);

            if (results.Success)
            {
                XmlSerializer x = new XmlSerializer(typeof(InlineForest));
                InlineForest f;
                Stream xmlFile = new FileStream(results.LogFile, FileMode.Open);
                f = (InlineForest)x.Deserialize(xmlFile);
                long inlineCount = f.Methods.Sum(m => m.InlineCount);
                Console.WriteLine("*** Default config has {0} methods, {1} inlines", f.Methods.Length, inlineCount);
                results.InlineForest = f;

                return results;
            }

            return null;
        }

        // The full model creates an inline forest at some prescribed
        // depth. The inline configurations that will be explored
        // are sub-forests of this full forest.
        Results BuildFullModel(Runner r, Benchmark b, Results baseResults)
        {
            string resultsDir = @"c:\repos\PerformanceExplorer\results";
            // Because we're jitting and inlining some methods won't be jitted on
            // their own at all. To unearth full trees for all methods we need
            // to iterate. The rough idea is as follows.
            //
            // Run with FullPolicy for all methods. This will end up jitting
            // some subset of methods seen in the base config. Compute this subset,
            // collect up their trees, and then disable inlining for those methods.
            // Rerun. This time around some of the methods missed in the first will
            // be jitted and will grow inline trees. Collect these new trees and
            // add those methods to the disabled set. Repeat until we've seen all methods.
            //
            // Unfortunately we don't have unique IDs for methods. To handle this we
            // need to determine which methods do have unique IDs.

            // This is the count of base methods with unique IDs.
            int methodCount = baseResults.Methods.Count;

            // We'll collect up these methods with their full trees here.
            HashSet<MethodId> fullMethodIds = new HashSet<MethodId>();
            List<Method> fullMethods = new List<Method>(methodCount);
            uint iteration = 0;
            uint maxInlineCount = 0;
            uint leafMethodCount = 0;
            uint newMethodCount = 0;
            Method maxInlineMethod = null;

            while (fullMethodIds.Count < methodCount + newMethodCount)
            {
                iteration++;

                Console.WriteLine("*** Full config -- iteration {0}, still need trees for {1} out of {2} methods",
                    iteration, methodCount + newMethodCount - fullMethodIds.Count, methodCount + newMethodCount);

                Configuration fullConfiguration = new Configuration("full-" + iteration);
                fullConfiguration.ResultsDirectory = resultsDir;
                fullConfiguration.Environment["COMPlus_ZapDisable"] = "1";
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

                if (!currentResults.Success)
                {
                    break;
                }

                // Parse the resulting xml
                XmlSerializer x = new XmlSerializer(typeof(InlineForest));
                Stream xmlFile = new FileStream(currentResults.LogFile, FileMode.Open);
                InlineForest f = (InlineForest)x.Deserialize(xmlFile);
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

                        if (!baseResults.Methods.ContainsKey(id))
                        {
                            // Need to figure out why this happens.
                            //
                            // Suspect we're inlining force inlines in the base model but not here.
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
                    Console.WriteLine("*** bailing out, unable to make forward progress");
                    break;
                }

                fullMethodIds.UnionWith(newMethodIds);
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

            return fullResults;
        }
    }
}
