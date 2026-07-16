using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FluentGpu.WindowsApi.Globalization;

/// <summary>Windows globalization helpers that remain usable when .NET invariant globalization is enabled.</summary>
[SupportedOSPlatform("windows5.0")]
public static partial class WindowsCulture
{
    private const int LocaleNameMaxLength = 85; // LOCALE_NAME_MAX_LENGTH, including the null terminator.
    private const uint DateShortDate = 0x00000001;
    private const uint TimeNoSeconds = 0x00000002;

    /// <summary>Returns the user's default Windows UI locale as a BCP-47 name (for example <c>en-US</c>), or an
    /// empty string when Windows cannot resolve it. This is the host provider for
    /// <c>FluentGpu.Localization.Localization.OsCultureProvider</c>.</summary>
    public static unsafe string GetUserDefaultLocaleName()
    {
        Span<char> buffer = stackalloc char[LocaleNameMaxLength];
        int length;
        fixed (char* p = buffer)
            length = GetUserDefaultLocaleNameNative(p, buffer.Length);

        // Win32 returns the character count including the null terminator; zero indicates failure.
        return length > 1 ? new string(buffer[..(length - 1)]) : string.Empty;
    }

    /// <summary>Formats the preserved calendar fields of <paramref name="value"/> with Windows NLS, which remains
    /// available when .NET invariant globalization is enabled. <paramref name="format"/> is an optional Windows date
    /// picture (for example <c>MMM yyyy</c>); null uses the locale's short-date pattern.</summary>
    public static unsafe string FormatDate(DateTimeOffset value, string? localeName, string? format = null)
    {
        var date = SystemTime.From(value);
        Span<char> buffer = stackalloc char[128];
        fixed (char* p = buffer)
        {
            int length = GetDateFormatExNative(NormalizeLocale(localeName), format is null ? DateShortDate : 0,
                in date, format, p, buffer.Length, null);
            return length > 1 ? new string(buffer[..(length - 1)]) : value.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Formats the preserved clock fields of <paramref name="value"/> with Windows NLS. Null format uses the
    /// locale's normal short-time representation without seconds.</summary>
    public static unsafe string FormatTime(DateTimeOffset value, string? localeName, string? format = null)
    {
        var time = SystemTime.From(value);
        Span<char> buffer = stackalloc char[128];
        fixed (char* p = buffer)
        {
            int length = GetTimeFormatExNative(NormalizeLocale(localeName), format is null ? TimeNoSeconds : 0,
                in time, format, p, buffer.Length);
            return length > 1 ? new string(buffer[..(length - 1)]) : value.ToString(format ?? "HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Formats an integer using the selected locale's grouping separators.</summary>
    public static string FormatNumber(long value, string? localeName)
        => FormatInvariantNumber(value.ToString(CultureInfo.InvariantCulture), localeName);

    /// <summary>Formats a decimal number with at most two fractional digits using the selected locale's separators.</summary>
    public static string FormatNumber(double value, string? localeName)
        => FormatInvariantNumber(value.ToString("0.##", CultureInfo.InvariantCulture), localeName);

    private static unsafe string FormatInvariantNumber(string invariantNumber, string? localeName)
    {
        Span<char> buffer = stackalloc char[128];
        fixed (char* p = buffer)
        {
            int length = GetNumberFormatExNative(NormalizeLocale(localeName), 0, invariantNumber, IntPtr.Zero, p, buffer.Length);
            return length > 1 ? new string(buffer[..(length - 1)]) : invariantNumber;
        }
    }

    private static string? NormalizeLocale(string? localeName)
        => string.IsNullOrWhiteSpace(localeName) ? null : localeName.Trim().Replace('_', '-');

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SystemTime
    {
        public readonly ushort Year;
        public readonly ushort Month;
        public readonly ushort DayOfWeek;
        public readonly ushort Day;
        public readonly ushort Hour;
        public readonly ushort Minute;
        public readonly ushort Second;
        public readonly ushort Milliseconds;

        private SystemTime(DateTimeOffset value)
        {
            Year = (ushort)value.Year;
            Month = (ushort)value.Month;
            DayOfWeek = (ushort)value.DayOfWeek;
            Day = (ushort)value.Day;
            Hour = (ushort)value.Hour;
            Minute = (ushort)value.Minute;
            Second = (ushort)value.Second;
            Milliseconds = (ushort)value.Millisecond;
        }

        public static SystemTime From(DateTimeOffset value) => new(value);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserDefaultLocaleName")]
    private static unsafe partial int GetUserDefaultLocaleNameNative(char* localeName, int localeNameCapacity);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDateFormatEx", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetDateFormatExNative(string? localeName, uint flags, in SystemTime date,
        string? format, char* output, int outputCapacity, string? calendar);

    [LibraryImport("kernel32.dll", EntryPoint = "GetTimeFormatEx", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetTimeFormatExNative(string? localeName, uint flags, in SystemTime time,
        string? format, char* output, int outputCapacity);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNumberFormatEx", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetNumberFormatExNative(string? localeName, uint flags, string value,
        IntPtr numberFormat, char* output, int outputCapacity);
}
