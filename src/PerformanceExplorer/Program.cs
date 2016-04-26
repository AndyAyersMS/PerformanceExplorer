using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
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

    // InlineData describes the inline forest used
    // for the run.
    public class InlineData
    {

    }

    // The benchmark of interest
    public class Benchmark
    {
        public string ShortName;
        public string FullPath;
    }

    // A mechanism to run the benchmark
    public abstract class Runner
    {
        public abstract void RunBenchmark(Benchmark b, Configuration c);
    }

    public class CoreClrRunner : Runner
    {
        public CoreClrRunner()
        {
            cmdExe = @"c:\windows\system32\cmd.exe";
            runnerExe = @"c:\repos\coreclr\bin\tests\windows_nt.x64.Release\tests\core_root\corerun.exe";
            verbose = true;
        }

        public override void RunBenchmark(Benchmark b, Configuration c)
        {
            // Setup process information
            System.Diagnostics.Process runnerProcess = new Process();
            runnerProcess.StartInfo.FileName = cmdExe;
            string stderrName = c.ResultsDirectory + @"\" + b.ShortName + "-" + c.Name + ".err";

            foreach (string envVar in c.Environment.Keys)
            {
                runnerProcess.StartInfo.Environment[envVar] = c.Environment[envVar];
            }
            runnerProcess.StartInfo.Environment["CORE_ROOT"] = Path.GetDirectoryName(runnerExe);

            runnerProcess.StartInfo.Arguments = "/C \"" + runnerExe + " " + b.FullPath + "> " + stderrName + "\"";
            runnerProcess.StartInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(b.FullPath);
            runnerProcess.StartInfo.UseShellExecute = false;

            if (verbose)
            {
                Console.WriteLine("CoreCLR: launching " + runnerProcess.StartInfo.Arguments);
            }

            runnerProcess.Start();
            runnerProcess.WaitForExit();

            if (verbose)
            {
                Console.WriteLine("CoreCLR: Finished running {0} -- configuration: {1}, exit code: {2}",
                    b.ShortName, c.Name, runnerProcess.ExitCode);
            }
        }

        private string runnerExe;
        private string cmdExe;
        bool verbose;
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

            p.BuildBaseModel(r, b);
            p.BuildDefaultModel(r, b);
            p.BuildFullModel(r, b);
        }

        // The base model is one where inlining is disabled.
        // The inline forest here is minimal.
        //
        // An attributed profile of this model helps the tool
        // identify areas for investigation.
        void BuildBaseModel(Runner r, Benchmark b)
        {
            Configuration baseConfiguration = new Configuration("base");
            baseConfiguration.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            baseConfiguration.Environment["COMPlus_ZapDisable"] = "1";
            baseConfiguration.Environment["COMPlus_JitInlinePolicyDiscretionary"] = "1";
            baseConfiguration.Environment["COMPlus_JitInlineLimit"] = "0";
            baseConfiguration.Environment["COMPlus_JitInlineDumpXml"] = "1";

            r.RunBenchmark(b, baseConfiguration);
        }

        // The default model reflects the current jit behavior.
        // Scoring of runs will be relative to this data.
        // The inherent noise level is also estimated here.
        void BuildDefaultModel(Runner r, Benchmark b)
        {
            Configuration defaultConfiguration = new Configuration("default");
            defaultConfiguration.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            defaultConfiguration.Environment["COMPlus_ZapDisable"] = "1";
            defaultConfiguration.Environment["COMPlus_JitInlineDumpXml"] = "1";

            r.RunBenchmark(b, defaultConfiguration);
        }

        // The full model creates an inline forest at some prescribed
        // depth. The inline configurations that will be explored
        // are sub-forests of this full forest.
        void BuildFullModel(Runner r, Benchmark b)
        {
            Configuration fullConfiguration = new Configuration("full");
            fullConfiguration.ResultsDirectory = @"c:\repos\PerformanceExplorer\results";
            fullConfiguration.Environment["COMPlus_ZapDisable"] = "1";
            fullConfiguration.Environment["COMPlus_JitInlinePolicyFull"] = "1";
            fullConfiguration.Environment["COMPlus_JitInlineDepthLimit"] = "10";
            fullConfiguration.Environment["COMPlus_JitInlineDumpXml"] = "1";

            r.RunBenchmark(b, fullConfiguration);
        }
    }
}
