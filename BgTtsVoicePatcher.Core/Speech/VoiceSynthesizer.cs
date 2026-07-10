using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using BgTtsVoicePatcher.State;

namespace BgTtsVoicePatcher.Speech;

/// <summary>
/// Thin wrapper around System.Speech (Windows SAPI) that renders lines to WAV files
/// in the 22.05kHz / 16-bit / mono PCM format Infinity Engine games expect for sound
/// resources. Optionally swaps between a male/female SAPI voice per line based on a
/// <see cref="Gender"/> value, falling back to the default voice when unknown.
/// </summary>
public sealed class VoiceSynthesizer : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SpeechAudioFormatInfo _format = new(22050, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
    private readonly string? _defaultVoice;
    private readonly string? _maleVoice;
    private readonly string? _femaleVoice;
    private string? _currentVoice;

    public VoiceSynthesizer(string? defaultVoice, string? maleVoice, string? femaleVoice, int rate, int volume)
    {
        _maleVoice = maleVoice;
        _femaleVoice = femaleVoice;
        _defaultVoice = ResolveDefaultVoiceAlias(defaultVoice, maleVoice, femaleVoice);

        // Fail fast if any configured voice name doesn't actually exist.
        ValidateVoiceExists(_defaultVoice);
        ValidateVoiceExists(_maleVoice);
        ValidateVoiceExists(_femaleVoice);

        SelectVoice(_defaultVoice);

        _synth.Rate = Math.Clamp(rate, -10, 10);
        _synth.Volume = Math.Clamp(volume, 0, 100);
    }

    /// <summary>Lets --default-voice (or --voice) be given as the literal word "male" or
    /// "female" instead of an actual SAPI voice name, meaning "just reuse whichever voice
    /// I already set for that gender" rather than requiring it to be typed twice.</summary>
    private static string? ResolveDefaultVoiceAlias(string? defaultVoice, string? maleVoice, string? femaleVoice)
    {
        if (string.IsNullOrWhiteSpace(defaultVoice))
            return defaultVoice;

        return defaultVoice.Trim().ToLowerInvariant() switch
        {
            "male" => maleVoice ?? throw new InvalidOperationException(
                "--default-voice/--voice was set to \"male\", but --male-voice wasn't given."),
            "female" => femaleVoice ?? throw new InvalidOperationException(
                "--default-voice/--voice was set to \"female\", but --female-voice wasn't given."),
            _ => defaultVoice
        };
    }

    /// <summary>The voice name that will actually be used for a given gender - useful
    /// for logging/hashing so a changed gender map correctly triggers regeneration.</summary>
    public string VoiceNameFor(Gender gender) => gender switch
    {
        Gender.Male when _maleVoice is not null => _maleVoice,
        Gender.Female when _femaleVoice is not null => _femaleVoice,
        _ => _defaultVoice ?? "(system default)"
    };

    public void SpeakToWaveFile(string text, string outputWavPath, Gender gender)
    {
        SelectVoice(gender switch
        {
            Gender.Male when _maleVoice is not null => _maleVoice,
            Gender.Female when _femaleVoice is not null => _femaleVoice,
            _ => _defaultVoice
        });

        var directory = System.IO.Path.GetDirectoryName(outputWavPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _synth.SetOutputToWaveFile(outputWavPath, _format);
        try
        {
            _synth.Speak(text);
        }
        finally
        {
            _synth.SetOutputToNull();
        }
    }

    public static IEnumerable<string> ListInstalledVoices()
    {
        using var synth = new SpeechSynthesizer();
        return synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => $"{v.VoiceInfo.Name}  ({v.VoiceInfo.Culture}, {v.VoiceInfo.Gender}, {v.VoiceInfo.Age})")
            .ToList();
    }

    private void ValidateVoiceExists(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            _synth.SelectVoice(name);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"No installed SAPI voice named '{name}'. Run the 'voices' command to list what's available.");
        }
    }

    private void SelectVoice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == _currentVoice)
            return;

        _synth.SelectVoice(name);
        _currentVoice = name;
    }

    public void Dispose() => _synth.Dispose();
}

