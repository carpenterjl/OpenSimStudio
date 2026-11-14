using System.Globalization;
using OpenSim.Core.Model;

namespace OpenSim.Optics.MultiConfig;

/// <summary>What a multi-configuration operand's cells hold.</summary>
public enum MultiConfigValueKind
{
    /// <summary>A real number (a thickness, a field value).</summary>
    Number,

    /// <summary>A whole number (a flag, a type code, a surface number).</summary>
    Integer,

    /// <summary>A text (a glass, a coating, a comment, the title).</summary>
    Text,
}

/// <summary>One cell's value: a number, or the text of a text operand.</summary>
public readonly record struct MultiConfigValue(double Number, string? Text)
{
    public static MultiConfigValue Of(double number) => new(number, null);
    public static MultiConfigValue Of(string text) => new(double.NaN, text ?? "");

    /// <summary>True when the value is a text.</summary>
    public bool IsText => Text is not null;

    public override string ToString() => Text ?? Number.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// One multi-configuration operand of OpticStudio's table ("Multi-Configuration Operands",
/// OpticStudio 2025 R2 help, plans/sources/optics/op-30-help/Multi_Configuration_Operands.html):
/// its code, what its up to three integer arguments are, what its cells hold, and — when this
/// build has the data it controls — how to read and write that data on an optics record.
/// An operand without a target here is registered as not supported, with the reason, so a
/// table naming it fails with its code instead of being skipped.
/// </summary>
public sealed class MultiConfigOperand
{
    internal MultiConfigOperand(string code, string description, MultiConfigValueKind kind, IReadOnlyList<string> arguments)
    {
        Code = code;
        Description = description;
        Kind = kind;
        Arguments = arguments;
    }

    /// <summary>The four-letter code.</summary>
    public string Code { get; }

    /// <summary>What the operand controls, after the help's table.</summary>
    public string Description { get; }

    /// <summary>What the cells hold.</summary>
    public MultiConfigValueKind Kind { get; }

    /// <summary>The meaning of Number1, Number2, Number3 in order ("Surface", "Parameter",
    /// "Field" …); empty for an operand whose numbers are ignored.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Null when supported; else why this build cannot apply it.</summary>
    public string? NotSupportedReason { get; internal init; }

    /// <summary>True when the operand has no effect on the system by design (MOFF, HOLD, CWGT,
    /// PUCN — the last two are read by the table itself).</summary>
    public bool HasNoTarget { get; internal init; }

    public bool IsSupported => NotSupportedReason is null;

    internal Func<OpticsSettings, int, int, int, MultiConfigValue>? Getter { get; init; }
    internal Func<OpticsSettings, int, int, int, MultiConfigValue, OpticsSettings>? Setter { get; init; }

    /// <summary>The arguments of every target the system holds data for (Add All Data).</summary>
    internal Func<OpticsSettings, IEnumerable<(int, int, int)>>? Targets { get; init; }

    /// <summary>True when Add All Data can add rows of this operand.</summary>
    public bool CanAddAllData => Targets is not null && Getter is not null;

    /// <summary>The value the operand's target has in <paramref name="system"/>; throws
    /// <see cref="ArgumentException"/> naming the target when it does not exist, or when the
    /// operand is not supported or has no target.</summary>
    public MultiConfigValue Get(OpticsSettings system, int number1 = 0, int number2 = 0, int number3 = 0)
    {
        ArgumentNullException.ThrowIfNull(system);
        Refuse();
        if (Getter is null) throw new ArgumentException($"{Code} controls nothing in the system, so it has no value to read.");
        return Getter(system, number1, number2, number3);
    }

    /// <summary><paramref name="system"/> with the operand's target set to
    /// <paramref name="value"/>; throws <see cref="ArgumentException"/> naming the problem when
    /// the target does not exist or the value does not suit it. An operand without a target
    /// returns the system unchanged.</summary>
    public OpticsSettings Set(OpticsSettings system, int number1, int number2, int number3, MultiConfigValue value)
    {
        ArgumentNullException.ThrowIfNull(system);
        Refuse();
        if (HasNoTarget) return system;
        switch (Kind)
        {
            case MultiConfigValueKind.Text:
                if (!value.IsText) throw new ArgumentException($"{Code} takes a text, not the number {value}.");
                // "When using operands that define string values ... the string length is limited
                // to 32 characters." (help: Comment About Operands that Define Character Strings)
                if (value.Text!.Length > MultiConfigOperands.MaxTextLength)
                    throw new ArgumentException($"{Code}: the text '{value.Text}' has {value.Text.Length} characters; OpticStudio limits operand strings to {MultiConfigOperands.MaxTextLength}.");
                break;
            case MultiConfigValueKind.Integer:
                if (value.IsText || Math.Round(value.Number) != value.Number)
                    throw new ArgumentException($"{Code} takes a whole number, not {value}.");
                break;
            default:
                if (value.IsText || double.IsNaN(value.Number))
                    throw new ArgumentException($"{Code} takes a number, not '{value}'.");
                break;
        }
        return Setter!(system, number1, number2, number3, value);
    }

    private void Refuse()
    {
        if (NotSupportedReason is { } why)
            throw new ArgumentException($"The multi-configuration operand {Code} is not supported yet: {why}");
    }

    public override string ToString() => Code;
}
