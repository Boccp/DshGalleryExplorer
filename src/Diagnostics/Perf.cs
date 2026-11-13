using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DshGalleryExplorer
{
    /// <summary>
    /// 低开销计时汇总：
    /// <code>using (Perf.Measure("grid.paint")) { ... }</code>
    /// Measure 返回结构体，using 不产生堆分配；每个名称保留最近 4096 个样本用于计算 p95。
    /// 统计项：count / avg / p95 / max（毫秒）。
    /// </summary>
    internal static class Perf
    {
        private const int RESERVOIR = 4096;
        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static readonly object _lock = new object();
        private static readonly double TICK_TO_MS = 1000.0 / Stopwatch.Frequency;

        private sealed class Stat
        {
            public long Count;
            public double TotalMs;
            public double MaxMs;
            public readonly double[] Samples = new double[RESERVOIR];
            public int SampleCount;
            public int Next;
        }

        public struct Scope : IDisposable
        {
            private readonly string _name;
            private readonly long _start;

            internal Scope(string name)
            {
                _name = name;
                _start = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (_name == null) return;
                Record(_name, (Stopwatch.GetTimestamp() - _start) * TICK_TO_MS);
            }
        }

        public static Scope Measure(string name)
        {
            return new Scope(name);
        }

        public static void Record(string name, double ms)
        {
            lock (_lock)
            {
                Stat s;
                if (!_stats.TryGetValue(name, out s))
                {
                    s = new Stat();
                    _stats[name] = s;
                }
                s.Count++;
                s.TotalMs += ms;
                if (ms > s.MaxMs) s.MaxMs = ms;
                s.Samples[s.Next] = ms;
                s.Next = (s.Next + 1) % RESERVOIR;
                if (s.SampleCount < RESERVOIR) s.SampleCount++;
            }
        }

        public static void Reset()
        {
            lock (_lock) { _stats.Clear(); }
        }

        public static void Reset(string prefix)
        {
            lock (_lock)
            {
                List<string> keys = new List<string>(_stats.Keys);
                foreach (string k in keys)
                {
                    if (k.StartsWith(prefix, StringComparison.Ordinal)) _stats.Remove(k);
                }
            }
        }

        public sealed class Summary
        {
            public string Name;
            public long Count;
            public double AvgMs;
            public double P50Ms;
            public double P95Ms;
            public double MaxMs;
            public double TotalMs;
        }

        public static Summary Get(string name)
        {
            lock (_lock)
            {
                Stat s;
                if (!_stats.TryGetValue(name, out s)) return null;
                return Summarize(name, s);
            }
        }

        public static List<Summary> Snapshot()
        {
            List<Summary> list = new List<Summary>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, Stat> kv in _stats) list.Add(Summarize(kv.Key, kv.Value));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        private static Summary Summarize(string name, Stat s)
        {
            double[] arr = new double[s.SampleCount];
            Array.Copy(s.Samples, arr, s.SampleCount);
            Array.Sort(arr);
            return new Summary
            {
                Name = name,
                Count = s.Count,
                TotalMs = s.TotalMs,
                AvgMs = s.Count > 0 ? s.TotalMs / s.Count : 0,
                P50Ms = Percentile(arr, 0.50),
                P95Ms = Percentile(arr, 0.95),
                MaxMs = s.MaxMs
            };
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        /// <summary>生成 Markdown 表格形式的汇总报告。</summary>
        public static string Report()
        {
            List<Summary> list = Snapshot();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("| 名称 | count | avg ms | p50 ms | p95 ms | max ms |");
            sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: |");
            foreach (Summary s in list)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:0.00} | {3:0.00} | {4:0.00} | {5:0.00} |",
                    s.Name, s.Count, s.AvgMs, s.P50Ms, s.P95Ms, s.MaxMs));
            }
            return sb.ToString();
        }
    }
}
