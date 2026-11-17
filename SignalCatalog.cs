using System;
using System.Collections.Generic;
using System.Text;

namespace StationeersPlc
{
    /// <summary>Decides which game signals are on/off (BOOL) and what scale analog ones get by default.</summary>
    internal static class SignalCatalog
    {
        public const string DefaultBooleans =
            "On,Open,Lock,Error,Activate,Idle,Power,Combustion,Plant,Harvest,ClearMemory,Flush,SoundAlert," +
            "AutoShutOff,AutoLand,Extended,Discover,Chart,Survey,Reset,ForceWrite";

        // Identifiers, not process values: 32-bit hashes and IDs that cannot be a 16-bit register.
        // Anything ending in "Hash" is excluded too.
        public const string DefaultExcludedSignals = "ReferenceId,PrefabHash,NameHash";

        private static readonly HashSet<string> Bools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ExcludedSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Configure(string booleanCsv)
        {
            Bools.Clear();
            foreach (var part in (booleanCsv ?? "").Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0) Bools.Add(s);
            }
        }

        public static bool IsBool(string signal) => Bools.Contains(signal);

        public static void ConfigureExclusions(string csv)
        {
            ExcludedSignals.Clear();
            foreach (var part in (csv ?? "").Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0) ExcludedSignals.Add(s);
            }
        }

        public static bool IsExcluded(string signal) =>
            ExcludedSignals.Contains(signal) || signal.EndsWith("Hash", StringComparison.Ordinal);

        /// <summary>
        /// Raw register value = game value x scale. Gas ratios (0..1) become 0..1000 (tenths of a percent).
        /// Everything else is 1:1. Edit the Scale column in the I/O list CSV to change it per point.
        /// </summary>
        public static double DefaultScale(string signal)
        {
            if (signal.StartsWith("Ratio", StringComparison.Ordinal) || signal == "CompletionRatio") return 1000.0;
            return 1.0;
        }

        /// <summary>Keeps letters, digits and '-'. Spaces and '_' become '_'. Safe for CSV and tag names.</summary>
        public static string Clean(string s, int max = 40)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
                else if (c == ' ' || c == '_') sb.Append('_');
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim('_');
        }
    }
}
