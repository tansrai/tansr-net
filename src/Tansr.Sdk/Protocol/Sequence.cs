using System;
using System.Globalization;

namespace Tansr.Sdk.Protocol;

/// <summary>SDK2 原十进制字符串序号，不经过浮点数，不回绕。</summary>
public readonly struct Sequence : IEquatable<Sequence>, IComparable<Sequence>
{
    private readonly long _value;

    private Sequence(long value) { _value = value; }

    public string Value => _value.ToString(CultureInfo.InvariantCulture);

    public static Sequence Parse(string value, bool allowZero = true)
    {
        if (!TryParse(value, out var result, allowZero))
        {
            throw new WireProtocolException("invalid_request");
        }

        return result;
    }

    public static bool TryParse(string? value, out Sequence result, bool allowZero = true)
    {
        result = default;
        if (string.IsNullOrEmpty(value) || value!.Length > 19 ||
            (value.Length > 1 && value[0] == '0')) { return false; }

        long number = 0;
        foreach (var c in value)
        {
            if (c < '0' || c > '9' || number > (long.MaxValue - (c - '0')) / 10) { return false; }
            number = (number * 10) + (c - '0');
        }

        if (!allowZero && number == 0) { return false; }
        result = new Sequence(number);
        return true;
    }

    public long ToInt64() => _value;
    public int CompareTo(Sequence other) => _value.CompareTo(other._value);
    public bool Equals(Sequence other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Sequence other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() => Value;
    public static bool operator ==(Sequence left, Sequence right) => left.Equals(right);
    public static bool operator !=(Sequence left, Sequence right) => !left.Equals(right);
}
