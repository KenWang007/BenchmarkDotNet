﻿using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace BenchmarkDotNet.Diagnostics.Windows
{
    public class EtwProfiler : IDiagnoser
    {
        private readonly RunMode runMode;
        private readonly int bufferSizeInMb;
        private readonly Dictionary<BenchmarkCase, string> benchmarkToEtlFile;

        private Session kernelSession, userSession;

        /// <param name="performExtraBenchmarksRun">if set to true, benchmarks will be executed on more time with the profiler attached. If set to false, there will be no extra run but the results will contain overhead</param>
        /// <param name="bufferSizeInMb">ETW session buffer size, in MB</param>
        public EtwProfiler(bool performExtraBenchmarksRun, int bufferSizeInMb)
        {
            runMode = performExtraBenchmarksRun ? RunMode.ExtraRun : RunMode.NoOverhead;
            this.bufferSizeInMb = bufferSizeInMb;
            benchmarkToEtlFile = new Dictionary<BenchmarkCase, string>();
        }

        public IEnumerable<string> Ids => new [] { nameof(EtwProfiler) };
        
        public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();
        
        public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();
        
        public IColumnProvider GetColumnProvider() => EmptyColumnProvider.Instance;

        public RunMode GetRunMode(BenchmarkCase benchmarkCase) => runMode;

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
            => HardwareCounters.Validate(validationParameters, mandatory: false);

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            if (signal == HostSignal.BeforeAnythingElse)
                Start(parameters);
            else if (signal == HostSignal.AfterActualRun)
                Stop(parameters);
        }

        public void ProcessResults(DiagnoserResults results) { }

        public void DisplayResults(ILogger logger)
        {
            if (!benchmarkToEtlFile.Any())
                return;
            
            logger.WriteLineInfo($"Exported {benchmarkToEtlFile.Count} trace file(s). Example:");
            logger.WriteLineInfo(benchmarkToEtlFile.Values.First());
        }

        private void Start(DiagnoserActionParameters parameters)
        {
            var counters = parameters.Config
                .GetHardwareCounters()
                .Select(counter => HardwareCounters.FromCounter(counter, info => Math.Min(info.MaxInterval, Math.Max(info.MinInterval, info.Interval))))
                .ToArray();

            if (counters.Any()) // we need to enable the counters before starting the kernel session
                HardwareCounters.Enable(counters);
            
            userSession = new UserSession(parameters, bufferSizeInMb).EnableProviders();
            kernelSession = new KernelSession(parameters, bufferSizeInMb).EnableProviders();
        }

        private void Stop(DiagnoserActionParameters parameters)
        {
            try
            {
                kernelSession.Stop();
                userSession.Stop();

                benchmarkToEtlFile[parameters.BenchmarkCase] = userSession.MergeFiles(kernelSession);
            }
            finally
            {
                kernelSession.Dispose();
                userSession.Dispose();
            }
        }
    }
}