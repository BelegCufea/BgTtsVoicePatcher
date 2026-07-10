using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BgTtsVoicePatcher.Gui.Models;

/// <summary>Base class for simple mutable DataGrid row models - just INotifyPropertyChanged
/// plumbing shared by every row type below.</summary>
public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One row of a simple string-to-string mapping (creNameReplacements,
/// genderOverrides).</summary>
public sealed class KeyValueRow : ObservableRow
{
    private string _key = "";
    public string Key { get => _key; set => Set(ref _key, value); }

    private string _value = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>One row of the genderTokens table: a token name plus its three
/// gendered replacement values.</summary>
public sealed class GenderTokenRow : ObservableRow
{
    private string _token = "";
    public string Token { get => _token; set => Set(ref _token, value); }

    private string _male = "";
    public string Male { get => _male; set => Set(ref _male, value); }

    private string _female = "";
    public string Female { get => _female; set => Set(ref _female, value); }

    private string _neutral = "";
    public string Neutral { get => _neutral; set => Set(ref _neutral, value); }
}

/// <summary>One row of a flat string list (identityTokens) - wrapped in a class
/// since a DataGrid needs a bindable property, not a raw string, to edit in place.</summary>
public sealed class TokenRow : ObservableRow
{
    private string _value = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>One row of the phoneticRules table.</summary>
public sealed class PhoneticRuleRow : ObservableRow
{
    private string _pattern = "";
    public string Pattern { get => _pattern; set => Set(ref _pattern, value); }

    private string _replacement = "";
    public string Replacement { get => _replacement; set => Set(ref _replacement, value); }

    private string _comment = "";
    public string Comment { get => _comment; set => Set(ref _comment, value); }
}

/// <summary>One row of the speaker review grid - a mutable wrapper around the engine's
/// SpeakerReviewRow, so the Gender and Cleaned Text columns can be edited in place.
/// Edits are tracked separately per field (IsGenderDirty / IsTextDirty) and written
/// back to speaker-names.json / text-overrides.json only when the user explicitly
/// saves, not on every keystroke.</summary>
public sealed class SpeakerRowViewModel : ObservableRow
{
    public int StrRef { get; }
    public string? SystemName { get; }
    public string? RealName { get; }
    public bool HasSound { get; }
    public string? SoundResRef { get; }

    /// <summary>The original, unmodified dialogue text straight from dialog.tlk.</summary>
    public string RawText { get; }

    private readonly string _originalGender;
    private string _gender;
    public string Gender
    {
        get => _gender;
        set
        {
            Set(ref _gender, value);
            IsGenderDirty = _gender != _originalGender;
        }
    }

    private bool _isGenderDirty;
    public bool IsGenderDirty { get => _isGenderDirty; private set => Set(ref _isGenderDirty, value); }

    /// <summary>The text that will actually be synthesized: token-replaced and
    /// phonetic-rule-cleaned automatically, or a prior hand-edited override. Editable -
    /// changing this and saving writes a permanent override for this StrRef, so
    /// future generate runs use exactly this wording instead of re-deriving it.</summary>
    private readonly string _originalCleanedText;
    private string _cleanedText;
    public string CleanedText
    {
        get => _cleanedText;
        set
        {
            Set(ref _cleanedText, value);
            IsTextDirty = _cleanedText != _originalCleanedText;
        }
    }

    private bool _isTextDirty;
    public bool IsTextDirty { get => _isTextDirty; private set => Set(ref _isTextDirty, value); }

    public bool IsTextOverridden { get; }

    public SpeakerRowViewModel(int strRef, string? systemName, string? realName, string gender,
        bool hasSound, string? soundResRef, string rawText, string cleanedText, bool isTextOverridden)
    {
        StrRef = strRef;
        SystemName = systemName;
        RealName = realName;
        HasSound = hasSound;
        SoundResRef = soundResRef;
        RawText = rawText;
        IsTextOverridden = isTextOverridden;
        _gender = gender;
        _originalGender = gender;
        _cleanedText = cleanedText;
        _originalCleanedText = cleanedText;
    }

    public void MarkSaved()
    {
        IsGenderDirty = false;
        IsTextDirty = false;
    }
}
