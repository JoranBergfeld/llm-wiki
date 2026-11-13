using System;
using System.Globalization;

namespace Wiki.Core;

// The one place the CLI's timestamp formats live. Every recorded time - log.md,
// the audit log, ledgers, issues, transcripts, the write journal - is UTC
// to the second, `yyyy-MM-ddTHH:mm:ssZ`, and every date `yyyy-MM-dd`; keeping
// the format strings here means no writer can drift from the rest.
//
// The clock itself is injected per CONTRIBUTING.md ("Inject the clock and
// RNG"): services and boundary steps take a `Func<long> nowUnixMs`
// defaulting to `NowUnixMs`, so tests can pin times.
public static class Timestamp
{
    public const string IsoFormat = "yyyy-MM-ddTHH:mm:ss'Z'";
    public const string DateFormat = "yyyy-MM-dd";

    public static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string Iso(long unixMs) => Iso(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture);

    public static string Date(long unixMs) => Date(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

    public static string Date(DateTimeOffset t) => t.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
}
